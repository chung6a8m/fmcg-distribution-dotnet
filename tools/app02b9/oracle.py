#!/usr/bin/env python3
"""APP-02B.9: independent, fail-closed PREPROD evidence oracle.

The oracle cannot provision PREPROD, perform physical observations, approve itself,
or make a source-only GitHub Actions run an operational acceptance. Trust roots
MUST be supplied out of band, outside the repository and evidence bundle.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

CASE_IDS = tuple(f"B{gate:02d}-{case:02d}" for gate in range(1, 6) for case in range(1, 7))
GATES = tuple(f"G-B{gate:02d}" for gate in range(1, 6))
CI_CHECKS = frozenset({
    "APP00A_FULL", "APP02B2_RUNTIME", "APP02B3_IDENTITY",
    "APP02B4_TENANTS", "APP02B4_WEB", "APP02B5_AUTHORIZATION",
    "APP02B6_POLICY", "APP02B7_RECOVERY", "APP02B8_OPERATIONS",
    "APP02B9_ORACLE",
})
HEX64 = re.compile(r"^[0-9a-fA-F]{64}$")
HEX40 = re.compile(r"^[0-9a-fA-F]{40}$")
DIGEST = re.compile(r"^sha256:[0-9a-fA-F]{64}$")
SCHEMA = "APP02B9-PHYSICAL-EVIDENCE-V1"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def canonical_path(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise ValueError("INVALID_RELATIVE_ARTIFACT_PATH")
    p = Path(relative)
    if p.is_absolute() or any(part in (".", "..") for part in p.parts):
        raise ValueError("UNSAFE_ARTIFACT_PATH")
    destination = (root / p).resolve()
    if not destination.is_relative_to(root.resolve()) or not destination.is_file():
        raise ValueError("MISSING_OR_ESCAPED_ARTIFACT")
    return destination


def parse_utc(value: object) -> dt.datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise ValueError("UTC_TIMESTAMP_REQUIRED")
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.utcoffset() != dt.timedelta(0):
        raise ValueError("UTC_OFFSET_INVALID")
    return result


def strict_json_bytes(data: bytes) -> object:
    def no_constant(value: str) -> object:
        raise ValueError(f"NON_FINITE_JSON_NUMBER:{value}")

    def unique_keys(pairs: list[tuple[str, object]]) -> dict:
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"DUPLICATE_JSON_PROPERTY:{key}")
            result[key] = value
        return result

    return json.loads(data, parse_constant=no_constant, object_pairs_hook=unique_keys)


def read_object(path: Path) -> dict:
    value = strict_json_bytes(path.read_bytes())
    if not isinstance(value, dict):
        raise ValueError("OBJECT_REQUIRED")
    return value


def key_and_signature(
    root: Path, trust: dict, role: str, signed_file: Path, signature_name: str
) -> None:
    entry = trust["keys"][role]
    key = Path(entry["publicKeyPath"]).resolve()
    if key.is_relative_to(root.resolve()) or key.is_relative_to(Path(__file__).resolve().parents[2]):
        raise ValueError("TRUST_KEY_MUST_BE_OUT_OF_BAND")
    if not key.is_file() or sha256(key.read_bytes()) != entry["publicKeySha256"]:
        raise ValueError(f"TRUST_KEY_PIN_MISMATCH:{role}")
    sig = canonical_path(root, signature_name)
    completed = subprocess.run(
        ["openssl", "dgst", "-sha256", "-verify", str(key), "-signature", str(sig), str(signed_file)],
        capture_output=True, text=True, timeout=15, check=False,
    )
    if completed.returncode != 0 or "Verified OK" not in completed.stdout:
        raise ValueError(f"INVALID_DETACHED_SIGNATURE:{role}")


def evaluate(bundle: Path, fixture: Path, expected_head: str, trust_path: Path | None) -> dict:
    """Return a machine-readable NO_GO or externally verified acceptance report."""
    errors: list[str] = []
    cases: dict[str, str] = {case: "NOT_RUN" for case in CASE_IDS}

    def require(condition: bool, code: str) -> bool:
        if not condition:
            errors.append(code)
            return False
        return True

    manifest_path = bundle / "manifest.json"
    manifest = {}
    if not manifest_path.is_file():
        errors.append("PHYSICAL_PREPROD_MANIFEST_MISSING")
    else:
        try:
            manifest = read_object(manifest_path)
        except (OSError, ValueError, json.JSONDecodeError):
            errors.append("INVALID_PHYSICAL_MANIFEST")

    if not HEX40.fullmatch(expected_head):
        errors.append("INVALID_EXPECTED_FINAL_HEAD")

    trusted: dict = {}
    try:
        if trust_path is None:
            raise ValueError("NO_EXTERNAL_TRUST_ROOTS")
        trust_file = trust_path.resolve()
        if trust_file.is_relative_to(bundle.resolve()) or trust_file.is_relative_to(Path(__file__).resolve().parents[2]):
            raise ValueError("TRUST_ROOTS_MUST_BE_OUT_OF_BAND")
        trusted = read_object(trust_file)
        require(trusted.get("schema") == "APP02B9-TRUST-V1", "TRUST_SCHEMA_MISMATCH")
        require(trusted.get("approvedSourceHeadSha") == expected_head, "TRUST_HEAD_MISMATCH")
        require(len({trusted["keys"][role]["publicKeySha256"]
                     for role in ("fixture", "observer", "security", "dba")}) == 4,
                "INDEPENDENT_TRUST_KEY_ROLES_REQUIRED")
        require(len(set(trusted["tenantDatabaseIds"].values())) == 2,
                "TRUST_TWO_DISTINCT_TENANT_DATABASES_REQUIRED")
        require(bool(HEX64.fullmatch(str(trusted.get("expectedFixtureSha256", "")))) and
                bool(HEX64.fullmatch(str(trusted.get("approvedHostSha256", "")))),
                "TRUST_APPROVED_SHA256_PINS_REQUIRED")
        require(all(isinstance(x, str) and bool(x.strip())
                    for x in trusted["tenantDatabaseIds"].values()),
                "TRUST_EMPTY_DATABASE_IDS")
    except (OSError, ValueError, TypeError, KeyError) as exc:
        errors.append(f"TRUST_UNAVAILABLE:{str(exc)[:120]}")

    fixture_json: dict = {}
    fixture_bytes: bytes = b""
    try:
        fixture_bytes = fixture.read_bytes()
        fixture_json = strict_json_bytes(fixture_bytes)
        defs = fixture_json["cases"]
        require(fixture_json.get("schema") == "APP02B9-FROZEN-FIXTURE-V1", "FIXTURE_SCHEMA")
        require(isinstance(defs, dict) and set(defs) == set(CASE_IDS),
                "FROZEN_THIRTY_CASE_SET_CHANGED")
        if isinstance(defs, dict):
            for code in CASE_IDS:
                row = defs.get(code, {})
                require(isinstance(row.get("expected"), str) and bool(row["expected"])
                        and isinstance(row.get("negativeInvariant"), str)
                        and bool(row["negativeInvariant"])
                        and isinstance(row.get("requiredKinds"), list)
                        and len(set(row["requiredKinds"])) == len(row["requiredKinds"])
                        and bool(row["requiredKinds"]), f"FIXTURE_INCOMPLETE:{code}")
        require(sha256(fixture_bytes) == trusted.get("expectedFixtureSha256"),
                "INDEPENDENT_FIXTURE_PIN_MISMATCH")
        if trusted:
            key_and_signature(bundle, trusted, "fixture", fixture, "expected.sig")
    except (OSError, ValueError, TypeError, KeyError, json.JSONDecodeError) as exc:
        errors.append(f"FIXTURE_UNVERIFIED:{str(exc)[:120]}")

    if manifest:
        require(manifest.get("schema") == SCHEMA, "MANIFEST_SCHEMA")
        require(manifest.get("evidenceClass") == "PHYSICAL_PREPROD_WITNESSED",
                "DISPOSABLE_CI_IS_NOT_PREPROD")
        require(manifest.get("environmentMode") == "PREPROD_NONPRODUCTION",
                "NOT_PREPROD_NONPRODUCTION")
        require(manifest.get("sourceHeadSha") == expected_head, "EVIDENCE_HEAD_STALE")
        require(manifest.get("fixtureSha256") == sha256(fixture_bytes), "MANIFEST_FIXTURE_HASH")
        require(manifest.get("environmentId") == trusted.get("approvedEnvironmentId"),
                "UNTRUSTED_ENVIRONMENT")
        require(manifest.get("hostSha256") == trusted.get("approvedHostSha256"),
                "UNTRUSTED_HOST")
        require(isinstance(manifest.get("testRunId"), str) and
                bool(manifest["testRunId"].strip()), "MISSING_TEST_RUN_ID")
        require(bool(DIGEST.fullmatch(str(manifest.get("apiImageDigest", "")))) and
                bool(DIGEST.fullmatch(str(manifest.get("workerImageDigest", "")))),
                "MISSING_REPRODUCIBLE_IMAGE_DIGESTS")
        require(bool(HEX64.fullmatch(str(manifest.get("schemaMigrationSetSha256", "")))),
                "MISSING_SCHEMA_MIGRATION_HASH")
        dbs = manifest.get("tenantDatabases", {})
        require(isinstance(dbs, dict) and set(dbs) == {"A", "B"}
                and all(isinstance(dbs[a], dict) and
                        dbs[a].get("databaseId") == trusted.get("tenantDatabaseIds", {}).get(a)
                        and dbs[a].get("physicalName") and dbs[a].get("registryBindingVersion")
                        for a in ("A", "B"))
                and dbs.get("A", {}).get("physicalName") != dbs.get("B", {}).get("physicalName"),
                "PHYSICAL_TWO_TENANT_BINDING_NOT_VERIFIED")
        require(manifest.get("openBlockingFindings") == [],
                "UNRESOLVED_BLOCKING_FINDINGS")
        try:
            if trusted:
                key_and_signature(bundle, trusted, "observer", manifest_path, "manifest.sig")
        except (OSError, ValueError, TypeError, KeyError) as exc:
            errors.append(f"OBSERVER_MANIFEST_UNVERIFIED:{str(exc)[:120]}")

        witness_map: dict[str, tuple[dict, dict]] = {}
        seen_paths: set[str] = set()
        declared = manifest.get("witnesses", [])
        if not isinstance(declared, list):
            declared = []
            errors.append("WITNESS_LIST_MISSING")
        for w in declared:
            try:
                ref, path = w["ref"], w["path"]
                if ref in witness_map or path in seen_paths:
                    raise ValueError("DUPLICATE_WITNESS_ID_OR_PATH")
                seen_paths.add(path)
                artifact = canonical_path(bundle, path)
                if not HEX64.fullmatch(str(w["sha256"])) or sha256(artifact.read_bytes()) != w["sha256"]:
                    raise ValueError("WITNESS_CONTENT_HASH_MISMATCH")
                body = read_object(artifact)
                if (body.get("evidenceClass") != "PHYSICAL_PREPROD_INDEPENDENT"
                    or body.get("testRunId") != manifest.get("testRunId")
                    or body.get("sourceHeadSha") != expected_head
                    or body.get("environmentId") != manifest.get("environmentId")
                    or body.get("kind") != w.get("kind")
                    or body.get("observerId") != manifest.get("observerId")
                    or body.get("independentlyMeasured") is not True):
                    raise ValueError("WITNESS_NOT_INDEPENDENT_OR_WRONG_LINEAGE")
                raw = canonical_path(bundle, body["rawArtifactPath"])
                if (not HEX64.fullmatch(str(body.get("rawArtifactSha256", ""))) or
                    sha256(raw.read_bytes()) != body["rawArtifactSha256"] or
                    raw.stat().st_size == 0):
                    raise ValueError("RAW_INDEPENDENT_ARTIFACT_MISSING_OR_HASH_MISMATCH")
                witness_map[ref] = (w, body)
            except (KeyError, TypeError, OSError, ValueError, json.JSONDecodeError) as exc:
                errors.append(f"WITNESS_INVALID:{str(exc)[:120]}")

        reported = manifest.get("cases", [])
        if not isinstance(reported, list):
            reported = []
            errors.append("CASE_LIST_MISSING")
        seen_cases: set[str] = set()
        for row in reported:
            try:
                code = row["caseId"]
                if code not in cases or code in seen_cases:
                    raise ValueError("UNKNOWN_OR_DUPLICATE_CASE")
                seen_cases.add(code)
                if row.get("status") != "PASS":
                    cases[code] = row.get("status", "NOT_RUN") if row.get("status") in {"FAIL", "BLOCKED", "NOT_RUN"} else "FAIL"
                    errors.append(f"REQUIRED_CASE_NOT_PASS:{code}")
                    continue
                frozen = fixture_json["cases"][code]
                if (row.get("gateId") != f"G-{code[:3]}"
                    or row.get("testRunId") != manifest.get("testRunId")
                    or row.get("sourceHeadSha") != expected_head
                    or row.get("tenantScope") != "A_AND_B_PHYSICAL"
                    or row.get("observerId") != manifest.get("observerId")
                    or row.get("expected") != frozen["expected"]
                    or row.get("actual") != frozen["expected"]
                    or row.get("difference") != 0 or type(row.get("difference")) is not int
                    or row.get("match") is not True
                    or row.get("negativeVerified") is not True
                    or row.get("negativeInvariant") != frozen["negativeInvariant"]
                    or not isinstance(row.get("faultId"), str) or not row["faultId"]
                    or not isinstance(row.get("peerSqlHashBefore"), str)
                    or not HEX64.fullmatch(row["peerSqlHashBefore"])
                    or row.get("peerSqlHashBefore") != row.get("peerSqlHashAfter")):
                    raise ValueError("EXPECTED_ACTUAL_DIFF_OR_PEER_INVARIANT")
                parse_utc(row.get("executionUtc"))
                positives, negatives = row["positiveRefs"], row["negativeRefs"]
                if (not isinstance(positives, list) or not isinstance(negatives, list)
                    or not positives or not negatives or set(positives) & set(negatives)
                    or len(positives) != len(set(positives))
                    or len(negatives) != len(set(negatives))):
                    raise ValueError("POSITIVE_AND_NEGATIVE_WITNESSES_REQUIRED")
                required_kinds = set(frozen["requiredKinds"])
                kinds: set[str] = set()
                for ref in positives + negatives:
                    w, body = witness_map[ref]
                    kinds.add(w["kind"])
                    if (body.get("caseId") != code or
                        body.get("peerSqlHashBefore") != row["peerSqlHashBefore"] or
                        body.get("peerSqlHashAfter") != row["peerSqlHashAfter"] or
                        body.get("peerReadRows") != 0):
                        raise ValueError("WITNESS_CASE_OR_NO_PEER_READ_MISMATCH")
                    if ref in positives and body.get("observedOutcome") != frozen["expected"]:
                        raise ValueError("ACTUAL_NOT_FROM_INDEPENDENT_WITNESS")
                    if ref in negatives and (body.get("faultId") != row["faultId"] or
                                              body.get("negativeInvariant") != frozen["negativeInvariant"]
                                              or body.get("negativeObserved") is not True):
                        raise ValueError("NEGATIVE_FAULT_NOT_WITNESSED")
                if not required_kinds.issubset(kinds):
                    raise ValueError("MISSING_REQUIRED_INDEPENDENT_WITNESS_KIND")
                cases[code] = "PASS"
            except (KeyError, TypeError, ValueError) as exc:
                code = row.get("caseId", "?") if isinstance(row, dict) else "?"
                if code in cases:
                    cases[code] = "FAIL"
                errors.append(f"CASE_INVALID:{code}:{str(exc)[:90]}")
        for code in CASE_IDS:
            if code not in seen_cases:
                errors.append(f"REQUIRED_CASE_MISSING:{code}")

        probes = manifest.get("runtimeProbes", [])
        if not isinstance(probes, list) or {p.get("id") for p in probes if isinstance(p, dict)} != {"RUNTIME-01", "RUNTIME-02"} or len(probes) != 2:
            errors.append("REAL_HOST_RUNTIME_01_02_MISSING")
        else:
            for probe in probes:
                ref = probe.get("witnessRef")
                if (probe.get("status") != "PASS" or ref not in witness_map or
                    witness_map[ref][0].get("kind") != "RUNTIME" or
                    witness_map[ref][1].get("caseId") != probe.get("id") or
                    witness_map[ref][1].get("observedOutcome") != "PASS"):
                    errors.append(f"RUNTIME_PROBE_UNVERIFIED:{probe.get('id')}")

        recovery = manifest.get("recovery", {})
        try:
            rpo = (parse_utc(recovery["failureUtc"]) -
                   parse_utc(recovery["latestRecoveredCommittedUtc"])).total_seconds()
            rto = recovery["monotonicSafeServiceSeconds"] - recovery["monotonicFailureSeconds"]
            if (type(recovery.get("rpoSeconds")) not in (int, float)
                or type(recovery.get("rtoSeconds")) not in (int, float)
                or not 0 <= rpo <= 900 or not 0 <= rto <= 14400
                or abs(recovery["rpoSeconds"] - rpo) > 0.001
                or abs(recovery["rtoSeconds"] - rto) > 0.001
                or type(recovery.get("monotonicFailureSeconds")) not in (int, float)
                or type(recovery.get("monotonicSafeServiceSeconds")) not in (int, float)
                or recovery["monotonicFailureSeconds"] < 0
                or not all(recovery.get(flag) is True for flag in (
                    "sqlFullRecovery", "lsnChainContinuous", "encryptedOffHostCopy",
                    "independentRestoreKey", "currentExternalRevocationFence",
                    "externalEffectsReconciled", "safeApiAndWorkerReentry",
                    "peerDatabaseUnchanged"))):
                raise ValueError("RPO_RTO_OR_SAFE_RESTORE_NOT_PROVEN")
        except (KeyError, TypeError, ValueError) as exc:
            errors.append(f"PHYSICAL_RECOVERY_UNVERIFIED:{str(exc)[:120]}")

        ci = manifest.get("ciChecks", [])
        if not isinstance(ci, list):
            ci = []
        if len(ci) != len(CI_CHECKS) or {x.get("name") for x in ci if isinstance(x, dict)} != CI_CHECKS:
            errors.append("FINAL_HEAD_REQUIRED_CI_MATRIX_INCOMPLETE")
        else:
            for item in ci:
                if (item.get("sourceHeadSha") != expected_head or
                    item.get("conclusion") != "success" or
                    item.get("failed") != 0 or type(item.get("failed")) is not int or
                    item.get("skippedRequired") != 0 or type(item.get("skippedRequired")) is not int or
                    type(item.get("runAttempt")) is not int or item["runAttempt"] < 1 or
                    not str(item.get("workflowRunUrl", "")).startswith(
                        "https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/") or
                    not HEX64.fullmatch(str(item.get("artifactSha256", "")))):
                    errors.append(f"CI_NOT_FINAL_HEAD_ALL_GREEN:{item.get('name')}")

        for role in ("security", "dba"):
            try:
                path = canonical_path(bundle, f"reviews/{role}.json")
                review = read_object(path)
                key_and_signature(bundle, trusted, role, path, f"reviews/{role}.sig")
                if (review.get("role") != role.upper() or
                    review.get("testRunId") != manifest.get("testRunId") or
                    review.get("sourceHeadSha") != expected_head or
                    review.get("manifestSha256") != sha256(manifest_path.read_bytes()) or
                    review.get("fixtureSha256") != sha256(fixture_bytes) or
                    review.get("reviewerId") == manifest.get("implementerId") or
                    review.get("reviewerId") == manifest.get("observerId") or
                    review.get("disposition") != "APPROVED" or
                    review.get("blockingFindings") != [] or
                    set(review.get("reviewedCases", [])) != set(CASE_IDS) or
                    len(review["reviewedCases"]) != len(CASE_IDS)):
                    raise ValueError("REVIEW_NOT_INDEPENDENT_FINAL_HEAD")
                parse_utc(review.get("reviewedUtc"))
                manifest[f"_verified_{role}_reviewer"] = review["reviewerId"]
            except (OSError, ValueError, TypeError, KeyError) as exc:
                errors.append(f"INDEPENDENT_{role.upper()}_REVIEW_UNVERIFIED:{str(exc)[:120]}")
        require(manifest.get("_verified_security_reviewer") and
                manifest.get("_verified_security_reviewer") != manifest.get("_verified_dba_reviewer"),
                "TWO_DISTINCT_INDEPENDENT_REVIEWERS_REQUIRED")

    # Never expose per-case PASS when the global observer trust, reviewer,
    # final-HEAD CI, physical recovery or security blocker gate is incomplete.
    # Signed/self-reported positive rows cannot outrank external acceptance.
    if errors:
        cases = {code: ("BLOCKED" if status == "PASS" else status)
                 for code, status in cases.items()}

    # Source-only CI cannot supply physical evidence/trust keys or close security debt.
    gate_results = {}
    for gate in GATES:
        relevant = [cases[c] for c in CASE_IDS if f"G-{c[:3]}" == gate]
        gate_results[gate] = ("PASS" if all(x == "PASS" for x in relevant)
                              and not errors else "NOT_VERIFIED")
    accepted = not errors and all(x == "PASS" for x in gate_results.values())
    return {
        "schema": "APP02B9-ORACLE-REPORT-V1",
        "testRunId": manifest.get("testRunId") if manifest else None,
        "sourceHeadSha": expected_head,
        "classification": "PREPROD_FOUNDATION_VERIFIED" if accepted else "NOT_VERIFIED",
        "decision": "PREPROD_FOUNDATION_ONLY" if accepted else "NO_GO",
        "cases": cases,
        "gates": gate_results,
        "passedCases": sum(1 for s in cases.values() if s == "PASS") if not errors else 0,
        "requiredCases": len(CASE_IDS),
        "errors": sorted(set(errors)),
        "actualEv08": "NOT_VERIFIED",
        "productionP0": "NOT_VERIFIED",
        "productionAndPilot": "NOT_AUTHORIZED",
        "app02cde": "NOT_AUTHORIZED",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--trust-roots", type=Path, default=None,
                        help="Out-of-band pinned keys/environment; never commit this file")
    parser.add_argument("--fixture", type=Path,
                        default=Path(__file__).with_name("expected-v1.json"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        report = evaluate(args.bundle, args.fixture, args.expected_head, args.trust_roots)
        # The CLI must never verify stale signed evidence against a different
        # checkout. A caller-provided --expected-head is not authoritative.
        inspected = subprocess.run(
            ["git", "-C", str(Path(__file__).resolve().parents[2]), "rev-parse", "HEAD"],
            capture_output=True, text=True, timeout=10, check=False,
        )
        if inspected.returncode != 0 or inspected.stdout.strip() != args.expected_head:
            report["classification"] = "NOT_VERIFIED"
            report["decision"] = "NO_GO"
            report["passedCases"] = 0
            report["gates"] = {g: "NOT_VERIFIED" for g in GATES}
            report["errors"] = sorted(set(report["errors"] + ["EXACT_CHECKOUT_HEAD_MISMATCH"]))
    except Exception as exc:  # no crash/fail-open on attacker-supplied evidence
        report = {
            "schema": "APP02B9-ORACLE-REPORT-V1", "decision": "NO_GO",
            "classification": "NOT_VERIFIED",
            "gates": {g: "NOT_VERIFIED" for g in GATES},
            "cases": {c: "NOT_RUN" for c in CASE_IDS},
            "errors": [f"VALIDATOR_FAIL_CLOSED:{type(exc).__name__}"],
            "productionAndPilot": "NOT_AUTHORIZED",
        }
    payload = json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload, encoding="utf-8")
    print(f"APP-02B.9: {report['classification']} / {report['decision']} / "
          f"gates={','.join(report['gates'].values())} / "
          f"blockers={len(report['errors'])}")
    return 0 if report["classification"] == "PREPROD_FOUNDATION_VERIFIED" else 2


if __name__ == "__main__":
    sys.exit(main())

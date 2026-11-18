"""Synthetic validator tests only: they do NOT witness a real PREPROD environment."""
import copy
import datetime
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "app02b9"))
import oracle  # noqa: E402

HEAD = "a" * 40
PEER = "b" * 64
RUN = "b9-synthetic-validator-fixture"
STAMP = "2026-11-18T10:00:00Z"
FIXTURE = REPO / "tools" / "app02b9" / "expected-v1.json"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class OracleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="app02b9-oracle-source-test-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.bundle = self.root / "bundle"
        self.bundle.mkdir()
        self.private = {}
        self.trust = {
            "schema": "APP02B9-TRUST-V1",
            "approvedSourceHeadSha": HEAD,
            "expectedFixtureSha256": digest(FIXTURE),
            "approvedEnvironmentId": "SYNTHETIC_VALIDATOR_UNIT_TEST_ONLY",
            "approvedHostSha256": "c" * 64,
            "tenantDatabaseIds": {"A": "mock-db-id-a", "B": "mock-db-id-b"},
            "keys": {},
        }
        for role in ("fixture", "observer", "security", "dba"):
            priv = self.root / f"{role}.key"
            pub = self.root / f"{role}.pub"
            subprocess.run(
                ["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout",
                 "-out", str(priv)],
                check=True, capture_output=True,
            )
            subprocess.run(
                ["openssl", "pkey", "-in", str(priv), "-pubout", "-out", str(pub)],
                check=True, capture_output=True,
            )
            self.private[role] = priv
            self.trust["keys"][role] = {
                "publicKeyPath": str(pub), "publicKeySha256": digest(pub)
            }
        self.trust_file = self.root / "approved-trust.json"
        self.persist_trust()
        self.sign("fixture", FIXTURE, self.bundle / "expected.sig")

        self.manifest = {
            "schema": oracle.SCHEMA,
            "evidenceClass": "PHYSICAL_PREPROD_WITNESSED",
            "environmentMode": "PREPROD_NONPRODUCTION",
            "environmentId": self.trust["approvedEnvironmentId"],
            "hostSha256": self.trust["approvedHostSha256"],
            "testRunId": RUN, "sourceHeadSha": HEAD,
            "observerId": "synthetic-independent-observer",
            "implementerId": "synthetic-implementer",
            "fixtureSha256": digest(FIXTURE),
            "apiImageDigest": "sha256:" + "d" * 64,
            "workerImageDigest": "sha256:" + "e" * 64,
            "schemaMigrationSetSha256": "f" * 64,
            "tenantDatabases": {
                "A": {"databaseId": "mock-db-id-a", "physicalName": "mock-sql-a",
                      "registryBindingVersion": "v1"},
                "B": {"databaseId": "mock-db-id-b", "physicalName": "mock-sql-b",
                      "registryBindingVersion": "v2"},
            },
            "openBlockingFindings": [],
            "witnesses": [],
            "cases": [],
            "runtimeProbes": [],
            "recovery": {
                "failureUtc": STAMP,
                "latestRecoveredCommittedUtc": "2026-11-18T09:50:00Z",
                "monotonicFailureSeconds": 1000,
                "monotonicSafeServiceSeconds": 2000,
                "rpoSeconds": 600,
                "rtoSeconds": 1000,
                "sqlFullRecovery": True,
                "lsnChainContinuous": True,
                "encryptedOffHostCopy": True,
                "independentRestoreKey": True,
                "currentExternalRevocationFence": True,
                "externalEffectsReconciled": True,
                "safeApiAndWorkerReentry": True,
                "peerDatabaseUnchanged": True,
                "witnessRefs": [],
            },
            "ciChecks": [{
                "name": name, "sourceHeadSha": HEAD, "conclusion": "success",
                "failed": 0, "skippedRequired": 0, "runAttempt": 1,
                "workflowRunUrl":
                    "https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/1234",
                "artifactSha256": "1" * 64,
            } for name in sorted(oracle.CI_CHECKS)],
        }
        frozen = json.loads(FIXTURE.read_text(encoding="utf-8"))["cases"]
        for code in oracle.CASE_IDS:
            f = frozen[code]
            refs = []
            for index, kind in enumerate(f["requiredKinds"]):
                ref = f"{code}-positive-{index}"
                self.add_witness(ref, kind, code, f["expected"], f["negativeInvariant"],
                                 f"{code}-fault", False)
                refs.append(ref)
            neg = f"{code}-negative"
            self.add_witness(neg, f["requiredKinds"][0], code, f["expected"],
                             f["negativeInvariant"], f"{code}-fault", True)
            if code == "B03-02":
                self.manifest["recovery"]["witnessRefs"] = list(refs)
            self.manifest["cases"].append({
                "caseId": code, "gateId": f"G-{code[:3]}",
                "testRunId": RUN, "sourceHeadSha": HEAD,
                "status": "PASS", "tenantScope": "A_AND_B_PHYSICAL",
                "observerId": self.manifest["observerId"],
                "expected": f["expected"], "actual": f["expected"],
                "difference": 0, "match": True, "negativeVerified": True,
                "negativeInvariant": f["negativeInvariant"],
                "faultId": f"{code}-fault", "executionUtc": STAMP,
                "peerSqlHashBefore": PEER, "peerSqlHashAfter": PEER,
                "positiveRefs": refs, "negativeRefs": [neg],
            })
        for probe in ("RUNTIME-01", "RUNTIME-02"):
            ref = probe + "-witness"
            self.add_witness(ref, "RUNTIME", probe, "PASS", "NO_EXTERNAL_PORTS",
                             probe, False)
            self.manifest["runtimeProbes"].append({
                "id": probe, "status": "PASS", "witnessRef": ref
            })
        self.seal()

    def persist_trust(self):
        self.trust_file.write_text(json.dumps(self.trust), encoding="utf-8")

    def sign(self, role, source, signature):
        signature.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", str(self.private[role]),
             "-out", str(signature), str(source)],
            check=True, capture_output=True,
        )

    def add_witness(self, ref, kind, code, observed, invariant, fault, negative):
        target = self.bundle / "witness" / (ref + ".json")
        target.parent.mkdir(parents=True, exist_ok=True)
        raw = self.bundle / "raw" / (ref + ".txt")
        raw.parent.mkdir(parents=True, exist_ok=True)
        raw.write_text("SYNTHETIC UNIT TEST ONLY: " + code + "/" + kind + "/" + ref,
                       encoding="utf-8")
        witness = {
            "evidenceClass": "PHYSICAL_PREPROD_INDEPENDENT",
            "testRunId": RUN, "sourceHeadSha": HEAD,
            "environmentId": self.trust["approvedEnvironmentId"],
            "observerId": self.manifest["observerId"], "independentlyMeasured": True,
            "kind": kind, "caseId": code, "observedOutcome": observed,
            "negativeInvariant": invariant, "negativeObserved": negative,
            "faultId": fault, "peerReadRows": 0,
            "peerSqlHashBefore": PEER, "peerSqlHashAfter": PEER,
            "rawArtifactPath": str(raw.relative_to(self.bundle)).replace("\\", "/"),
            "rawArtifactSha256": digest(raw),
        }
        if code == "B03-02" and kind == "RESTORE":
            recovery = self.manifest["recovery"]
            witness["recoveryMeasurements"] = {
                key: recovery[key] for key in (
                    "failureUtc", "latestRecoveredCommittedUtc",
                    "monotonicFailureSeconds", "monotonicSafeServiceSeconds",
                )
            }
        target.write_text(json.dumps(witness), encoding="utf-8")
        self.manifest["witnesses"].append({
            "ref": ref, "path": str(target.relative_to(self.bundle)).replace("\\", "/"),
            "sha256": digest(target), "kind": kind,
        })

    def seal(self):
        p = self.bundle / "manifest.json"
        p.write_text(json.dumps(self.manifest), encoding="utf-8")
        self.sign("observer", p, self.bundle / "manifest.sig")
        for role in ("security", "dba"):
            d = self.bundle / "reviews" / f"{role}.json"
            d.parent.mkdir(parents=True, exist_ok=True)
            review = {
                "role": role.upper(), "reviewerId": "synthetic-" + role + "-reviewer",
                "testRunId": RUN, "sourceHeadSha": HEAD,
                "manifestSha256": digest(p), "fixtureSha256": digest(FIXTURE),
                "disposition": "APPROVED", "blockingFindings": [],
                "reviewedCases": list(oracle.CASE_IDS), "reviewedUtc": STAMP,
            }
            d.write_text(json.dumps(review), encoding="utf-8")
            self.sign(role, d, self.bundle / "reviews" / f"{role}.sig")

    def assess(self, trust=True):
        return oracle.evaluate(
            self.bundle, FIXTURE, HEAD, self.trust_file if trust else None
        )

    def test_signed_synthetic_control_case_only(self):
        # This is an isolated UNIT TEST, not real PREPROD acceptance.
        report = self.assess()
        self.assertEqual(report["errors"], [], report["errors"])
        self.assertEqual(report["classification"], "PREPROD_FOUNDATION_VERIFIED")
        self.assertEqual(len(report["cases"]), 30)
        self.assertTrue(all(x == "PASS" for x in report["gates"].values()))

    def test_no_trust_or_physical_bundle_is_no_go(self):
        r = self.assess(trust=False)
        self.assertEqual(r["classification"], "NOT_VERIFIED")
        self.assertEqual(r["decision"], "NO_GO")
        self.assertEqual(r["passedCases"], 0)
        self.assertNotIn("PASS", set(r["cases"].values()))
        empty = self.root / "empty"
        empty.mkdir()
        r = oracle.evaluate(empty, FIXTURE, HEAD, None)
        self.assertEqual(set(r["cases"].values()), {"NOT_RUN"})
        self.assertEqual(set(r["gates"].values()), {"NOT_VERIFIED"})

    def test_reject_wrong_reviewer_or_missing_signature(self):
        (self.bundle / "reviews" / "dba.sig").unlink()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        self.seal()
        self.manifest["implementerId"] = "synthetic-security-reviewer"
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")

    def test_cli_rejects_signed_but_superseded_checkout_head(self):
        # The signed synthetic bundle is internally consistent with 'aaaa...';
        # production CLI must compare it to the ACTUAL Git checkout HEAD.
        output = self.root / "stale-head-report.json"
        cmd = [
            sys.executable, str(REPO / "tools" / "app02b9" / "oracle.py"),
            "--bundle", str(self.bundle), "--expected-head", HEAD,
            "--trust-roots", str(self.trust_file),
            "--fixture", str(FIXTURE), "--output", str(output),
        ]
        process = subprocess.run(cmd, capture_output=True, text=True)
        self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
        report = json.loads(output.read_text(encoding="utf-8"))
        self.assertIn("EXACT_CHECKOUT_HEAD_MISMATCH", report["errors"])
        self.assertEqual(report["decision"], "NO_GO")

    def test_reject_missing_witness_and_traversal(self):
        target = self.bundle / self.manifest["witnesses"][0]["path"]
        target.unlink()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        self.manifest["witnesses"][0]["path"] = "../outside.json"
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")

    def test_reject_raw_artifact_or_ambiguous_json(self):
        raw = self.bundle / "raw" / (self.manifest["witnesses"][0]["ref"] + ".txt")
        raw.unlink()
        self.assertEqual(self.assess()["decision"], "NO_GO")
        self.assertIn("WITNESS_INVALID:", " ".join(self.assess()["errors"]))
        # Duplicate JSON object keys are forbidden even when both values agree.
        self.seal()
        path = self.bundle / "witness" / (self.manifest["witnesses"][1]["ref"] + ".json")
        body = path.read_text(encoding="utf-8")
        path.write_text(body[:-1] + ', "peerReadRows": 0}', encoding="utf-8")
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        with self.assertRaises(ValueError):
            oracle.strict_json_bytes(b'{"value": NaN}')

    def test_reject_missing_case_and_bad_recovery(self):
        self.manifest["cases"].pop()
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        self.manifest["recovery"]["rtoSeconds"] = 15000
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")

    def test_reject_missing_manifest_actor_identities_and_swapped_tenant_keys(self):
        self.manifest.pop("observerId")
        for row in self.manifest["cases"]:
            row.pop("observerId")
        for witness in self.manifest["witnesses"]:
            path = self.bundle / witness["path"]
            record = json.loads(path.read_text())
            record.pop("observerId")
            path.write_text(json.dumps(record), encoding="utf-8")
            witness["sha256"] = digest(path)
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        self.assertIn("DISTINCT_NONEMPTY_OBSERVER_AND_IMPLEMENTER_REQUIRED",
                      self.assess()["errors"])
        self.manifest["observerId"] = self.manifest["implementerId"]
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")

        self.trust["tenantDatabaseIds"] = {"X": "mock-db-id-a", "Y": "mock-db-id-b"}
        self.persist_trust()
        self.assertIn("TRUST_EXACT_TWO_NAMED_TENANT_DATABASES_REQUIRED",
                      self.assess()["errors"])

    def test_reject_signed_alternate_expected_fixture(self):
        # Even a fixture re-signed by the synthetic fixture key and re-pinned
        # out-of-band must not silently replace the reviewed source contract.
        alternative = self.root / "wrong-expected.json"
        fixture = json.loads(FIXTURE.read_text())
        fixture["cases"]["B01-01"]["expected"] = "SYNTHETIC_DIFFERENT_VALUE"
        alternative.write_text(json.dumps(fixture), encoding="utf-8")
        self.trust["expectedFixtureSha256"] = digest(alternative)
        self.persist_trust()
        self.sign("fixture", alternative, self.bundle / "expected.sig")
        report = oracle.evaluate(self.bundle, alternative, HEAD, self.trust_file)
        self.assertEqual(report["decision"], "NO_GO")
        self.assertIn("FIXTURE_DIFFERS_FROM_REVIEWED_SOURCE", report["errors"])

    def test_reject_unbound_recovery_witnesses_and_clock_forgery(self):
        self.manifest["recovery"].pop("witnessRefs")
        self.seal()
        self.assertIn("PHYSICAL_RECOVERY_UNVERIFIED", " ".join(self.assess()["errors"]))
        self.manifest["recovery"]["witnessRefs"] = [
            w["ref"] for w in self.manifest["witnesses"]
            if w["ref"].startswith("B03-02-positive-")
        ]
        restore_ref = next(w for w in self.manifest["witnesses"]
                           if w["ref"].startswith("B03-02-positive-0"))
        path = self.bundle / restore_ref["path"]
        body = json.loads(path.read_text())
        body["recoveryMeasurements"]["monotonicSafeServiceSeconds"] += 1
        path.write_text(json.dumps(body), encoding="utf-8")
        restore_ref["sha256"] = digest(path)
        self.seal()
        self.assertIn("RECOVERY_CLOCK_ANCHORS_UNWITNESSED",
                      " ".join(self.assess()["errors"]))

    def test_reject_reused_localdb_label_and_full_audit_failure(self):
        self.manifest["evidenceClass"] = "GITHUB_HOSTED_LOCALDB_ONLY"
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")
        self.manifest["evidenceClass"] = "PHYSICAL_PREPROD_WITNESSED"
        next(x for x in self.manifest["ciChecks"] if x["name"] == "APP00A_FULL")["conclusion"] = "failure"
        self.seal()
        self.assertEqual(self.assess()["classification"], "NOT_VERIFIED")

    def test_signed_mutation_matrix_fails_closed(self):
        # Each mutation gets a new signed manifest/reviews to prove that the
        # validator itself, not signature tamper alone, rejects the difference.
        matrix = {
            "EXPECTED": lambda m: m["cases"][0].update(expected="MUTATED"),
            "ACTUAL": lambda m: m["cases"][0].update(actual="MUTATED"),
            "SIGNED_DIFFERENCE": lambda m: m["cases"][0].update(difference=-1),
            "MATCH_FLAG": lambda m: m["cases"][0].update(match=False),
            "PEER_HASH": lambda m: m["cases"][0].update(peerSqlHashAfter="a" * 64),
            "SOURCE_HEAD": lambda m: m.update(sourceHeadSha="b" * 40),
            "MISSING_PHYSICAL": lambda m: m["witnesses"].pop(0),
            "API_ONLY_FORGED_SUCCESS": lambda m: m["cases"][0].update(
                positiveRefs=["RUNTIME-01-witness"]),
            "SKIPPED_REQUIRED_CI": lambda m: m["ciChecks"][0].update(skippedRequired=1),
            "STALE_REVIEW": lambda m: m.update(openBlockingFindings=["#53", "#59"]),
        }
        original = copy.deepcopy(self.manifest)
        results = {}
        for name, mutation in matrix.items():
            self.manifest = copy.deepcopy(original)
            mutation(self.manifest)
            self.seal()
            r = self.assess()
            results[name] = {
                "Rejected": r["classification"] == "NOT_VERIFIED"
                and r["decision"] == "NO_GO",
                "Errors": r["errors"][:3],
            }
            with self.subTest(mutation=name):
                self.assertTrue(results[name]["Rejected"], r)
        expected_bad_pin = copy.deepcopy(self.trust)
        expected_bad_pin["expectedFixtureSha256"] = "0" * 64
        self.trust = expected_bad_pin
        self.persist_trust()
        results["FROZEN_FIXTURE_SHA"] = {
            "Rejected": self.assess()["decision"] == "NO_GO",
            "Errors": self.assess()["errors"][:3],
        }
        self.assertTrue(results["FROZEN_FIXTURE_SHA"]["Rejected"])
        report = {
            "EvidenceClass": "SYNTHETIC_VALIDATOR_TEST_ONLY",
            "RealPreprod": "NOT_VERIFIED", "Gates": "NOT_RUN",
            "SourceHeadSha": os.getenv("APP02B9_SOURCE_HEAD", "LOCAL_SOURCE_TEST"),
            "Mutations": results,
        }
        out = REPO / "TestResults" / "APP-02B9-MUTATIONS.json"
        out.parent.mkdir(exist_ok=True)
        out.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()

"""Publish and validate the prepared coverage index and append-only attempts."""

from pathlib import Path
import re
import tempfile
from uuid import uuid4

from .catalog import CASE_IDS, GATE_IDS, RUNTIME_PROBES, coverage_catalog, parse_catalog
from .context import (
    LabRunContext, RunInputs, approved_root, utc_now, validate_admission_root, validate_manifest,
    validate_run_id, validate_source,
)
from .contract import (
    LabError, canonical_json, child, digest, guarded_directory, publish, read_bytes,
    require, safe_path, strict_json, sync_directory,
)
from .schema import LabValidationReport, ValidationIssue, validate_document

LABELS = {
    "schema": "APP02B-LAB-EVIDENCE-V1",
    "evidenceClass": "LOCAL_OPERATOR_LAB",
    "humanReview": "SOLO_OWNER_SELF_ATTESTED",
    "originalGateStatus": "NOT_VERIFIED",
}

_BASELINE_FILES = {
    "run.json", "environment.json", "protected/host-fingerprint.bin",
    "protected/lab-admission.json", "enrollment-references.json",
    "expected-v1.json", "report.json", "report.md",
}
_DIRECTORY_NAMES = ("attempts", "runtime", "ci", "hashes", "protected")
_ALL_IDS = (*CASE_IDS, *RUNTIME_PROBES)
_JOURNAL_NAME = re.compile(r"[0-9]{8}\.json")


def selection_ids(selection: str) -> tuple[str, ...]:
    if selection == "all":
        return _ALL_IDS
    prefix, separator, value = selection.partition(":")
    if separator:
        if prefix == "case" and value in CASE_IDS:
            return (value,)
        if prefix == "gate" and value in GATE_IDS:
            return tuple(case for case in CASE_IDS if case.startswith(value[2:] + "-"))
        if prefix == "runtime" and value in RUNTIME_PROBES:
            return (value,)
    raise LabError("INVALID_SELECTION")


def _row(definition, attempt_id=None, selected=False) -> dict:
    blocked = definition.caseId == "B05-02"
    return {
        "caseId": definition.caseId, "expected": definition.expected,
        "negativeInvariant": definition.negativeInvariant,
        "requiredKinds": list(definition.requiredKinds),
        "technicalResult": "BLOCKED" if blocked else "NOT_RUN",
        "acceptanceStatus": "NOT_VERIFIED",
        "missingRequirements": (
            ["INDEPENDENT_HUMAN_REVIEW"] if blocked
            else ["DRIVER_UNAVAILABLE"] if selected else ["CASE_NOT_EXECUTED"]
        ),
        "tenantScope": "UNASSIGNED", "peerScope": "UNASSIGNED", "targetRefs": [], "actualObservations": [],
        "diff": None, "positiveRefs": [], "negativeRefs": [], "peerBefore": None,
        "peerAfter": None, "faultId": None, "observedUtc": None, "attemptId": attempt_id,
    }


def _identity(context: LabRunContext) -> dict:
    return {
        "runId": context.runId, "sourceHeadSha": context.sourceHeadSha,
        "environmentId": context.environmentId,
    }


def _baseline(context, catalog) -> dict:
    return {
        **LABELS, **_identity(context), "caseCount": 30, "runtimeProbeCount": 2,
        "cases": [_row(catalog[key]) for key in CASE_IDS],
        "runtimeProbes": [_row(catalog[key]) for key in RUNTIME_PROBES],
    }


def _markdown(report: dict) -> bytes:
    lines = [
        "# LAB-E05 prepared coverage", "",
        f"Run {report['runId']}", f"Source {report['sourceHeadSha']}", "",
        "Original acceptance gates remain NOT_VERIFIED.", "",
        "| Coverage | Technical result |", "| --- | --- |",
    ]
    for row in report["cases"] + report["runtimeProbes"]:
        lines.append(f"| {row['caseId']} | {row['technicalResult']} |")
    return ("\n".join(lines) + "\n").encode("ascii")


def _enrollment(config: dict) -> dict:
    return {
        "schema": "APP02B-LAB-ENROLLMENT-REFERENCES-V1",
        "environmentId": config["environmentId"],
        "authorizationRecord": config["authorizationRecord"],
        "secretReferences": config["secretReferences"],
    }


def prepare_run(evidence_root: Path, inputs: RunInputs) -> Path:
    root = approved_root(evidence_root)
    with guarded_directory(root):
        return _prepare_run(root, inputs)


def _prepare_run(root: Path, inputs: RunInputs) -> Path:
    context = inputs.context
    validate_document(context.document(), "run-context")
    validate_run_id(context.runId)
    validate_source(context.sourceHeadSha)
    catalog = coverage_catalog(parse_catalog(inputs.fixture))
    report = _baseline(context, catalog)
    manifest = strict_json(inputs.manifest)
    admission = strict_json(inputs.admission)
    validate_manifest(manifest, admission, inputs.host)
    validate_admission_root(admission, root)
    require(inputs.manifest == canonical_json(manifest)
            and inputs.admission == canonical_json(admission), "NONCANONICAL_RUN_INPUT")
    require(_inputs_match(context, inputs), "PREPARE_PROVENANCE_MISMATCH")
    directory = child(root, context.runId, missing=True)
    require(not directory.exists(), "RUN_ALREADY_EXISTS")
    # This per-run reservation serializes directory publication on POSIX too.
    reservation = safe_path(root / (".reserve-" + context.runId), missing=True)
    try:
        reservation.mkdir(mode=0o700)
    except FileExistsError:
        raise LabError("RUN_PREPARATION_RESERVED") from None
    try:
        require(not directory.exists(), "RUN_ALREADY_EXISTS")
        staging = Path(tempfile.mkdtemp(prefix=".lab-preparing-", dir=root))
        for name in _DIRECTORY_NAMES:
            (staging / name).mkdir(mode=0o700)
        contents = {
            "run.json": canonical_json({"state": "PREPARED", "context": context.document()}),
            "environment.json": inputs.manifest,
            "protected/host-fingerprint.bin": inputs.host,
            "protected/lab-admission.json": inputs.admission,
            "enrollment-references.json": canonical_json(_enrollment(manifest)),
            "expected-v1.json": inputs.fixture,
            "report.json": canonical_json(report),
            "report.md": _markdown(report),
        }
        for key, data in contents.items():
            publish(child(staging, key, missing=True), data)
        publish(staging / "hashes" / "baseline.json",
                canonical_json({key: digest(value) for key, value in contents.items()}))
        sync_directory(staging)
        validate_source(context.sourceHeadSha)
        staging.rename(directory)
        sync_directory(root)
    finally:
        reservation.rmdir()
    return directory


def _inputs_match(context: LabRunContext, inputs: RunInputs) -> bool:
    proposed = inputs.context
    return (
        _identity(context) == _identity(proposed)
        and context.hostSha256 == proposed.hostSha256 == digest(inputs.host)
        and context.fixtureSha256 == proposed.fixtureSha256 == digest(inputs.fixture)
        and context.configurationSha256 == proposed.configurationSha256 == digest(inputs.manifest)
        and context.environmentId == strict_json(inputs.manifest)["environmentId"]
        and context.mode == proposed.mode == "LOCAL_OPERATOR_LAB"
    )


def _directory(root: Path, name: str) -> Path:
    directory = child(root, name)
    require(directory.is_dir(), "NOT_A_DIRECTORY", name)
    return directory


def _check_hash(root: Path, reference: dict) -> bytes:
    data = read_bytes(child(root, reference["relativePath"]))
    require(digest(data) == reference["sha256"], "ARTIFACT_HASH_MISMATCH")
    return data


def _read_baseline(root: Path):
    baseline_bytes = read_bytes(child(root, "hashes/baseline.json"))
    hashes = strict_json(baseline_bytes)
    require(isinstance(hashes, dict) and set(hashes) == _BASELINE_FILES,
            "BASELINE_CHECKSUM_INVENTORY")
    data = {
        key: _check_hash(root, {"relativePath": key, "sha256": value})
        for key, value in hashes.items()
    }
    run = strict_json(data["run.json"])
    validate_document(run, "run-context", "run")
    context = LabRunContext(**{key: value for key, value in run["context"].items()
                               if key != "schema"})
    validate_run_id(context.runId)
    require(context.runId == root.name, "RUN_DIRECTORY_IDENTITY")
    manifest = strict_json(data["environment.json"])
    admission = strict_json(data["protected/lab-admission.json"])
    validate_manifest(manifest, admission, data["protected/host-fingerprint.bin"])
    catalog = coverage_catalog(parse_catalog(data["expected-v1.json"]))
    inputs = RunInputs(context, data["expected-v1.json"], data["environment.json"],
                       data["protected/host-fingerprint.bin"], data["protected/lab-admission.json"])
    require(_inputs_match(context, inputs) and inputs.manifest == canonical_json(manifest)
            and inputs.admission == canonical_json(admission), "FROZEN_PROVENANCE_MISMATCH")
    require(data["enrollment-references.json"] == canonical_json(_enrollment(manifest)),
            "ENROLLMENT_REFERENCE_MISMATCH")
    report = strict_json(data["report.json"])
    validate_document(report, "evidence")
    require(data["report.json"] == canonical_json(_baseline(context, catalog)),
            "PREPARED_COVERAGE_MISMATCH")
    require(data["report.md"] == _markdown(report), "PREPARED_MARKDOWN_MISMATCH")
    require(set(path.name for path in root.iterdir()) ==
            ({name for name in _BASELINE_FILES if "/" not in name} | set(_DIRECTORY_NAMES)),
            "UNEXPECTED_RUN_ENTRIES")
    require({path.name for path in _directory(root, "protected").iterdir()} ==
            {"host-fingerprint.bin", "lab-admission.json"}, "PROTECTED_INVENTORY")
    for name in ("ci", "runtime"):
        require(not any(_directory(root, name).iterdir()), "UNINDEXED_ARTIFACTS", name)
    return context, catalog, manifest, digest(baseline_bytes)


def _attempt_file(attempt_id: str) -> str:
    return f"attempts/{attempt_id}/attempt.json"


def _attempt_files(root: Path, folder: Path) -> set[str]:
    files = set()
    for entry in folder.iterdir():
        entry = child(root, entry.relative_to(root).as_posix())
        if entry.is_dir():
            files.update(_attempt_files(root, entry))
        else:
            files.add(entry.relative_to(root).as_posix())
    return files


def _witnesses(root, context, attempt, row) -> set[str]:
    prefix = f"attempts/{attempt['attemptId']}/cases/{row['caseId']}"
    artifacts = set()
    kinds = set()
    for phase, refs in (("POSITIVE", row["positiveRefs"]), ("NEGATIVE", row["negativeRefs"])):
        for ref in refs:
            relative = ref["relativePath"]
            require(relative.startswith(prefix + "/witnesses/") and relative.endswith(".json"),
                    "WITNESS_PATH_OUTSIDE_CASE")
            witness = strict_json(_check_hash(root, ref))
            validate_document(witness, "evidence", "witness")
            expected = row["expected"] if phase == "POSITIVE" else row["negativeInvariant"]
            binding = {
                **_identity(context), "configurationSha256": context.configurationSha256,
                "attemptId": attempt["attemptId"], "caseId": row["caseId"], "phase": phase,
                "faultId": row["faultId"], "targetIdentity": row["targetRefs"],
                "observedSymbol": expected,
            }
            if row["technicalResult"] == "FAIL":
                binding.pop("observedSymbol")
            require(all(witness[key] == value for key, value in binding.items()),
                    "WITNESS_PROVENANCE_MISMATCH")
            require(attempt["startedUtc"] <= witness["observedUtc"] <= row["observedUtc"],
                    "WITNESS_TIMESTAMP_MISMATCH")
            raw_ref = witness["rawArtifact"]
            raw = raw_ref["relativePath"]
            require(raw.startswith(prefix + "/raw/"), "RAW_PATH_OUTSIDE_CASE")
            require(relative not in artifacts and raw not in artifacts,
                    "DUPLICATE_EVIDENCE_REFERENCE")
            require(bool(_check_hash(root, raw_ref).strip()), "EMPTY_RAW_ARTIFACT")
            artifacts.update((relative, raw))
            if phase == "POSITIVE":
                kinds.add(witness["kind"])
    if row["technicalResult"] == "PASS":
        require(set(row["requiredKinds"]) <= kinds, "REQUIRED_WITNESS_KIND_MISSING")
        # Bindings and hashes establish integrity, while reviewed collectors
        # will own interpretation of the raw measurements in later increments.
        raise LabError("MEASUREMENT_ASSESSOR_UNAVAILABLE")
    return artifacts


def _validate_attempt(root, context, catalog, manifest, entry) -> dict:
    file = _attempt_file(entry["attemptId"])
    attempt = strict_json(_check_hash(root, {"relativePath": file, "sha256": entry["attemptSha256"]}))
    validate_document(attempt, "evidence", "attempt")
    binding = {**_identity(context), "configurationSha256": context.configurationSha256,
               "attemptId": entry["attemptId"]}
    require(all(attempt[key] == value for key, value in binding.items())
            and attempt["startedUtc"] >= context.startedUtc, "ATTEMPT_PROVENANCE_MISMATCH")
    rows = {row["caseId"]: row for row in attempt["results"]}
    require(set(rows) == set(catalog), "ATTEMPT_COVERAGE_MISMATCH")
    files = {file}
    selected = set(attempt["selection"])
    for key, definition in catalog.items():
        row = rows[key]
        expected = _row(definition, attempt["attemptId"], key in selected)
        require(all(row[field] == expected[field] for field in
                    ("expected", "negativeInvariant", "requiredKinds", "attemptId")),
                "CASE_CONTRACT_MISMATCH", key)
        if key == "B05-02" or key not in selected or row["technicalResult"] in {"BLOCKED", "NOT_RUN"}:
            require(row == expected, "UNAUTHORIZED_OR_UNEXECUTED_CASE_RESULT", key)
            continue
        require(row["observedUtc"] is not None and row["observedUtc"] >= attempt["startedUtc"]
                and row["faultId"] is not None, "OBSERVATION_TIMELINE_MISSING", key)
        scope = row["tenantScope"]
        scopes = {"A": ("A",), "B": ("B",), "A_AND_B": ("A", "B"), "RUNTIME": ()}
        require(scope in scopes and (scope == "RUNTIME") == (key in RUNTIME_PROBES),
                "INVALID_TENANT_SCOPE", key)
        require(row["targetRefs"] == [manifest["targets"][tenant]["targetRef"]
                                      for tenant in scopes[scope]], "WRONG_TARGET_PROVENANCE", key)
        peer_scopes = {"A": "B", "B": "A", "A_AND_B": "A_AND_B", "RUNTIME": "NONE"}
        require(row["peerScope"] == peer_scopes[scope], "INVALID_PEER_SCOPE", key)
        if row["technicalResult"] == "PASS":
            require(row["expected"] in row["actualObservations"], "EXPECTED_OBSERVATION_MISSING", key)
            if scope != "RUNTIME":
                require(row["peerBefore"] is not None and row["peerBefore"] == row["peerAfter"],
                        "PEER_INVARIANT_UNVERIFIED", key)
        files.update(_witnesses(root, context, attempt, row))
    require(files == _attempt_files(root, _directory(root, f"attempts/{entry['attemptId']}")),
            "ATTEMPT_ARTIFACT_INVENTORY")
    return attempt


def _history(root, context, catalog, manifest):
    folder = _directory(root, "hashes")
    names = sorted(path.name for path in folder.iterdir() if path.name != "baseline.json")
    previous = None
    seen = set()
    timestamp = context.startedUtc
    for sequence, name in enumerate(names, 1):
        require(_JOURNAL_NAME.fullmatch(name) is not None and name == f"{sequence:08d}.json",
                "ATTEMPT_JOURNAL_SEQUENCE")
        raw = read_bytes(child(root, "hashes/" + name))
        entry = strict_json(raw)
        validate_document(entry, "evidence", "journal")
        require(entry["sequence"] == sequence and entry["previousSha256"] == previous
                and entry["attemptId"] not in seen, "ATTEMPT_JOURNAL_CHAIN")
        attempt = _validate_attempt(root, context, catalog, manifest, entry)
        require(attempt["startedUtc"] >= timestamp, "ATTEMPT_TIMESTAMP_ORDER")
        timestamp = attempt["startedUtc"]
        previous = digest(raw)
        seen.add(entry["attemptId"])
        yield attempt, previous
    require({path.name for path in _directory(root, "attempts").iterdir()} == seen,
            "ATTEMPT_HISTORY_INVENTORY")


def validate_bundle(root: Path, *, checkpoint: dict | None = None) -> LabValidationReport:
    context = None
    attempts = []
    coverage = {}
    try:
        root = safe_path(root)
        approved_root(root.parent)
        context, catalog, manifest, baseline_sha = _read_baseline(root)
        coverage = {key: _row(catalog[key])["technicalResult"] for key in catalog}
        head = None
        for attempt, head in _history(root, context, catalog, manifest):
            attempts.append(attempt)
            for row in attempt["results"]:
                if row["caseId"] in attempt["selection"]:
                    coverage[row["caseId"]] = row["technicalResult"]
        current = _checkpoint(context, baseline_sha, len(attempts), head, checkpoint)
    except LabError as error:
        return LabValidationReport(context, coverage, tuple(attempts),
                                   (ValidationIssue(error.code, error.location),))
    except (OSError, RecursionError):
        return LabValidationReport(context, coverage, tuple(attempts),
                                   (ValidationIssue("BUNDLE_IO_FAILURE", ""),))
    return LabValidationReport(context, coverage, tuple(attempts), (), current)


def _checkpoint(context, baseline_sha, count, head, expected):
    current = {"runId": context.runId, "baselineSha256": baseline_sha,
               "attemptCount": count, "journalHeadSha256": head}
    if expected is not None:
        validate_document(expected, "evidence", "checkpoint")
        require(current == expected, "EXTERNAL_CHECKPOINT_MISMATCH")
    return current


def append_attempt(root: Path, inputs: RunInputs, selected: tuple[str, ...],
                   *, checkpoint: dict | None = None) -> dict:
    root = safe_path(root)
    evidence_root = approved_root(root.parent)
    with guarded_directory(root):
        return _append_attempt(root, evidence_root, inputs, selected, checkpoint)


def _append_attempt(root: Path, evidence_root: Path, inputs: RunInputs,
                    selected: tuple[str, ...], checkpoint: dict | None) -> dict:
    validate_source(inputs.context.sourceHeadSha)
    context, catalog, manifest, baseline_sha = _read_baseline(root)
    require(_inputs_match(context, inputs), "RESUME_PROVENANCE_MISMATCH")
    admission = strict_json(inputs.admission)
    validate_manifest(manifest, admission, inputs.host)
    validate_admission_root(admission, evidence_root)
    require(inputs.admission == read_bytes(child(root, "protected/lab-admission.json")),
            "RESUME_ADMISSION_MISMATCH")
    require(bool(selected) and len(set(selected)) == len(selected) and set(selected) <= catalog.keys(),
            "INVALID_SELECTION")
    previous = None
    sequence = 1
    last_started = context.startedUtc
    for existing, previous in _history(root, context, catalog, manifest):
        last_started = existing["startedUtc"]
        sequence += 1
    _checkpoint(context, baseline_sha, sequence - 1, previous, checkpoint)
    started = utc_now()
    require(started >= last_started, "ATTEMPT_CLOCK_REGRESSION")
    attempt_id = "attempt-" + uuid4().hex
    directory = child(root, f"attempts/{attempt_id}", missing=True)
    require(not directory.exists(), "ATTEMPT_ID_ALREADY_EXISTS")
    attempt = {
        **LABELS, **_identity(context), "configurationSha256": context.configurationSha256,
        "attemptId": attempt_id, "startedUtc": started, "selection": list(selected),
        "results": [_row(definition, attempt_id, definition.caseId in selected)
                    for definition in catalog.values()],
    }
    data = canonical_json(attempt)
    entry = {"sequence": sequence, "attemptId": attempt_id,
             "attemptSha256": digest(data), "previousSha256": previous}
    validate_source(context.sourceHeadSha)
    journal = child(root, f"hashes/{sequence:08d}.json", missing=True)
    try:
        publish(journal, canonical_json(entry))
    except FileExistsError:
        raise LabError("ATTEMPT_SEQUENCE_ALREADY_RESERVED") from None
    # Reserving first makes an interrupted write visible, preserving the
    # prepared coverage and every previously sealed attempt.
    directory = child(root, f"attempts/{attempt_id}", missing=True)
    directory.mkdir(mode=0o700)
    sync_directory(directory.parent)
    publish(directory / "attempt.json", data)
    return attempt

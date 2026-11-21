import importlib
import os
import py_compile
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from uuid import UUID

from support import DisposableLab, RUN_ID, get, put
from tools.app02b_lab.bundle import LABELS
from tools.app02b_lab.contract import LabError, canonical_json, digest, guarded_directory, publish
from tools.app02b_lab.run import exit_code
from tools.app02b_lab.schema import strict_json, validate_lab_bundle

bundle_module = importlib.import_module("tools.app02b_lab.bundle")


class ParserTests(unittest.TestCase):
    def test_reject_ambiguous_json(self):
        for raw in (b'{"x":1,"x":2}', b'{"x":NaN}', b'{"x":Infinity}',
                    b'{"x":-Infinity}', b'{"x":1e999}', rb'{"x":"\ud800"}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                strict_json(raw)

    def test_exit_codes_use_selected_results_and_integrity(self):
        self.assertEqual(exit_code(["PASS", "PASS"]), 0)
        self.assertEqual(exit_code(["PASS", "FAIL", "BLOCKED"]), 1)
        self.assertEqual(exit_code(["PASS"], integrity_ok=False), 1)
        self.assertEqual(exit_code(["PASS", "NOT_RUN"]), 2)
        self.assertEqual(exit_code(["BLOCKED"]), 2)
        self.assertEqual(exit_code([]), 2)


class PathGuardTests(unittest.TestCase):
    def test_guard_rejects_directory_replacement_or_untrusted_parent(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            guard = parent / "directory"
            guard.mkdir(mode=0o700)
            moved = parent / "moved"
            if sys.platform == "win32":
                with guarded_directory(guard), self.assertRaises(PermissionError):
                    guard.rename(moved)
                guard.rename(moved)
                self.assertTrue(moved.is_dir())
            else:
                parent.chmod(0o777)
                with self.assertRaisesRegex(LabError, "UNTRUSTED_DIRECTORY_PERMISSIONS"):
                    with guarded_directory(guard):
                        self.fail("Guard admitted an untrusted hierarchy")


class BundleTests(DisposableLab):
    def check(self, code=None):
        report = validate_lab_bundle(self.bundle)
        self.assertEqual(report.status, "FAIL" if code else "VALID", report.issues)
        self.assertEqual(report.originalGateStatus, "NOT_VERIFIED")
        if code:
            self.assertEqual(report.issues[0].code, code, report.issues)
        return report

    def reseal_baseline(self, key, value):
        path = self.bundle / key
        put(path, value)
        index = self.bundle / "hashes" / "baseline.json"
        hashes = get(index)
        hashes[key] = digest(path.read_bytes())
        put(index, hashes)

    def reseal_attempt(self, attempt, number=1):
        path = self.bundle / "attempts" / attempt["attemptId"] / "attempt.json"
        put(path, attempt)
        journal = self.bundle / "hashes" / f"{number:08d}.json"
        put(journal, get(journal) | {"attemptSha256": digest(path.read_bytes())})

    def test_prepare_and_resume_preserve_complete_coverage_and_history(self):
        self.prepare()
        report = self.check()
        self.assertEqual(len(report.coverage), 32)
        self.assertEqual(report.coverage["B05-02"], "BLOCKED")
        self.assertEqual(set(report.coverage.values()), {"BLOCKED", "NOT_RUN"})
        original = (self.bundle / "run.json").read_bytes()
        first = self.append()
        second = self.append("gate:G-B05")
        self.assertNotEqual(first["attemptId"], second["attemptId"])
        self.assertEqual(self.check().attempts, (first, second))
        self.assertEqual((self.bundle / "run.json").read_bytes(), original)

    def test_interrupted_capture_retains_raw_and_prior_attempts(self):
        self.prepare()
        first = self.append()

        def interrupt(draft):
            draft.write_artifact("B01-01", "raw", "partial.txt", b"SYNTHETIC partial capture")
            raise OSError("simulated capture interruption")

        with self.assertRaises(OSError):
            self.append(capture=interrupt)
        report = self.check()
        self.assertEqual(len(report.coverage), 32)
        self.assertEqual(report.attempts, (first,))
        self.assertFalse((self.bundle / "hashes" / "00000002.json").exists())
        partials = list(self.root.glob(".lab-capture-*/attempts/*/cases/B01-01/raw/partial.txt"))
        self.assertEqual([path.read_bytes() for path in partials], [b"SYNTHETIC partial capture"])
        header = get(partials[0].parents[5] / "capture.json")
        self.assertEqual(header["attemptId"], partials[0].parents[3].name)
        self.assertEqual(header["sourceHeadSha"], self.source)
        self.assertEqual(header["selection"], ["B01-01"])
        self.assertEqual(header["publicationState"], "CAPTURING")

    def test_crash_during_attempt_seal_preserves_coverage_and_prior_attempt(self):
        self.prepare()
        first = self.append()

        rename = Path.rename

        def interrupt(source, target):
            if source.name.startswith("attempt-"):
                raise OSError("simulated publication interruption")
            return rename(source, target)

        with patch.object(Path, "rename", autospec=True, side_effect=interrupt), self.assertRaises(OSError):
            self.append()
        report = self.check("PATH_MISSING")
        self.assertEqual(len(report.coverage), 32)
        self.assertEqual(report.attempts, (first,))
        entry = get(self.bundle / "hashes" / "00000002.json")
        staged = list(self.root.glob(f".lab-capture-*/attempts/{entry['attemptId']}/attempt.json"))
        self.assertEqual(len(staged), 1)
        self.assertEqual(digest(staged[0].read_bytes()), entry["attemptSha256"])

    def test_checksum_and_unindexed_entries_detect_modification(self):
        self.prepare()
        attempt = self.append()
        path = self.bundle / "attempts" / attempt["attemptId"] / "attempt.json"
        original = path.read_bytes()
        with self.assertRaises(FileExistsError):
            publish(path, b"overwrite")
        self.assertEqual(path.read_bytes(), original)
        path.write_bytes(original + b" ")
        self.check("ARTIFACT_HASH_MISMATCH")
        path.write_bytes(original)
        (path.parent / "unindexed.txt").write_bytes(b"SYNTHETIC")
        self.check("ATTEMPT_ARTIFACT_INVENTORY")

    def test_duplicate_or_missing_attempt_is_detected(self):
        self.prepare()
        self.append()
        self.append()
        first = get(self.bundle / "hashes/00000001.json")
        second = self.bundle / "hashes/00000002.json"
        put(second, get(second) | {"attemptId": first["attemptId"]})
        self.check("ATTEMPT_JOURNAL_CHAIN")
        second.unlink()
        self.check("ATTEMPT_HISTORY_INVENTORY")

    def test_duplicate_generated_id_does_not_poison_existing_run(self):
        self.prepare()
        attempt = self.append()
        identifier = UUID(hex=attempt["attemptId"].removeprefix("attempt-"))
        with patch.object(bundle_module, "uuid4", return_value=identifier), self.assertRaisesRegex(
                LabError, "ATTEMPT_ID_ALREADY_EXISTS"):
            self.append()
        self.assertEqual(self.check().attempts, (attempt,))

    def test_external_checkpoint_detects_terminal_rollback_and_resealed_baseline(self):
        self.prepare()
        first = self.append()
        second = self.append()
        checkpoint = self.check().checkpoint
        shutil.rmtree(self.bundle / "attempts" / second["attemptId"])
        (self.bundle / "hashes/00000002.json").unlink()
        self.assertEqual(self.check().attempts, (first,))
        rollback = validate_lab_bundle(self.bundle, checkpoint=checkpoint)
        self.assertEqual(rollback.issues[0].code, "EXTERNAL_CHECKPOINT_MISMATCH")
        checkpoint = self.check().checkpoint
        run = get(self.bundle / "run.json")
        run["context"]["startedUtc"] = "2025-01-01T00:00:00Z"
        self.reseal_baseline("run.json", run)
        self.check()
        resealed = validate_lab_bundle(self.bundle, checkpoint=checkpoint)
        self.assertEqual(resealed.issues[0].code, "EXTERNAL_CHECKPOINT_MISMATCH")

    def test_observed_attempt_is_captured_before_seal(self):
        self.prepare()
        target = self.manifest["targets"]["A"]["targetRef"]

        def capture(draft):
            row = dict(draft.results["B01-01"])
            fault = "ref-" + "f" * 16
            raw = draft.write_artifact("B01-01", "raw", "reading.txt", b"SYNTHETIC observation")
            witness = {
                **LABELS, "runId": draft.context.runId, "attemptId": draft.attempt_id,
                "caseId": row["caseId"], "sourceHeadSha": draft.context.sourceHeadSha,
                "environmentId": draft.context.environmentId,
                "configurationSha256": draft.context.configurationSha256,
                "kind": "HTTP", "phase": "POSITIVE", "observedUtc": draft.started_utc,
                "observedSymbol": "SYNTHETIC_MISMATCH", "faultId": fault,
                "observerPrincipalRef": "ref-" + "b" * 16, "targetIdentity": [target],
                "vantagePoint": "WINDOWS_HOST", "rawArtifact": raw,
            }
            descriptor = draft.write_artifact(
                "B01-01", "witnesses", "reading.json", canonical_json(witness))
            row.update(technicalResult="FAIL", tenantScope="A", peerScope="B",
                       faultId=fault, targetRefs=[target], observedUtc=draft.started_utc,
                       positiveRefs=[descriptor], actualObservations=["SYNTHETIC_MISMATCH"],
                       diff="MISMATCH", missingRequirements=[])
            return {"B01-01": row}

        first = self.append(capture=capture)
        first_bytes = (self.bundle / "attempts" / first["attemptId"] / "attempt.json").read_bytes()
        second = self.append()
        report = self.check()
        self.assertEqual(report.coverage["B01-01"], "FAIL")
        self.assertEqual(report.attempts, (first, second))
        self.assertEqual(exit_code([report.coverage["B01-01"]]), 1)
        self.assertEqual((self.bundle / "attempts" / first["attemptId"] / "attempt.json").read_bytes(),
                         first_bytes)

    def test_capture_with_unindexed_raw_is_rejected_before_seal(self):
        self.prepare()

        def capture(draft):
            draft.write_artifact("B01-01", "raw", "unindexed.txt", b"SYNTHETIC unindexed")
            return {}

        with self.assertRaisesRegex(LabError, "ATTEMPT_ARTIFACT_INVENTORY"):
            self.append(capture=capture)
        self.assertEqual(self.check().attempts, ())
        self.assertFalse((self.bundle / "hashes" / "00000001.json").exists())

    def test_schema_rejects_illegal_fields_values_and_physical_promotion(self):
        self.prepare()
        path = self.bundle / "report.json"
        baseline = get(path)
        for field, value, code in (
            ("schema", "APP02B-LAB-EVIDENCE-V2", "SCHEMA_CONSTANT"),
            ("originalGateStatus", "PASS", "SCHEMA_CONSTANT"),
            ("unknown", {"secret": "TEST_SECRET_NEVER_REPORT"}, "SCHEMA_UNKNOWN_FIELD"),
            ("caseCount", None, "SCHEMA_REQUIRED"),
        ):
            with self.subTest(field=field):
                report = baseline | {field: value}
                if value is None:
                    report.pop(field)
                self.reseal_baseline("report.json", report)
                self.check(code)

    def test_case_b0502_cannot_be_self_attested_pass(self):
        self.prepare()
        attempt = self.append("case:B05-02")
        row = next(row for row in attempt["results"] if row["caseId"] == "B05-02")
        row.update(technicalResult="FAIL")
        self.reseal_attempt(attempt)
        self.check("UNAUTHORIZED_OR_UNEXECUTED_CASE_RESULT")
        ref = {"relativePath": "witness.json", "sha256": "f" * 64}
        row.update(technicalResult="PASS", missingRequirements=[], actualObservations=[row["expected"]],
                   diff="MATCH", positiveRefs=[ref], negativeRefs=[ref],
                   faultId="ref-" + "f" * 16, observedUtc=attempt["startedUtc"])
        self.reseal_attempt(attempt)
        self.check("UNAUTHORIZED_OR_UNEXECUTED_CASE_RESULT")

    def test_unknown_result_and_stale_attempt_source_are_rejected(self):
        self.prepare()
        attempt = self.append()
        row = attempt["results"][0]
        row["technicalResult"] = "SUCCESS"
        self.reseal_attempt(attempt)
        self.check("SCHEMA_ENUM")
        row["technicalResult"] = "NOT_RUN"
        attempt["sourceHeadSha"] = "f" * 40
        self.reseal_attempt(attempt)
        self.check("ATTEMPT_PROVENANCE_MISMATCH")

    def witness_candidate(self):
        attempt = self.append()
        row = attempt["results"][0]
        scope = self.manifest["targets"]["A"]["targetRef"]
        prefix = f"attempts/{attempt['attemptId']}/cases/{row['caseId']}"
        row.update(technicalResult="PASS", missingRequirements=[], tenantScope="A", peerScope="B",
                   targetRefs=[scope], actualObservations=[row["expected"]], diff="MATCH",
                   peerBefore="e" * 64, peerAfter="e" * 64, faultId="ref-" + "f" * 16,
                   observedUtc=attempt["startedUtc"])

        def write_witness(phase, kind):
            name = f"{phase.lower()}-{kind.lower()}"
            witness_relative = f"{prefix}/witnesses/{name}.json"
            raw_relative = f"{prefix}/raw/{name}.txt"
            raw = self.bundle / raw_relative
            raw.parent.mkdir(parents=True, exist_ok=True)
            raw.write_bytes(b"SYNTHETIC RAW BYTES ONLY")
            witness_path = self.bundle / witness_relative
            witness_path.parent.mkdir(parents=True, exist_ok=True)
            witness = {
                **LABELS, **{key: attempt[key] for key in (
                    "runId", "attemptId", "sourceHeadSha", "environmentId", "configurationSha256"
                )},
                "caseId": row["caseId"], "kind": kind, "phase": phase,
                "observedUtc": row["observedUtc"],
                "observedSymbol": row["expected" if phase == "POSITIVE" else "negativeInvariant"],
                "faultId": row["faultId"], "observerPrincipalRef": "ref-" + "4" * 16,
                "targetIdentity": [scope], "vantagePoint": "WINDOWS_HOST",
                "rawArtifact": {"relativePath": raw_relative, "sha256": digest(raw.read_bytes())},
            }
            put(witness_path, witness)
            return {"relativePath": witness_relative, "sha256": digest(witness_path.read_bytes())}

        row["positiveRefs"] = [write_witness("POSITIVE", kind) for kind in row["requiredKinds"]]
        row["negativeRefs"] = [write_witness("NEGATIVE", row["requiredKinds"][0])]
        self.reseal_attempt(attempt)
        return attempt, row

    def test_even_structurally_complete_synthetic_witnesses_cannot_claim_pass(self):
        self.prepare()
        attempt, row = self.witness_candidate()
        self.check("MEASUREMENT_ASSESSOR_UNAVAILABLE")
        row["targetRefs"] = [self.manifest["targets"]["B"]["targetRef"]]
        self.reseal_attempt(attempt)
        self.check("WRONG_TARGET_PROVENANCE")

    def test_witness_tamper_empty_absent_and_forged_provenance(self):
        self.prepare()
        attempt, row = self.witness_candidate()
        ref = row["positiveRefs"][0]
        path = self.bundle / ref["relativePath"]
        witness = get(path)
        raw = self.bundle / witness["rawArtifact"]["relativePath"]
        raw.write_bytes(b"modified")
        self.check("ARTIFACT_HASH_MISMATCH")
        raw.unlink()
        self.check("PATH_MISSING")
        raw.write_bytes(b"  ")
        witness["rawArtifact"]["sha256"] = digest(raw.read_bytes())
        put(path, witness)
        ref["sha256"] = digest(path.read_bytes())
        self.reseal_attempt(attempt)
        self.check("EMPTY_RAW_ARTIFACT")
        witness["phase"] = "NEGATIVE"
        put(path, witness)
        ref["sha256"] = digest(path.read_bytes())
        self.reseal_attempt(attempt)
        self.check("WITNESS_PROVENANCE_MISMATCH")
        path.unlink()
        self.check("PATH_MISSING")

    def test_witness_relative_path_traversal(self):
        self.prepare()
        attempt, row = self.witness_candidate()
        ref = row["positiveRefs"][0]
        ref["relativePath"] = str(Path(ref["relativePath"]).parent / ".." / "outside.json").replace(
            "\\", "/")
        self.reseal_attempt(attempt)
        self.check("UNSAFE_RELATIVE_PATH")

    def test_symlink_or_junction_escape_is_rejected(self):
        self.prepare()
        target = self.root / "external"
        target.mkdir()
        original = self.bundle / "runtime"
        original.rmdir()
        try:
            original.symlink_to(target, target_is_directory=True)
        except OSError:
            if sys.platform != "win32":
                raise
            subprocess.run(["cmd", "/d", "/c", "mklink", "/J", str(original), str(target)],
                           capture_output=True, check=True)
        self.check("LINK_OR_REPARSE_POINT")


class CliTests(DisposableLab):
    def cli(self, selection="all", *flags):
        return subprocess.run([
            sys.executable, "-B", "tools/app02b_lab/run.py",
            "--config", str(self.config), "--selection", selection, "--run-id", RUN_ID,
            "--evidence-root", str(self.root), *flags,
        ], cwd=self.checkout, capture_output=True)

    def test_cli_ignores_timestamp_valid_bytecode_from_clean_checkout(self):
        self.install_cli()
        module = self.checkout / "tools/app02b_lab/bundle.py"
        original = module.read_bytes()
        identity = module.stat()
        stale = original.replace(b'"INVALID_SELECTION"', b'"STALE_CACHE_PROBE"')
        self.assertEqual(len(stale), len(original))
        self.assertNotEqual(stale, original)
        module.write_bytes(stale)
        os.utime(module, ns=(identity.st_atime_ns, identity.st_mtime_ns))
        py_compile.compile(str(module), doraise=True)
        module.write_bytes(original)
        os.utime(module, ns=(identity.st_atime_ns, identity.st_mtime_ns))
        self.assertEqual(self.git("status", "--porcelain"), "")
        outcome = self.cli("case:unknown")
        self.assertEqual(outcome.returncode, 1, outcome.stdout + outcome.stderr)
        self.assertEqual(strict_json(outcome.stdout)["issues"][0]["code"], "INVALID_SELECTION")

    def test_cli_refuses_dirty_checkout_without_importing_dirty_module(self):
        self.install_cli()
        module = self.checkout / "tools/app02b_lab/bundle.py"
        module.write_bytes(module.read_bytes() + b'\nraise RuntimeError("UNCOMMITTED_PROBE")\n')
        outcome = self.cli("case:B01-01")
        self.assertEqual(outcome.returncode, 1, outcome.stdout + outcome.stderr)
        self.assertNotIn(b"UNCOMMITTED_PROBE", outcome.stderr)
        self.assertEqual(strict_json(outcome.stdout)["issues"][0]["code"], "DIRTY_CHECKOUT")
        self.assertFalse(self.bundle.exists())

    def test_cli_validates_archive_with_schema_from_pinned_commit(self):
        self.install_cli()
        self.prepare()
        self.append()
        schema = self.checkout / "tools/app02b_lab/schemas/lab-evidence-v1.json"
        schema.write_bytes(b"{}")
        outcome = self.cli("case:B01-01", "--validate")
        self.assertEqual(outcome.returncode, 2, outcome.stdout + outcome.stderr)
        self.assertEqual(strict_json(outcome.stdout)["validation"], "VALID")

    def test_cli_controls_selection_and_preserves_history(self):
        self.install_cli()
        attempts = []
        for index, (selection, count) in enumerate((
            ("case:B01-01", 1), ("gate:G-B05", 6), ("all", 32), ("runtime:RUNTIME-01", 1)
        )):
            outcome = self.cli(selection, *(("--resume",) if index else ()))
            self.assertEqual(outcome.returncode, 2, outcome.stdout + outcome.stderr)
            output = strict_json(outcome.stdout)
            self.assertEqual(output["validation"], "VALID")
            self.assertEqual(len(output["selectedResults"]), count)
            self.assertEqual(output["originalGateStatus"], "NOT_VERIFIED")
            attempts.append(output["attemptIds"][-1])
            self.assertEqual(output["attemptIds"], attempts)
        readonly = self.cli("all", "--validate")
        self.assertEqual(readonly.returncode, 2)
        self.assertEqual(strict_json(readonly.stdout)["attemptIds"], attempts)
        self.assertEqual(self.cli().returncode, 1)

    def test_cli_retry_keeps_prior_failure_in_output_and_exit_code(self):
        self.install_cli()
        self.prepare()
        target = self.manifest["targets"]["A"]["targetRef"]

        def capture(draft):
            row = draft.results["B01-01"] | {
                "technicalResult": "FAIL", "tenantScope": "A", "peerScope": "B",
                "faultId": "ref-" + "f" * 16, "targetRefs": [target],
                "observedUtc": draft.started_utc,
            }
            return {"B01-01": row}

        failed = self.append(capture=capture)
        result = self.cli("case:B01-01", "--resume")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        body = strict_json(result.stdout)
        self.assertEqual(body["validation"], "VALID")
        self.assertEqual(body["selectedResults"], {"B01-01": "FAIL"})
        self.assertEqual(len(body["attemptIds"]), 2)
        self.assertEqual(body["attemptIds"][0], failed["attemptId"])

    def test_invalid_arguments_and_secret_fields_are_not_echoed(self):
        self.install_cli()
        sensitive = "TEST_SECRET_NEVER_REPORT"
        for result in (self.cli(sensitive), self.cli("all", "--" + sensitive)):
            self.assertEqual(result.returncode, 1)
            self.assertNotIn(sensitive.encode(), result.stdout + result.stderr)
        put(self.config, self.manifest | {"password": sensitive})
        result = self.cli()
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(sensitive.encode(), result.stdout + result.stderr)
        self.assertFalse(self.bundle.exists())

    def test_powershell_wrapper_preserves_native_exit_with_spaced_paths(self):
        self.install_cli()
        outcome = subprocess.run([
            "pwsh", "-NoProfile", "-File", "scripts/lab/Invoke-LabAcceptance.ps1",
            "-Config", str(self.config), "-RunId", RUN_ID, "-EvidenceRoot", str(self.root),
            "-Selection", "case:B01-01",
        ], cwd=self.checkout, capture_output=True)
        self.assertEqual(outcome.returncode, 2, outcome.stdout + outcome.stderr)
        self.assertEqual(strict_json(outcome.stdout)["selectedResults"], {"B01-01": "NOT_RUN"})

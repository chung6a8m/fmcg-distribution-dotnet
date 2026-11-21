from dataclasses import FrozenInstanceError, replace
from unittest.mock import patch
import unittest

from support import DisposableLab, RUN_ID, get, put
from tools.app02b_lab import context
from tools.app02b_lab.bundle import append_attempt, prepare_run, selection_ids
from tools.app02b_lab.contract import LabError, canonical_json
from tools.app02b_lab.schema import validate_document


class RunIdTests(unittest.TestCase):
    def test_portable_real_utc(self):
        context.validate_run_id(RUN_ID)
        for value in ("../escape", "b-lab-20260230T123456Z-6e018ed4",
                      "b-lab-20261121T123456Z-ABCD1234", "b-lab-20261121T123456Z-a"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                context.validate_run_id(value)


class ContextTests(DisposableLab):
    def test_valid_immutable_context_and_canonical_serialization(self):
        with patch.object(context, "utc_now", return_value="2026-11-21T12:34:56Z"):
            first = context.create_context(self.config, self.root, self.source, RUN_ID)
            self.config.write_bytes(b" \n" + self.config.read_bytes().strip())
            second = context.create_context(self.config, self.root, self.source, RUN_ID)
        self.assertEqual(first.to_bytes(), second.to_bytes())
        self.assertEqual(first.sourceHeadSha, self.source)
        self.assertEqual(first.mode, "LOCAL_OPERATOR_LAB")
        self.assertTrue(first.to_bytes().endswith(b"\n"))
        with self.assertRaises(FrozenInstanceError):
            first.sourceHeadSha = "f" * 40
        with self.assertRaisesRegex(LabError, "INVALID_UTC"):
            validate_document(first.document() | {"startedUtc": "2026-02-30T12:34:56Z"},
                              "run-context")

    def test_wrong_or_invalid_source_sha(self):
        for sha, error in (("abc", "INVALID_SOURCE_SHA"),
                           ("f" * 40, "CHECKOUT_HEAD_MISMATCH")):
            with self.subTest(sha=sha), self.assertRaisesRegex(LabError, error):
                context.create_context(self.config, self.root, sha, RUN_ID)

    def test_dirty_and_hidden_checkout(self):
        tracked = self.checkout / "synthetic.txt"
        tracked.write_text("dirty", encoding="utf-8")
        with self.assertRaisesRegex(LabError, "DIRTY_CHECKOUT"):
            self.inputs()
        self.git("update-index", "--assume-unchanged", tracked.name)
        with self.assertRaisesRegex(LabError, "CHECKOUT_HAS_HIDDEN_TRACKED_FILES"):
            self.inputs()

    def test_invalid_or_missing_host_fingerprint_and_environment(self):
        original = self.host.read_bytes()
        self.host.write_bytes(b"")
        with self.assertRaisesRegex(LabError, "HOST_FINGERPRINT_MISMATCH"):
            self.inputs()
        self.host.unlink()
        with self.assertRaisesRegex(LabError, "PROTECTED_HOST_OR_ADMISSION_MISSING"):
            self.inputs()
        self.host.write_bytes(original)
        put(self.config, self.manifest | {"environmentId": "production"})
        with self.assertRaisesRegex(LabError, "SCHEMA_PATTERN"):
            self.inputs()

    def test_changed_config_and_external_record_require_new_run(self):
        self.prepare()
        self.manifest["authorizationRecord"]["version"] = 2
        put(self.config, self.manifest)
        with self.assertRaisesRegex(LabError, "RESUME_PROVENANCE_MISMATCH"):
            self.append()
        self.assertEqual(list((self.bundle / "attempts").iterdir()), [])

    def test_changed_fixture_fails_source_freeze(self):
        path = self.config.with_name("fake-expected.json")
        path.write_bytes(context.FIXTURE.read_bytes() + b"\n")
        with patch.object(context, "FIXTURE", path), self.assertRaisesRegex(
                LabError, "FIXTURE_BYTES_CHANGED"):
            self.inputs()

    def test_duplicate_run_is_never_replaced(self):
        original = self.prepare()
        run = (self.bundle / "run.json").read_bytes()
        with self.assertRaisesRegex(LabError, "RUN_ALREADY_EXISTS"):
            prepare_run(self.root, original)
        with self.assertRaisesRegex(LabError, "RUN_ALREADY_EXISTS"):
            context.create_context(self.config, self.root, self.source, RUN_ID)
        self.assertEqual((self.bundle / "run.json").read_bytes(), run)

    def test_current_admission_cannot_replace_lineage(self):
        self.prepare()
        receipt = {
            "schema": "APP02B-LAB-LINEAGE-RECEIPT-V1", "runId": RUN_ID,
            "environmentId": self.manifest["environmentId"], "tenant": "A",
            "oldBinding": self.manifest["targets"]["A"],
            "newBinding": self.manifest["targets"]["A"] | {
                "identitySha256": "e" * 64, "registryVersion": 2,
            },
            "approvalRecord": self.manifest["authorizationRecord"],
            "approvedUtc": "2026-11-21T12:34:56Z",
        }
        context.validate_lineage_receipt(receipt)
        put(self.config.with_name("lineage-receipt.json"), receipt)
        current = get(self.admission)
        current["targets"]["A"] = receipt["newBinding"]
        put(self.admission, current)
        with self.assertRaisesRegex(LabError, "TARGET_LINEAGE_NOT_ADMITTED"):
            self.append()

    def test_stale_checkout_cannot_resume_original_run(self):
        original = self.prepare()
        self.git("commit", "--allow-empty", "-qm", "Changed source")
        with self.assertRaisesRegex(LabError, "CHECKOUT_HEAD_MISMATCH"):
            append_attempt(self.bundle, original, selection_ids("all"))

    def test_checkout_cannot_be_evidence_root(self):
        with self.assertRaisesRegex(LabError, "PATH_INSIDE_REPOSITORY"):
            context.create_context(self.config, self.checkout, self.source, RUN_ID)

    def test_preparation_and_resume_require_admitted_root_and_valid_inputs(self):
        inputs = self.inputs()
        alternative = self.root.parent / "different evidence root"
        alternative.mkdir()
        with self.assertRaisesRegex(LabError, "EVIDENCE_ROOT_NOT_ADMITTED"):
            prepare_run(alternative, inputs)
        with self.assertRaisesRegex(LabError, "INVALID_UTC"):
            prepare_run(self.root, replace(inputs, context=replace(
                inputs.context, startedUtc="2026-02-30T12:34:56Z")))
        with self.assertRaisesRegex(LabError, "NONCANONICAL_RUN_INPUT"):
            prepare_run(self.root, replace(inputs, manifest=b" " + inputs.manifest))
        self.assertFalse(self.bundle.exists())
        self.prepare()
        admission = get(self.admission)
        admission["targets"]["A"]["identitySha256"] = "f" * 64
        with self.assertRaisesRegex(LabError, "TARGET_LINEAGE_NOT_ADMITTED"):
            append_attempt(self.bundle, replace(inputs, admission=canonical_json(admission)),
                           selection_ids("all"))

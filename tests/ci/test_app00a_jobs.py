from itertools import product
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

from tools.verify_app00a_jobs import evaluate_needs


GATE_SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "verify_app00a_jobs.py"
WORKERS = ("core_sql", "web_sales", "app03", "distribution")
PR_CONTEXT = {
    "event_name": "pull_request",
    "base_sha": "a" * 40,
    "source_head_sha": "b" * 40,
    "workflow_sha": "c" * 40,
}
PUSH_CONTEXT = {
    "event_name": "push",
    "base_sha": "",
    "source_head_sha": "c" * 40,
    "workflow_sha": "c" * 40,
}


def classification_receipt(mode="full", context=PR_CONTEXT):
    docs_only = mode == "docs-only"
    changes = (
        [{"path": "README.md", "status": "M", "old_mode": "100644", "new_mode": "100644"}]
        if docs_only else []
    )
    return {
        "version": 1,
        "mode": mode,
        **context,
        "merge_base_sha": "d" * 40 if docs_only else "",
        "reason": "allowlisted_prose" if docs_only else "requires_full_ci",
        "changes": changes,
    }


def successful_needs(mode="full", context=PR_CONTEXT):
    status = "skipped" if mode == "docs-only" else "success"
    return {
        "classify": {
            "result": "success",
            "outputs": {"mode": mode, "receipt": json.dumps(classification_receipt(mode, context))},
        },
        **{worker: {"result": status, "outputs": {}} for worker in WORKERS},
    }


class GateCommandTests(unittest.TestCase):
    def run_gate(self, payload, context=PR_CONTEXT):
        env = os.environ.copy()
        for field in PR_CONTEXT:
            env.pop(f"APP00A_{field.upper()}", None)
        env.update({f"APP00A_{field.upper()}": value for field, value in context.items()})
        if payload is None:
            env.pop("APP00A_NEEDS_JSON", None)
        else:
            env["APP00A_NEEDS_JSON"] = payload
        return subprocess.run(
            [sys.executable, "-B", str(GATE_SCRIPT)],
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_exit_zero_and_report_each_job_on_success(self):
        result = self.run_gate(json.dumps(successful_needs()))

        self.assertEqual(result.returncode, 0, result.stderr)
        for job in successful_needs():
            self.assertIn(f"{job}: success", result.stdout)

    def test_exit_nonzero_and_report_unsuccessful_job(self):
        needs = successful_needs()
        needs["distribution"]["result"] = "cancelled"

        result = self.run_gate(json.dumps(needs))

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("distribution: cancelled", result.stdout)

    def test_exit_nonzero_on_invalid_json(self):
        result = self.run_gate("{")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("invalid JSON", result.stdout)

    def test_exit_nonzero_when_environment_variable_is_absent(self):
        result = self.run_gate(None)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("APP00A_NEEDS_JSON", result.stdout)

    def test_docs_exit_zero_and_explicitly_report_runtime_workers_not_run(self):
        result = self.run_gate(json.dumps(successful_needs("docs-only")))

        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertIn("runtime workers NOT_RUN", result.stdout)
        for worker in WORKERS:
            self.assertIn(f"{worker}: skipped", result.stdout)

    def test_main_push_passes_with_four_successful_workers(self):
        result = self.run_gate(json.dumps(successful_needs(context=PUSH_CONTEXT)), PUSH_CONTEXT)

        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        for worker in WORKERS:
            self.assertIn(f"{worker}: success", result.stdout)

    def test_cli_rejects_receipt_from_a_different_source_head(self):
        context = dict(PR_CONTEXT, source_head_sha="e" * 40)
        result = self.run_gate(json.dumps(successful_needs("docs-only")), context)

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("source_head_sha", result.stdout)
        self.assertNotIn("runtime workers NOT_RUN", result.stdout)


class NeedsValidationTests(unittest.TestCase):
    def evaluate(self, needs, context=PR_CONTEXT):
        return evaluate_needs(json.dumps(needs), **context)

    def test_all_jobs_succeed(self):
        passed, _ = self.evaluate(successful_needs())

        self.assertTrue(passed)

    def test_each_job_must_succeed(self):
        for job in successful_needs():
            for status in ("failure", "cancelled", "skipped", "unknown", "SUCCESS"):
                with self.subTest(job=job, status=status):
                    needs = successful_needs()
                    needs[job]["result"] = status

                    passed, messages = self.evaluate(needs)

                    self.assertFalse(passed)
                    self.assertTrue(
                        any(message.startswith(f"{job}:") and status in message for message in messages)
                    )

    def test_each_required_job_must_be_present(self):
        for job in successful_needs():
            with self.subTest(job=job):
                needs = successful_needs()
                del needs[job]

                passed, messages = self.evaluate(needs)

                self.assertFalse(passed)
                self.assertIn(f"{job}: missing job", messages)

    def test_extra_jobs_are_rejected_even_if_successful(self):
        needs = successful_needs()
        needs["extra"] = {"result": "success"}

        passed, messages = self.evaluate(needs)

        self.assertFalse(passed)
        self.assertTrue(any("extra" in message for message in messages))

    def test_result_must_be_present(self):
        needs = successful_needs()
        del needs["app03"]["result"]

        passed, messages = self.evaluate(needs)

        self.assertFalse(passed)
        self.assertIn("app03: missing result", messages)

    def test_top_level_payload_must_be_an_object(self):
        for needs in (None, [], "success", 10, True):
            with self.subTest(needs=needs):
                passed, _ = self.evaluate(needs)

                self.assertFalse(passed)

    def test_job_entry_must_be_an_object(self):
        for entry in (None, [], "success", 10, True):
            with self.subTest(entry=entry):
                needs = successful_needs()
                needs["web_sales"] = entry

                passed, _ = self.evaluate(needs)

                self.assertFalse(passed)

    def test_result_must_be_a_string(self):
        for status in (None, [], {}, 10, True):
            with self.subTest(status=status):
                needs = successful_needs()
                needs["core_sql"]["result"] = status

                passed, _ = self.evaluate(needs)

                self.assertFalse(passed)

    def test_duplicate_job_keys_are_invalid_even_if_last_result_is_success(self):
        successful_payload = json.dumps(successful_needs())
        duplicate_payload = '{"core_sql": {"result": "failure"}, ' + successful_payload[1:]

        passed, _ = evaluate_needs(duplicate_payload, **PR_CONTEXT)

        self.assertFalse(passed)

    def test_duplicate_result_keys_are_invalid_even_if_last_result_is_success(self):
        successful_payload = json.dumps(successful_needs())
        duplicate_payload = successful_payload.replace(
            '"core_sql": {"result": "success"',
            '"core_sql": {"result": "failure", "result": "success"',
            1,
        )

        passed, _ = evaluate_needs(duplicate_payload, **PR_CONTEXT)

        self.assertFalse(passed)

    def test_non_json_literals_are_invalid_even_in_outputs(self):
        needs = successful_needs()
        needs["core_sql"]["outputs"]["output"] = float("nan")

        passed, _ = self.evaluate(needs)

        self.assertFalse(passed)

    def test_docs_receipt_requires_a_pull_request(self):
        needs = successful_needs("docs-only", PUSH_CONTEXT)

        passed, messages = self.evaluate(needs, PUSH_CONTEXT)

        self.assertFalse(passed)
        self.assertTrue(any("pull_request" in message for message in messages))

    def test_docs_receipt_requires_every_worker_to_be_skipped(self):
        for worker in WORKERS:
            for unexpected_status in ("success", "failure", "cancelled", "unknown"):
                with self.subTest(worker=worker, status=unexpected_status):
                    needs = successful_needs("docs-only")
                    needs[worker]["result"] = unexpected_status

                    passed, messages = self.evaluate(needs)

                    self.assertFalse(passed)
                    self.assertIn(f"{worker}: expected skipped in docs-only mode", messages)
                    self.assertNotIn("runtime workers NOT_RUN", "\n".join(messages))

    def test_only_exact_worker_state_combinations_pass(self):
        for mode, passing_status in (("docs-only", "skipped"), ("full", "success")):
            for statuses in product(("success", "failure", "cancelled", "skipped"), repeat=4):
                with self.subTest(mode=mode, statuses=statuses):
                    needs = successful_needs(mode)
                    for worker, status in zip(WORKERS, statuses, strict=True):
                        needs[worker]["result"] = status

                    passed, _ = self.evaluate(needs)

                    self.assertEqual(passed, statuses == (passing_status,) * 4)

    def test_docs_worker_skips_do_not_override_a_failed_classifier(self):
        for status in ("failure", "cancelled", "skipped"):
            with self.subTest(status=status):
                needs = successful_needs("docs-only")
                needs["classify"]["result"] = status

                passed, messages = self.evaluate(needs)

                self.assertFalse(passed)
                self.assertIn(f"classify: {status}", messages)

    def test_classifier_outputs_require_a_known_mode_and_receipt_string(self):
        valid_outputs = successful_needs()["classify"]["outputs"]
        for outputs, expected_error in (
            (None, "outputs"),
            ([], "outputs"),
            ({}, "mode"),
            ({"mode": "FULL", "receipt": valid_outputs["receipt"]}, "mode"),
            ({"mode": "docs-only", "receipt": valid_outputs["receipt"]}, "mode"),
            ({"mode": "full"}, "receipt"),
            ({"mode": "full", "receipt": classification_receipt()}, "receipt"),
        ):
            with self.subTest(outputs=outputs):
                needs = successful_needs()
                needs["classify"]["outputs"] = outputs

                passed, messages = self.evaluate(needs)

                self.assertFalse(passed)
                self.assertIn(expected_error, "\n".join(messages))

    def test_receipt_rejects_non_objects_and_schema_changes(self):
        for receipt in ([], None, classification_receipt() | {"extra": "value"}):
            with self.subTest(receipt=receipt):
                self.assert_receipt_rejected(receipt, "receipt")
        for field in classification_receipt():
            with self.subTest(missing=field):
                receipt = classification_receipt()
                del receipt[field]
                self.assert_receipt_rejected(receipt, "receipt")

    def test_receipt_requires_supported_integer_version(self):
        for version in (0, 2, True, 1.0, "1"):
            with self.subTest(version=version):
                self.assert_receipt_rejected(classification_receipt() | {"version": version}, "version")

    def test_receipt_json_rejects_duplicate_keys_and_non_json_literals(self):
        receipt = json.dumps(classification_receipt("docs-only"))
        for malformed in (
            receipt.replace('"version": 1', '"version": 2, "version": 1'),
            receipt.replace('"path": "README.md"', '"path": "other.md", "path": "README.md"'),
            receipt.replace('"reason": "allowlisted_prose"', '"reason": NaN'),
            "{",
        ):
            with self.subTest(receipt=malformed):
                needs = successful_needs("docs-only")
                needs["classify"]["outputs"]["receipt"] = malformed

                passed, messages = self.evaluate(needs)

                self.assertFalse(passed)
                self.assertIn("invalid JSON", "\n".join(messages))

    def test_receipt_requires_each_current_event_identity_field(self):
        for field in PR_CONTEXT:
            with self.subTest(mismatched=field):
                different = "push" if field == "event_name" else "e" * 40
                self.assert_receipt_rejected(classification_receipt() | {field: different}, field)
            with self.subTest(missing=field):
                context = PR_CONTEXT.copy()
                del context[field]

                passed, messages = self.evaluate(successful_needs(), context)

                self.assertFalse(passed)
                self.assertIn(field, "\n".join(messages))

    def test_receipt_rejects_malformed_sha_even_when_it_matches_context(self):
        for field in ("base_sha", "source_head_sha", "workflow_sha"):
            with self.subTest(field=field):
                context = PR_CONTEXT | {field: "abc123"}

                passed, messages = self.evaluate(successful_needs("docs-only", context), context)

                self.assertFalse(passed)
                self.assertIn(field, "\n".join(messages))

    def test_push_receipt_rejects_pr_base_and_unrelated_source_head(self):
        for field in ("base_sha", "source_head_sha"):
            with self.subTest(field=field):
                context = PUSH_CONTEXT | {field: "a" * 40}

                passed, messages = self.evaluate(successful_needs(context=context), context)

                self.assertFalse(passed)
                self.assertIn(field, "\n".join(messages))

    def test_docs_receipt_requires_positive_reason_merge_base_and_nonempty_changes(self):
        for field, value in (
            ("reason", "requires_full_ci"),
            ("merge_base_sha", "not-a-sha"),
            ("merge_base_sha", ""),
            ("changes", None),
            ("changes", []),
        ):
            with self.subTest(field=field, value=value):
                receipt = classification_receipt("docs-only") | {field: value}
                self.assert_receipt_rejected(receipt, field, "docs-only")

    def test_docs_receipt_rejects_any_change_outside_the_prose_policy(self):
        for invalid_fields in (
            {"path": "docs/new.md"},
            {"path": "readme.md"},
            {"path": "./README.md"},
            {"status": "T"},
            {"old_mode": "120000"},
            {"new_mode": "100755"},
            {"status": "A"},
            {"extra": True},
        ):
            with self.subTest(invalid_fields=invalid_fields):
                receipt = classification_receipt("docs-only")
                receipt["changes"].append(receipt["changes"][0] | invalid_fields)
                self.assert_receipt_rejected(receipt, "changes", "docs-only")

    def test_full_receipt_rejects_malformed_fallback_fields(self):
        for field, value in (
            ("changes", classification_receipt("docs-only")["changes"]),
            ("reason", None),
            ("reason", ""),
            ("reason", "allowlisted_prose"),
            ("merge_base_sha", "not-a-sha"),
        ):
            with self.subTest(field=field, value=value):
                self.assert_receipt_rejected(classification_receipt() | {field: value}, field)

    def test_full_receipt_accepts_resolved_merge_base(self):
        needs = successful_needs()
        receipt = classification_receipt() | {"merge_base_sha": "f" * 40}
        needs["classify"]["outputs"]["receipt"] = json.dumps(receipt)

        passed, _ = self.evaluate(needs)

        self.assertTrue(passed)

    def assert_receipt_rejected(self, receipt, expected_error, mode="full"):
        needs = successful_needs(mode)
        needs["classify"]["outputs"]["receipt"] = json.dumps(receipt)

        passed, messages = self.evaluate(needs)
        self.assertFalse(passed)
        self.assertIn(expected_error, "\n".join(messages))

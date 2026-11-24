import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

from tools.verify_app00a_jobs import evaluate_needs


GATE_SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "verify_app00a_jobs.py"


def successful_needs():
    return {
        "core_sql": {"result": "success", "outputs": {}},
        "web_sales": {"result": "success", "outputs": {}},
        "app03": {"result": "success", "outputs": {}},
        "distribution": {"result": "success", "outputs": {}},
    }


class GateCommandTests(unittest.TestCase):
    def run_gate(self, payload):
        env = os.environ.copy()
        if payload is None:
            env.pop("APP00A_NEEDS_JSON", None)
        else:
            env["APP00A_NEEDS_JSON"] = payload
        return subprocess.run(
            [sys.executable, str(GATE_SCRIPT)],
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


class NeedsValidationTests(unittest.TestCase):
    def evaluate(self, needs):
        return evaluate_needs(json.dumps(needs))

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

        passed, _ = evaluate_needs(duplicate_payload)

        self.assertFalse(passed)

    def test_duplicate_result_keys_are_invalid_even_if_last_result_is_success(self):
        successful_payload = json.dumps(successful_needs())
        duplicate_payload = successful_payload.replace(
            '"result": "success"', '"result": "failure", "result": "success"', 1
        )

        passed, _ = evaluate_needs(duplicate_payload)

        self.assertFalse(passed)

    def test_non_json_literals_are_invalid_even_in_outputs(self):
        needs = successful_needs()
        needs["core_sql"]["outputs"]["output"] = float("nan")

        passed, _ = self.evaluate(needs)

        self.assertFalse(passed)

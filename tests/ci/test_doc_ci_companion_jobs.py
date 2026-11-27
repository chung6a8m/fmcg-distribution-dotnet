"""Native companion route and check-context contract regressions (stdlib only)."""

import json
from pathlib import Path
import re
import unittest

from tools.verify_doc_ci_companions import COMPANIONS, evaluate_companion, expected_status
from test_app00a_jobs import PR_CONTEXT, PUSH_CONTEXT, successful_needs

ROOT = Path(__file__).resolve().parents[2]


def payload(filename, mode="full", context=PR_CONTEXT):
    classifier = successful_needs(mode, context)["classify"]
    _, _, workers = COMPANIONS[filename]
    return {"classify": classifier, **{
        job: {"result": expected_status(route, mode), "outputs": {}}
        for job, route in workers.items()
    }}


class CompanionGateTests(unittest.TestCase):
    def evaluate(self, name, needs, context=PR_CONTEXT):
        return evaluate_companion(json.dumps(needs), name, context)[0]

    def test_exact_full_light_and_main_routes(self):
        for filename in COMPANIONS:
            for mode in ("full", "docs-only"):
                with self.subTest(file=filename, mode=mode):
                    self.assertTrue(self.evaluate(filename, payload(filename, mode)))
            self.assertTrue(self.evaluate(filename, payload(filename, context=PUSH_CONTEXT), PUSH_CONTEXT))
            self.assertFalse(self.evaluate(filename, payload(filename, "docs-only", PUSH_CONTEXT), PUSH_CONTEXT))

    def test_each_wrong_result_and_missing_job_fails(self):
        for filename in COMPANIONS:
            for mode in ("full", "docs-only"):
                for job, item in payload(filename, mode).items():
                    for result in ("success", "failure", "cancelled", "skipped", None, "unknown"):
                        if result == item["result"]:
                            continue
                        with self.subTest(file=filename, mode=mode, job=job, result=result):
                            needs = payload(filename, mode)
                            needs[job]["result"] = result
                            self.assertFalse(self.evaluate(filename, needs))
                    needs = payload(filename, mode)
                    del needs[job]
                    self.assertFalse(self.evaluate(filename, needs))

    def test_extra_wrong_scope_malformed_and_receipt_errors(self):
        for filename in COMPANIONS:
            needs = payload(filename, "docs-only")
            needs["extra"] = {"result": "success"}
            self.assertFalse(self.evaluate(filename, needs))
            needs = payload(filename, "docs-only")
            self.assertFalse(self.evaluate(filename, needs, dict(PR_CONTEXT, source_head_sha="f"*40)))
            self.assertFalse(self.evaluate(filename, needs, dict(PR_CONTEXT, workflow_sha="f"*40)))
            self.assertFalse(self.evaluate(filename, needs, dict(PR_CONTEXT, base_sha="f"*40)))
            for mode in ("bogus", "full", None):
                needs = payload(filename, "docs-only")
                needs["classify"]["outputs"]["mode"] = mode
                self.assertFalse(self.evaluate(filename, needs))
            for raw_receipt in (None, "{", '{"version":1}', '{"mode":"full","mode":"docs-only"}'):
                needs = payload(filename, "docs-only")
                needs["classify"]["outputs"]["receipt"] = raw_receipt
                self.assertFalse(self.evaluate(filename, needs))
            for raw in (None, "{", "[]", "null", '{"classify":{},"classify":{}}'):
                self.assertFalse(evaluate_companion(raw, filename, PR_CONTEXT)[0])
        self.assertFalse(evaluate_companion("{}", "unknown.yml", PR_CONTEXT)[0])

    def test_light_scope_never_claims_full_runtime_pass(self):
        for filename, (_, _, workers) in COMPANIONS.items():
            passed, messages = evaluate_companion(json.dumps(payload(filename, "docs-only")), filename, PR_CONTEXT)
            self.assertTrue(passed)
            self.assertIn("NOT_RUN", "\n".join(messages))
            for job, route in workers.items():
                if route == "full":
                    self.assertIn(f"{job}: skipped; expected skipped", messages)


class CompanionWorkflowContractTests(unittest.TestCase):
    def test_workflow_graph_preserves_public_check_names_and_exact_gate_dependencies(self):
        self.assertEqual(len(COMPANIONS), 9)
        self.assertEqual(sum(len(entry[1]) for entry in COMPANIONS.values()), 19)
        visible_names = []
        for filename, (key, public, workers) in COMPANIONS.items():
            text = (ROOT / ".github/workflows" / filename).read_text(encoding="utf-8")
            prefix, body = text.split("\njobs:\n", 1)
            self.assertIn("  pull_request:\n", prefix)
            self.assertIn("  push:\n    branches: [main]", prefix)
            self.assertIn("permissions:\n  contents: read", prefix)
            self.assertNotIn("paths-ignore:", prefix)
            self.assertNotIn("pull_request_target:", prefix)
            matches = list(re.finditer(r"^  ([A-Za-z0-9_-]+):$", body, re.M))
            jobs = {m[1]: body[m.end():matches[i+1].start() if i+1 < len(matches) else len(body)]
                    for i, m in enumerate(matches)}
            self.assertEqual(len(matches), len(jobs), filename)
            self.assertEqual(set(jobs), {"classify", *public, *workers}, filename)
            for job in jobs.values():
                visible_names.append(re.search(r"^    name: (.+)$", job, re.M)[1])
            self.assertIn(f"name: doc-ci-{key}-classify", jobs["classify"])
            self.assertIn("fetch-depth: 0", jobs["classify"])
            self.assertIn("uses: ./.github/actions/doc-ci-classify", jobs["classify"])
            for gate in public:
                with self.subTest(workflow=filename, gate=gate):
                    job = jobs[gate]
                    self.assertIn(f"\n    name: {gate}\n", job)
                    needs = re.search(r"^    needs: \[([^\]]+)\]$", job, re.M)[1].split(", ")
                    self.assertEqual(len(needs), len(set(needs)))
                    self.assertEqual(set(needs), {"classify", *workers})
                    self.assertIn("if: " + "$" + "{{ always() }}", job)
                    self.assertIn(f"DOC_CI_COMPANION: {filename}", job)
                    self.assertIn("run: python3 -B tools/verify_doc_ci_companions.py", job)
                    self.assertNotIn("continue-on-error:", job)
            for worker, route in workers.items():
                job = jobs[worker]
                self.assertIn(f"\n    name: doc-ci-{key}-{worker}\n", job)
                self.assertIn("needs: [classify]", job)
                condition = {
                    "always": "!cancelled()",
                    "full": "!cancelled() && (needs.classify.result != 'success' || needs.classify.outputs.mode != 'docs-only')",
                    "light": "!cancelled() && needs.classify.result == 'success' && needs.classify.outputs.mode == 'docs-only'",
                }[route]
                self.assertIn("if: " + "$" + "{{ " + condition + " }}", job)
                self.assertNotIn("continue-on-error:", job)
        self.assertEqual(len(visible_names), len(set(visible_names)))

    def test_shared_classifier_has_all_light_checks(self):
        action = (ROOT / ".github/actions/doc-ci-classify/action.yml").read_text(encoding="utf-8")
        for command in ("tools/classify_app00a_pr.py", "--classification-only",
                        "./scripts/Verify-Business.ps1", "tools/validate_business.py",
                        "tools/validate_e_sim_01.py", "unittest discover",
                        "actions/upload-artifact@v7"):
            self.assertIn(command, action)
        self.assertNotIn("continue-on-error:", action)

    def test_all_security_jobs_remain_required_in_light(self):
        _, names, workers = COMPANIONS["security-picomatch-issue59.yml"]
        self.assertEqual(len(names), 5)
        self.assertEqual(workers, {"run-" + name: "always" for name in names})

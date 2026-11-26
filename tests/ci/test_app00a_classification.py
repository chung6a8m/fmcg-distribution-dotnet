import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "classify_app00a_pr.py"
RECEIPT_PATH = Path("TestResults/app00a-classification.json")


class ClassificationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.repo = Path(directory.name) / "repo"
        self.repo.mkdir()
        self.git("init", "--quiet", "--initial-branch=main", "--object-format=sha1")
        self.git("config", "user.name", "Classifier test")
        self.git("config", "user.email", "classifier@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        (self.repo / ".git/info/exclude").write_text("/TestResults/\n", encoding="utf-8")
        self.write("docs/status/app-05-status.md", "Read me\n")
        self.write("src/runtime.cs", "base runtime\n")
        self.write("docs/receipts/app-05-receipts.md", "Build cache\n")
        self.base = self.commit("base")
        self.git("switch", "-c", "source")

    def git(self, *args, input=None, cwd=None):
        return subprocess.run(
            ["git", *args], cwd=cwd or self.repo, input=input,
            capture_output=True, text=True, check=True,
        ).stdout.strip()

    def write(self, name, content="new content\n"):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "--quiet", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def commit_tree(self, message, *parents, tree=None):
        parent_args = [arg for parent in parents for arg in ("-p", parent)]
        return self.git("commit-tree", tree or self.git("write-tree"), *parent_args, "-m", message)

    def merge(self, source, *, base=None):
        self.git("switch", "--detach", base or self.base)
        self.git("merge", "--no-ff", "--quiet", "-m", "workflow merge", source)
        return self.git("rev-parse", "HEAD")

    def run_cli(self, source, workflow, *, event="pull_request", base=None, output=None):
        env = {
            **os.environ,
            "APP00A_EVENT_NAME": event,
            "APP00A_BASE_SHA": self.base if base is None else base,
            "APP00A_SOURCE_HEAD_SHA": source,
            "APP00A_WORKFLOW_SHA": workflow,
            "GITHUB_OUTPUT": str(output or self.repo.parent / "github-output"),
            "GITHUB_STEP_SUMMARY": str(self.repo.parent / "github-summary"),
        }
        return subprocess.run(
            [sys.executable, str(SCRIPT)], cwd=self.repo,
            env=env, capture_output=True, text=True, check=False,
        )

    def receipt(self, source, workflow, **kwargs):
        result = self.run_cli(source, workflow, **kwargs)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return json.loads((self.repo / RECEIPT_PATH).read_text(encoding="utf-8"))

    def assert_full(self, source, workflow, **kwargs):
        receipt = self.receipt(source, workflow, **kwargs)
        self.assertEqual(receipt["mode"], "full", receipt)
        self.assertEqual(receipt["changes"], [])
        self.assertNotEqual(receipt["reason"], "allowlisted_prose")

    def test_cli_emits_exact_docs_receipt_output_and_summary(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("readme")
        workflow = self.merge(source)

        expected = {
            "version": 2, "mode": "docs-only", "event_name": "pull_request",
            "base_sha": self.base, "source_head_sha": source,
            "workflow_sha": workflow, "merge_base_sha": self.base,
            "diagnostics": {"phase": "complete", "offending_count": 0, "offenders": []},
            "reason": "allowlisted_prose", "changes": [
                {"path": "docs/status/app-05-status.md", "status": "M", "old_mode": "100644", "new_mode": "100644"},
            ],
        }
        self.assertEqual(self.receipt(source, workflow), expected)
        lines = (self.repo.parent / "github-output").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual(lines[0], "mode=docs-only")
        self.assertTrue(lines[1].startswith("receipt="))
        self.assertEqual(json.loads(lines[1].removeprefix("receipt=")), expected)
        summary = (self.repo.parent / "github-summary").read_text(encoding="utf-8")
        self.assertIn("docs-only", summary)

    def test_delete_stable_document_requires_full(self):
        self.git("rm", "docs/receipts/app-05-receipts.md")
        source = self.commit("delete descriptive receipt")
        self.assert_full(source, self.merge(source))

    def test_add_stable_document_requires_full(self):
        self.git("rm", "docs/receipts/app-05-receipts.md")
        self.base = self.commit("base without receipt")
        self.write("docs/receipts/app-05-receipts.md")
        source = self.commit("add receipt")
        self.assert_full(source, self.merge(source))

    def test_all_audited_paths_can_be_in_same_proof(self):
        paths = ["docs/status/app-05-status.md", "docs/receipts/app-05-receipts.md"]
        for path in paths:
            self.write(path)
        source = self.commit("update audited paths")
        receipt = self.receipt(source, self.merge(source))
        self.assertEqual(receipt["mode"], "docs-only", receipt)
        self.assertCountEqual([change["path"] for change in receipt["changes"]], paths)

    def test_v1_authority_paths_and_policy_edits_require_full(self):
        from tools.classify_app00a_pr import path_preview
        paths = ["docs/roadmap/app-00a-parallel-ci.md","docs/roadmap/app-00a-build-cache.md","docs/roadmap/localdb-ci-bootstrap.md","docs/roadmap/pr-ci-concurrency.md","docs/roadmap/docs-pr-ci.md","docs/superpowers/plans/2026-11-16-issue-13-pwa-external-update.md","docs/superpowers/plans/2026-11-20-ui01-runtime-foundations.md","docs/superpowers/plans/2026-11-22-app-02d2.md","docs/superpowers/plans/2026-11-22-app-02d3.md","docs/superpowers/plans/2026-11-24-app00a-parallel-ci.md","docs/superpowers/plans/2026-11-25-app00a-build-cache.md","docs/superpowers/plans/2026-11-25-app03-read-diagnostics.md","docs/superpowers/plans/2026-11-25-docs-pr-ci.md","docs/superpowers/plans/2026-11-25-localdb-bootstrap.md","docs/superpowers/plans/2026-11-25-pr-ci-concurrency.md","README.md","AGENTS.md","docs/roadmap/app-05.md","tools/classify_app00a_pr.py","tools/verify_app00a_jobs.py",".github/workflows/app00a.yml"]
        for path in paths:
            with self.subTest(path=path):
                self.git("switch", "--detach", self.base)
                self.write(path)
                self.write("docs/status/app-05-status.md")
                source = self.commit("mixed policy update")
                receipt = self.receipt(source, self.merge(source))
                self.assertEqual(receipt["mode"], "full")
                self.assertEqual(receipt["changes"], [])
                self.assertEqual(receipt["diagnostics"]["phase"], "comparisons")
                self.assertEqual(receipt["diagnostics"]["offending_count"], 2)
                self.assertEqual({item["path_preview"] for item in receipt["diagnostics"]["offenders"]}, {path_preview(path)})
                self.assertEqual({item["phase"] for item in receipt["diagnostics"]["offenders"]}, {"whole-pr", "integration"})

    def test_diagnostics_are_bounded_and_escape_workflow_and_markdown_syntax(self):
        for n in range(12):
            self.write(f"docs/unlisted-{n:02}.md")
        if os.name != "nt":
            self.write("docs/000-<tag>:\n.md")
        source = self.commit("unknown paths")
        workflow = self.merge(source)
        receipt = self.receipt(source, workflow)
        detail = receipt["diagnostics"]
        self.assertEqual(receipt["mode"], "full")
        self.assertGreater(detail["offending_count"], 8)
        self.assertEqual(len(detail["offenders"]), 8)
        for item in detail["offenders"]:
            self.assertLessEqual(len(item["path_preview"]), 240)
            self.assertNotIn("\n", item["path_preview"])
            self.assertNotIn("<", item["path_preview"])
            self.assertNotIn(":", item["path_preview"])
        summary = (self.repo.parent / "github-summary").read_text(encoding="utf-8")
        self.assertIn("Additional records omitted", summary)
        self.assertNotIn("<tag>", summary)

    def test_mixed_source_and_allowed_prose_require_full(self):
        self.write("src/runtime.cs")
        self.write("docs/status/app-05-status.md")
        source = self.commit("mixed")

        self.assert_full(source, self.merge(source))

    def test_earlier_runtime_change_remains_visible_after_prose_commit(self):
        self.write("src/runtime.cs")
        self.commit("runtime update")
        self.write("docs/status/app-05-status.md")
        source = self.commit("separate prose update")
        self.git("switch", "main")
        self.write("src/runtime.cs")
        latest_base = self.commit("base independently changed runtime")
        workflow = self.merge(source, base=latest_base)
        self.assertEqual(self.git("diff", "--name-only", latest_base, workflow), "docs/status/app-05-status.md")

        self.assert_full(source, workflow, base=latest_base)

    def test_unknown_case_and_control_character_paths_require_full(self):
        paths = ["docs/superpowers/plans/new-plan.md", "ReadMe.md", "other/app-05-status.md"]
        if os.name != "nt":
            paths.append("docs/README\n.md")
        for path in paths:
            with self.subTest(path=path):
                # Use the index so Git sees exact paths even on case-insensitive hosts.
                self.git("switch", "--detach", "--discard-changes", self.base)
                self.write("docs/receipts/app-05-receipts.md")
                self.git("add", "docs/receipts/app-05-receipts.md")
                object_id = self.git("hash-object", "-w", "--stdin", input="new prose\n")
                self.git("update-index", "--add", "--cacheinfo", f"100644,{object_id},{path}")
                source = self.commit_tree("unknown path", self.base)
                merge = self.commit_tree("workflow merge", self.base, source)
                self.git("update-ref", "--no-deref", "HEAD", merge)

                self.assert_full(source, merge)

    def test_git_modes_and_type_changes_require_full(self):
        self.git("config", "diff.ignoreSubmodules", "all")
        for mode in ("100755", "120000", "160000"):
            with self.subTest(mode=mode):
                self.git("switch", "--detach", "--discard-changes", self.base)
                self.write("docs/receipts/app-05-receipts.md")
                self.git("add", "docs/receipts/app-05-receipts.md")
                object_id = self.base if mode == "160000" else self.git("rev-parse", "HEAD:docs/status/app-05-status.md")
                self.git("update-index", "--cacheinfo", f"{mode},{object_id},docs/status/app-05-status.md")
                source = self.commit_tree("mode change", self.base)
                merge = self.commit_tree("workflow merge", self.base, source)
                self.git("update-ref", "--no-deref", "HEAD", merge)

                self.assert_full(source, merge)

    def test_source_to_docs_and_docs_to_source_moves_require_full(self):
        moves = (("src/runtime.cs", "docs/roadmap/docs-pr-ci.md"), ("docs/status/app-05-status.md", "src/moved.cs"))
        for old, new in moves:
            with self.subTest(old=old, new=new):
                self.git("switch", "--detach", self.base)
                self.git("mv", old, new)
                source = self.commit("move")

                self.assert_full(source, self.merge(source))

    def test_empty_diff_requires_full(self):
        source = self.commit("empty")

        self.assert_full(source, self.merge(source))

    def test_invalid_event_sha_requires_full(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)

        for invalid_workflow in ("f" * 40, workflow[:10], f"{workflow}^1"):
            with self.subTest(workflow=invalid_workflow):
                self.assert_full(source, invalid_workflow)

    def test_missing_git_object_requires_full(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        source_tree = self.git("rev-parse", f"{source}^{{tree}}")
        (self.repo / ".git/objects" / source_tree[:2] / source_tree[2:]).unlink()

        self.assert_full(source, workflow)

    def test_missing_historical_prose_blob_requires_full(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        old_blob = self.git("rev-parse", f"{self.base}:docs/status/app-05-status.md")
        (self.repo / ".git/objects" / old_blob[:2] / old_blob[2:]).unlink()

        self.assert_full(source, workflow)

    def test_corrupt_historical_blob_requires_full_when_truncated(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        old_blob = self.git("rev-parse", f"{self.base}:docs/status/app-05-status.md")
        loose = self.repo / ".git/objects" / old_blob[:2] / old_blob[2:]
        loose.chmod(0o600)
        loose.write_bytes(loose.read_bytes()[:-4])

        self.assert_full(source, workflow)

    def test_corrupt_historical_blob_requires_full_when_hash_mismatches(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        old_blob = self.git("rev-parse", f"{self.base}:docs/status/app-05-status.md")
        new_blob = self.git("rev-parse", f"{source}:docs/status/app-05-status.md")
        objects = self.repo / ".git/objects"
        old_object = objects / old_blob[:2] / old_blob[2:]
        new_object = objects / new_blob[:2] / new_blob[2:]
        old_object.chmod(0o600)
        old_object.write_bytes(new_object.read_bytes())

        self.assert_full(source, workflow)

    def test_regular_mode_pointing_to_non_blob_in_integration_requires_full(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        blob_oid = bytes.fromhex(self.git("rev-parse", f"{source}:docs/status/app-05-status.md"))
        directory_oid = bytes.fromhex(self.git("rev-parse", f"{source}:docs"))
        status_oid = bytes.fromhex(self.git("rev-parse", f"{source}:docs/status"))

        def replace_tree(ref, name, old, new):
            data = subprocess.check_output(["git", "cat-file", "tree", ref], cwd=self.repo)
            bad = data.replace(name + b"\0" + old, name + b"\0" + new, 1)
            self.assertNotEqual(data, bad)
            return bytes.fromhex(subprocess.check_output(
                ["git", "hash-object", "-w", "--literally", "-t", "tree", "--stdin"],
                cwd=self.repo, input=bad).decode().strip())

        bad_status = replace_tree(f"{source}:docs/status", b"app-05-status.md", blob_oid, directory_oid)
        bad_docs = replace_tree(f"{source}:docs", b"status", status_oid, bad_status)
        bad_root = replace_tree(source, b"docs", directory_oid, bad_docs)
        workflow = self.commit_tree("workflow merge", self.base, source, tree=bad_root.hex())
        self.git("update-ref", "--no-deref", "HEAD", workflow)
        self.assert_full(source, workflow)

    def test_shallow_checkout_requires_full_even_with_merge_parents_present(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        shallow = self.repo.parent / "shallow"
        self.git("clone", "--quiet", "--depth=2", self.repo.as_uri(), str(shallow))
        self.repo = shallow
        self.assertEqual(self.git("rev-parse", "--is-shallow-repository"), "true")

        self.assert_full(source, workflow)

    def test_multiple_best_merge_bases_require_full(self):
        first = self.commit_tree("first fork", self.base)
        second = self.commit_tree("second fork", self.base)
        base = self.commit_tree("first integration", first, second)
        self.write("docs/status/app-05-status.md")
        self.git("add", "docs/status/app-05-status.md")
        source = self.commit_tree("second integration", second, first)
        workflow = self.commit_tree("workflow merge", base, source)
        self.git("update-ref", "--no-deref", "HEAD", workflow)
        self.assertCountEqual(self.git("merge-base", "--all", base, source).splitlines(), [first, second])

        self.assert_full(source, workflow, base=base)

    def test_workflow_checkout_and_ordered_merge_parents_must_match(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        reversed_parents = self.commit_tree("reverse merge", source, self.base)
        single_parent = self.commit_tree("single parent", self.base)
        extra_parent = self.commit_tree("third parent")
        octopus = self.commit_tree("three parents", self.base, source, extra_parent)

        for checkout, event_base, event_source, event_workflow in (
            (source, self.base, source, workflow),
            (workflow, "f" * 40, source, workflow),
            (workflow, self.base, self.base, workflow),
            (reversed_parents, self.base, source, reversed_parents),
            (single_parent, self.base, source, single_parent),
            (octopus, self.base, source, octopus),
        ):
            with self.subTest(checkout=checkout, base=event_base, source=event_source, workflow=event_workflow):
                self.git("update-ref", "--no-deref", "HEAD", checkout)

                self.assert_full(event_source, event_workflow, base=event_base)

    def test_base_movement_uses_merge_base_and_ignores_unrelated_base_change(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        self.git("switch", "main")
        self.write("src/runtime.cs", "new base runtime\n")
        latest_base = self.commit("base advanced")
        workflow = self.merge(source, base=latest_base)

        receipt = self.receipt(source, workflow, base=latest_base)

        self.assertEqual(receipt["mode"], "docs-only", receipt)
        self.assertEqual(receipt["base_sha"], latest_base)
        self.assertEqual(receipt["merge_base_sha"], self.base)
        self.assertEqual([change["path"] for change in receipt["changes"]], ["docs/status/app-05-status.md"])

    def test_integration_runtime_change_requires_full_even_when_source_is_prose(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        self.write("src/runtime.cs", "integration-only runtime\n")
        self.git("add", "src/runtime.cs")
        workflow = self.commit_tree("workflow merge", self.base, source)
        self.git("update-ref", "--no-deref", "HEAD", workflow)

        self.assert_full(source, workflow)

    def test_integration_only_prose_is_in_complete_proof(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        self.write("docs/receipts/app-05-receipts.md")
        self.git("add", "docs/receipts/app-05-receipts.md")
        workflow = self.commit_tree("workflow merge", self.base, source)
        self.git("update-ref", "--no-deref", "HEAD", workflow)

        receipt = self.receipt(source, workflow)

        self.assertEqual(receipt["mode"], "docs-only", receipt)
        self.assertCountEqual([change["path"] for change in receipt["changes"]], [
            "docs/status/app-05-status.md", "docs/receipts/app-05-receipts.md",
        ])

    def test_non_pr_events_always_require_full_even_for_valid_docs_merge(self):
        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)

        for event in ("push", "pull_request_target", "workflow_dispatch", ""):
            with self.subTest(event=event):
                self.assert_full(source, workflow, event=event)

    def test_cli_escapes_untrusted_event_in_single_line_receipt(self):
        event = "push\n::error::unexpected\rñ"

        receipt = self.receipt("", "", event=event, base="")

        self.assertEqual(receipt["event_name"], event)
        self.assertEqual(receipt["mode"], "full")
        lines = (self.repo.parent / "github-output").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual(lines[0], "mode=full")
        self.assertEqual(json.loads(lines[1].removeprefix("receipt=")), receipt)
        summary = (self.repo.parent / "github-summary").read_text(encoding="utf-8")
        self.assertNotIn("::error::", summary)

    def test_cli_output_write_failure_exits_nonzero(self):
        result = self.run_cli("", "", event="push", output=self.repo)

        self.assertNotEqual(result.returncode, 0)

    def test_git_output_over_limit_requires_full_even_for_allowed_changes(self):
        from tools import classify_app00a_pr as classifier

        self.write("docs/status/app-05-status.md")
        self.write("docs/receipts/app-05-receipts.md")
        source = self.commit("prose")
        workflow = self.merge(source)

        with patch.object(classifier, "_GIT_MAX_BYTES", 128):
            receipt = classifier.classify(
                self.repo, event_name="pull_request", base_sha=self.base,
                source_head_sha=source, workflow_sha=workflow,
            )

        self.assertEqual(receipt["mode"], "full", receipt)
        self.assertEqual(receipt["reason"], "git_output_too_large")
        self.assertEqual(receipt["changes"], [])

    @unittest.skipIf(os.name == "nt", "Git for Windows may reject non-UTF-8 index paths")
    def test_real_non_utf8_path_requires_full(self):
        self.write("docs/status/app-05-status.md")
        self.git("add", "docs/status/app-05-status.md")
        object_id = self.git("rev-parse", "HEAD:docs/status/app-05-status.md").encode("ascii")
        subprocess.run(
            ["git", "update-index", "-z", "--index-info"], cwd=self.repo,
            input=b"100644 " + object_id + b"\tdocs/README.\xffmd\0", check=True,
        )
        source = self.commit_tree("non-UTF-8 path", self.base)
        workflow = self.commit_tree("workflow merge", self.base, source)
        self.git("update-ref", "--no-deref", "HEAD", workflow)

        self.assert_full(source, workflow)

    def test_malformed_git_output_requires_full_instead_of_partial_proof(self):
        from tools import classify_app00a_pr as classifier

        self.write("docs/status/app-05-status.md")
        source = self.commit("prose")
        workflow = self.merge(source)
        git = classifier._git
        raw = git(self.repo, "diff", "--raw", "-z", "--no-abbrev", self.base, source, "--")

        for malformed in (
            raw[:-1], raw + b"truncated\0", raw + raw,
            raw.replace(b" M\0", b" M100\0"), raw.replace(b":100644", b":10064x"),
            raw.replace(b" M\0", b" A\0"),
        ):
            with self.subTest(raw=malformed):
                def git_with_malformed_diff(repo, *args, **kwargs):
                    return malformed if args[0] == "diff" else git(repo, *args, **kwargs)

                with patch.object(classifier, "_git", side_effect=git_with_malformed_diff):
                    receipt = classifier.classify(
                        self.repo, event_name="pull_request", base_sha=self.base,
                        source_head_sha=source, workflow_sha=workflow,
                    )

                self.assertEqual(receipt["mode"], "full", receipt)
                self.assertEqual(receipt["changes"], [])

"""Run explicitly on Windows. The only replaced boundary is host inventory acquisition."""

import json
import os
from pathlib import Path
from unittest.mock import patch
import shutil
import subprocess
import tempfile
import unittest

from tools.app02b_lab import preflight
from test_environment import PRIVATE, ROOT, observed_host, read_example


class PowerShellPreflight(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="LAB I01 CLI ")
        self.addCleanup(self.scratch.cleanup)
        self.base = Path(self.scratch.name).resolve()
        self.repo = self.base / "disposable checkout"
        for path in ("tools/app02b_lab", "scripts/lab", "deploy/lab/windows"):
            shutil.copytree(ROOT / path, self.repo / path)
        subprocess.run(["git", "init", "-q", str(self.repo)], check=True)
        subprocess.run(["git", "-C", str(self.repo), "config", "core.autocrlf", "false"], check=True)
        self.evidence = self.base / "evidence root"
        self.evidence.mkdir()
        self.storage = self.base / "vm storage"
        self.storage.mkdir()
        self.output = self.evidence / "report with spaces"
        self.plan_path = self.base / "environment with spaces.json"
        self.plan = read_example("environment.example.json")
        self.plan.update(storageRoot=str(self.storage), evidenceRoot=str(self.evidence))
        self.plan["operatorHostRef"] = "ref-33e52d58f1e0ff25"
        self.host = observed_host()

    def invoke(self, code, *, config=None, output=None, host=None, raw_input=None):
        self.plan_path.write_text(
            raw_input if raw_input is not None else json.dumps(self.plan if config is None else config),
            encoding="utf-8",
        )
        body = json.dumps(self.host if host is None else host)
        fixture = self.repo / "scripts/lab/Lab.Common.psm1"
        fixture.write_text("function Get-LabWindowsInventory { param([string]$DriveLetter)\n"
                           + "if ($DriveLetter -ne '" + self.storage.drive[0] + "') { throw 'Wrong drive' }\n"
                           + "@'\n" + body + "\n'@\n}\n"
                           + "Export-ModuleMember -Function Get-LabWindowsInventory\n", encoding="utf-8")
        subprocess.run(["git", "-C", str(self.repo), "add", "."], check=True)
        subprocess.run(["git", "-C", str(self.repo), "-c", "user.name=LAB I01 Fixture",
                        "-c", "user.email=fixture@example.invalid", "commit", "-q", "--allow-empty",
                        "-m", "synthetic preflight checkout"], check=True)

        result = subprocess.run([
            "pwsh", "-NoProfile", "-NonInteractive", "-File",
            str(self.repo / "scripts/lab/Test-LabPrerequisites.ps1"),
            "-EnvironmentFile", str(self.plan_path), "-OutputDirectory", str(output or self.output),
        ], cwd=self.base, capture_output=True, text=True, timeout=100)
        self.assertEqual(result.returncode, code, result.stdout + result.stderr)
        self.assertNotIn(PRIVATE, result.stdout + result.stderr)
        status = subprocess.run(["git", "-C", str(self.repo), "status", "--porcelain"],
                                check=True, capture_output=True)
        self.assertEqual(status.stdout, b"")
        if (self.output / "preflight.json").exists():
            for name in ("preflight.json", "preflight.md"):
                self.assertNotIn(PRIVATE, (self.output / name).read_text(encoding="utf-8"))
            return json.loads((self.output / "preflight.json").read_text(encoding="utf-8"))
        return None

    def test_ready_and_repeat_cannot_overwrite(self):
        report = self.invoke(0)
        self.assertEqual(report["readiness"], "READY_FOR_LAB_SETUP")
        contents = sorted(self.output.iterdir())
        before = {file.name: file.read_bytes() for file in contents}
        second = self.invoke(1)
        self.assertEqual(second, report)
        self.assertEqual({file.name: file.read_bytes() for file in self.output.iterdir()}, before)

    def test_missing_tool_blocks(self):
        del self.host["tools"]["openssl"]
        report = self.invoke(2)
        self.assertEqual(report["readiness"], "BLOCKED")
        self.assertIn("TOOL_OPENSSL", report["missingPrerequisites"])

    def test_invalid_config_failure_report_is_redacted(self):
        report = self.invoke(1, config=self.plan | {"password": PRIVATE})
        self.assertEqual(report["readiness"], "COLLECTION_FAILED")
        self.assertEqual(report["issueCodes"], ["UNKNOWN_FIELD"])

    def test_invalid_json_failure_report_is_redacted(self):
        report = self.invoke(1, raw_input='{"schema": "' + PRIVATE)
        self.assertEqual(report["issueCodes"], ["INVALID_JSON"])

    def test_missing_bootstrap_python_fails_closed(self):
        powershell = shutil.which("pwsh")
        self.assertIsNotNone(powershell)
        result = subprocess.run([
            powershell, "-NoProfile", "-NonInteractive", "-File",
            str(self.repo / "scripts/lab/Test-LabPrerequisites.ps1"),
            "-EnvironmentFile", str(self.plan_path), "-OutputDirectory", str(self.output),
        ], cwd=self.base, env=os.environ | {"PATH": str(Path(powershell).parent)},
            capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(result.stderr.strip(), "COLLECTION_FAILED MISSING_TOOL_PYTHON")
        self.assertFalse(self.output.exists())

    def test_collector_failure_is_redacted(self):
        report = self.invoke(1, host={"malformed": PRIVATE})
        self.assertEqual(report["issueCodes"], ["INVENTORY_COLLECTION_FAILED"])

    def test_rejects_worktree_and_external_wrong_root(self):
        for output in (self.repo / "bad-output", self.base / "bad-output"):
            with self.subTest(output=output.name):
                self.invoke(1, output=output)
                self.assertFalse(output.exists())
        self.assertEqual(list(self.evidence.iterdir()), [])

    def test_staging_directory_cannot_be_replaced_during_publication(self):
        rename_stage = preflight.rename_stage

        def guarded_rename(handle, parent, name):
            staging, = self.evidence.glob(".lab-preflight-*")
            with self.assertRaises(PermissionError):
                staging.rename(self.evidence / "swapped")
            return rename_stage(handle, parent, name)

        with patch.object(preflight, "rename_stage", side_effect=guarded_rename) as rename:
            preflight.publish_reports(self.output, preflight.failed(["INVALID_JSON"]), self.evidence)
        rename.assert_called_once()
        self.assertEqual({file.name for file in self.output.iterdir()}, {"preflight.json", "preflight.md"})
        self.assertFalse((self.evidence / "swapped").exists())

    def test_concurrent_destination_is_preserved_and_staging_is_removed(self):
        rename_stage = preflight.rename_stage

        def competing_publish(handle, parent, name):
            destination = self.evidence / name
            destination.mkdir()
            (destination / "preflight.json").write_text("existing", encoding="utf-8")
            return rename_stage(handle, parent, name)

        with patch.object(preflight, "rename_stage", side_effect=competing_publish):
            with self.assertRaisesRegex(preflight.LabError, "REPORT_PUBLICATION_FAILED"):
                preflight.publish_reports(self.output, preflight.failed(["INVALID_JSON"]), self.evidence)
        self.assertEqual((self.output / "preflight.json").read_text(encoding="utf-8"), "existing")
        self.assertFalse((self.output / "preflight.md").exists())
        self.assertEqual(list(self.evidence.glob(".lab-preflight-*")), [])

    def test_rejects_output_junction(self):
        jump = self.evidence / "jump"
        subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(jump), str(self.repo)],
                       check=True, capture_output=True)
        self.invoke(1, output=jump / "bad-output")
        self.assertFalse((self.repo / "bad-output").exists())


if __name__ == "__main__":
    unittest.main()

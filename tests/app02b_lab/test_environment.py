"""LAB-I01 planning boundaries and synthetic host capability regressions."""

import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from tools.app02b_lab import environment as preflight

ROOT = Path(__file__).resolve().parents[2]
DEPLOY = ROOT / "deploy/lab/windows"
PRIVATE = "PRIVATE-INPUT-NEVER-REPORT"


def read_example(name):
    return json.loads((DEPLOY / name).read_text(encoding="utf-8"))


def observed_host():
    return {
        "hostKey": PRIVATE, "edition": "Professional", "version": "10.0.22631",
        "build": "22631", "osArch": "X64", "cpuArch": "X64",
        "cpuModel": "Intel(R) Core(TM) i7-12700H", "cores": 14, "logical": 20,
        "firmwareVirtualization": True, "slat": True, "vmMonitor": True,
        "hypervisor": True, "hyperVFeature": "Enabled", "gen2": True, "reboot": False,
        "totalRam": 64 * 2**30, "freeRam": 48 * 2**30,
        "diskCapacity": 512 * 2**30, "diskFree": 310 * 2**30, "diskMedia": "SSD",
        "networks": {"INTERFACE": ["192.168.10.1/24"], "ROUTE": ["0.0.0.0/0"],
                     "WINNAT": [], "HNS": [], "DOCKER": []},
        "switchCount": 1, "dnsCount": 1, "timeService": "Running",
        "timeSource": PRIVATE, "timeSourceKind": "EXTERNAL_CONFIGURED",
        "lastSyncUtc": "2026-11-21T01:00:00Z",
        "tools": {"git": "2.50.0", "gh": "2.89.0", "pwsh": "7.6.0",
                  "dotnet": "10.0.404", "node": "26.13.1", "npm": "11.5.0",
                  "python": "3.14.1", "openssl": "3.5.2", "ssh": "9.9.0",
                  "hyperv": "2.0.0"},
    }


class EnvironmentValidation(unittest.TestCase):
    def setUp(self):
        self.plan = read_example("environment.example.json")

    def test_example_is_valid_deterministic_and_read_only(self):
        before = copy.deepcopy(self.plan)
        self.assertEqual(preflight.validate_environment(self.plan), [])
        self.assertEqual(preflight.validate_environment(self.plan), [])
        self.assertEqual(self.plan, before)

    def test_closed_schema_and_secret_bearing_fields(self):
        self.plan["vms"]["APP"]["rootToken"] = PRIVATE
        self.plan["approval"] = PRIVATE
        self.assertEqual(preflight.validate_environment(self.plan), ["UNKNOWN_FIELD"])
        del self.plan["vms"]["APP"]["rootToken"]
        del self.plan["approval"]
        self.plan["tls"]["caRef"] = "-----BEGIN PRIVATE KEY-----"
        self.assertEqual(preflight.validate_environment(self.plan), ["INVALID_REFERENCE"])

    def test_required_fields_and_types(self):
        del self.plan["tenantRefs"]["B"]
        self.plan["vms"]["DATA"]["ramGiB"] = True
        self.assertEqual(preflight.validate_environment(self.plan),
                         ["INVALID_RESOURCE_BUDGET", "MISSING_FIELD"])

    def test_duplicate_identities_are_case_insensitive(self):
        self.plan["tenantRefs"]["B"] = self.plan["tenantRefs"]["A"].upper()
        self.plan["vms"]["DATA"]["id"] = self.plan["vms"]["APP"]["id"].upper()
        self.assertEqual(preflight.validate_environment(self.plan),
                         ["DUPLICATE_TENANT_ID", "DUPLICATE_VM_ID"])

    def test_rejects_overlaps_and_invalid_cidr(self):
        self.plan["networks"]["MANAGEMENT"] = {"cidr": "172.28.200.0/25", "gateway": "172.28.200.1"}
        self.assertIn("NETWORK_OVERLAP", preflight.validate_environment(self.plan))
        for cidr in ("172.28.200.1/24", "172.28.400.0/24", "::/64", "172.28.200.0/255.255.255.0"):
            with self.subTest(cidr=cidr):
                self.plan["networks"]["MANAGEMENT"]["cidr"] = cidr
                self.assertIn("INVALID_CIDR", preflight.validate_environment(self.plan))

    def test_gateway_must_be_a_usable_host(self):
        self.plan["networks"]["DATA"]["gateway"] = self.plan["networks"]["DATA"]["cidr"].split("/")[0]
        self.assertIn("INVALID_GATEWAY", preflight.validate_environment(self.plan))

    def test_unsafe_paths(self):
        for value in ("Evidence", r"C:\Lab\..\Evidence", r"\\host\share\lab",
                      r"\\?\C:\Lab", r"C:\Lab\CON.txt", r"C:\Lab\COM¹.txt",
                      r"C:\Lab\callback:secret", r"C:\Lab\dot.", "C:\\"):
            with self.subTest(value=value):
                self.plan["evidenceRoot"] = value
                self.assertIn("UNSAFE_PATH", preflight.validate_environment(self.plan))

    def test_link_and_git_ancestor_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary).resolve()
            repo = base / "checkout"
            repo.mkdir()
            (repo / ".git").mkdir()
            (repo / "Evidence").mkdir()
            with self.assertRaisesRegex(preflight.LabError, "GIT_LOCATION_FORBIDDEN"):
                preflight.check_location(repo / "Evidence")
            # Windows directory junctions do not require Developer Mode or elevation.
            link = base / "junction"
            if os.name == "nt":
                subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(repo)],
                               check=True, capture_output=True)
            else:
                link.symlink_to(repo, target_is_directory=True)
            with self.assertRaisesRegex(preflight.LabError, "LINK_OR_REPARSE_POINT"):
                preflight.check_location(link / "Evidence")


class Readiness(unittest.TestCase):
    def setUp(self):
        self.plan = read_example("environment.example.json")
        self.lock = read_example("toolchain.lock.json")
        self.host = observed_host()
        self.plan["operatorHostRef"] = "ref-33e52d58f1e0ff25"

    def report(self):
        result = preflight.evaluate(self.plan, self.lock, self.host)
        return result, {check["id"]: check for check in result["checks"]}

    def test_ready_read_only_and_redacted(self):
        before = copy.deepcopy((self.plan, self.lock, self.host))
        first, rows = self.report()
        second, _ = self.report()
        self.assertEqual(first, second)
        self.assertEqual((self.plan, self.lock, self.host), before)
        self.assertEqual(first["readiness"], "READY_FOR_LAB_SETUP")
        self.assertEqual(rows["TOOL_DOTNET"]["status"], "PASS")
        self.assertEqual(rows["TOOL_DOCKER"]["status"], "NOT_APPLICABLE")
        for output in (json.dumps(first), preflight.markdown(first)):
            for sensitive in (PRIVATE, "192.168.10.1", self.plan["tenantRefs"]["A"],
                              self.plan["evidenceRoot"], self.plan["networks"]["APP"]["cidr"]):
                self.assertNotIn(sensitive, output)

    def test_configured_host_must_match_observed_host(self):
        self.plan["operatorHostRef"] = "ref-1000000000000001"
        first, rows = self.report()
        self.assertEqual(first["readiness"], "BLOCKED")
        self.assertEqual(rows["HOST_TARGET"]["reasonCode"], "HOST_REFERENCE_MISMATCH")
        self.assertNotIn(self.plan["operatorHostRef"], json.dumps(first))

    def test_unsupported_architecture_edition_and_unknown_virtualization(self):
        self.host.update(cpuArch="Arm64", edition="Core", slat=None)
        result, rows = self.report()
        self.assertEqual(result["readiness"], "BLOCKED")
        self.assertEqual(rows["CPU_ARCH"]["reasonCode"], "UNSUPPORTED_CPU_ARCHITECTURE")
        self.assertEqual(rows["WINDOWS_EDITION"]["reasonCode"], "UNSUPPORTED_WINDOWS_EDITION")
        self.assertEqual(rows["HYPERV_SLAT"]["observed"], "UNKNOWN")
        self.assertEqual(rows["HYPERV_SLAT"]["status"], "BLOCKED")

    def test_missing_tools_and_version_boundaries(self):
        self.host["tools"].update(openssl=None, dotnet="10.0.500", node="26.13.0", python="3.13.9")
        _, rows = self.report()
        self.assertEqual(rows["TOOL_OPENSSL"]["reasonCode"], "MISSING_TOOL")
        for name in ("DOTNET", "NODE", "PYTHON"):
            self.assertEqual(rows[f"TOOL_{name}"]["reasonCode"], "VERSION_MISMATCH")
        self.host["tools"].update(dotnet="10.0.405", node="26.99.0", python="3.14.4")
        _, rows = self.report()
        for name in ("DOTNET", "NODE", "PYTHON"):
            self.assertEqual(rows[f"TOOL_{name}"]["status"], "PASS")

    def test_lock_cannot_inject_report_text_or_replace_version_constraints(self):
        self.lock["tools"]["node"]["requiredVersion"] = PRIVATE
        with self.assertRaisesRegex(preflight.LabError, "INVALID_TOOLCHAIN_LOCK"):
            self.report()

    def test_short_openssh_version_and_optional_prerelease_disclosure(self):
        self.host["tools"].update(ssh="9.5p1", node="26.13.1-beta.1")
        _, rows = self.report()
        self.assertEqual(rows["TOOL_SSH"]["status"], "PASS")
        self.assertEqual(rows["TOOL_NODE"]["status"], "BLOCKED")

    def test_unpinned_artifacts_and_signature_unavailability(self):
        _, rows = self.report()
        self.assertEqual(rows["ARTIFACT_DOTNET"]["reasonCode"], "NOT_PINNED")
        self.assertEqual(rows["SIGNATURE_DOTNET"]["reasonCode"], "SIGNATURE_VERIFICATION_UNAVAILABLE")
        self.lock["tools"]["dotnet"].update(pinState="PINNED", sha256="g" * 64)
        with self.assertRaisesRegex(preflight.LabError, "INVALID_TOOLCHAIN_LOCK"):
            self.report()

    def test_resources_and_live_network_conflicts(self):
        self.host.update(freeRam=4 * 2**30, diskFree=12 * 2**30)
        self.host["networks"]["WINNAT"] = ["172.28.200.128/25"]
        _, rows = self.report()
        self.assertEqual(rows["RAM_AVAILABLE"]["reasonCode"], "INSUFFICIENT_RAM")
        self.assertEqual(rows["DISK_FREE"]["reasonCode"], "INSUFFICIENT_DISK")
        self.assertEqual(rows["NETWORK_OVERLAP"]["observed"], ["APP/WINNAT"])
        self.assertEqual(rows["NETWORK_OVERLAP"]["status"], "BLOCKED")

    def test_unknown_network_inventory_blocks_and_default_route_is_ignored(self):
        self.host["networks"]["HNS"] = None
        _, rows = self.report()
        self.assertEqual(rows["NETWORK_HNS"]["status"], "BLOCKED")
        self.assertEqual(rows["NETWORK_OVERLAP"]["status"], "NOT_TESTED")
        self.host["networks"]["HNS"] = []
        _, rows = self.report()
        self.assertEqual(rows["NETWORK_OVERLAP"]["status"], "PASS")

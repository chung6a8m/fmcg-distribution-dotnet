import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import subprocess

from tools.app02b_lab import docker_preflight as preflight
from tools.app02b_lab.contract import LabError


ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40


def fixture():
    plan = json.loads((ROOT / "deploy/lab/docker-wsl2/host-plan.example.json").read_text())
    plan["sourceHead"] = SHA
    for image in plan["images"]:
        image["reference"] = image["reference"].split("@")[0] + "@sha256:" + "1" * 64
        image["publisherReceiptSha256"] = "2" * 64
    host = {
        "machine": "private host identity", "architecture": "X64", "build": 26100,
        "freeRam": 8 * 2**30, "totalRam": 16 * 2**30, "diskFree": 120 * 2**30,
        "diskRef": "private data disk identity", "diskCapacity": 500 * 2**30,
        "dns": 2, "ports": [], "excluded": [], "routes": [],
        "pwsh": "7.5.0", "desktop": "4.50.0", "git": "2.49.0", "python": "3.14.1",
    }
    engine = {
        "ID": "private daemon identity", "OSType": "linux", "Architecture": "x86_64",
        "OperatingSystem": "Docker Desktop", "KernelVersion": "6.6.87.2-microsoft-standard-WSL2",
        "MemTotal": 8 * 2**30, "NCPU": 4, "Driver": "overlayfs",
        "DockerRootDir": "/var/lib/docker", "ServerVersion": "29.1.0",
    }
    host["wsl"] = "2.6.1.0"
    host["wslMode"] = 2
    host["dockerCli"] = "29.1.0"
    host["compose"] = "2.40.3"
    host["clockSkewSeconds"] = 2
    host["context"] = preflight.CONTEXT
    host["endpoint"] = preflight.ENDPOINT
    host["engine"] = engine
    host["containers"] = []
    host["projects"] = []
    host["networks"] = []
    host["volumes"] = []
    host["images"] = {
        image["role"]: {"Os": "linux", "Architecture": "amd64", "RepoDigests": [image["reference"]]}
        for image in plan["images"]
    }
    plan["hostRef"], plan["engineRef"] = preflight.identity_refs(host)
    return plan, host


def resume_fixture():
    plan, host = fixture()
    name = plan["project"] + "_app"
    subnet = next(row["subnet"] for row in plan["networks"] if row["name"] == name)
    network_id, nonce = "3" * 64, "4" * 32
    plan["resumeD02"] = {"appNetworkId": network_id, "nonce": nonce}
    host["networks"] = [{"id": network_id, "name": name, "subnets": [subnet]}]
    host["resumeAppNetwork"] = {
        "Id": network_id, "Name": name, "Driver": "bridge", "Scope": "local", "Internal": True,
        "EnableIPv6": False, "Options": {"com.docker.network.enable_ipv4": "true",
                                        "com.docker.network.enable_ipv6": "false"},
        "IPAM": {"Driver": "default", "Config": [{"Subnet": subnet}]},
        "Labels": {"io.fmcg-dms.lab-d02.project": plan["project"], "io.fmcg-dms.lab-d02.nonce": nonce},
        "Containers": {},
    }
    return plan, host


class DockerPreflightTests(unittest.TestCase):
    def test_ready_report_is_versioned_and_redacted(self):
        plan, host = fixture()
        preflight.validate_plan(plan)
        report = preflight.evaluate(plan, host, (SHA, "CLEAN"), "b" * 64)
        self.assertEqual("READY", report["readiness"], report)
        preflight.validate_document(report, "docker-host-preflight")
        rendered = json.dumps(report) + preflight.markdown(report)
        for value in ("private host identity", "private daemon identity", "private data disk",
                      preflight.ENDPOINT, plan["evidenceRoot"], plan["networks"][0]["subnet"]):
            self.assertNotIn(value, rendered)

    def test_each_required_negative_blocks(self):
        cases = (
            ("DOCKER_DAEMON", lambda h: h.update(engine=None)),
            ("WSL2_BACKEND", lambda h: h.update(wslMode=1)),
            ("LINUX_ENGINE", lambda h: h["engine"].update(OSType="windows")),
            ("LOCAL_CONTEXT", lambda h: h.update(endpoint="tcp://192.0.2.5:2375")),
            ("LOCAL_CONTEXT", lambda h: h.update(context="unexpected")),
            ("SQL_X64", lambda h: h.update(architecture="Arm64")),
            ("SQL_X64", lambda h: h["engine"].update(Architecture="aarch64")),
            ("HOST_MEMORY", lambda h: h.update(freeRam=1)),
            ("WSL_MEMORY", lambda h: h["engine"].pop("MemTotal")),
            ("WINDOWS_BACKING_STORAGE", lambda h: h.update(diskFree=None)),
            ("WINDOWS_BACKING_STORAGE", lambda h: h.update(diskRef=None)),
            ("WINDOWS_BACKING_STORAGE", lambda h: h.update(diskCapacity=1)),
            ("WINDOWS_BACKING_STORAGE", lambda h: h["engine"].update(DockerRootDir="/unknown")),
            ("PORTS", lambda h: h.update(ports=[18443])),
            ("PORTS", lambda h: h.update(excluded=[[18400, 18500]])),
            ("NETWORKS", lambda h: h.update(routes=["172.29.40.0/23"])),
            ("PROJECT", lambda h: h.update(containers=[{"project": "app02b-docker-lab", "ports": [], "binds": 0}])),
            ("VOLUMES", lambda h: h.update(volumes=["app02b-docker-lab_sql"])),
            ("IMAGE_SQL", lambda h: h["images"].pop("SQL")),
            ("IMAGE_SQL", lambda h: h["images"]["SQL"].update(RepoDigests=[])),
            ("ENGINE_IDENTITY", lambda h: h["engine"].update(ID="different")),
        )
        for check, change in cases:
            with self.subTest(check=check):
                plan, host = fixture()
                change(host)
                report = preflight.evaluate(plan, host, (SHA, "CLEAN"), "b" * 64)
                self.assertEqual("BLOCKED", report["readiness"])
                self.assertNotEqual("PASS", report["checks"][check])
                preflight.validate_document(report, "docker-host-preflight")

    def test_resumes_only_the_pinned_owned_app_bridge_without_disclosing_identity(self):
        plan, host = resume_fixture()
        preflight.validate_plan(plan)
        report = preflight.evaluate(plan, host, (SHA, "CLEAN"), "b" * 64)
        self.assertEqual("READY", report["readiness"])
        self.assertEqual("NOT_EVALUATED", report["authorization"])
        self.assertEqual(1, report["observed"]["networkCount"])
        preflight.validate_document(report, "docker-host-preflight")
        serialized = json.dumps(report) + preflight.markdown(report)
        self.assertNotIn(plan["resumeD02"]["appNetworkId"], serialized)
        self.assertNotIn(plan["resumeD02"]["nonce"], serialized)

    def test_resume_rejects_missing_replaced_foreign_or_drifted_networks(self):
        def set_detail(key, value):
            return lambda host: host["resumeAppNetwork"].update({key: value})

        cases = {
            "absent": lambda h: h.update(networks=[]),
            "replacement-id": lambda h: h["networks"][0].update(id="5"*64),
            "wrong-inspected-id": set_detail("Id", "5"*64),
            "wrong-project": lambda h: h["resumeAppNetwork"]["Labels"].update(
                {"io.fmcg-dms.lab-d02.project": "other-project"}),
            "wrong-nonce": lambda h: h["resumeAppNetwork"]["Labels"].update(
                {"io.fmcg-dms.lab-d02.nonce": "5"*32}),
            "wrong-subnet": lambda h: h["resumeAppNetwork"]["IPAM"]["Config"][0].update(
                Subnet="172.29.80.0/24"),
            "public": set_detail("Internal", False),
            "driver": set_detail("Driver", "overlay"),
            "ipv6": set_detail("EnableIPv6", True),
            "ipv4-disabled": lambda h: h["resumeAppNetwork"].update(EnableIPv4=False, Options={}),
            "unexpected-option": set_detail("Options", {"com.docker.network.bridge.enable_icc": "false"}),
            "endpoint": set_detail("Containers", {"foreign": {"Name": "foreign"}}),
            "other-overlap": lambda h: h["networks"].append(
                {"name": "foreign", "subnets": h["networks"][0]["subnets"]}),
            "duplicate-name": lambda h: h["networks"].append(h["networks"][0].copy()),
            "windows-route": lambda h: h.update(routes=h["networks"][0]["subnets"]),
        }
        for name, alter in cases.items():
            with self.subTest(name=name):
                plan, host = resume_fixture()
                alter(host)
                report = preflight.evaluate(plan, host, (SHA, "CLEAN"), "b"*64)
                self.assertEqual("FAIL", report["checks"]["NETWORKS"])
                self.assertEqual("BLOCKED", report["readiness"])

    def test_fresh_plan_rejects_even_an_owned_app_bridge(self):
        plan, host = resume_fixture()
        del plan["resumeD02"]
        self.assertIs(preflight.conflicts(plan, host)["NETWORKS"], False)

    def test_source_must_match_clean_plan(self):
        plan, host = fixture()
        for source in ((SHA, "DIRTY"), ("c" * 40, "CLEAN"), (None, "UNKNOWN")):
            report = preflight.evaluate(plan, host, source, "b" * 64)
            self.assertEqual("BLOCKED", report["readiness"])
            self.assertNotEqual("PASS", report["checks"]["SOURCE"])

    def test_plan_rejects_unpinned_unknown_images_and_secret_fields(self):
        for change in (
            lambda p: p.update(password="credential"),
            lambda p: p["images"][0].update(reference="mcr.microsoft.com/mssql/server:latest"),
            lambda p: p["images"][0].update(reference="foreign.example/mssql/server@sha256:" + "b" * 64),
            lambda p: p["networks"][0].update(subnet="192.0.2.0/24"),
            lambda p: p["ports"].append(18443),
        ):
            plan, _ = fixture()
            change(plan)
            with self.assertRaises(LabError):
                preflight.validate_plan(plan)

    def test_doctor_refuses_target_overrides_before_invoking_docker(self):
        for name in ("DOCKER_HOST", "DOCKER_CONTEXT", "Docker_Config", "DOCKER_DEFAULT_PLATFORM", "COMPOSE_ENV_FILES"):
            with self.subTest(name=name), patch.dict(os.environ, {name: "private"}):
                with patch.object(preflight, "command") as command:
                    self.assertEqual("DOCKER_ENV_OVERRIDE", preflight.docker_inventory(Path("docker"), {}))
                    command.assert_not_called()

    def test_remote_endpoint_is_never_contacted(self):
        with patch.object(preflight, "command", side_effect=[
            b"desktop-linux\n", b'"tcp://192.0.2.5:2375"\n',
        ]) as command:
            host = {}
            self.assertEqual("UNSAFE_CONTEXT", preflight.docker_inventory(Path("docker"), host))
            self.assertEqual(2, command.call_count)

    def test_formatted_info_surfaces_api_errors_even_with_successful_exit(self):
        responses = [b"desktop-linux", json.dumps(preflight.ENDPOINT).encode(),
                     b'{"ServerErrors":["unexpected daemon API failure"]}']
        with patch.object(preflight, "command", side_effect=responses):
            with self.assertRaisesRegex(LabError, "^COLLECTION_ERROR$"):
                preflight.docker_inventory(Path("docker"), {})

    def test_malformed_json_is_collection_error(self):
        for data in (b"{bad", b'{"a":1,"a":2}', b"[]"):
            with self.assertRaises(LabError):
                preflight.json_result(data, dict)

    def test_classifies_missing_daemon_and_permissions_at_native_boundary(self):
        cases = (
            ("daemon", b"The system cannot find the file specified", None),
            ("daemon", b"Access is denied: private pipe", "HOST_ACCESS_DENIED"),
            ("image", b"Error response from daemon: No such image: private reference", None),
            ("image", b"unexpected engine API response", "COLLECTION_ERROR"),
        )
        for discovery, stderr, failure in cases:
            result = subprocess.CompletedProcess([], 1, b"", stderr)
            with self.subTest(discovery=discovery, failure=failure), patch.object(
                    preflight.subprocess, "run", return_value=result):
                if failure:
                    with self.assertRaisesRegex(LabError, "^" + failure + "$"):
                        preflight.command(["docker"], missing=discovery)
                else:
                    self.assertIsNone(preflight.command(["docker"], missing=discovery))

    def test_inventory_with_swarm_id_and_configured_port_range(self):
        network_id = "zzj6nj4cr3y5387js5rjo4hf8"
        container_id = "a" * 64
        rows = iter([
            b"", b"", container_id.encode(),
            b'{"Name":"/foreign","Project":"","Bindings":{"8080/tcp":[{"HostPort":"18440-18450"}]},'
            b'"LivePorts":null,"Mounts":[]}',
            network_id.encode(),
            b'{"Name":"foreign","Subnets":[{"Subnet":"172.30.20.0/24"},{"Subnet":"fc00::/64"}]}',
        ])
        plan, host = fixture()
        preflight.collect_objects(lambda *args: next(rows), host)
        self.assertEqual([(18440, 18450)], host["containers"][0]["ports"])
        self.assertEqual(["172.30.20.0/24"], host["networks"][0]["subnets"])
        self.assertFalse(preflight.conflicts(plan, host)["PORTS"])

    def test_git_evidence_path_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            (path / ".git").mkdir()
            with self.assertRaisesRegex(LabError, "GIT_LOCATION_FORBIDDEN"):
                preflight.check_location(path / "new-evidence", missing=True)

    def test_daemon_queries_are_pinned_to_local_pipe_and_read_only(self):
        plan, host = fixture()
        calls = [
            b"desktop-linux", json.dumps(preflight.ENDPOINT).encode(),
            json.dumps(host["engine"]).encode(), b"Docker version 29.1.0", b"v2.40.3",
            b"", b"", b"", b"",
            *[json.dumps(host["images"][image["role"]]).encode() for image in plan["images"]],
            json.dumps(host["engine"]["ID"]).encode(), b"desktop-linux", json.dumps(preflight.ENDPOINT).encode(),
        ]
        with patch.object(preflight, "command", side_effect=calls) as run:
            self.assertIsNone(preflight.docker_inventory(Path("docker"), host, plan))
            server_calls = [call.args[0][3:] for call in run.call_args_list if call.args[0][1] == "--host"]
            self.assertTrue(all(call.args[0][2] == preflight.ENDPOINT for call in run.call_args_list
                                if call.args[0][1] == "--host"))
            self.assertEqual(["info", "compose", "compose", "volume", "container", "network",
                              "image", "image", "image", "image", "info"],
                             [args[0] for args in server_calls])
            self.assertTrue(all(args[1] == "inspect" for args in server_calls if args[0] == "image"))


@unittest.skipUnless(os.name == "nt", "Windows publication and reparse point checks")
class WindowsDockerPreflightTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="lab-d01-")
        self.addCleanup(temp.cleanup)
        # Model canonical Windows known-folder paths even when TEMP uses an alias.
        self.root = Path(temp.name).resolve(strict=True)
        self.config = self.root / "plan.json"
        self.evidence = self.root / "evidence"
        self.evidence.mkdir()
        self.output = self.evidence / "attempt"
        self.plan, self.host = fixture()
        self.plan["evidenceRoot"] = str(self.evidence)
        self.receipts = self.root / "receipts"
        self.receipts.mkdir()
        for image in self.plan["images"]:
            image["publisherReceiptSha256"] = self.write_receipt(image["reference"])
        self.config.write_text(json.dumps(self.plan), encoding="utf-8")

    def write_receipt(self, reference):
        receipt = preflight.canonical_json({"schema": "APP02B-LAB-DOCKER-PUBLISHER-RECEIPT-V1",
                                            "reference": reference, "platform": "linux/amd64",
                                            "proofSha256": "3" * 64})
        sha = preflight.digest(receipt)
        (self.receipts / f"{sha}.json").write_bytes(receipt)
        return sha

    def run_preflight(self, issue=None, error=None):
        def collect(pwsh, plan, host, folders):
            if error is not None:
                raise error
            host.update(self.host)
            return issue

        with (patch.object(preflight, "source_info", return_value=(SHA, "CLEAN")),
              patch.object(preflight, "collect_inventory", side_effect=collect)):
            return preflight.run(str(self.config), str(self.output), "pwsh")

    def test_publishes_three_files_atomically_and_refuses_overwrite(self):
        self.assertEqual(0, self.run_preflight())
        journal = self.output / "SHA256SUMS"
        hashes = journal.read_text(encoding="utf-8").splitlines()
        self.assertEqual(2, len(hashes))
        for line in hashes:
            sha, name = line.split("  ")
            self.assertEqual(sha, preflight.digest((self.output / name).read_bytes()))
        with self.assertRaisesRegex(LabError, "OUTPUT_ALREADY_EXISTS"):
            self.run_preflight()
        self.assertEqual(hashes, journal.read_text(encoding="utf-8").splitlines())

    def test_missing_privileges_preserves_redacted_collection_error_receipt(self):
        self.assertEqual(1, self.run_preflight(error=PermissionError("private path")))
        report = (self.output / "docker-preflight.json").read_text(encoding="utf-8")
        self.assertNotIn("private path", report)
        self.assertEqual(["HOST_ACCESS_DENIED"], json.loads(report)["issueCodes"])

    def test_refuses_missing_mismatched_and_unrelated_publisher_receipts(self):
        image = self.plan["images"][0]
        receipt = self.receipts / (image["publisherReceiptSha256"] + ".json")
        receipt.unlink()
        with self.assertRaisesRegex(LabError, "PATH_MISSING"):
            self.run_preflight()
        receipt.write_bytes(b"tampered")
        with self.assertRaisesRegex(LabError, "PUBLISHER_RECEIPT_DIGEST_MISMATCH"):
            self.run_preflight()
        image["publisherReceiptSha256"] = self.write_receipt("unrelated")
        self.config.write_text(json.dumps(self.plan), encoding="utf-8")
        with self.assertRaisesRegex(LabError, "INVALID_PUBLISHER_RECEIPT"):
            self.run_preflight()
        self.assertFalse(self.output.exists())

    def test_blocked_measurements_use_exit_two(self):
        self.host["engine"] = None
        self.assertEqual(2, self.run_preflight(issue="DAEMON_UNAVAILABLE"))

    def test_wrapper_rejects_malformed_plan_without_publishing(self):
        self.config.write_bytes(b'{"secret":"private path"')
        result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File",
                                 ROOT / "scripts/lab/Test-DockerWslPrerequisites.ps1",
                                 "-ConfigFile", self.config, "-OutputDirectory", self.output],
                                capture_output=True, timeout=30)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn(b"INVALID_JSON", result.stderr)
        self.assertNotIn(b"private path", result.stderr)
        self.assertFalse(self.output.exists())

    def test_output_under_junction_is_refused(self):
        junction = self.root / "junction"
        with patch.dict(os.environ, {"LAB_D01_TARGET": str(self.evidence), "LAB_D01_LINK": str(junction)}):
            subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command",
                            "New-Item -ItemType Junction -Path $env:LAB_D01_LINK -Target $env:LAB_D01_TARGET"],
                           capture_output=True, check=True, timeout=30)
        self.addCleanup(junction.rmdir)
        self.output = junction / "attempt"
        with self.assertRaisesRegex(LabError, "LINK_OR_REPARSE_POINT"):
            self.run_preflight()
        self.assertFalse((self.evidence / "attempt").exists())

    def test_desktop_frontend_and_launcher_use_the_same_signed_package(self):
        folders = {"ProgramFiles": self.root / "ProgramFiles", "ProgramData": self.root / "ProgramData",
                   "LOCALAPPDATA": self.root / "local", "USERPROFILE": self.root / "user",
                   "SystemDirectory": self.root / "system"}
        install = folders["ProgramFiles"] / "Docker/Docker"
        desktop = install / "Docker Desktop.exe"
        frontend = install / "frontend/Docker Desktop.exe"
        cli = install / "resources/bin/docker.exe"
        for path in (desktop, frontend, cli):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.touch()
        inventory = {"desktopPaths": [str(frontend), str(desktop)]}
        with (patch.object(preflight, "command", side_effect=[b"git version 2.50.0", json.dumps(inventory).encode()]),
              patch.object(preflight, "collect_wsl"),
              patch.object(preflight, "verify_signatures", return_value="4.98.0") as signatures,
              patch.object(preflight, "docker_inventory", return_value=None) as docker):
            host = {}
            plan, _ = fixture()
            self.assertIsNone(preflight.collect_inventory("pwsh", plan, host, folders))
            self.assertEqual("4.98.0", host["desktop"])
            signatures.assert_called_once_with("pwsh", [desktop, frontend, cli], "Docker Inc",
                                               read_desktop_version=True)
            docker.assert_called_once_with(cli, host, plan)

    def test_desktop_refuses_a_second_install_before_executing_cli(self):
        folders = {"ProgramFiles": self.root / "ProgramFiles", "ProgramData": self.root / "ProgramData",
                   "LOCALAPPDATA": self.root / "local", "USERPROFILE": self.root / "user",
                   "SystemDirectory": self.root / "system"}
        installs = [folders["ProgramFiles"] / "Docker/Docker", folders["LOCALAPPDATA"] / "Programs/DockerDesktop"]
        desktops = []
        for install in installs:
            cli = install / "resources/bin/docker.exe"
            cli.parent.mkdir(parents=True)
            cli.touch()
            desktop = install / "Docker Desktop.exe"
            desktop.touch()
            desktops.append(str(desktop))
        inventory = {"desktopPaths": desktops}
        with (patch.object(preflight, "command", side_effect=[b"git version 2.50.0", json.dumps(inventory).encode()]),
              patch.object(preflight, "collect_wsl"),
              patch.object(preflight, "docker_inventory") as docker):
            self.assertEqual("UNSUPPORTED_DESKTOP_LAYOUT", preflight.collect_inventory("pwsh", {}, {}, folders))
            docker.assert_not_called()

    def test_storage_resolves_one_configured_candidate_with_stale_setting(self):
        config = self.root / "Docker/settings-store.json"
        config.parent.mkdir()
        candidate = self.root / "active/disk/docker_data.vhdx"
        other = self.root / "other/disk/docker_data.vhdx"
        candidate.parent.mkdir(parents=True)
        candidate.touch()
        config.write_text(json.dumps({"CustomWslDistroDir": str(candidate.parents[1]),
                                      "DataFolder": str(other.parents[1])}), encoding="utf-8")
        folders = {"APPDATA": self.root, "LOCALAPPDATA": self.root}
        with patch.object(preflight, "command", return_value=b'{"attached":false,"capacity":512}') as query:
            observed = {}
            preflight.storage("pwsh", folders, observed)
            self.assertEqual({}, observed)
            query.assert_called_once()
            probed = Path(query.call_args.kwargs["input_data"].decode().strip())
            self.assertTrue(probed.samefile(candidate))
        with patch.object(preflight, "command", return_value=b'{"attached":true,"capacity":512}'):
            observed = {}
            preflight.storage("pwsh", folders, observed)
            self.assertEqual(512, observed["diskCapacity"])
        other.parent.mkdir(parents=True)
        other.touch()
        with patch.object(preflight, "command") as query:
            observed = {}
            preflight.storage("pwsh", folders, observed)
            self.assertEqual({}, observed)
            query.assert_not_called()

    def test_storage_leaves_unknown_when_a_configured_volume_is_unavailable(self):
        config = self.root / "Docker/settings-store.json"
        config.parent.mkdir()
        config.write_text(json.dumps({"CustomWslDistroDir": str(self.root / "offhost")}), encoding="utf-8")
        folders = {"APPDATA": self.root, "LOCALAPPDATA": self.root}
        with (patch.object(preflight, "require_fixed_volume", side_effect=LabError("NONLOCAL_VOLUME")),
              patch.object(preflight, "command") as query):
            observed = {}
            preflight.storage("pwsh", folders, observed)
            self.assertEqual({}, observed)
            query.assert_not_called()

    def test_detached_windows_view_requires_matching_live_wsl_vhd_evidence(self):
        settings = self.root / "Docker/settings-store.json"
        settings.parent.mkdir()
        settings.write_text("{}", encoding="utf-8")
        disk = self.root / "Docker/wsl/disk/docker_data.vhdx"
        disk.parent.mkdir(parents=True)
        disk.touch()
        folders = {"APPDATA": self.root, "LOCALAPPDATA": self.root}
        add = {"controller": 0, "lun": 3, "request": "Add", "matchesVhd": True}
        proof = {"stableVm": True, "guestLun": 3, "guestCapacity": 512, "changes": [add]}
        cases = {
            "exact live VM and LUN": (proof, "HCS_WSL"),
            "VM replaced": (dict(proof, stableVm=False), None),
            "unrelated guest LUN": (dict(proof, guestLun=4), None),
            "capacity mismatch": (dict(proof, guestCapacity=513), None),
            "different VHD": (dict(proof, changes=[dict(add, matchesVhd=False)]), None),
            "later removal": (dict(proof, changes=[dict(add, request="Remove"), add]), None),
            "later replacement": (dict(proof, changes=[dict(add, matchesVhd=False), add]), None),
            "ambiguous controller": (dict(proof, changes=[add, dict(add, controller=1)]), None),
        }
        for case, (attachment, method) in cases.items():
            with self.subTest(case=case):
                response = {"attached": False, "capacity": 512, "wslAttachment": attachment}
                with patch.object(preflight, "command", return_value=json.dumps(response).encode()):
                    observed = {}
                    preflight.storage("pwsh", folders, observed)
                self.assertEqual(method, observed.get("diskAttachmentMethod"))
                self.assertEqual(512 if method else None, observed.get("diskCapacity"))
                if method:
                    plan, host = fixture()
                    host.update(observed)
                    report = preflight.evaluate(plan, host, (SHA, "CLEAN"), "b" * 64)
                    preflight.validate_document(report, "docker-host-preflight")
                    self.assertEqual("HCS_WSL", report["observed"]["storageAttachmentMethod"])
                    self.assertIn("HCS_WSL", preflight.markdown(report))

    def test_storage_uses_configured_attached_vhd_without_stale_default_fallback(self):
        config_dir = self.root / "Docker"
        config_dir.mkdir()
        settings = config_dir / "settings-store.json"
        moved = self.root / "moved"
        stale = config_dir / "wsl/disk/docker_data.vhdx"
        disk = moved / "disk/docker_data.vhdx"
        for path in (stale, disk):
            path.parent.mkdir(parents=True)
            path.write_bytes(b"synthetic")
        settings.write_text(json.dumps({"CustomWslDistroDir": str(moved)}), encoding="utf-8")
        folders = {"APPDATA": self.root, "LOCALAPPDATA": self.root}
        with patch.object(preflight, "command", return_value=b'{"attached":false,"capacity":512}') as query:
            observed = {}
            preflight.storage("pwsh", folders, observed)
            self.assertEqual({}, observed)
            probed = Path(query.call_args.kwargs["input_data"].decode().strip())
            self.assertTrue(probed.samefile(disk))
        with patch.object(preflight, "command", return_value=b'{"attached":true,"capacity":512}'):
            preflight.storage("pwsh", folders, observed)
            self.assertEqual(str(disk.resolve()).casefold(), observed["diskRef"])
            self.assertEqual(512, observed["diskCapacity"])
        with patch.object(preflight, "command", return_value=None):
            with self.assertRaisesRegex(LabError, "^COLLECTION_ERROR$"):
                preflight.storage("pwsh", folders, {})
        disk.unlink()
        with patch.object(preflight, "command") as query:
            preflight.storage("pwsh", folders, {})
            query.assert_not_called()

    def test_plugin_discovery_requires_packaged_or_system_managed_plugin(self):
        install = self.root / "DockerDesktop"
        folders = {"ProgramFiles": self.root / "ProgramFiles", "ProgramData": self.root / "ProgramData",
                   "USERPROFILE": self.root / "user"}
        system = folders["ProgramData"] / "Docker/cli-plugins"
        system.mkdir(parents=True)
        packaged = system / "docker-compose.exe"
        packaged.touch()
        user = folders["USERPROFILE"] / ".docker/cli-plugins"
        user.mkdir(parents=True)
        self.assertEqual([packaged.resolve()], preflight.docker_plugin_paths(install, folders))
        (user / "docker-compose.exe").touch()
        with self.assertRaisesRegex(LabError, "^UNTRUSTED_PLUGIN_CONFIGURATION$"):
            preflight.docker_plugin_paths(install, folders)
        (user / "docker-compose.exe").unlink()
        extra = self.root / "unexpected"
        extra.mkdir()
        (extra / "docker-compose.exe").touch()
        (user.parent / "config.json").write_text(json.dumps({"cliPluginsExtraDirs": [str(extra)]}))
        with self.assertRaisesRegex(LabError, "^UNTRUSTED_PLUGIN_CONFIGURATION$"):
            preflight.docker_plugin_paths(install, folders)

    def test_spoofed_windows_roots_refuse_execution_before_native_collection(self):
        fake_root = self.root / "spoof"
        fake_plugins = fake_root / "Docker/cli-plugins"
        fake_plugins.mkdir(parents=True)
        (fake_plugins / "docker-compose.exe").write_bytes(b"unexpected synthetic executable")
        fake_os = fake_root / "System32"
        fake_os.mkdir()
        (fake_os / "wsl.exe").write_bytes(b"unexpected synthetic executable")
        (fake_os / "netsh.exe").write_bytes(b"unexpected synthetic executable")
        invalid_config = self.root / "invalid.json"
        invalid_config.write_text("intentionally invalid", encoding="utf-8")
        for variable in ("ProgramFiles", "ProgramData", "SystemRoot"):
            with self.subTest(variable=variable), patch.dict(os.environ, {variable: str(fake_root)}):
                with (patch.object(preflight, "source_info") as source,
                      patch.object(preflight, "command") as native):
                    with self.assertRaisesRegex(LabError, "^WINDOWS_ROOT_ENV_OVERRIDE$"):
                        preflight.run(str(self.config), str(self.output), "pwsh")
                    source.assert_not_called()
                    native.assert_not_called()
                # PowerShell startup can reconstruct ProgramFiles. Spoof it in
                # the invoking shell after startup to exercise the wrapper guard.
                env = {**os.environ, "LAB_D01_SPOOF": variable, "LAB_D01_FAKE_ROOT": str(fake_root),
                       "LAB_D01_CONFIG": str(invalid_config), "LAB_D01_OUTPUT": str(self.output)}
                result = subprocess.run(
                    ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
                     "[Environment]::SetEnvironmentVariable($env:LAB_D01_SPOOF, $env:LAB_D01_FAKE_ROOT); "
                     "& ./scripts/lab/Test-DockerWslPrerequisites.ps1 -ConfigFile $env:LAB_D01_CONFIG "
                     "-OutputDirectory $env:LAB_D01_OUTPUT"],
                    cwd=ROOT, env=env, capture_output=True, timeout=30,
                )
                self.assertEqual(1, result.returncode)
                self.assertIn(b"WINDOWS_ROOT_ENV_OVERRIDE", result.stderr)
                self.assertFalse(self.output.exists())

    def test_authenticode_requires_a_valid_signature_from_the_expected_publisher(self):
        folders = preflight.verified_windows_folders()
        netsh = folders["SystemDirectory"] / "netsh.exe"
        preflight.verify_signatures("pwsh", [netsh], "Microsoft Corporation")
        with self.assertRaisesRegex(LabError, "^UNTRUSTED_NATIVE_EXECUTABLE$"):
            preflight.verify_signatures("pwsh", [netsh], "Docker Inc")
        fake_compose = self.root / "docker-compose.exe"
        fake_compose.write_bytes(netsh.read_bytes() + b"tampered fixture")
        with self.assertRaisesRegex(LabError, "^UNTRUSTED_NATIVE_EXECUTABLE$"):
            preflight.verify_signatures("pwsh", [fake_compose], "Microsoft Corporation")


if __name__ == "__main__":
    unittest.main()

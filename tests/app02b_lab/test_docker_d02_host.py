import json
import os
import subprocess
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

from tools.app02b_lab import docker_d02_host as target
from tools.app02b_lab import docker_preflight as d01
from tools.app02b_lab.contract import LabError, digest, canonical_json


PROJECT = "app02b-docker-lab"
HOST_REF = "a" * 64
DAEMON_ID = "desktop-fixture"
ENGINE_REF = digest(canonical_json(["LAB-D01-ENGINE-V1", HOST_REF, d01.CONTEXT, d01.ENDPOINT, DAEMON_ID]))
SOURCE = "b" * 40
IMAGES = {
    "api": "example.test/lab-api@sha256:" + "c" * 64,
    "worker": "example.test/lab-worker@sha256:" + "d" * 64,
}
IDENTITY = {"ID": DAEMON_ID, "OSType": "linux", "OperatingSystem": "Docker Desktop",
            "KernelVersion": "6.6.87.2-microsoft-standard-WSL2", "ServerErrors": None}
CLI = Path(r"C:\Program Files\Docker\Docker\resources\bin\docker.exe")


def reply(value):
    return json.dumps(value).encode("utf-8")


def docker_stub():
    return target.Docker(CLI, HOST_REF, ENGINE_REF)


def image(role, **config):
    entry = "FmcgDms.Preprod.Api.dll" if role == "api" else "FmcgDms.Preprod.Worker.dll"
    return {
        "Id": "sha256:" + ("1" if role == "api" else "2") * 64,
        "Os": "linux", "Architecture": "amd64", "RepoDigests": [IMAGES[role]],
        "Entrypoint": ["dotnet", entry], "User": "1654:1654",
        "Labels": {"org.opencontainers.image.revision": SOURCE}, **config,
    }


class DockerHostTests(unittest.TestCase):
    def test_native_transport_pins_local_pipe_and_rejects_remote_selection(self):
        docker = docker_stub()
        with patch.object(target.d01, "command", side_effect=[
            b"desktop-linux", reply(d01.ENDPOINT), reply(IDENTITY), b"result",
        ]) as command:
            self.assertEqual(docker.run("volume", "ls"), b"result")
            self.assertEqual(list(command.call_args.args[0]), [
                CLI, "--host", d01.ENDPOINT, "volume", "ls",
            ])
        with patch.object(target.d01, "command", return_value=b"foreign") as command:
            with self.assertRaisesRegex(LabError, "^UNSAFE_CONTEXT$"):
                docker.run("volume", "create", PROJECT + "_sql")
            self.assertEqual(list(command.call_args.args[0]), [CLI, "context", "show"])
        with patch.object(target.d01, "command", side_effect=[
            b"desktop-linux", reply(d01.ENDPOINT), reply(dict(IDENTITY, ID="replaced-daemon")),
        ]) as command:
            with self.assertRaisesRegex(LabError, "^ENGINE_IDENTITY_CHANGED$"):
                docker.run("volume", "create", PROJECT + "_sql")
            self.assertEqual(command.call_count, 3)

    def test_scope_retains_foreign_owners_with_exact_names_or_project_labels(self):
        docker = docker_stub()
        containers = [
            {"Id": "a" * 64, "Name": "/" + PROJECT + "-api-1", "Config": {"Labels": None}},
            {"Id": "b" * 64, "Name": "/unrelated", "Config": {"Labels": None},
             "Mounts": [{"Type": "volume", "Name": PROJECT + "_sql"}]},
        ]
        networks = [
            {"Id": "c" * 64, "Name": "other-name", "Labels": {"io.fmcg-dms.lab-d02.project": PROJECT}},
        ]
        volumes = [
            {"Name": PROJECT + "_sql", "Labels": None},
            {"Name": "project-extra", "Labels": {"com.docker.compose.project": PROJECT}},
        ]
        # Observe Docker's list/inspect shape without connecting to a daemon.
        objects = {"container": containers, "network": networks, "volume": volumes}
        def respond(kind, action, *args, input_data=None):
            rows = objects[kind]
            if action == "ls":
                key = "Name" if kind == "volume" else "Id"
                return "\n".join(row[key] for row in rows).encode()
            if action == "inspect":
                key = "Name" if kind == "volume" else "Id"
                if kind == "container" and args[1] != "{{json .}}":
                    return b"\n".join(reply({"Id": row["Id"], "Name": row["Name"],
                                              "Labels": row["Config"]["Labels"], "Mounts": row.get("Mounts")})
                                       for row in rows if row[key] in args)
                return b"\n".join(reply(row) for row in rows if row[key] in args)
            raise AssertionError(action)
        with patch.object(docker, "_server", side_effect=respond), patch.object(docker, "verify"):
            scoped = docker.state(PROJECT)
        self.assertEqual(scoped, {"containers": containers[:1], "networks": networks, "volumes": volumes,
                                  "volumeUsers": {PROJECT + "_sql": ["b" * 64], "project-extra": []}})

    def test_namespace_discovery_catches_case_variants(self):
        self.assertTrue(target.in_scope(PROJECT, PROJECT.upper() + "_sql", None))
        self.assertTrue(target.in_scope(PROJECT, "unrelated", {target.COMPOSE_LABEL: PROJECT.upper()}))

    def test_cli_proxy_environment_is_refused_before_a_compose_mutation(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp).resolve()
            config = home / ".docker" / "config.json"
            config.parent.mkdir()
            config.write_text('{"proxies":{"default":{"httpsProxy":"https://proxy.example.invalid"}}}')
            docker = target.Docker(CLI, HOST_REF, ENGINE_REF, pwsh="pwsh", folders={"USERPROFILE": home})
            with patch.object(target.d01, "command") as command:
                with self.assertRaisesRegex(LabError, "^D02_DOCKER_PROXY_CONFIG$"):
                    docker.run("compose", "up")
                command.assert_not_called()

    def test_ambiguous_listing_fails_before_any_inspection(self):
        docker = docker_stub()
        with patch.object(docker, "_server", return_value=b"f"*64+b"\n"+b"f"*64), patch.object(docker, "verify"):
            with self.assertRaisesRegex(LabError, "^AMBIGUOUS_DOCKER_INVENTORY$"):
                docker.state(PROJECT)

    def test_cached_images_must_match_original_repo_digest_and_runtime_contract(self):
        docker = docker_stub()
        with patch.object(docker, "run", side_effect=[reply(image("api")), reply(image("worker"))]):
            ids = docker.image_ids(IMAGES, SOURCE)
            self.assertEqual(ids, {"api": "sha256:" + "1"*64, "worker": "sha256:" + "2"*64})
        for bad in (
            image("api", Entrypoint=["sh"]),
            image("api", User="root"),
            image("api", Volumes={"/data": {}}),
            image("api", Labels={"org.opencontainers.image.revision": "stale"}),
            dict(image("api"), RepoDigests=[IMAGES["worker"]]),
            dict(image("api"), Architecture="arm64"),
        ):
            with self.subTest(image=bad):
                with patch.object(docker, "run", return_value=reply(bad)):
                    with self.assertRaisesRegex(LabError, "^UNTRUSTED_D02_IMAGE$"):
                        docker.image_ids(IMAGES, SOURCE)
        with patch.object(docker, "run") as run:
            with self.assertRaisesRegex(LabError, "^UNPINNED_D02_IMAGE$"):
                docker.image_ids(dict(IMAGES, api="example.test/lab-api:latest"), SOURCE)
            run.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "Windows ACL security descriptors")
    def test_acl_refuses_writes_by_everyone_on_private_artifact(self):
        script = r"""param([string]$Path, [string]$WorldRights)
$ErrorActionPreference = 'Stop'
$file = [IO.FileInfo]::new($Path)
$acl = [IO.FileSystemAclExtensions]::GetAccessControl($file, [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $false)
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User
$everyone = [Security.Principal.SecurityIdentifier]::new('S-1-1-0')
$acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($me, 'FullControl', 'Allow'))
$acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone, $WorldRights, 'Allow'))
[IO.FileSystemAclExtensions]::SetAccessControl($file, $acl)
"""
        with tempfile.TemporaryDirectory(prefix="d02-acl-") as directory:
            root = Path(directory)
            setup = root / "set-fixture-acl.ps1"
            setup.write_text(script, encoding="utf-8")
            evidence = root / "fixture.json"
            evidence.write_text("{}", encoding="utf-8")
            for rights in ("Read", "Write"):
                subprocess.run(["pwsh.exe", "-NoProfile", "-NonInteractive", "-File", str(setup),
                                str(evidence), rights], check=True, capture_output=True, timeout=20)
                if rights == "Read":
                    target.assert_private("pwsh.exe", [evidence])
                else:
                    with self.assertRaisesRegex(LabError, "^UNSAFE_D02_ACL$"):
                        target.assert_private("pwsh.exe", [evidence])

    def test_acl_adapter_checks_all_artifacts_and_refuses_native_failure(self):
        with patch.object(target.d01, "command", return_value=b"PRIVATE\n") as command:
            paths = [Path(r"C:\Lab\a"), Path(r"C:\Lab\b")]
            target.assert_private("pwsh.exe", paths)
            self.assertEqual(json.loads(command.call_args.kwargs["input_data"]), list(map(str, paths)))
        with patch.object(target.d01, "command", return_value=None):
            with self.assertRaisesRegex(LabError, "^UNSAFE_D02_ACL$"):
                target.assert_private("pwsh.exe", [Path(r"C:\Lab\a")])


if __name__ == "__main__":
    unittest.main()

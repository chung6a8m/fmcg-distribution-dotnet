"""Read-only D02 discovery and a verified Docker Desktop transport for scoped callers."""

import base64
from pathlib import Path
import re

from . import docker_preflight as d01
from .contract import canonical_json, digest, require
from .docker_d02_topology import IMAGE, PROJECT, PROJECT_LABEL
from .docker_windows import verified_windows_folders
from .preflight import existing_local_input
from .environment import check_location


COMPOSE_LABEL = "com.docker.compose.project"
IMAGE_ID = re.compile(r"sha256:[0-9a-f]{64}", re.ASCII)
OBJECT_ID = re.compile(r"[0-9a-f]{64}", re.ASCII)
NETWORK_ID = re.compile(r"(?:[0-9a-f]{64}|[a-z0-9]{25})", re.ASCII)
VOLUME_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,254}", re.ASCII)
NAMESPACE_PREFIXES = ("-", "_")
# The global scan excludes container configuration and environment secrets.
FIND_CONTAINER = d01.projection(("Id", ".Id"), ("Name", ".Name"),
                                ("Labels", ".Config.Labels"), ("Mounts", ".Mounts"))
FIND_NETWORK = d01.projection(("Id", ".Id"), ("Name", ".Name"), ("Labels", ".Labels"))
FIND_VOLUME = d01.projection(("Name", ".Name"), ("Labels", ".Labels"))
IMAGE_FORMAT = d01.projection(
    ("Id", ".Id"), ("Os", ".Os"), ("Architecture", ".Architecture"), ("RepoDigests", ".RepoDigests"),
    ("User", ".Config.User"), ("Entrypoint", ".Config.Entrypoint"),
    # Docker can omit empty config keys; index renders a missing key as null.
    ("Cmd", '(index .Config "Cmd")'), ("Volumes", '(index .Config "Volumes")'),
    ("Labels", ".Config.Labels"),
)
ENTRYPOINTS = {
    "api": ["dotnet", "FmcgDms.Preprod.Api.dll"],
    "worker": ["dotnet", "FmcgDms.Preprod.Worker.dll"],
}

ACL_CHECK = r"""
$ErrorActionPreference = 'Stop'
. (Join-Path $PWD.Path 'scripts/lab/DockerWsl.Windows.ps1')
Assert-D01WindowsEnvironment
$env:PSModulePath = Get-D01TrustedModulePath
$sidType = [Security.Principal.SecurityIdentifier]
$current = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trusted = @($current, 'S-1-5-18', 'S-1-5-32-544')
$writeMask = [int]([Security.AccessControl.FileSystemRights]::Write -bor
    [Security.AccessControl.FileSystemRights]::Delete -bor
    [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [Security.AccessControl.FileSystemRights]::TakeOwnership -bor 0x50000000)
$paths = @(ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd()))
if ($paths.Count -eq 0) { exit 1 }
foreach ($path in $paths) {
    $file = Get-Item -LiteralPath $path -Force
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
    $acl = Microsoft.PowerShell.Security\Get-Acl -LiteralPath $path
    if ($acl.GetOwner($sidType).Value -notin $trusted) { exit 1 }
    $rules = $acl.GetAccessRules($true, $true, $sidType)
    if (-not $rules.Count) { exit 1 }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq 'Allow' -and
            $rule.IdentityReference.Value -notin $trusted -and
            ([int]$rule.FileSystemRights -band $writeMask)) { exit 1 }
    }
}
[Console]::WriteLine('PRIVATE')
"""


def assert_private(pwsh, paths):
    values = list(dict.fromkeys(map(str, paths)))
    require(bool(values), "UNSAFE_D02_ACL")
    encoded = base64.b64encode(ACL_CHECK.encode("utf-16-le")).decode("ascii")
    raw = d01.command([pwsh, "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                      input_data=canonical_json(values))
    require(d01.text_result(raw) == "PRIVATE", "UNSAFE_D02_ACL")


def in_scope(project, name, labels):
    require(type(name) is str and (labels is None or type(labels) is dict), "INVALID_DOCKER_INVENTORY")
    labels = labels or {}
    folded = name.casefold()
    return (folded == project or folded.startswith(tuple(project + sep for sep in NAMESPACE_PREFIXES))
            or any(isinstance(labels.get(key), str) and labels[key].casefold() == project
                   for key in (PROJECT_LABEL, COMPOSE_LABEL)))


def refuse_cli_proxies(folders):
    path = folders["USERPROFILE"] / ".docker/config.json"
    if path.is_file():
        config = d01.json_result(d01.bounded_input(check_location(path)), dict)
        require(not any(value for key, value in config.items() if key.casefold() == "proxies"),
                "D02_DOCKER_PROXY_CONFIG")


class Docker:
    """Use one pinned local Desktop daemon. Callers own operation-specific authorization."""

    def __init__(self, executable, host_ref, engine_ref, *, pwsh=None, folders=None):
        self._executable = executable
        self._host_ref = host_ref
        self._engine_ref = engine_ref
        self._pwsh = pwsh
        self._folders = folders

    def _server(self, *args, input_data=None):
        result = d01.command([self._executable, "--host", d01.ENDPOINT, *args],
                             timeout=120, input_data=input_data)
        require(result is not None, "D02_DOCKER_COMMAND_FAILED")
        return result

    def identity(self):
        require(not d01.docker_overrides(), "DOCKER_ENV_OVERRIDE")
        if self._pwsh is not None:
            refuse_cli_proxies(self._folders)
            install = self._executable.parents[2]
            plugins = d01.docker_plugin_paths(install, self._folders)
            d01.verify_signatures(self._pwsh, [self._executable, *plugins], "Docker Inc")
        selected = d01.command([self._executable, "context", "show"])
        require(d01.text_result(selected) == d01.CONTEXT, "UNSAFE_CONTEXT")
        endpoint = d01.command([self._executable, "context", "inspect", d01.CONTEXT,
                                "--format", "{{json .Endpoints.docker.Host}}"])
        require(d01.json_result(endpoint, str) == d01.ENDPOINT, "UNSAFE_CONTEXT")
        engine = d01.json_result(self._server("info", "--format", d01.ENGINE_FORMAT), dict)
        require(not engine.get("ServerErrors") and d01.linux_desktop(engine)
                and d01.wsl_backend({"engine": engine, "wslMode": 2}),
                "UNSUPPORTED_DOCKER_ENGINE")
        daemon_id = engine.get("ID")
        require(type(daemon_id) is str and bool(daemon_id), "ENGINE_IDENTITY_CHANGED")
        actual_ref = digest(canonical_json(
            ["LAB-D01-ENGINE-V1", self._host_ref, d01.CONTEXT, d01.ENDPOINT, daemon_id]))
        require(actual_ref == self._engine_ref, "ENGINE_IDENTITY_CHANGED")
        return self._host_ref, actual_ref

    def verify(self):
        return self.identity()

    def run(self, *args, input_data=None):
        self.verify()
        return self._server(*args, input_data=input_data)

    def _names(self, kind, *options):
        rows = d01.text_result(self._server(kind, "ls", "--quiet", *options)).splitlines()
        syntax = {"container": OBJECT_ID, "network": NETWORK_ID, "volume": VOLUME_NAME}[kind]
        require(len(rows) <= 1024 and len(rows) == len(set(rows)) and all(syntax.fullmatch(row) for row in rows),
                "AMBIGUOUS_DOCKER_INVENTORY")
        return rows

    def _inspect(self, kind, names, template="{{json .}}"):
        key = "Name" if kind == "volume" else "Id"
        rows = []
        for start in range(0, len(names), 32):
            batch = names[start:start + 32]
            raw = self._server(kind, "inspect", "--format", template, "--", *batch)
            inspected = [d01.json_result(line, dict) for line in raw.splitlines()]
            identifiers = [item.get(key) for item in inspected]
            require(all(type(value) is str for value in identifiers)
                    and len(identifiers) == len(batch) and set(identifiers) == set(batch),
                    "AMBIGUOUS_DOCKER_INVENTORY")
            rows.extend(inspected)
        return rows

    def state(self, project):
        require(type(project) is str and PROJECT.fullmatch(project), "INVALID_PROJECT_NAMESPACE")
        self.verify()
        ids = {"container": self._names("container", "--all", "--no-trunc"),
               "network": self._names("network", "--no-trunc"),
               "volume": self._names("volume")}
        containers = self._inspect("container", ids["container"], FIND_CONTAINER)
        networks = self._inspect("network", ids["network"], FIND_NETWORK)
        volumes = self._inspect("volume", ids["volume"], FIND_VOLUME)
        selected = {}
        for kind, rows in (("container", containers), ("network", networks), ("volume", volumes)):
            key = "Name" if kind == "volume" else "Id"
            scope = []
            for row in rows:
                name = row.get("Name")
                require(type(name) is str and bool(name), "INVALID_DOCKER_INVENTORY")
                if in_scope(project, name.lstrip("/"), row.get("Labels")):
                    scope.append(row[key])
            selected[kind + "s"] = self._inspect(kind, scope)
        names = [volume["Name"] for volume in selected["volumes"]]
        users = {name: [] for name in names}
        for row in containers:
            mounts = row.get("Mounts")
            require(mounts is None or type(mounts) is list, "INVALID_DOCKER_INVENTORY")
            for mount in d01.objects(mounts or []):
                if mount.get("Type") == "volume" and mount.get("Name") in users:
                    users[mount["Name"]].append(row["Id"])
        selected["volumeUsers"] = {name: sorted(set(referrers)) for name, referrers in users.items()}
        self.verify()
        return selected

    def image_ids(self, images, source_head):
        require(type(images) is dict and set(images) == set(ENTRYPOINTS)
                and all(type(ref) is str and IMAGE.fullmatch(ref)
                        and not ref.endswith("0" * 64) for ref in images.values()),
                "UNPINNED_D02_IMAGE")
        require(type(source_head) is str and re.fullmatch(r"[0-9a-f]{40}", source_head)
                and source_head != "0" * 40, "INVALID_D02_SOURCE")
        result = {}
        for role, ref in images.items():
            info = d01.json_result(self.run("image", "inspect", "--format", IMAGE_FORMAT, "--", ref), dict)
            labels = info.get("Labels")
            image_id = info.get("Id")
            repo_digests = info.get("RepoDigests")
            require(type(image_id) is str and IMAGE_ID.fullmatch(image_id)
                    and info.get("Os") == "linux" and info.get("Architecture") == "amd64"
                    and type(repo_digests) is list and ref in repo_digests
                    and type(labels) is dict and labels.get("org.opencontainers.image.revision") == source_head
                    and info.get("Entrypoint") == ENTRYPOINTS[role] and info.get("User") == "1654:1654"
                    and info.get("Cmd") in (None, []) and info.get("Volumes") in (None, {}),
                    "UNTRUSTED_D02_IMAGE")
            result[role] = image_id
        return result


def snapshot(pwsh, d01_plan):
    folders = verified_windows_folders()
    host = {}
    issue = d01.collect_inventory(pwsh, d01_plan, host, folders)
    require(issue is None, issue or "COLLECTION_ERROR")
    require(d01.local_context(host) and d01.linux_desktop(host.get("engine") or {})
            and d01.wsl_backend(host), "UNSUPPORTED_DOCKER_ENGINE")
    host_ref, engine_ref = d01.identity_refs(host)
    require(host_ref and engine_ref, "ENGINE_IDENTITY_CHANGED")
    # D01 validates the Desktop installation and signatures before inventory.
    processes = [existing_local_input(path, directory=False) for path in host["desktopPaths"]]
    launchers = {str(path): path for path in processes if (path.parent / "resources/bin/docker.exe").is_file()}
    require(len(launchers) == 1, "UNSUPPORTED_DESKTOP_LAYOUT")
    install = next(iter(launchers.values())).parent
    executable = existing_local_input(str(install / "resources/bin/docker.exe"), directory=False)
    docker = Docker(executable, host_ref, engine_ref, pwsh=pwsh, folders=folders)
    docker.verify()
    return docker, host

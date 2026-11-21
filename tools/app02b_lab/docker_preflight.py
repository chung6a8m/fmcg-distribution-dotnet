"""LAB-D01 Windows Docker Desktop/WSL2 readiness. No Apply or admission path."""

from datetime import datetime, timezone
from ipaddress import IPv4Network
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

# D01 adapter: reuse strict serialization, path checks and Windows atomic
# publication primitives without accepting or issuing an E05 admission.
from tools.app02b_lab.contract import (LabError, canonical_json, digest, guarded_directory,
                                       read_json, require, strict_json)
from tools.app02b_lab.docker_windows import verified_windows_folders
from tools.app02b_lab.environment import PRIVATE_NETWORKS, check_location, require_fixed_volume, windows_path
from tools.app02b_lab.preflight import beneath, existing_local_input, output_path, source_info
from tools.app02b_lab.schema import validate_document
from tools.app02b_lab.windows_output import (close_handle, create_report, create_stage,
                                             discard_stage, open_parent, rename_stage)

CONTEXT = "desktop-linux"
ENDPOINT = "npipe:////./pipe/dockerDesktopLinuxEngine"
PUBLISHERS = {
    "APP": "mcr.microsoft.com/dotnet/aspnet",
    "SQL": "mcr.microsoft.com/mssql/server",
    "VAULT": "hashicorp/vault",
    "PROXY": "caddy",
}
REDISPLAY_VERSIONS = ("python", "git", "pwsh", "wsl", "desktop", "dockerCli", "engine", "compose")
DOCKER_OVERRIDES = {"DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "DOCKER_TLS",
                    "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_API_VERSION",
                    "DOCKER_DEFAULT_PLATFORM"}
LIMIT = 8_000_000
MIB = 2**20
WINDOWS_COLLECTOR = ROOT / "scripts/lab/Get-DockerWslInventory.ps1"


def projection(*fields):
    return "{" + ",".join(f'"{name}":{{{{json {selector}}}}}' for name, selector in fields) + "}"


ENGINE_FORMAT = projection(*((field, "." + field) for field in (
    "ID", "OSType", "Architecture", "OperatingSystem", "KernelVersion", "MemTotal",
    "NCPU", "Driver", "DockerRootDir", "ServerVersion", "SystemTime", "ServerErrors",
)))
CONTAINER_FORMAT = projection(
    ("Name", ".Name"), ("Project", '(index .Config.Labels "com.docker.compose.project")'),
    ("Bindings", ".HostConfig.PortBindings"), ("LivePorts", ".NetworkSettings.Ports"), ("Mounts", ".Mounts"),
)
NETWORK_FORMAT = projection(("Id", ".Id"), ("Name", ".Name"), ("Subnets", ".IPAM.Config"))
IMAGE_FORMAT = projection(("Os", ".Os"), ("Architecture", ".Architecture"), ("RepoDigests", ".RepoDigests"))


def validate_plan(plan):
    validate_document(plan, "docker-host-plan")
    require(windows_path(plan["evidenceRoot"]), "UNSAFE_EVIDENCE_ROOT")
    require(all(port <= 65535 for port in plan["ports"]), "INVALID_PORT")
    require(all(value <= 2**30 for value in plan["budget"].values()), "INVALID_RESOURCE_BUDGET")
    prefix = plan["project"] + "_"
    names = [network["name"] for network in plan["networks"]]
    require(len(set(names)) == len(names) and all(name.startswith(prefix) for name in names + plan["volumes"]),
            "INVALID_PROJECT_NAMESPACE")
    resume = plan.get("resumeD02")
    if resume is not None:
        require(prefix + "app" in names and nonzero_digest(resume["appNetworkId"])
                and resume["nonce"] != "0" * 32, "INVALID_D02_RESUME_SCOPE")
    networks = [network(entry["subnet"], "INVALID_SUBNET") for entry in plan["networks"]]
    for index, subnet in enumerate(networks):
        require(16 <= subnet.prefixlen <= 29 and any(subnet.subnet_of(block) for block in PRIVATE_NETWORKS)
                and not any(subnet.overlaps(other) for other in networks[:index]), "INVALID_SUBNET")
    require({image["role"] for image in plan["images"]} == PUBLISHERS.keys(), "INVALID_IMAGE_ROLES")
    for image in plan["images"]:
        repository, pin = image["reference"].split("@sha256:")
        require(repository == PUBLISHERS[image["role"]], "UNAPPROVED_IMAGE_PUBLISHER")
        require(nonzero_digest(pin) and nonzero_digest(image["publisherReceiptSha256"]), "UNVERIFIED_IMAGE_PIN")


def nonzero_digest(value):
    return value != "0" * 64


def network(value, error):
    try:
        return IPv4Network(value)
    except (ValueError, TypeError):
        raise LabError(error) from None


def command(args, *, timeout=25, inventory=False, input_data=None, missing=None):
    try:
        result = subprocess.run(args, cwd=ROOT, stdin=subprocess.DEVNULL if input_data is None else None,
                                input=input_data, capture_output=True, timeout=timeout)
    except (OSError, subprocess.SubprocessError):
        raise LabError("COLLECTION_ERROR") from None
    if inventory and result.returncode == 3:
        raise LabError("HOST_ACCESS_DENIED")
    if inventory and result.returncode == 4:
        raise LabError("UNTRUSTED_NATIVE_EXECUTABLE")
    require(len(result.stdout) <= LIMIT, "COLLECTION_ERROR")
    if result.returncode and missing:
        check_discovery_error(result.stderr, missing)
    return result.stdout if result.returncode == 0 else None


def check_discovery_error(stderr, missing):
    error = stderr.lower()
    if any(marker in error for marker in (b"access is denied", b"permission denied", b"0x80070005")):
        raise LabError("HOST_ACCESS_DENIED")
    expected = {
        "daemon": (b"the system cannot find the file specified", b"cannot connect to the docker daemon"),
        "image": (b"no such image:", b"no such object:"),
    }
    require(any(marker in error for marker in expected[missing]), "COLLECTION_ERROR")


def json_result(raw, expected):
    require(raw is not None, "COLLECTION_ERROR")
    value = strict_json(raw)
    require(type(value) is expected, "COLLECTION_ERROR")
    return value


def text_result(raw):
    if raw is None:
        return ""
    # Inbox WSL and older Store WSL can emit UTF-16LE even when redirected.
    encoding = "utf-16" if raw.startswith(b"\xff\xfe") else "utf-16-le" if b"\0" in raw else "utf-8-sig"
    return raw.decode(encoding, errors="replace").strip()


def version(value, prefix=""):
    match = re.fullmatch(prefix + r"v?([0-9]{1,6}(?:\.[0-9]{1,6}){1,3})(?:[-+., ].*)?", value or "")
    return match[1] if match else None


def verify_signatures(pwsh, paths, publisher, *, read_desktop_version=False):
    request = {"paths": list(map(str, paths)), "publisher": publisher}
    if read_desktop_version:
        request["readDesktopVersion"] = True
    raw = command([pwsh, "-NoProfile", "-NonInteractive", "-File", WINDOWS_COLLECTOR, "-VerifySignatures"],
                  input_data=canonical_json(request), timeout=100, inventory=True)
    if read_desktop_version:
        desktop_version = json_result(raw, dict).get("desktopVersion")
        require(desktop_version is None or type(desktop_version) is str, "COLLECTION_ERROR")
        return version(desktop_version)
    require(text_result(raw) == "VERIFIED", "COLLECTION_ERROR")


def collect_wsl(pwsh, system_directory, host):
    executable = system_directory / "wsl.exe"
    if not executable.is_file():
        return
    executable = existing_local_input(str(executable), directory=False)
    verify_signatures(pwsh, [executable], "Microsoft Corporation")
    raw = text_result(command([executable, "--version"]))
    first_line = next(iter(raw.splitlines()), "")
    # --version lists WSL first, before kernel, WSLg and Windows.
    match = re.fullmatch(r"[^:]+:\s*(\d{1,6}(?:\.\d{1,6}){1,3})", first_line.strip())
    host["wsl"] = match[1] if match else None
    verbose = text_result(command([executable, "--list", "--verbose"]))
    running = text_result(command([executable, "--list", "--running", "--quiet"]))
    match = re.search(r"^[ \t]*\*?[ \t]*docker-desktop[ \t]+\S[^\r\n]*[ \t]+([12])[ \t]*$",
                      verbose, re.M)
    host["wslMode"] = int(match[1]) if match else None
    if "docker-desktop" not in [line.strip() for line in running.splitlines()] and host["wslMode"] == 2:
        host["wslMode"] = None


def verified_wsl_attachment(proof, capacity):
    if not isinstance(proof, dict) or proof.get("stableVm") is not True:
        return False
    lun = numeric(proof.get("guestLun"))
    changes = proof.get("changes")
    if lun is None or numeric(proof.get("guestCapacity")) != capacity or not isinstance(changes, list):
        return False
    latest = None
    for change in changes:
        if not isinstance(change, dict) or numeric(change.get("lun")) is None:
            return False
        if change["lun"] == lun:
            # The guest LUN only identifies a slot for a single supported
            # controller. Any same-LUN record on another controller is ambiguous.
            if numeric(change.get("controller")) != 0:
                return False
            if latest is None:
                latest = change
    return bool(latest and latest.get("request") == "Add" and latest.get("matchesVhd") is True)


def storage(pwsh, folders, host):
    """Use the Docker Desktop settings and recognized VHD layout on a fixed Windows drive."""
    import shutil

    settings = folders["APPDATA"] / "Docker/settings-store.json"
    if not settings.is_file() or settings.stat().st_size > LIMIT:
        return
    configuration = json_result(settings.read_bytes(), dict)
    locations = [value for key, value in configuration.items()
                 if key.casefold() in ("customwsldistrodir", "datafolder", "diskimagelocation")
                 and value not in (None, "")]
    if locations and not all(windows_path(value) for value in locations):
        return
    bases = map(Path, locations) if locations else [folders["LOCALAPPDATA"] / "Docker/wsl"]
    layouts = ("docker_data.vhdx", "disk/docker_data.vhdx", "DockerDesktopWSL/disk/docker_data.vhdx")
    present = {}
    for base in bases:
        try:
            require_fixed_volume(base)
        except LabError as error:
            if error.code in ("NONLOCAL_VOLUME", "UNSUPPORTED_FILESYSTEM"):
                return
            raise
        for layout in layouts:
            try:
                path = check_location(base / layout, missing=True)
            except LabError as error:
                if error.code != "PATH_MISSING":
                    raise
                continue
            if path.is_file():
                present[str(path)] = path
    if len(present) != 1:
        return
    path = next(iter(present.values()))
    raw = command([pwsh, "-NoProfile", "-NonInteractive", "-File", WINDOWS_COLLECTOR, "-DataDisk"],
                  input_data=(str(path) + "\n").encode("utf-8"), inventory=True)
    observed = json_result(raw, dict)
    require(type(observed.get("attached")) is bool and numeric(observed.get("capacity")) is not None,
            "COLLECTION_ERROR")
    capacity = observed["capacity"]
    if not capacity:
        return
    if observed["attached"]:
        method = "WINDOWS_STORAGE"
    elif verified_wsl_attachment(observed.get("wslAttachment"), capacity):
        method = "HCS_WSL"
    else:
        return
    host["diskCapacity"] = capacity
    host["diskFree"] = shutil.disk_usage(path.parent).free
    host["diskRef"] = str(path).casefold()
    host["diskAttachmentMethod"] = method


def collect_inventory(pwsh, plan, host, folders):
    if docker_overrides():
        return "DOCKER_ENV_OVERRIDE"
    host["python"] = ".".join(map(str, sys.version_info[:3]))
    host["git"] = version(text_result(command(["git.exe", "--version"])), r"git version ")
    raw = command([pwsh, "-NoProfile", "-NonInteractive", "-File", WINDOWS_COLLECTOR], timeout=100, inventory=True)
    host.update(json_result(raw, dict))
    collect_wsl(pwsh, folders["SystemDirectory"], host)
    desktop_paths = host.get("desktopPaths")
    require(type(desktop_paths) is list, "COLLECTION_ERROR")
    if not desktop_paths:
        return "DAEMON_UNAVAILABLE"
    processes = {}
    per_user = folders["LOCALAPPDATA"] / "Programs/DockerDesktop"
    for value in desktop_paths:
        path = existing_local_input(value, directory=False)
        require(path.name.casefold() == "docker desktop.exe"
                and (path.parent == per_user or beneath(path, per_user) or beneath(path, folders["ProgramFiles"])),
                "UNTRUSTED_NATIVE_EXECUTABLE")
        processes[str(path)] = path
    launchers = [path for path in processes.values() if (path.parent / "resources/bin/docker.exe").is_file()]
    if len(launchers) != 1:
        return "UNSUPPORTED_DESKTOP_LAYOUT"
    desktop = launchers[0]
    install = desktop.parent
    if not (str(install) == str(per_user) or beneath(install, folders["ProgramFiles"])):
        return "UNSUPPORTED_DESKTOP_LAYOUT"
    frontend = install / "frontend" / desktop.name
    if processes.keys() - {str(desktop), str(frontend)}:
        return "UNSUPPORTED_DESKTOP_LAYOUT"
    executable = existing_local_input(str(install / "resources/bin/docker.exe"), directory=False)
    try:
        plugins = docker_plugin_paths(install, folders)
    except LabError as error:
        if error.code != "UNTRUSTED_PLUGIN_CONFIGURATION":
            raise
        return error.code
    desktop_files = [desktop]
    if str(frontend) in processes:
        desktop_files.append(frontend)
    host["desktop"] = verify_signatures(pwsh, [*desktop_files, executable, *plugins], "Docker Inc",
                                         read_desktop_version=True)
    issue = docker_inventory(executable, host, plan)
    if issue is None and local_context(host) and linux_desktop(host.get("engine") or {}) and wsl_backend(host):
        storage(pwsh, folders, host)
    return issue


def docker_plugin_paths(install, folders):
    """Collect only executable candidates from the packaged and OS-known CLI plugin roots."""
    docker_config = folders["USERPROFILE"] / ".docker"
    trusted = [install / "resources/cli-plugins",
               folders["ProgramFiles"] / "Docker/cli-plugins",
               folders["ProgramData"] / "Docker/cli-plugins"]
    trusted_targets = {check_location(path) for path in trusted if path.exists()}
    config = docker_config / "config.json"
    if config.is_file():
        config = check_location(config)
        require(config.stat().st_size <= LIMIT, "COLLECTION_ERROR")
        settings = json_result(config.read_bytes(), dict)
        for key, paths in settings.items():
            if key.casefold() == "clipluginsextradirs":
                require(isinstance(paths, list) and all(windows_path(path) and Path(path) in trusted
                                                       for path in paths), "UNTRUSTED_PLUGIN_CONFIGURATION")

    def candidates(path):
        return [plugin for plugin in path.iterdir()
                if plugin.name.lower().startswith("docker-") and plugin.suffix.lower() == ".exe"]

    user = docker_config / "cli-plugins"
    require(not user.exists() or not candidates(check_location(user)), "UNTRUSTED_PLUGIN_CONFIGURATION")
    plugins = set()
    for path in trusted_targets:
        for entry in candidates(path):
            plugin = entry.resolve(strict=True)
            require(plugin.parent in trusted_targets and plugin.is_file(), "UNTRUSTED_PLUGIN_CONFIGURATION")
            plugins.add(check_location(plugin))
    return sorted(plugins)


def objects(value):
    require(isinstance(value, list) and all(isinstance(item, dict) for item in value), "COLLECTION_ERROR")
    return value


def inspect_list(docker, kind, ids, template):
    identifier = r"(?:[a-f0-9]{12,64}|[a-z0-9]{25})" if kind == "network" else r"[a-f0-9]{12,64}"
    require(len(ids) <= 1024 and all(re.fullmatch(identifier, item) for item in ids),
            "COLLECTION_ERROR")
    result = []
    # Stay under the Windows command line limit, even with full-length IDs.
    for start in range(0, len(ids), 64):
        batch = ids[start:start + 64]
        raw = docker(kind, "inspect", "--format", template, *batch)
        require(raw is not None, "COLLECTION_ERROR")
        rows = [json_result(line, dict) for line in raw.splitlines()]
        require(len(rows) == len(batch), "COLLECTION_ERROR")
        result.extend(rows)
    return result


def docker_overrides():
    keys = {key.upper() for key in os.environ}
    return bool(DOCKER_OVERRIDES & keys or any(key.startswith("COMPOSE_") for key in keys))


def docker_inventory(executable, host, plan=None):
    if docker_overrides():
        return "DOCKER_ENV_OVERRIDE"

    host["context"] = text_result(command([executable, "context", "show"]))
    if host["context"] != CONTEXT:
        return "UNSAFE_CONTEXT"
    endpoint = command([executable, "context", "inspect", CONTEXT, "--format",
                        "{{json .Endpoints.docker.Host}}"])
    host["endpoint"] = json_result(endpoint, str)
    if host["endpoint"] != ENDPOINT:
        return "UNSAFE_CONTEXT"

    # Pin every server call to the verified local pipe even if the selected
    # context is changed after inspection.
    def docker(*args, missing=None):
        return command([executable, "--host", ENDPOINT, *args], missing=missing)

    raw = docker("info", "--format", ENGINE_FORMAT, missing="daemon")
    if raw is None:
        return "DAEMON_UNAVAILABLE"
    host["engine"] = json_result(raw, dict)
    errors = host["engine"].pop("ServerErrors", None)
    if errors:
        check_discovery_error("\n".join(errors).encode(), "daemon")
        host.pop("engine")
        return "DAEMON_UNAVAILABLE"
    host["dockerCli"] = version(text_result(command([executable, "--version"])), r"Docker version ")
    host["compose"] = version(text_result(docker("compose", "version", "--short")))
    host["clockSkewSeconds"] = clock_skew(host["engine"].get("SystemTime"))
    # Stop after identifying an unsupported engine. Linux inventory details
    # have different semantics on a Windows daemon or a non-WSL backend.
    if not linux_desktop(host["engine"]) or not wsl_backend(host):
        return None
    collect_objects(docker, host)
    if "resumeD02" in plan:
        resume_id = plan["resumeD02"]["appNetworkId"]
        if any(row["id"] == resume_id for row in host["networks"]):
            row, = inspect_list(docker, "network", [resume_id], "{{json .}}")
            host["resumeAppNetwork"] = row
    host["images"] = {}
    for image in plan["images"]:
        raw = docker("image", "inspect", "--format", IMAGE_FORMAT, image["reference"], missing="image")
        if raw is not None:
            host["images"][image["role"]] = json_result(raw, dict)

    # Recheck daemon identity and selection after the snapshot.
    end = json_result(docker("info", "--format", "{{json .ID}}"), str)
    selection = text_result(command([executable, "context", "show"]))
    end_endpoint = command([executable, "context", "inspect", CONTEXT, "--format",
                            "{{json .Endpoints.docker.Host}}"])
    require(end == host["engine"].get("ID") and selection == CONTEXT
            and json_result(end_endpoint, str) == ENDPOINT, "COLLECTION_ERROR")
    return None


def collect_objects(docker, host):
    def lines(*args):
        raw = docker(*args)
        require(raw is not None, "COLLECTION_ERROR")
        return text_result(raw).splitlines()

    if host["compose"]:
        host["projects"] = lines("compose", "ls", "--all", "--quiet")
    host["volumes"] = lines("volume", "ls", "--quiet")
    host["containers"] = []
    for row in inspect_list(docker, "container", lines("container", "ls", "--all", "--quiet", "--no-trunc"),
                            CONTAINER_FORMAT):
        port_maps = (row["Bindings"], row["LivePorts"])
        bindings = sorted({port_range(binding["HostPort"]) for port_map in port_maps
                           for group in (port_map or {}).values() if group
                           for binding in group if binding["HostPort"]})
        host["containers"].append({
            "name": row["Name"].lstrip("/"),
            "project": row["Project"],
            "ports": bindings,
            "binds": sum(mount["Type"] == "bind" for mount in objects(row["Mounts"] or [])),
        })
    host["networks"] = []
    for row in inspect_list(docker, "network", lines("network", "ls", "--quiet", "--no-trunc"), NETWORK_FORMAT):
        host["networks"].append({"id": row.get("Id"), "name": row["Name"],
                                 "subnets": [item["Subnet"] for item in objects(row["Subnets"] or [])
                                             if item.get("Subnet") and ":" not in item["Subnet"]]})


def port_range(value):
    match = re.fullmatch(r"(\d{1,5})(?:-(\d{1,5}))?", value, re.ASCII)
    require(match is not None, "COLLECTION_ERROR")
    first = int(match[1])
    last = int(match[2] or match[1])
    require(0 <= first <= last <= 65535, "COLLECTION_ERROR")
    return first, last


def clock_skew(value):
    if isinstance(value, str):
        try:
            return round(abs((datetime.now(timezone.utc) - datetime.fromisoformat(value)).total_seconds()))
        except (ValueError, TypeError, OverflowError):
            pass
    return None


def identity_refs(host):
    engine = host.get("engine") or {}
    machine = host.get("machine")
    daemon_id = engine.get("ID")
    host_ref = digest(canonical_json(["LAB-D01-HOST-V1", machine])) if machine else None
    engine_ref = (digest(canonical_json(["LAB-D01-ENGINE-V1", host_ref, CONTEXT, ENDPOINT, daemon_id]))
                  if host_ref and daemon_id and local_context(host) else None)
    return host_ref, engine_ref


def local_context(host):
    return host.get("context") == CONTEXT and host.get("endpoint") == ENDPOINT


def linux_desktop(engine):
    return engine.get("OSType") == "linux" and engine.get("OperatingSystem") == "Docker Desktop"


def wsl_backend(host):
    kernel = (host.get("engine") or {}).get("KernelVersion", "")
    return host.get("wslMode") == 2 and "microsoft-standard-wsl2" in kernel.casefold()


def numeric(value):
    return value if type(value) is int and value >= 0 else None


def mib(value):
    return value // MIB if numeric(value) is not None else None


def minimum(value, floor):
    return None if value is None else value >= floor


def match_expected(value, expected):
    return None if value is None else value == expected


def architecture(value):
    return {"amd64": "X64", "x86_64": "X64", "x64": "X64", "arm64": "ARM64",
            "aarch64": "ARM64", "x86": "X86", "arm": "ARM"}.get((value or "").casefold())


def image_check(image, observed):
    if not observed:
        return None
    reference = image["reference"]
    aliases = {reference}
    if image["role"] == "VAULT":
        aliases.add(f"docker.io/{reference}")
    if image["role"] == "PROXY":
        aliases.update((f"docker.io/library/{reference}", f"library/{reference}"))
    return (observed["Os"] == "linux" and observed["Architecture"] == "amd64"
            and bool(aliases.intersection(observed["RepoDigests"] or [])))


def resumable_app_network(plan, host, listed):
    # Reuse the D02 ownership and topology checks for the empty, stopped-stage APP bridge.
    from tools.app02b_lab.docker_d02_topology import _validate_network, labels, owned_objects

    resume = plan["resumeD02"]
    name = plan["project"] + "_app"
    subnet = next((entry["subnet"] for entry in plan["networks"] if entry["name"] == name), None)
    if (subnet is None or listed["name"] != name or listed.get("id") != resume["appNetworkId"]
            or listed["subnets"] != [subnet]):
        return False
    row = host.get("resumeAppNetwork")
    if (not isinstance(row, dict) or row.get("Id") != resume["appNetworkId"]
            or "Containers" not in row or row["Containers"] not in (None, {})):
        return False
    try:
        owned_objects([row], {name}, labels({"project": plan["project"], "nonce": resume["nonce"]}))
        _validate_network(row, subnet, {})
    except LabError:
        return False
    return True


def conflicts(plan, host):
    checks = dict.fromkeys(("PROJECT", "NETWORKS", "PORTS", "VOLUMES"))
    containers = host.get("containers")
    networks = host.get("networks")
    volumes = host.get("volumes")
    ports = host.get("ports")
    routes = host.get("routes")
    excluded = host.get("excluded")
    projects = host.get("projects")
    prefix = plan["project"] + "_"
    if containers is not None and projects is not None:
        project = plan["project"]
        checks["PROJECT"] = project not in projects and not any(
            item.get("project") == project or item.get("name", "").startswith((project + "-", project + "_"))
            for item in containers)
    if networks is not None and routes is not None:
        if "resumeD02" in plan:
            matches = [row for row in networks if row["name"].casefold() == (prefix + "app").casefold()
                       or row.get("id") == plan["resumeD02"]["appNetworkId"]]
            if len(matches) == 1 and resumable_app_network(plan, host, matches[0]):
                networks = [row for row in networks if row is not matches[0]]
            else:
                checks["NETWORKS"] = False
        occupied = [network(value, "COLLECTION_ERROR") for value in routes]
        occupied.extend(network(value, "COLLECTION_ERROR") for item in networks for value in item["subnets"])
        names = {item["name"] for item in networks}
        planned = [network(item["subnet"], "INVALID_SUBNET") for item in plan["networks"]]
        checks["NETWORKS"] = (checks["NETWORKS"] is not False
                              and not any(name.casefold().startswith(prefix) for name in names)
                              and not any(subnet.overlaps(existing) for subnet in planned for existing in occupied))
    if containers is not None and ports is not None and excluded is not None:
        occupied_ranges = [*excluded, *(span for item in containers for span in item["ports"])]
        checks["PORTS"] = all(port not in ports and not any(first <= port <= last for first, last in occupied_ranges)
                              for port in plan["ports"])
    if volumes is not None:
        checks["VOLUMES"] = not any(name.startswith(prefix) for name in volumes)
    return checks


def status(value):
    return "UNKNOWN" if value is None else "PASS" if value else "FAIL"


def evaluate(plan, host, source, plan_hash, issue=None):
    lock = read_json(ROOT / "deploy/lab/docker-wsl2/toolchain.lock.json")
    engine = host.get("engine") or {}
    host_ref, engine_ref = identity_refs(host)
    versions = {key: version(engine.get("ServerVersion") if key == "engine" else host.get(key))
                for key in REDISPLAY_VERSIONS}
    driver = engine.get("Driver")
    driver = driver if driver in ("overlay2", "overlayfs") else None
    build = numeric(host.get("build"))
    observed = {
        "windowsBuild": build, "hostArchitecture": architecture(host.get("architecture")),
        "engineArchitecture": architecture(engine.get("Architecture")), "wslMode": numeric(host.get("wslMode")),
        "versions": versions, "storageDriver": driver,
        "storageRef": digest(canonical_json(["LAB-D01-STORAGE-V1", host_ref, host["diskRef"]]))
        if host_ref and host.get("diskRef") else None,
        "hostFreeRamMiB": mib(host.get("freeRam")), "hostTotalRamMiB": mib(host.get("totalRam")),
        "engineRamMiB": mib(engine.get("MemTotal")), "engineCpu": numeric(engine.get("NCPU")),
        "diskFreeMiB": mib(host.get("diskFree")),
        "vhdCapacityMiB": mib(host.get("diskCapacity")),
    }
    if host.get("diskAttachmentMethod"):
        observed["storageAttachmentMethod"] = host["diskAttachmentMethod"]
    for key in ("containers", "projects", "networks", "volumes"):
        inventory = host.get(key)
        observed[key.removesuffix("s") + "Count"] = len(inventory) if inventory is not None else None
    containers = host.get("containers")
    observed["bindCount"] = sum(item["binds"] for item in containers) if containers is not None else None
    observed["publishedPortCount"] = sum(len(item["ports"]) for item in containers) if containers is not None else None

    def supported(key):
        found = versions[key]
        if found is None:
            return None
        parts = list(map(int, found.split(".")))
        floor = list(map(int, lock[key].split(".")))
        return (parts + [0] * 4)[:4] >= (floor + [0] * 4)[:4]

    budget = plan["budget"]
    checks = {
        "SOURCE": match_expected(source, (plan["sourceHead"], "CLEAN")) if source[0] else None,
        "WINDOWS": minimum(build, lock["windowsBuild"]), "POWERSHELL": supported("pwsh"),
        "PYTHON": supported("python"), "GIT": supported("git"),
        "HOST_IDENTITY": match_expected(host_ref, plan["hostRef"]),
        "HOST_MEMORY": minimum(observed["hostFreeRamMiB"], budget["hostFreeRamMiB"]),
        "HOST_DNS": minimum(numeric(host.get("dns")), 2),
        "HOST_CLOCK": host["clockSkewSeconds"] <= 30 if numeric(host.get("clockSkewSeconds")) is not None else None,
        "WSL_VERSION": supported("wsl"), "WSL2_BACKEND": wsl_backend(host) if host.get("wslMode") else None,
        "DOCKER_DESKTOP": supported("desktop"), "DOCKER_CLI": supported("dockerCli"),
        "LOCAL_CONTEXT": local_context(host) if "context" in host or issue == "DOCKER_ENV_OVERRIDE" else None,
        "DOCKER_DAEMON": supported("engine") if engine else False if issue == "DAEMON_UNAVAILABLE" else None,
        "ENGINE_IDENTITY": match_expected(engine_ref, plan["engineRef"]),
        "LINUX_ENGINE": linux_desktop(engine) if engine else None,
        "SQL_X64": (observed["hostArchitecture"] == observed["engineArchitecture"] == "X64")
        if observed["hostArchitecture"] and observed["engineArchitecture"] else None,
        "WSL_MEMORY": (observed["engineRamMiB"] >= budget["engineRamMiB"]
                       and observed["engineCpu"] >= budget["engineCpu"])
        if observed["engineRamMiB"] is not None and observed["engineCpu"] is not None else None,
        "WINDOWS_BACKING_STORAGE": min(observed["diskFreeMiB"], observed["vhdCapacityMiB"]) >= budget["diskFreeMiB"]
        if (observed["storageRef"] and driver and engine.get("DockerRootDir") == "/var/lib/docker"
            and observed["diskFreeMiB"] is not None and observed["vhdCapacityMiB"] is not None) else None,
        "COMPOSE": supported("compose"),
        **conflicts(plan, host),
    }
    observed["images"] = []
    for image in plan["images"]:
        role = image["role"]
        verified = image_check(image, host.get("images", {}).get(role))
        checks[f"IMAGE_{role}"] = verified
        observed["images"].append({"role": role, "sha256": image["reference"].split("@sha256:")[1],
                                   "publisherReceiptSha256": image["publisherReceiptSha256"],
                                   "status": status(verified)})
    collection_error = issue in ("COLLECTION_ERROR", "HOST_ACCESS_DENIED", "UNTRUSTED_NATIVE_EXECUTABLE")
    return {
        "schema": "APP02B-LAB-DOCKER-HOST-PREFLIGHT-V1",
        "evidenceClassification": "WINDOWS_HOST_PREFLIGHT", "authorization": "NOT_EVALUATED",
        "publisherProvenance": "OPERATOR_RECEIPT_DIGEST_BOUND",
        "engineFilesystemHeadroom": "NOT_MEASURED",
        "collectedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sourceHead": source[0], "sourceState": source[1], "planSha256": plan_hash,
        "hostRef": host_ref, "engineRef": engine_ref, "observed": observed,
        "checks": {name: status(value) for name, value in checks.items()},
        "issueCodes": [issue] if issue else [],
        "readiness": "COLLECTION_FAILED" if collection_error else
        "READY" if not issue and all(value is True for value in checks.values()) else "BLOCKED",
    }


def markdown(report):
    lines = ["# LAB-D01 Docker Desktop / WSL2 preflight", "",
             f"**{report['readiness']}** · {report['collectedUtc']}", "",
             f"- Source `{report['sourceHead']}` ({report['sourceState']})",
             f"- Plan SHA-256 `{report['planSha256']}`",
             f"- Host reference `{report['hostRef']}`",
             f"- Engine reference `{report['engineRef']}`", "",
             "| Required check | Status |", "| --- | --- |"]
    lines.extend(f"| {name} | {result} |" for name, result in report["checks"].items())
    lines.extend(["", "Issue codes " + (", ".join(report["issueCodes"]) or "none")])
    if method := report["observed"].get("storageAttachmentMethod"):
        lines.append(f"Windows backing storage attachment {method}.")
    lines.extend(["", "Docker Linux filesystem headroom NOT_MEASURED.",
                  "Shared Windows/WSL2 host. Authorization NOT_EVALUATED. Original gates NOT_VERIFIED.", ""])
    return "\n".join(lines)


def publish(path, evidence, report):
    import msvcrt

    validate_document(report, "docker-host-preflight")
    outputs = {"docker-preflight.json": canonical_json(report),
               "docker-preflight.md": markdown(report).encode("utf-8")}
    outputs["SHA256SUMS"] = "".join(f"{digest(content)}  {name}\n" for name, content in outputs.items()).encode()
    with guarded_directory(path.parent), open_parent(path.parent) as parent:
        require(str(output_path(str(path))) == str(path) and beneath(path, evidence), "UNSAFE_OUTPUT_PATH")
        with create_stage(parent) as stage:
            created = []
            try:
                for name, content in outputs.items():
                    handle = create_report(stage, name)
                    created.append(name)
                    try:
                        descriptor = msvcrt.open_osfhandle(handle.value, os.O_WRONLY | os.O_BINARY)
                    except OSError:
                        close_handle(handle)
                        raise
                    with os.fdopen(descriptor, "wb") as stream:
                        stream.write(content)
                        stream.flush()
                        os.fsync(stream.fileno())
                rename_stage(stage, parent, path.name)
            except BaseException:
                discard_stage(stage, created)
                raise


def bounded_input(path):
    with guarded_directory(path.parent):
        info = path.lstat()
        require(info.st_nlink == 1 and 0 < info.st_size <= 65536, "UNSAFE_PLAN_FILE")
        with path.open("rb") as stream:
            require(os.path.samestat(info, os.fstat(stream.fileno())), "UNSAFE_PLAN_FILE")
            raw = stream.read(65537)
    require(len(raw) <= 65536, "UNSAFE_PLAN_FILE")
    return raw


def validate_publisher_receipts(config, evidence, plan):
    for image in plan["images"]:
        receipt_hash = image["publisherReceiptSha256"]
        path = existing_local_input(str(config.parent / "receipts" / f"{receipt_hash}.json"), directory=False)
        require(not beneath(path, evidence), "UNSAFE_CUSTODY_BOUNDARY")
        raw = bounded_input(path)
        require(digest(raw) == receipt_hash, "PUBLISHER_RECEIPT_DIGEST_MISMATCH")
        receipt = json_result(raw, dict)
        proof = receipt.get("proofSha256")
        require(isinstance(proof, str) and re.fullmatch(r"[a-f0-9]{64}", proof) and nonzero_digest(proof),
                "INVALID_PUBLISHER_RECEIPT")
        expected = {"schema": "APP02B-LAB-DOCKER-PUBLISHER-RECEIPT-V1", "reference": image["reference"],
                    "platform": "linux/amd64", "proofSha256": proof}
        require(receipt == expected, "INVALID_PUBLISHER_RECEIPT")


def run(config_file, output_directory, pwsh):
    folders = verified_windows_folders()
    config = existing_local_input(config_file, directory=False)
    path = output_path(output_directory)
    raw = bounded_input(config)
    plan = strict_json(raw)
    validate_plan(plan)
    evidence = existing_local_input(plan["evidenceRoot"], directory=True)
    require(beneath(path, evidence) and not beneath(config, evidence), "UNSAFE_CUSTODY_BOUNDARY")
    validate_publisher_receipts(config, evidence, plan)
    initial = source_info()
    host = {}
    try:
        issue = collect_inventory(pwsh, plan, host, folders)
        report = evaluate(plan, host, initial, digest(raw), issue)
    except (LabError, PermissionError) as error:
        issue = "HOST_ACCESS_DENIED" if isinstance(error, PermissionError) else error.code
        report = evaluate(plan, {}, initial, digest(raw), issue
                          if issue in ("HOST_ACCESS_DENIED", "UNTRUSTED_NATIVE_EXECUTABLE") else "COLLECTION_ERROR")
    except (OSError, ValueError, TypeError, AttributeError, KeyError):
        report = evaluate(plan, {}, initial, digest(raw), "COLLECTION_ERROR")
    final = source_info()
    if final != initial:
        report["sourceHead"] = final[0]
        report["sourceState"] = "CHANGED_DURING_COLLECTION"
        report["checks"]["SOURCE"] = "FAIL"
        if report["readiness"] == "READY":
            report["readiness"] = "BLOCKED"
    publish(path, evidence, report)
    print(report["readiness"])
    return {"READY": 0, "COLLECTION_FAILED": 1, "BLOCKED": 2}[report["readiness"]]


def main():
    if os.name != "nt" or len(sys.argv) != 4 or not (
            sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode):
        print("COLLECTION_FAILED INVALID_INVOCATION", file=sys.stderr)
        return 1
    try:
        return run(*sys.argv[1:])
    except LabError as error:
        print(f"COLLECTION_FAILED {error.code}", file=sys.stderr)
    except Exception:
        print("COLLECTION_FAILED PREFLIGHT_RUNTIME_FAILED", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())

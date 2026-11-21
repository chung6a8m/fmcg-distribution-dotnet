"""Independent LAB-I01 planning contract and redacted readiness evaluation."""

from datetime import datetime
from hashlib import sha256
from ipaddress import IPv4Address, IPv4Network
from itertools import combinations
import os
from pathlib import Path
import re

from .contract import LabError, require, safe_path

SCHEMA = "APP02B-LAB-WINDOWS-PLAN-V1"
LOCK_PATH = "deploy/lab/windows/toolchain.lock.json"
GIB = 2**30
VM_RAM = {"APP": (4, 4), "DATA": (8, 12), "VAULT": (2, 4), "OBSERVER": (4, 4)}
NETWORKS = ("APP", "DATA", "VAULT", "MANAGEMENT")
NETWORK_SOURCES = ("INTERFACE", "ROUTE", "WINNAT", "HNS", "DOCKER")
HOST_TOOLS = ("git", "gh", "pwsh", "dotnet", "node", "npm", "python", "openssl", "ssh",
              "sqlcmd", "docker", "compose", "hyperv", "localdb", "wsl")
REQUIRED_TOOLS = {"git", "gh", "pwsh", "dotnet", "node", "npm", "python", "openssl", "ssh", "hyperv"}
GUEST_TOOLS = ("playwright", "caddy", "vault", "sqlserver", "linuxguest")
EDITIONS = {"Professional", "ProfessionalN", "ProfessionalEducation",
            "ProfessionalEducationN", "ProfessionalWorkstation", "ProfessionalWorkstationN",
            "Enterprise", "EnterpriseN", "EnterpriseS", "EnterpriseSN"}
PRIVATE_NETWORKS = tuple(map(IPv4Network, ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")))
RESERVED = re.compile(r"(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9¹²³])", re.I)
IDENTIFIER = re.compile(r"[a-z][a-z0-9-]{2,31}", re.I | re.ASCII)
REFERENCE = re.compile(r"ref-[0-9a-f]{16}", re.I | re.ASCII)
VERSION = re.compile(r"(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})"
                     r"(?:\.[0-9]{1,6}|p[0-9]{1,3})?")
SSH_VERSION = re.compile(r"[0-9]{1,3}\.[0-9]{1,3}p[0-9]{1,3}")
VERSION_REQUIREMENTS = {
    "dotnet": "10.0.404 latestPatch",
    "node": ">=26.13.1 <27",
    "python": ">=3.14 <3.15",
    "pwsh": ">=7 <8",
}
DIGEST = re.compile(r"[0-9a-f]{64}")
IMAGE = re.compile(r"(?:[a-z0-9][a-z0-9./_-]*[a-z0-9])@sha256:[a-f0-9]{64}")
PLAN_FIELDS = {
    "schema", "environmentId", "operatorHostRef", "profileGiB", "hostReserveGiB", "vms",
    "networks", "storageRoot", "evidenceRoot", "backupGiB", "evidenceGiB",
    "configurationPaths", "software", "tls", "tenantRefs", "toolchainLock",
}


def match(expression, value):
    return isinstance(value, str) and expression.fullmatch(value) is not None


def bounded_integer(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def windows_path(value):
    """Check spelling before Win32 can canonicalize it or interpret a device name."""
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z]:\\.{1,240}", value):
        return False
    return all(part and part not in (".", "..") and not part.endswith((" ", "."))
               and not part.startswith(" ") and not RESERVED.fullmatch(part.split(".")[0].rstrip())
               and not any(ord(c) < 32 or ord(c) == 127 or c in '<>:"/|?*' for c in part)
               for part in value[3:].split("\\"))


def check_location(path: Path, *, missing=False) -> Path:
    path = safe_path(path, missing=missing)
    if os.name == "nt":
        require_fixed_volume(path)
    # Resolve existing parents to expose any Win32 short-name aliases.
    canonical = safe_path(path.resolve(strict=False), missing=missing)
    if os.name == "nt":
        require_fixed_volume(canonical)
    require(not any(safe_git_marker(parent / ".git") for parent in (canonical, *canonical.parents)),
            "GIT_LOCATION_FORBIDDEN")
    require(not any(part.casefold() == ".git" for part in canonical.parts), "GIT_LOCATION_FORBIDDEN")
    return canonical


def require_fixed_volume(path):
    import ctypes

    # SUBST aliases can point at a directory on a fixed volume.
    require(re.fullmatch(r"[A-Za-z]:\\", path.anchor) is not None, "NONLOCAL_VOLUME")
    kernel = ctypes.windll.kernel32
    target = ctypes.create_unicode_buffer(32768)
    require(kernel.GetDriveTypeW(path.anchor) == 3
            and kernel.QueryDosDeviceW(path.drive, target, len(target))
            and target.value.casefold().startswith(r"\device\harddiskvolume"),
            "NONLOCAL_VOLUME")
    filesystem = ctypes.create_unicode_buffer(32)
    require(kernel.GetVolumeInformationW(path.anchor, None, 0, None, None, None,
                                         filesystem, len(filesystem))
            and filesystem.value in ("NTFS", "ReFS"), "UNSUPPORTED_FILESYSTEM")


def safe_git_marker(path):
    try:
        path.lstat()
    except (FileNotFoundError, NotADirectoryError):
        return False
    return True


def parse_network(value, *, strict=True):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9.]{7,15}/(?:[0-9]|[12][0-9]|3[0-2])", value):
        raise ValueError("INVALID_CIDR")
    return IPv4Network(value, strict=strict)


def validate_environment(document: dict) -> list[str]:
    """Return stable sorted issue codes without I/O or disclosure of input values."""
    issues = set()

    def fields(value, required, optional=()):
        if not isinstance(value, dict):
            issues.add("INVALID_STRUCTURE")
            return {}
        if value.keys() - set(required) - set(optional):
            issues.add("UNKNOWN_FIELD")
        if set(required) - value.keys():
            issues.add("MISSING_FIELD")
        return value

    plan = fields(document, PLAN_FIELDS)
    if not plan:
        return sorted(issues)
    if plan.get("schema") != SCHEMA:
        issues.add("INVALID_PLAN_SCHEMA")
    if not match(IDENTIFIER, plan.get("environmentId")):
        issues.add("INVALID_IDENTIFIER")
    if not match(REFERENCE, plan.get("operatorHostRef")):
        issues.add("INVALID_REFERENCE")

    vms = fields(plan.get("vms"), ("APP", "DATA", "VAULT"), ("OBSERVER",))
    vm_ids = []
    for role, bounds in VM_RAM.items():
        if role not in vms:
            continue
        vm = fields(vms[role], ("id", "ramGiB", "vhdxGiB"))
        if match(IDENTIFIER, vm.get("id")):
            vm_ids.append(vm["id"].casefold())
        else:
            issues.add("INVALID_IDENTIFIER")
        if not bounded_integer(vm.get("ramGiB"), *bounds) or not bounded_integer(vm.get("vhdxGiB"), 16, 4096):
            issues.add("INVALID_RESOURCE_BUDGET")
    if len(set(vm_ids)) != len(vm_ids):
        issues.add("DUPLICATE_VM_ID")
    if (not bounded_integer(plan.get("profileGiB"), 32, 64) or plan["profileGiB"] not in (32, 64)
            or not bounded_integer(plan.get("hostReserveGiB"), 8, 64)
            or not bounded_integer(plan.get("backupGiB"), 1, 4096)
            or not bounded_integer(plan.get("evidenceGiB"), 1, 4096)):
        issues.add("INVALID_RESOURCE_BUDGET")
    if "INVALID_RESOURCE_BUDGET" not in issues and "INVALID_STRUCTURE" not in issues:
        if (sum(vms[role]["ramGiB"] for role in VM_RAM if role in vms) + plan["hostReserveGiB"]
                > plan["profileGiB"]):
            issues.add("PROFILE_RAM_EXCEEDED")

    planned_networks = fields(plan.get("networks"), NETWORKS)
    cidrs = []
    for role in NETWORKS:
        if role not in planned_networks:
            continue
        network = fields(planned_networks[role], ("cidr", "gateway"))
        try:
            cidr = parse_network(network.get("cidr"))
            if cidr.prefixlen > 30 or not any(cidr.subnet_of(private) for private in PRIVATE_NETWORKS):
                raise ValueError("INVALID_CIDR")
        except ValueError:
            issues.add("INVALID_CIDR")
            continue
        cidrs.append(cidr)
        try:
            gateway = network.get("gateway")
            if not isinstance(gateway, str):
                raise ValueError("INVALID_GATEWAY")
            if not cidr.network_address < IPv4Address(gateway) < cidr.broadcast_address:
                raise ValueError("INVALID_GATEWAY")
        except ValueError:
            issues.add("INVALID_GATEWAY")
    if any(left.overlaps(right) for left, right in combinations(cidrs, 2)):
        issues.add("NETWORK_OVERLAP")

    config = fields(plan.get("configurationPaths"), ("guestConfig",))
    roots = [plan.get("evidenceRoot"), plan.get("storageRoot")]
    if not all(map(windows_path, (*roots, config.get("guestConfig")))):
        issues.add("UNSAFE_PATH")
    else:
        evidence, storage = (item.casefold() for item in roots)
        if (evidence == storage or evidence.startswith(storage + "\\") or storage.startswith(evidence + "\\")):
            issues.add("OVERLAPPING_ROOTS")
        if evidence[:2] != storage[:2]:
            issues.add("STORAGE_VOLUME_MISMATCH")
    if plan.get("toolchainLock") != LOCK_PATH:
        issues.add("INVALID_TOOLCHAIN_REFERENCE")
    software = fields(plan.get("software"), ("linuxGuest", "sqlServer", "vault", "appRuntime"))
    for role in ("linuxGuest", "sqlServer", "vault", "appRuntime"):
        value = software.get(role)
        if value is not None and value != "NOT_PINNED" and not match(IMAGE, value):
            issues.add("INVALID_SOFTWARE_IDENTITY")
        if role in software and value is None:
            issues.add("INVALID_SOFTWARE_IDENTITY")
    tls = fields(plan.get("tls"), ("caRef",))
    tenants = fields(plan.get("tenantRefs"), ("A", "B"))
    refs = [tenants[key] for key in ("A", "B") if key in tenants]
    if ("caRef" in tls and not match(REFERENCE, tls["caRef"])) or not all(match(REFERENCE, ref) for ref in refs):
        issues.add("INVALID_REFERENCE")
    if len(refs) == 2 and all(match(REFERENCE, ref) for ref in refs) and refs[0].casefold() == refs[1].casefold():
        issues.add("DUPLICATE_TENANT_ID")
    return sorted(issues)


def validate_lock(lock):
    def valid_entry(name, tool):
        if not isinstance(tool, dict) or tool.keys() != {
            "required", "requiredVersion", "proposedVersion", "origin", "artifactIdentity", "sha256",
            "imageDigest", "verificationRule", "platform", "pinState",
        }:
            return False
        if (tool["required"] is not (name in REQUIRED_TOOLS)
                or tool["requiredVersion"] != VERSION_REQUIREMENTS.get(name)
                or not isinstance(tool["origin"], str) or not tool["origin"].startswith("https://")
                or not all(isinstance(tool[key], str) and tool[key] for key in ("verificationRule", "platform"))
                or not all(tool[key] is None or isinstance(tool[key], str)
                           for key in ("requiredVersion", "proposedVersion"))):
            return False
        if tool["pinState"] == "NOT_PINNED":
            return all(tool[key] is None for key in ("artifactIdentity", "sha256", "imageDigest"))
        if (tool["pinState"] != "PINNED" or not tool["proposedVersion"]
                or not match(re.compile(r"[A-Za-z0-9][A-Za-z0-9._/+@:-]{0,199}"), tool["artifactIdentity"])
                or re.search(r"\blatest\b", tool["artifactIdentity"], re.I)):
            return False
        return ((match(DIGEST, tool["sha256"]) and tool["sha256"] != "0" * 64
                 and tool["imageDigest"] is None)
                or (tool["sha256"] is None and match(IMAGE, tool["imageDigest"])
                    and not tool["imageDigest"].endswith("0" * 64)))

    require(isinstance(lock, dict) and lock.keys() == {"schema", "tools"}
            and lock["schema"] == "APP02B-LAB-WINDOWS-TOOLCHAIN-V1"
            and isinstance(lock["tools"], dict) and lock["tools"].keys() == set(HOST_TOOLS + GUEST_TOOLS)
            and all(valid_entry(name, row) for name, row in lock["tools"].items()),
            "INVALID_TOOLCHAIN_LOCK")


def version_matches(name, version):
    if name not in VERSION_REQUIREMENTS:
        return True
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        return False
    parts = VERSION.fullmatch(version).groups()
    major, minor, patch = map(int, parts)
    return {
        "dotnet": (major, minor) == (10, 0) and 404 <= patch < 500,
        "node": major == 26 and (minor, patch) >= (13, 1),
        "python": (major, minor) == (3, 14),
        "pwsh": major == 7,
    }.get(name, True)


def evaluate(plan, lock, host):
    """Evaluate raw observations and construct the only representation fit for reporting."""
    validate_lock(lock)
    report = {"checks": [], "readiness": "READY_FOR_LAB_SETUP",
              "authorization": "NOT_EVALUATED", "configurationValidation": "VALID",
              "hostRef": None}
    if isinstance(host.get("hostKey"), str) and host["hostKey"]:
        report["hostRef"] = "ref-" + sha256(("LAB-I01:" + host["hostKey"]).encode()).hexdigest()[:16]

    def add(check_id, value, passed, reason, *, required=True, expected=None, status=None):
        result = status or ("PASS" if passed else "BLOCKED" if required else "NOT_TESTED")
        report["checks"].append({
            "id": check_id, "required": required, "observed": "UNKNOWN" if value is None else value,
            "status": result, "reasonCode": "CONFIRMED" if result == "PASS" else reason,
            "expected": expected,
        })
        if required and result != "PASS":
            report["readiness"] = "BLOCKED"

    def numeric(key):
        value = host.get(key)
        return value if bounded_integer(value, 0, 2**63 - 1) else None

    def evidence_version(key):
        value = host.get(key)
        return value if isinstance(value, str) and re.fullmatch(r"[0-9.]{1,24}", value) else None

    add("HOST_IDENTITY", report["hostRef"], report["hostRef"] is not None, "CAPABILITY_NOT_CONFIRMED")
    matches_host = report["hostRef"] == plan["operatorHostRef"].lower()
    add("HOST_TARGET", None if report["hostRef"] is None else matches_host, matches_host,
        "HOST_REFERENCE_UNAVAILABLE" if report["hostRef"] is None else "HOST_REFERENCE_MISMATCH")
    editions = EDITIONS | {"Core", "CoreN", "CoreSingleLanguage", "Education", "EducationN"}
    edition = host.get("edition")
    edition = edition if isinstance(edition, str) and edition in editions else None
    add("WINDOWS_EDITION", edition, edition in EDITIONS, "UNSUPPORTED_WINDOWS_EDITION")
    for key, check in (("version", "OS_VERSION"), ("build", "OS_BUILD")):
        version = evidence_version(key)
        add(check, version, version is not None, "CAPABILITY_NOT_CONFIRMED")
    for key, check in (("cpuArch", "CPU_ARCH"), ("osArch", "OS_ARCH")):
        arch = host.get(key)
        arch = arch.upper() if isinstance(arch, str) and arch.upper() in ("X64", "ARM64", "X86", "ARM") else None
        add(check, arch, arch == "X64", "UNSUPPORTED_CPU_ARCHITECTURE", expected="X64")
    model = host.get("cpuModel")
    if isinstance(model, str) and model.strip():
        sku = re.search(r"\b(?:i[3579]-[0-9]{4,5}[A-Z]{0,3}|Ryzen [3579] [0-9]{4}[A-Z0-9]{0,4}"
                        r"|Core(?: Ultra)? [3579] [0-9]{3}[A-Z]{0,3})\b", model, re.I)
        model = sku[0] if sku else "ref-" + sha256(model.encode()).hexdigest()[:16]
    else:
        model = None
    add("CPU_MODEL", model, model is not None, "CAPABILITY_NOT_CONFIRMED")
    for key, check in (("cores", "CPU_CORES"), ("logical", "CPU_LOGICAL_PROCESSORS")):
        value = numeric(key)
        add(check, value, value is not None and value > 0, "CAPABILITY_NOT_CONFIRMED")

    for key, check in (("firmwareVirtualization", "HYPERV_FIRMWARE"),
                       ("slat", "HYPERV_SLAT"), ("vmMonitor", "HYPERV_VM_MONITOR"),
                       ("hypervisor", "HYPERV_HYPERVISOR"), ("gen2", "HYPERV_GEN2_CAPABILITY")):
        value = host.get(key)
        value = value if type(value) is bool else None
        add(check, value, value is True, "CAPABILITY_NOT_CONFIRMED" if value is None else "CAPABILITY_UNAVAILABLE")
    feature = host.get("hyperVFeature")
    feature = feature if feature in ("Enabled", "Disabled", "EnablePending", "DisablePending", "Unavailable") else None
    add("HYPERV_FEATURE", feature, feature == "Enabled",
        "CAPABILITY_NOT_CONFIRMED" if feature is None else "HYPERV_FEATURE_NOT_ENABLED")
    reboot = host.get("reboot")
    reboot = reboot if type(reboot) is bool else None
    add("REBOOT_PENDING", reboot, reboot is False,
        "REBOOT_STATE_UNKNOWN" if reboot is None else "REBOOT_REQUIRED")
    add("GEN2_LINUX_BOOT", None, False, "VM_BOOT_OUTSIDE_SCOPE", required=False)

    vm_budget = {role: {key: plan["vms"][role][key] for key in ("ramGiB", "vhdxGiB")}
                 for role in VM_RAM if role in plan["vms"]}
    vm_ram = sum(vm["ramGiB"] for vm in vm_budget.values())
    vm_disk = sum(vm["vhdxGiB"] for vm in vm_budget.values())
    disk_need = max(200, vm_disk + plan["backupGiB"] + plan["evidenceGiB"])
    report["resourceBudget"] = {
        "profileGiB": plan["profileGiB"], "hostReserveGiB": plan["hostReserveGiB"],
        "vmAllocation": vm_budget, "backupGiB": plan["backupGiB"],
        "evidenceGiB": plan["evidenceGiB"], "freeDiskRequiredGiB": disk_need,
    }
    for key, check, threshold, shortage in (
        ("totalRam", "RAM_TOTAL", plan["profileGiB"], "INSUFFICIENT_RAM"),
        ("freeRam", "RAM_AVAILABLE", vm_ram + plan["hostReserveGiB"], "INSUFFICIENT_RAM"),
        ("diskCapacity", "DISK_CAPACITY", disk_need, "INSUFFICIENT_DISK"),
        ("diskFree", "DISK_FREE", disk_need, "INSUFFICIENT_DISK"),
    ):
        value = numeric(key)
        add(check, value, value is not None and value >= threshold * GIB,
            "CAPABILITY_NOT_CONFIRMED" if value is None else shortage, expected=threshold * GIB)
    media = host.get("diskMedia")
    media = media if media in ("SSD", "HDD", "SCM") else None
    add("DISK_MEDIA", media, media == "SSD", "DISK_SSD_NOT_CONFIRMED", expected="SSD")

    proposed = {role: parse_network(plan["networks"][role]["cidr"]) for role in NETWORKS}
    conflicts = set()
    inventory_complete = True
    for source in NETWORK_SOURCES:
        raw = host.get("networks", {}).get(source)
        try:
            observed = [parse_network(value, strict=False) for value in raw] if isinstance(raw, list) else None
        except ValueError:
            observed = None
        if observed is None:
            inventory_complete = False
        else:
            for net in observed:
                if source == "ROUTE" and net.prefixlen == 0:
                    continue
                if source == "INTERFACE" and net.subnet_of(IPv4Network("127.0.0.0/8")):
                    continue
                conflicts.update(f"{role}/{source}" for role, candidate in proposed.items() if candidate.overlaps(net))
        add(f"NETWORK_{source}", len(observed) if observed is not None else None, observed is not None,
            "NETWORK_INVENTORY_UNAVAILABLE")
    add("NETWORK_OVERLAP", sorted(conflicts), inventory_complete and not conflicts,
        "NETWORK_CONFLICT" if conflicts else "NETWORK_INVENTORY_UNAVAILABLE",
        status="NOT_TESTED" if not conflicts and not inventory_complete else None)
    for key, check in (("switchCount", "HYPERV_SWITCHES"), ("dnsCount", "DNS_RESOLVERS")):
        count = numeric(key)
        add(check, count, count is not None, "NETWORK_INVENTORY_UNAVAILABLE")

    time_service = host.get("timeService")
    time_service = time_service if time_service in ("Running", "Stopped", "Absent") else None
    add("TIME_SERVICE", time_service, time_service == "Running", "TIME_SERVICE_UNAVAILABLE")
    source_kind = host.get("timeSourceKind")
    source_kind = source_kind if source_kind in ("LOCAL_CLOCK", "VM_CLOCK", "EXTERNAL_CONFIGURED") else None
    add("TIME_SOURCE", source_kind, source_kind == "EXTERNAL_CONFIGURED", "TIME_SOURCE_NOT_CONFIRMED")
    last_sync = host.get("lastSyncUtc")
    if isinstance(last_sync, str):
        try:
            last_sync = datetime.strptime(last_sync, "%Y-%m-%dT%H:%M:%SZ").strftime("%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            last_sync = None
    else:
        last_sync = None
    add("TIME_LAST_SYNC", last_sync, last_sync is not None, "LAST_SYNC_UNAVAILABLE", required=False)
    add("CLOCK_UNCERTAINTY", None, False, "NO_INDEPENDENT_TIME_COMPARISON", required=False)

    for name in HOST_TOOLS:
        version = host.get("tools", {}).get(name)
        valid = match(VERSION, version) or (name == "ssh" and match(SSH_VERSION, version))
        observed = version if valid else "UNAVAILABLE" if version is not None else None
        required = name in REQUIRED_TOOLS
        passed = valid and version_matches(name, version)
        reason = ("MISSING_TOOL" if observed is None else "VERSION_UNAVAILABLE"
                  if observed == "UNAVAILABLE" else "VERSION_MISMATCH")
        add(f"TOOL_{name.upper()}", observed, passed, reason, required=required,
            status="NOT_APPLICABLE" if not required and observed is None else None,
            expected=lock["tools"][name]["requiredVersion"])

    for name in HOST_TOOLS + GUEST_TOOLS:
        tool = lock["tools"][name]
        pinned = tool["pinState"] == "PINNED"
        add(f"ARTIFACT_{name.upper()}", tool["pinState"], False,
            "ARTIFACT_BYTES_NOT_INSPECTED" if pinned else "NOT_PINNED",
            required=False, status="NOT_TESTED" if pinned else "BLOCKED")
        add(f"SIGNATURE_{name.upper()}", None, False, "SIGNATURE_VERIFICATION_UNAVAILABLE",
            required=False)
    for role, value in plan["software"].items():
        add(f"SOFTWARE_{role.upper()}", "NOT_PINNED" if value == "NOT_PINNED" else "DIGEST_PROPOSED",
            False, "NOT_PINNED" if value == "NOT_PINNED" else "ARTIFACT_BYTES_NOT_INSPECTED",
            required=False, status="BLOCKED" if value == "NOT_PINNED" else "NOT_TESTED")
    report["missingPrerequisites"] = [row["id"] for row in report["checks"]
                                      if row["required"] and row["status"] != "PASS"]
    report["installationPrerequisites"] = [row["id"] for row in report["checks"]
                                           if row["id"].startswith(("ARTIFACT_", "SOFTWARE_"))
                                           or row["reasonCode"] in ("MISSING_TOOL", "VERSION_MISMATCH")]
    return report


def markdown(report):
    lines = [
        "# LAB-I01 Windows preflight", "",
        f"**Host readiness** {report['readiness']}", "",
        f"**Evidence classification** {report.get('evidenceClassification', 'SYNTHETIC_TEST')}", "",
        "Host readiness is informational. Setup authorization and physical acceptance are separate.", "",
        f"**Source HEAD** {report.get('sourceHead') or 'UNKNOWN'}", "",
        f"**Collection UTC** {report.get('collectedUtc', 'UNKNOWN')}", "",
        f"**Host reference** {report.get('hostRef') or 'UNKNOWN'}", "",
        f"**Configuration** {report['configurationValidation']}", "",
    ]
    if report.get("issueCodes"):
        lines.extend(["**Issue codes** " + ", ".join(report["issueCodes"]), ""])
    budget = report.get("resourceBudget")
    if budget:
        lines.extend(["## Planning allocation", "", "| Role | RAM GiB | VHDX GiB |",
                      "| --- | ---: | ---: |"])
        lines.extend(f"| {role} | {vm['ramGiB']} | {vm['vhdxGiB']} |"
                     for role, vm in budget["vmAllocation"].items())
        lines.extend(["", f"Profile {budget['profileGiB']} GiB. Host reserve "
                      f"{budget['hostReserveGiB']} GiB. Backups {budget['backupGiB']} GiB. "
                      f"Evidence {budget['evidenceGiB']} GiB. Free disk target "
                      f"{budget['freeDiskRequiredGiB']} GiB.", ""])
    lines.extend(["## Checks", "", "| Check | Required | Observed | Status | Reason |",
                  "| --- | --- | --- | --- | --- |"])
    for row in report["checks"]:
        value = row["observed"]
        value = ", ".join(value) or "None" if isinstance(value, list) else value
        lines.append(f"| {row['id']} | {row['required']} | {value} | {row['status']} | {row['reasonCode']} |")
    lines.extend(["", "## Operator follow-up", "",
                  "1. Resolve configuration and collection issues.",
                  "2. Address required blocked checks. The overlap row identifies planning roles and inventory sources.",
                  "3. Review installation prerequisites and verify publisher signatures and artifact digests before use.",
                  "", "Readings are point-in-time observations. Installer bytes, boot, clock drift and physical workload "
                  "acceptance require separate verification.", ""])
    return "\n".join(lines)

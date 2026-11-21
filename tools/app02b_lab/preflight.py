"""Windows preflight entry point. The isolated LAB-E05 launcher is independent."""

import base64
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys

# PowerShell launches this exact file with -I -S -B. Imports are from this checkout.
ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab.contract import (LabError, guarded_directory, read_json, require,
                                       strict_json)
from tools.app02b_lab.environment import (LOCK_PATH, check_location, evaluate, markdown,
                                          validate_environment, windows_path)

from tools.app02b_lab.windows_output import (close_handle, create_report, create_stage, discard_stage,
                                            open_parent, rename_stage)

SCHEMA = "APP02B-LAB-WINDOWS-PREFLIGHT-V1"
COLLECTOR = r"""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    Import-Module (Join-Path $PWD 'scripts/lab/Lab.Common.psm1')
    $drive = [Console]::ReadLine()
    if ($drive -cnotmatch '^[A-Za-z]$') { exit 1 }
    Get-LabWindowsInventory -DriveLetter $drive
    exit 0
} catch { exit 1 }
"""


def collect(pwsh, drive):
    command = base64.b64encode(COLLECTOR.encode("utf-16-le")).decode("ascii")
    try:
        result = subprocess.run([pwsh, "-NoProfile", "-NonInteractive", "-EncodedCommand", command],
                                input=drive.encode() + b"\n", cwd=ROOT, capture_output=True, timeout=180)
        require(result.returncode == 0 and len(result.stdout) < 4_000_000, "INVENTORY_COLLECTION_FAILED")
        host = strict_json(result.stdout)
        require(isinstance(host, dict) and all(key in host for key in ("hostKey", "tools", "networks"))
                and isinstance(host["tools"], dict) and isinstance(host["networks"], dict),
                "INVENTORY_COLLECTION_FAILED")
        return host
    except (OSError, subprocess.SubprocessError, LabError):
        raise LabError("INVENTORY_COLLECTION_FAILED") from None


def source_info():
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    try:
        head = subprocess.run(["git.exe", "--no-optional-locks", "rev-parse", "--verify", "HEAD"],
                              cwd=ROOT, capture_output=True, env=env, timeout=10, check=True).stdout.strip()
        state = subprocess.run(["git.exe", "--no-optional-locks", "-c", "core.fsmonitor=false",
                                "status", "--porcelain",
                                "--untracked-files=normal", "--ignore-submodules=all"],
                               cwd=ROOT, capture_output=True, env=env, timeout=15, check=True).stdout
        if re.fullmatch(rb"[a-f0-9]{40}", head):
            return head.decode("ascii"), "DIRTY" if state else "CLEAN"
    except (OSError, subprocess.SubprocessError):
        pass
    return None, "UNKNOWN"


def existing_local_input(value, *, directory):
    require(windows_path(value), "UNSAFE_PATH")
    location = check_location(Path(value))
    require(location.is_dir() if directory else location.is_file(), "INVALID_PATH_TYPE")
    return location


def output_path(value):
    require(windows_path(value), "UNSAFE_PATH")
    path = check_location(Path(value), missing=True)
    require(not path.exists(), "OUTPUT_ALREADY_EXISTS")
    return path


def beneath(path, root):
    # Resolved paths preserve the on-disk case. A case-sensitive Windows
    # directory can contain two distinct names differing only by case.
    return any(str(parent) == str(root) for parent in path.parents)


def publish_reports(path, report, evidence_root=None):
    """Publish both reports at once by moving a sibling directory on Windows."""
    import msvcrt

    with guarded_directory(path.parent), open_parent(path.parent) as parent:
        current = output_path(str(path))
        require(str(current) == str(path), "OUTPUT_PATH_CHANGED")
        if evidence_root is not None:
            require(beneath(current, evidence_root) and str(check_location(evidence_root)) == str(evidence_root),
                    "OUTPUT_OUTSIDE_EVIDENCE_ROOT")
        files = {"preflight.md": markdown(report), "preflight.json": json.dumps(report, indent=2) + "\n"}
        with create_stage(parent) as stage:
            created = []
            try:
                for name, content in files.items():
                    handle = create_report(stage, name)
                    created.append(name)
                    try:
                        descriptor = msvcrt.open_osfhandle(handle.value, os.O_WRONLY | os.O_BINARY)
                    except OSError:
                        close_handle(handle)
                        raise
                    with os.fdopen(descriptor, "wb") as stream:
                        stream.write(content.encode("utf-8"))
                        stream.flush()
                        os.fsync(stream.fileno())
                rename_stage(stage, parent, path.name)
            except BaseException:
                discard_stage(stage, created)
                raise


def failed(issues, *, configuration="NOT_TESTED"):
    return {"readiness": "COLLECTION_FAILED", "authorization": "NOT_EVALUATED",
            "configurationValidation": configuration, "checks": [], "issueCodes": issues,
            "hostRef": None, "missingPrerequisites": [], "installationPrerequisites": []}


def run(config_file, destination, pwsh):
    path = output_path(destination)
    evidence = None
    initial_source = source_info()
    try:
        config = existing_local_input(config_file, directory=False)
        require(config.stat().st_size <= 256_000, "CONFIGURATION_TOO_LARGE")
        plan = read_json(config)
    except LabError as error:
        report = failed([error.code], configuration="INVALID")
    except OSError:
        report = failed(["CONFIGURATION_UNREADABLE"], configuration="INVALID")
    else:
        issues = validate_environment(plan)
        if issues:
            report = failed(issues, configuration="INVALID")
        else:
            # A valid configuration supplies the custody boundary. Do not report to
            # a destination outside that boundary, even on subsequent probe errors.
            evidence = existing_local_input(plan["evidenceRoot"], directory=True)
            require(beneath(path, evidence), "OUTPUT_OUTSIDE_EVIDENCE_ROOT")
            try:
                storage = existing_local_input(plan["storageRoot"], directory=True)
                require(storage != evidence and storage not in evidence.parents and evidence not in storage.parents,
                        "OVERLAPPING_ROOTS")
                require(storage.drive.casefold() == evidence.drive.casefold(), "STORAGE_VOLUME_MISMATCH")
                lock = read_json(ROOT / LOCK_PATH)
                report = evaluate(plan, lock, collect(pwsh, storage.drive[0]))
            except LabError as error:
                report = failed([error.code], configuration="VALID")
            except (OSError, ValueError, TypeError, AttributeError, KeyError):
                report = failed(["INVENTORY_COLLECTION_FAILED"], configuration="VALID")

    head, source_state = source_info()
    if (head, source_state) != initial_source:
        source_state = "CHANGED_DURING_COLLECTION"
    if report["readiness"] != "COLLECTION_FAILED":
        bound = head is not None and source_state == "CLEAN"
        report["checks"].append({
            "id": "SOURCE_CHECKOUT", "required": True, "observed": source_state,
            "expected": "CLEAN", "status": "PASS" if bound else "BLOCKED",
            "reasonCode": "CONFIRMED" if bound else "SOURCE_CHECKOUT_NOT_CONFIRMED_CLEAN",
        })
        if not bound:
            report["missingPrerequisites"].append("SOURCE_CHECKOUT")
            report["readiness"] = "BLOCKED"

    report.update({
        "schema": SCHEMA, "collectedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sourceHead": head, "sourceState": source_state,
        "evidenceClassification": ("HOST_PREFLIGHT_COLLECTION_FAILED" if report["readiness"] == "COLLECTION_FAILED"
                                   else "HOST_PREFLIGHT_OBSERVED"),
    })
    publish_reports(path, report, evidence)
    return {"READY_FOR_LAB_SETUP": 0, "COLLECTION_FAILED": 1, "BLOCKED": 2}[report["readiness"]], report["readiness"]


def main():
    if os.name != "nt" or len(sys.argv) != 4 or not (
            sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode):
        print("COLLECTION_FAILED INVALID_INVOCATION", file=sys.stderr)
        return 1
    try:
        code, readiness = run(*sys.argv[1:])
    except LabError as error:
        print(f"COLLECTION_FAILED {error.code}", file=sys.stderr)
        return 1
    except Exception:  # Native diagnostics can contain paths or other private values.
        print("COLLECTION_FAILED PREFLIGHT_RUNTIME_FAILED", file=sys.stderr)
        return 1
    print(readiness)
    return code


if __name__ == "__main__":
    raise SystemExit(main())

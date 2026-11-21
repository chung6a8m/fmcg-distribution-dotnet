"""LAB-D02: plan and stage a stopped, private foundation on an enrolled daemon."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import os
from pathlib import Path
import re
import socket
import sys

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab import docker_preflight as d01
from tools.app02b_lab.contract import LabError, canonical_json, digest, guarded_directory, require, strict_json
from tools.app02b_lab.preflight import beneath, existing_local_input, output_path, source_info
from tools.app02b_lab.schema import validate_document
from tools.app02b_lab.windows_output import close_handle, create_report, create_stage, discard_stage, open_parent, rename_stage

CONFIG = "APP02B-LAB-DOCKER-REHEARSAL-CONFIG-V1"
PLAN = "APP02B-LAB-DOCKER-REHEARSAL-PLAN-V1"
ENROLLMENT = "APP02B-LAB-DOCKER-ENROLLMENT-V1"
RESULT = "APP02B-LAB-DOCKER-REHEARSAL-RESULT-V1"
OPERATION = "CREATE_STOPPED_FOUNDATION"
HEX = re.compile(r"[a-f0-9]{64}", re.ASCII)
NONCE = re.compile(r"[a-f0-9]{32}", re.ASCII)
IMAGE = re.compile(r"[a-z0-9][a-z0-9.:-]*/[a-z0-9][a-z0-9._/-]*@sha256:[a-f0-9]{64}", re.ASCII)


def object_fields(value, fields, code):
    require(type(value) is dict and set(value) == set(fields.split()), code)


def utc_now():
    return datetime.now(timezone.utc)


def timestamp(now):
    return now.strftime("%Y-%m-%dT%H:%M:%SZ")


def authorize(raw, approval_sha, plan_sha, nonce, *, now):
    """Verify the operator-supplied approval fingerprint; D02 never issues approval."""
    require(isinstance(approval_sha, str) and HEX.fullmatch(approval_sha)
            and digest(raw) == approval_sha, "ENROLLMENT_UNAPPROVED")
    enrollment = strict_json(raw)
    object_fields(enrollment, "schema operation nonce planSha256 expiresUtc", "INVALID_ENROLLMENT")
    require(enrollment["schema"] == ENROLLMENT and enrollment["operation"] == OPERATION
            and enrollment["nonce"] == nonce and enrollment["planSha256"] == plan_sha,
            "ENROLLMENT_SCOPE_MISMATCH")
    try:
        expiry = datetime.strptime(enrollment["expiresUtc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except (TypeError, ValueError):
        raise LabError("INVALID_ENROLLMENT_EXPIRY") from None
    require(timestamp(expiry) == enrollment["expiresUtc"] and now < expiry, "ENROLLMENT_EXPIRED")


def require_baseline(report, plan, plan_sha, source):
    require(report.get("schema") == "APP02B-LAB-DOCKER-HOST-PREFLIGHT-V1"
            and report.get("readiness") == "READY" and report.get("authorization") == "NOT_EVALUATED"
            and report.get("sourceState") == "CLEAN" and source == (plan["sourceHead"], "CLEAN")
            and report.get("sourceHead") == source[0] and report.get("planSha256") == plan_sha
            and report.get("hostRef") == plan["hostRef"] and report.get("engineRef") == plan["engineRef"]
            and all(d01.nonzero_digest(plan[key]) for key in ("hostRef", "engineRef"))
            and not report.get("issueCodes") and report.get("checks")
            and all(value == "PASS" for value in report["checks"].values()), "D01_BASELINE_MISMATCH")


def read_config(filename, pwsh):
    from tools.app02b_lab.docker_d02_host import assert_private

    config_path = existing_local_input(filename, directory=False)
    assert_private(pwsh, [config_path.parent, config_path])
    raw = d01.bounded_input(config_path)
    config = strict_json(raw)
    object_fields(config, "schema nonce baselinePlan baselineReport images", "INVALID_D02_CONFIG")
    require(config["schema"] == CONFIG and isinstance(config["nonce"], str)
            and NONCE.fullmatch(config["nonce"]) and config["nonce"] != "0" * 32, "INVALID_D02_CONFIG")
    images = config["images"]
    object_fields(images, "api worker", "INVALID_WORKLOAD_IMAGES")
    require(all(isinstance(ref, str) and IMAGE.fullmatch(ref)
                and d01.nonzero_digest(ref.split("@sha256:")[1]) for ref in images.values())
            and images["api"] != images["worker"], "INVALID_WORKLOAD_IMAGES")

    baseline_path = existing_local_input(config["baselinePlan"], directory=False)
    report_path = existing_local_input(config["baselineReport"], directory=False)
    sums = existing_local_input(str(report_path.parent / "SHA256SUMS"), directory=False)
    assert_private(pwsh, [baseline_path.parent, baseline_path, report_path.parent, report_path, sums])
    baseline_raw = d01.bounded_input(baseline_path)
    baseline = strict_json(baseline_raw)
    d01.validate_plan(baseline)
    evidence = existing_local_input(baseline["evidenceRoot"], directory=True)
    assert_private(pwsh, [evidence])
    require(report_path.name == "docker-preflight.json" and beneath(report_path.parent, evidence)
            and not beneath(config_path, evidence), "UNSAFE_D02_CUSTODY")
    report_raw = d01.bounded_input(report_path)
    md = existing_local_input(str(report_path.parent / "docker-preflight.md"), directory=False)
    assert_private(pwsh, [md])
    manifest = checksum_file({"docker-preflight.json": report_raw, "docker-preflight.md": d01.bounded_input(md)})
    require(d01.bounded_input(sums) == manifest, "D01_CHECKSUM_MISMATCH")
    report = strict_json(report_raw)
    validate_document(report, "docker-host-preflight")
    require_baseline(report, baseline, digest(baseline_raw), source_info())
    receipts = [existing_local_input(str(baseline_path.parent / "receipts" /
                                        f"{item['publisherReceiptSha256']}.json"), directory=False)
                for item in baseline["images"]]
    assert_private(pwsh, [receipts[0].parent, *receipts])
    d01.validate_publisher_receipts(baseline_path, evidence, baseline)
    return config, baseline, evidence, {"configSha256": digest(raw), "d01PlanSha256": digest(baseline_raw),
                                         "d01ReportSha256": digest(report_raw)}


def scoped_inventory(state):
    return {
        "containers": sorted(item["Name"].lstrip("/") for item in state["containers"]),
        "volumes": sorted(item["Name"] for item in state["volumes"]),
        "networks": sorted(item["Name"] for item in state["networks"]),
    }


def require_fresh_host(baseline, hashes, host, state, *, observe_ports=False):
    require(d01.identity_refs(host) == (baseline["hostRef"], baseline["engineRef"]), "D02_TARGET_CHANGED")
    owned = scoped_inventory(state)
    # validate_state has already checked that each omitted object is ours.
    # D01 still checks every foreign subnet, bind, reservation and fresh resource floor.
    scrubbed = {**host, "projects": [name for name in host["projects"] if name != baseline["project"]],
                "networks": [row for row in host["networks"] if row["name"] not in owned["networks"]],
                "volumes": [name for name in host["volumes"] if name not in owned["volumes"]],
                "containers": [row for row in host["containers"] if row["name"] not in owned["containers"]]}
    fresh = d01.evaluate(baseline, scrubbed, source_info(), hashes["d01PlanSha256"])
    failures = [name for name, result in fresh["checks"].items()
                if result != "PASS" and not (observe_ports and name == "PORTS")]
    require(not failures and not fresh["issueCodes"], "D02_FRESH_D01_BLOCKED", ",".join(failures))
    return fresh["checks"]["PORTS"]


def intent(config, baseline, hashes, spec, model, image_ids):
    return {"schema": PLAN, "operation": OPERATION, "sourceHead": baseline["sourceHead"], **hashes,
            "hostRef": baseline["hostRef"], "engineRef": baseline["engineRef"], "context": d01.CONTEXT,
            "endpoint": d01.ENDPOINT, "spec": spec, "images": config["images"], "imageIds": image_ids,
            "compose": model, "exposure": "NONE", "runtimeMode": "FOUNDATION_ONLY", "admission": "NO_GO"}


def require_plan(plan, expected):
    object_fields(plan, "intent observed", "INVALID_D02_PLAN")
    require(plan["intent"] == expected, "D02_PLAN_CHANGED")
    observed = plan["observed"]
    object_fields(observed, "containers networks volumes", "INVALID_D02_PLAN")
    require(all(type(names) is list and all(isinstance(name, str) for name in names)
                for names in observed.values()), "INVALID_D02_PLAN")


def apply_change(docker, spec, model, image_ids, *, ensure_current, reviewed_volumes=frozenset()):
    """Converge towards the enrolled stopped foundation. Every mutation has a guarded readback."""
    from tools.app02b_lab.docker_d02_topology import labels, validate_state

    project = spec["project"]
    owner = [argument for key, value in labels(spec).items() for argument in ("--label", f"{key}={value}")]
    seen_volumes = set(reviewed_volumes)

    def current(*, complete=False, refresh=False):
        if refresh:
            ensure_current()
        docker.verify()
        state = docker.state(project)
        volume_names = {row["Name"] for row in state["volumes"]}
        require(seen_volumes <= volume_names, "D02_OWNED_VOLUME_DISAPPEARED")
        validate_state(spec, model, state, image_ids, complete=complete)
        seen_volumes.update(volume_names)
        return state

    current(refresh=True)
    for entry in spec["networks"]:
        state = current(refresh=True)
        if not any(row["Name"] == entry["name"] for row in state["networks"]):
            docker.run("network", "create", "--driver", "bridge", "--internal", "--subnet", entry["subnet"],
                       *owner, entry["name"])
            state = current()
            require(any(row["Name"] == entry["name"] for row in state["networks"]), "D02_NETWORK_MISSING")
    for volume in spec["volumes"]:
        state = current(refresh=True)
        if volume not in {row["Name"] for row in state["volumes"]}:
            docker.run("volume", "create", "--name", volume, *owner)
            seen_volumes.add(volume)
            current()
    state = current(refresh=True)
    if len(state["containers"]) != len(model["services"]):
        docker.run("compose", "--project-name", project, "--env-file", os.devnull, "-f", "-", "up",
                   "--no-start", "--no-recreate", "--no-build", "--pull", "never",
                   input_data=canonical_json(model))
    return current(complete=True, refresh=True)


@contextmanager
def project_lock(spec, baseline):
    """Serialize cooperating D02 Apply processes for the same daemon and namespace."""
    import ctypes
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32", winmode=0x800, use_last_error=True)
    kernel.CreateMutexW.argtypes = (wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.ReleaseMutex.argtypes = kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    lock_name = "Global\\FmcgDmsLabD02-" + digest(canonical_json([baseline["engineRef"], spec["project"]]))
    handle = kernel.CreateMutexW(None, False, lock_name)
    require(bool(handle), "D02_LOCK_UNAVAILABLE")
    try:
        require(kernel.WaitForSingleObject(handle, 0) in (0, 0x80), "D02_APPLY_BUSY")
        try:
            yield
        finally:
            kernel.ReleaseMutex(handle)
    finally:
        kernel.CloseHandle(handle)


def checksum_file(files):
    return "".join(f"{digest(content)}  {name}\n" for name, content in files.items()).encode("ascii")


def publish(path, root, name, value):
    """Publish an append-only protected attempt with an integrity manifest."""
    import msvcrt

    outputs = {name: canonical_json(value)}
    outputs["SHA256SUMS"] = checksum_file(outputs)
    with guarded_directory(path.parent), open_parent(path.parent) as parent:
        require(str(output_path(str(path))) == str(path) and beneath(path, root), "UNSAFE_D02_OUTPUT")
        with create_stage(parent) as stage:
            files = []
            try:
                for filename, content in outputs.items():
                    handle = create_report(stage, filename)
                    files.append(filename)
                    try:
                        fd = msvcrt.open_osfhandle(handle.value, os.O_WRONLY | os.O_BINARY)
                    except OSError:
                        close_handle(handle)
                        raise
                    with os.fdopen(fd, "wb") as stream:
                        stream.write(content)
                        stream.flush()
                        os.fsync(stream.fileno())
                rename_stage(stage, parent, path.name)
            except BaseException:
                discard_stage(stage, files)
                raise


def run(args):
    from tools.app02b_lab.docker_d02_host import assert_private, snapshot
    from tools.app02b_lab.docker_d02_topology import compose, resources, validate_state

    config, baseline, evidence, hashes = read_config(args.config_file, args.pwsh)
    out = output_path(args.output_directory)
    require(beneath(out, evidence), "UNSAFE_D02_OUTPUT")
    assert_private(args.pwsh, [out.parent])
    spec = resources(baseline, config["nonce"])
    model = compose(spec, config["images"])

    def observe():
        docker, host = snapshot(args.pwsh, baseline)
        image_ids = docker.image_ids(config["images"], baseline["sourceHead"])
        state = docker.state(spec["project"])
        validate_state(spec, model, state, image_ids, complete=args.mode == "verify")
        port_inventory = require_fresh_host(baseline, hashes, host, state, observe_ports=args.mode == "verify")
        return docker, image_ids, state, port_inventory

    if args.mode == "plan":
        _, image_ids, state, _ = observe()
        document = {"intent": intent(config, baseline, hashes, spec, model, image_ids),
                    "observed": scoped_inventory(state)}
        publish(out, evidence, "docker-rehearsal-plan.json", document)
        print("PLANNED")
        return 0

    plan_path = existing_local_input(args.plan_file, directory=False)
    require(beneath(plan_path, evidence), "UNSAFE_D02_PLAN_PATH")
    assert_private(args.pwsh, [plan_path.parent, plan_path])
    plan_raw = d01.bounded_input(plan_path)
    plan_sha = digest(plan_raw)
    plan = strict_json(plan_raw)

    if args.mode == "verify":
        docker, image_ids, _, port_inventory = observe()
        require_plan(plan, intent(config, baseline, hashes, spec, model, image_ids))
        sockets = {}
        for port in spec["ports"]:
            for family, ip in ((socket.AF_INET, "127.0.0.1"), (socket.AF_INET6, "::1")):
                with socket.socket(family, socket.SOCK_STREAM) as probe:
                    probe.settimeout(2)
                    result = probe.connect_ex((ip, port))
                    sockets[f"{ip}/{port}"] = "DENIED" if result == 10061 else "UNKNOWN" if result else "REACHABLE"
        docker.verify()
        status = ("OBSERVED" if port_inventory == "PASS" and all(value == "DENIED" for value in sockets.values())
                  else "BLOCKED")
        publish(out, evidence, "docker-rehearsal-network.json",
                {"schema": RESULT, "status": status, "planSha256": plan_sha, "collectedUtc": timestamp(utc_now()),
                 "vantage": "WINDOWS_HOST", "loopbackReservations": sockets, "reservationInventory": port_inventory,
                 "ownershipAndNoPublishing": "OBSERVED", "privatePeerConnectivity": "NOT_OBSERVED",
                 "outsideAppBridge": "NOT_OBSERVED", "forwardedHeaders": "NO_EDGE",
                 "sharedHostBypass": "NOT_OBSERVED", "D03": "BLOCKED", "D04": "BLOCKED"})
        print(status)
        return 0 if status == "OBSERVED" else 2

    enrollment = existing_local_input(args.enrollment_file, directory=False)
    assert_private(args.pwsh, [enrollment.parent, enrollment])
    enrollment_raw = d01.bounded_input(enrollment)

    def require_authority():
        require(source_info() == (baseline["sourceHead"], "CLEAN"), "D02_SOURCE_CHANGED")
        authorize(enrollment_raw, args.enrollment_sha256, plan_sha, spec["nonce"], now=utc_now())

    require_authority()
    with project_lock(spec, baseline):
        docker, image_ids, _, _ = observe()
        require_plan(plan, intent(config, baseline, hashes, spec, model, image_ids))

        def check_current_conditions():
            require_authority()
            _, live_image_ids, _, _ = observe()
            require_plan(plan, intent(config, baseline, hashes, spec, model, live_image_ids))
            require_authority()

        result = {"schema": RESULT, "planSha256": plan_sha, "sourceHead": baseline["sourceHead"],
                  "enrollmentSha256": digest(enrollment_raw), "operation": OPERATION,
                  "collectedUtc": timestamp(utc_now()), "D03": "BLOCKED", "D04": "BLOCKED"}
        try:
            state = apply_change(docker, spec, model, image_ids, ensure_current=check_current_conditions,
                                 reviewed_volumes=set(plan["observed"]["volumes"]))
            result.update(status="STAGED", observed=scoped_inventory(state))
        except LabError as error:
            result.update(status="BLOCKED", issue=error.code)
            publish(out, evidence, "docker-rehearsal-apply.json", result)
            raise
        publish(out, evidence, "docker-rehearsal-apply.json", result)
    print("STAGED")
    return 0


def main():
    if os.name != "nt" or not (sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode):
        print("COLLECTION_FAILED INVALID_D02_INVOCATION", file=sys.stderr)
        return 1
    parser = argparse.ArgumentParser(prog="LAB-D02")
    parser.add_argument("mode", choices=["plan", "apply", "verify"])
    parser.add_argument("--config-file", required=True)
    parser.add_argument("--output-directory", required=True)
    parser.add_argument("--pwsh", required=True)
    parser.add_argument("--plan-file")
    parser.add_argument("--enrollment-file")
    parser.add_argument("--enrollment-sha256")
    args = parser.parse_args()
    try:
        require((args.mode == "plan") == (not args.plan_file), "INVALID_D02_ARGUMENTS")
        require((args.mode == "apply" and bool(args.enrollment_file) and bool(args.enrollment_sha256))
                or (args.mode != "apply" and not args.enrollment_file and not args.enrollment_sha256),
                "INVALID_D02_ARGUMENTS")
        return run(args)
    except LabError as error:
        # Codes and controlled check names only, never Docker or native stdout.
        print(f"BLOCKED {error.code}", file=sys.stderr)
        return 2
    except (OSError, ValueError, TypeError, KeyError, AttributeError):
        print("COLLECTION_FAILED D02_RUNTIME_ERROR", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

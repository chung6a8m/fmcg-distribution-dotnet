"""Run LAB-D02 staging on a disposable GitHub-hosted Linux daemon."""

from ipaddress import ip_network
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import stat
import subprocess
import sys
import time
from urllib.error import URLError
from urllib.request import ProxyHandler, build_opener

from tools.app02b_lab.contract import LabError, canonical_json, require
from tools.app02b_lab.docker_d02 import apply_change
from tools.app02b_lab.docker_d02_topology import compose, labels, resources


SOCKET = Path("/var/run/docker.sock")
SYNTHETIC_IMAGES = {
    "api": "docker.io/library/busybox:1.37.0-glibc",
    "worker": "docker.io/library/busybox:1.37.0-musl",
}
CI_LABEL = "io.fmcg-dms.lab-d02.ci-fixture"
STORAGE_MARKER = "synthetic-d02-volume-marker"


class CiDocker:
    """Explicit CI-only endpoint and API adapter for the common staging path."""

    def __init__(self):
        require(sys.platform == "linux" and os.environ.get("GITHUB_ACTIONS") == "true"
                and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
                and os.environ.get("RUNNER_OS") == "Linux", "GITHUB_HOSTED_LINUX_REQUIRED")
        require(SOCKET.is_socket() and stat.S_ISSOCK(SOCKET.stat().st_mode), "LOCAL_ENGINE_SOCKET_REQUIRED")
        require(not any(name.startswith(("DOCKER_", "COMPOSE_")) for name in os.environ),
                "AMBIENT_DOCKER_OVERRIDE")
        self.cli = shutil.which("docker")
        require(self.cli is not None, "DOCKER_CLI_REQUIRED")
        self.engine_id = self.engine()
        self.mutations = []
        self.interrupt_once = False

    def command(self, *args, input_data=None, expect_failure=False):
        proc = subprocess.run([self.cli, "--host", f"unix://{SOCKET}", *args],
                              input=input_data, capture_output=True, timeout=120)
        if expect_failure:
            return proc
        if proc.returncode:
            detail = proc.stderr.decode("utf-8", errors="replace").strip()
            raise LabError("SYNTHETIC_DOCKER_COMMAND_FAILED", f"{args[:2]}: {detail}")
        return proc.stdout

    def engine(self):
        info = json.loads(self.command("info", "--format", "{{json .}}"))
        require(info.get("OSType") == "linux" and info.get("ID"), "SYNTHETIC_LINUX_ENGINE_REQUIRED")
        return info["ID"]

    def verify(self):
        require(self.engine() == self.engine_id, "SYNTHETIC_DAEMON_CHANGED")

    def run(self, *args, input_data=None):
        # The staging controller is allowed to perform exactly these operations.
        kind = args[0]
        is_create = args[:2] in (("volume", "create"), ("network", "create"))
        is_stage = (kind == "compose" and "up" in args and "--no-start" in args
                    and "--no-recreate" in args and "--no-build" in args
                    and "--pull" in args and args[args.index("--pull") + 1] == "never")
        require(is_create or is_stage, "UNEXPECTED_SYNTHETIC_APPLY_OPERATION")
        self.verify()
        self.mutations.append(args)
        output = self.command(*args, input_data=input_data)
        if self.interrupt_once and is_create:
            self.interrupt_once = False
            raise LabError("SYNTHETIC_RESPONSE_LOST")
        return output

    def inspect(self, kind, names):
        return json.loads(self.command(kind, "inspect", *names)) if names else []

    def listed(self, kind, *flags):
        return self.command(kind, "ls", "--quiet", *flags).decode().splitlines()

    def state(self, project):
        containers = self.inspect("container", self.listed("container", "--all", "--no-trunc"))
        networks = self.inspect("network", self.listed("network", "--no-trunc"))
        volumes = self.inspect("volume", self.listed("volume"))

        def scoped(name, row_labels):
            row_labels = row_labels or {}
            return (name.lstrip("/").lower().startswith((project.lower() + "-", project.lower() + "_"))
                    or project.lower() in {(row_labels.get("com.docker.compose.project") or "").lower(),
                                           (row_labels.get("io.fmcg-dms.lab-d02.project") or "").lower()})

        scoped_volumes = [v for v in volumes if scoped(v["Name"], v.get("Labels"))]
        return {
            "containers": [c for c in containers if scoped(c["Name"], c["Config"].get("Labels"))],
            "networks": [n for n in networks if scoped(n["Name"], n.get("Labels"))],
            "volumes": scoped_volumes,
            "volumeUsers": {v["Name"]: [c["Id"] for c in containers
                                       if any(m.get("Name") == v["Name"] and m["Type"] == "volume"
                                              for m in c.get("Mounts", []))]
                            for v in scoped_volumes},
        }

    def fixture_image(self, tag):
        self.command("pull", "--platform", "linux/amd64", tag)
        image, = self.inspect("image", [tag])
        require(image["Os"] == "linux" and image["Architecture"] == "amd64", "CI_FIXTURE_WRONG_PLATFORM")
        ref = next(ref for ref in image["RepoDigests"] if "busybox@sha256:" in ref)
        if ref.startswith("busybox@"):
            ref = "docker.io/library/" + ref
        require(re.fullmatch(r"docker\.io/library/busybox@sha256:[a-f0-9]{64}", ref), "CI_FIXTURE_DIGEST_REQUIRED")
        return ref, image["Id"]


def local_reservation():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def nonoverlapping_subnets(docker):
    networks = docker.inspect("network", docker.listed("network", "--no-trunc"))
    used = [ip_network(config["Subnet"]) for n in networks for config in n["IPAM"]["Config"] or []
            if config.get("Subnet") and ":" not in config["Subnet"]]
    routes = json.loads(subprocess.check_output(["ip", "-j", "-4", "route", "show", "table", "all"]))
    used += [ip_network(route["dst"], strict=False) for route in routes if route.get("dst") not in ("default", None)]
    candidates = (net for net in ip_network("10.231.0.0/16").subnets(new_prefix=28)
                  if not any(net.overlaps(existing) for existing in used))
    return [str(next(candidates)) for _ in range(3)]


def spec_for(docker):
    nonce = secrets.token_hex(16)
    project = "fmcg-d02-ci-" + nonce[:12]
    return resources({
        "project": project,
        "networks": [{"name": f"{project}_{role}", "subnet": subnet}
                     for role, subnet in zip(("app", "data", "vault"), nonoverlapping_subnets(docker), strict=True)],
        "volumes": [f"{project}_sql", f"{project}_vault"],
        "ports": [local_reservation()],
    }, nonce)


def docker_labels(mapping):
    return [token for key, value in mapping.items() for token in ("--label", f"{key}={value}")]


def owns(row_labels, spec):
    return all((row_labels or {}).get(key) == value for key, value in labels(spec).items())


def clean_owned(docker, spec):
    project = spec["project"]
    state = docker.state(project)
    # Cleanup exists only in ephemeral CI. Do not remove unknown or foreign objects.
    for c in state["containers"]:
        if owns(c["Config"]["Labels"], spec):
            docker.command("container", "rm", "--force", c["Id"])
    for n in state["networks"]:
        if owns(n["Labels"], spec):
            docker.command("network", "rm", n["Id"])
    for v in state["volumes"]:
        if owns(v["Labels"], spec):
            docker.command("volume", "rm", v["Name"])


def reject(call, expected=None):
    try:
        call()
    except LabError as err:
        require(expected is None or err.code == expected, "CI_WRONG_REFUSAL", str(err))
        return
    raise LabError("CI_GUARD_DID_NOT_REFUSE")


def fingerprint(state):
    return (sorted((c["Name"], c["Id"]) for c in state["containers"]),
            sorted((n["Name"], n["Id"]) for n in state["networks"]),
            sorted((v["Name"], v.get("CreatedAt"), v["Mountpoint"]) for v in state["volumes"]))


def fixture_container(docker, spec, image, network, *args, detached_name=None):
    command = ["container", "run", *docker_labels({**labels(spec), CI_LABEL: "true"}),
               "--network", network, "--cap-drop", "ALL", "--security-opt", "no-new-privileges"]
    if detached_name:
        command += ["--detach", "--name", detached_name]
    else:
        command += ["--rm"]
    return docker.command(*command, image, *args)


def refused_fixture(docker, spec, image, network, url):
    return docker.command("container", "run", "--rm", *docker_labels({**labels(spec), CI_LABEL: "true"}),
                          "--network", network, "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                          image, "wget", "-T", "2", "-q", "-O", "-", url, expect_failure=True)


def observe_networks(docker, spec, image, report):
    """Calibrate a live TCP observer before testing the actual separate bridges."""
    project = spec["project"]
    app_network = f"{project}_app"
    observer = project + "-ci-listener"
    fixture_container(docker, spec, image, app_network, "sh", "-ec",
                      "mkdir /tmp/site; printf %s synthetic-listener >/tmp/site/index.html; "
                      "exec httpd -f -h /tmp/site -p 18080", detached_name=observer)
    try:
        listener, = docker.inspect("container", [observer])
        ip = listener["NetworkSettings"]["Networks"][app_network]["IPAddress"]
        require(ip, "CI_LISTENER_NO_ADDRESS")
        url = f"http://{ip}:18080/"
        # Check an actual TCP route to an actual listener. DNS failure alone cannot be a denial witness.
        for _ in range(20):
            result = refused_fixture(docker, spec, image, app_network, url)
            if result.returncode == 0 and result.stdout == b"synthetic-listener":
                break
            time.sleep(0.25)
        else:
            raise LabError("CI_CALIBRATION_COULD_NOT_REACH_LISTENER")
        report["DOCKER_PRIVATE_PEER"] = "SYNTHETIC_LISTENER_REACHED"
        for role in ("data", "vault"):
            result = refused_fixture(docker, spec, image, f"{project}_{role}", url)
            require(result.returncode not in (0, 125, 126, 127) and not result.stdout,
                    "CI_CROSS_BRIDGE_TCP_NOT_DENIED", role)
        report["OUTSIDE_APP_BRIDGE"] = "DATA_AND_VAULT_PEERS_DENIED"
        # No ports have been published. Linux shared-host direct addressing is a
        # separate observation, not a claim about Windows/Docker Desktop.
        require(not listener["HostConfig"]["PortBindings"], "CI_FIXTURE_EXTERNALLY_PUBLISHED")
        with socket.socket() as probe:
            probe.settimeout(1)
            require(probe.connect_ex(("127.0.0.1", spec["ports"][0])) != 0,
                    "CI_PORT_RESERVATION_UNEXPECTEDLY_SERVES_HOST")
        try:
            with build_opener(ProxyHandler({})).open(url, timeout=1) as response:
                reached = response.read() == b"synthetic-listener"
        except (OSError, URLError):
            reached = False
        report["GITHUB_RUNNER_HOST"] = {"loopback_reservation": "NO_LISTENER",
                                       "direct_bridge_listener": "REACHABLE" if reached else "NOT_REACHED"}
    finally:
        found, = docker.inspect("container", [observer])
        require(owns(found["Config"]["Labels"], spec), "CI_FIXTURE_OWNER_CHANGED")
        docker.command("container", "rm", "--force", found["Id"])


def persist_marker(docker, spec, image, *, write):
    volume = f'{spec["project"]}_sql'
    observed, = docker.inspect("volume", [volume])
    require(owns(observed["Labels"], spec), "CI_VOLUME_OWNER_CHANGED")
    command = ["container", "run", "--rm", *docker_labels({**labels(spec), CI_LABEL: "true"}),
               "--network", "none", "--mount", f"type=volume,source={volume},target=/scratch"
               + ("" if write else ",readonly"), image]
    if write:
        docker.command(*command, "sh", "-ec", f"printf %s '{STORAGE_MARKER}' >/scratch/d02-synthetic.txt")
    else:
        require(docker.command(*command, "cat", "/scratch/d02-synthetic.txt") == STORAGE_MARKER.encode(),
                "CI_PERSISTED_MARKER_CHANGED")


def verify(docker, report):
    spec = spec_for(docker)
    project = spec["project"]
    pinned = {role: docker.fixture_image(tag) for role, tag in SYNTHETIC_IMAGES.items()}
    references = {role: ref for role, (ref, _) in pinned.items()}
    ids = {role: ident for role, (_, ident) in pinned.items()}
    image = references["api"]
    empty = {"containers": [], "networks": [], "volumes": [], "volumeUsers": {}}
    try:
        require(docker.state(project) == empty, "CI_SCOPE_NOT_EMPTY")
        model = compose(spec, references)
        config = json.loads(docker.command("compose", "--project-name", project, "--env-file", os.devnull,
                                          "-f", "-", "config", "--format", "json",
                                          input_data=canonical_json(model)))
        require(set(config["services"]) == {"api", "worker"}
                and all(not (c.get("ports") or c.get("volumes") or c.get("build"))
                        and c["environment"]["Runtime__Mode"] == "FOUNDATION_ONLY"
                        and c["environment"]["Runtime__Admission"] == "NO_GO"
                        for c in config["services"].values()), "CI_UNSAFE_COMPOSE_MODEL")
        require(docker.state(project) == empty and not docker.mutations, "CI_DRY_RUN_MUTATED_STATE")
        report["render_and_config_no_mutation"] = "PASS"

        def stage(guard=docker.verify):
            apply_change(docker, spec, model, ids, ensure_current=guard)

        def stale():
            raise LabError("SYNTHETIC_SCOPE_CHANGED")

        reject(lambda: stage(stale), "SYNTHETIC_SCOPE_CHANGED")
        require(docker.state(project) == empty and not docker.mutations, "CI_STALE_PLAN_MUTATED_STATE")
        report["stale_scope_refused"] = "PASS"

        volume = f"{project}_sql"
        reject(lambda: apply_change(docker, spec, model, ids, ensure_current=docker.verify,
                                    reviewed_volumes={volume}), "D02_OWNED_VOLUME_DISAPPEARED")
        require(docker.state(project) == empty and not docker.mutations, "CI_MISSING_VOLUME_WAS_RECREATED")
        report["reviewed_volume_disappearance_refused"] = "PASS"

        # This is a CI-owned negative fixture with deliberately mismatched D02 ownership.
        docker.command("volume", "create", "--label", f"{CI_LABEL}={spec['nonce']}", volume)
        try:
            reject(stage)
            require(len(docker.state(project)["volumes"]) == 1 and not docker.mutations,
                    "CI_FOREIGN_VOLUME_ADOPTED")
        finally:
            foreign, = docker.inspect("volume", [volume])
            require(foreign["Labels"].get(CI_LABEL) == spec["nonce"], "CI_FIXTURE_OWNER_CHANGED")
            docker.command("volume", "rm", volume)
        report["foreign_volume_refused"] = "PASS"

        obstruction = f"{project}-foreign"
        foreign_id = docker.command("container", "create", "--name", obstruction, "--network", "none",
                                    "--label", f"{CI_LABEL}={spec['nonce']}", image).decode().strip()
        try:
            reject(stage)
            require(len(docker.state(project)["containers"]) == 1 and not docker.mutations,
                    "CI_FOREIGN_NAMESPACE_ADOPTED")
        finally:
            foreign, = docker.inspect("container", [foreign_id])
            require(foreign["Config"]["Labels"].get(CI_LABEL) == spec["nonce"], "CI_FIXTURE_OWNER_CHANGED")
            docker.command("container", "rm", foreign_id)
        report["foreign_namespace_refused"] = "PASS"

        docker.interrupt_once = True
        reject(stage, "SYNTHETIC_RESPONSE_LOST")
        partial = docker.state(project)
        require(not partial["containers"] and len(partial["networks"]) + len(partial["volumes"]) == 1,
                "CI_EXPECTED_SINGLE_PARTIAL_RESOURCE")
        stage()
        before = docker.state(project)
        require(len(before["containers"]) == 2 and len(before["networks"]) == 3
                and len(before["volumes"]) == 2, "CI_INCOMPLETE_STAGING")
        require(all(c["State"]["Status"] == "created" for c in before["containers"]),
                "CI_D02_STARTED_WORKLOAD")
        require(all(not c["HostConfig"]["PortBindings"] for c in before["containers"]),
                "CI_D02_PUBLISHED_PORT")
        report["staging_and_interrupted_retry"] = "PASS"

        stage()
        require(fingerprint(docker.state(project)) == fingerprint(before), "CI_IDEMPOTENCY_DRIFT")
        report["repeat_staging_preserved_objects"] = "PASS"

        persist_marker(docker, spec, image, write=True)
        api = next(c for c in before["containers"] if c["Name"] == f"/{project}-api")
        require(owns(api["Config"]["Labels"], spec), "CI_FIXTURE_OWNER_CHANGED")
        docker.command("container", "rm", api["Id"])
        stage()
        recreated = next(c for c in docker.state(project)["containers"] if c["Name"] == api["Name"])
        require(recreated["Id"] != api["Id"] and recreated["State"]["Status"] == "created",
                "CI_EXPECTED_STOPPED_RECREATION")
        persist_marker(docker, spec, image, write=False)
        report["volume_marker_survived_recreation"] = "PASS"
        observe_networks(docker, spec, image, report)
    finally:
        clean_owned(docker, spec)
        require(docker.state(project) == empty, "CI_OWNERSHIP_CLEANUP_INCOMPLETE")


def main():
    source_head = os.environ.get("LAB_D02_SOURCE_HEAD", "")
    require(re.fullmatch(r"[a-f0-9]{40}", source_head)
            and subprocess.check_output(["git", "rev-parse", "HEAD"]).decode().strip() == source_head,
            "CI_SOURCE_HEAD_MISMATCH")
    report = {
        "evidence_class": "GITHUB_HOSTED_SYNTHETIC_CI_ONLY",
        "source_head": source_head,
        "workflow_run": os.environ.get("GITHUB_RUN_ID"),
        "workflow_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "WINDOWS_HOST": "NOT_RUN",
        "edge_tls_and_forwarded_headers": "NOT_CONFIGURED_UNTIL_D03",
        "sql_and_vault_socket_denials": "SERVICES_NOT_PROVISIONED",
        "workload_foundation_mode": "STAGED_STOPPED",
        "physical": "NOT_VERIFIED",
    }
    try:
        verify(CiDocker(), report)
        report["result"] = "PASS"
    except Exception:
        report["result"] = "FAIL"
        raise
    finally:
        out = Path(os.environ["RUNNER_TEMP"]) / "lab-d02-ci.json"
        with out.open("xb") as handle:
            handle.write(canonical_json(report))
        print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()

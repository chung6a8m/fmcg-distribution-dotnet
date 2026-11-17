#!/usr/bin/env python3
"""Independent Docker runtime port/network oracle for ephemeral APP-02B.2 CI.

Consumes 'docker inspect edge api worker vault' on stdin. Emits only safe
service/binding summaries; never serializes Docker inspect environment/mounts.
Mutates the observed fixture to ensure unsafe public/management ports FAIL.
"""
from __future__ import annotations

import copy
import json
import sys

PREFIX = "fmcg-preprod-b2_"
EXPECTED_NETWORKS = {
    "edge": {PREFIX + "public", PREFIX + "private"},
    "api": {PREFIX + "private", PREFIX + "outbound"},
    "worker": {PREFIX + "private", PREFIX + "outbound"},
    "vault": {PREFIX + "outbound"},
}


def published(info: dict, location: str) -> list[dict[str, str]]:
    ports = info["NetworkSettings"]["Ports"] if location == "actual" else info["HostConfig"]["PortBindings"]
    observed: list[dict[str, str]] = []
    for container_port, binds in ports.items():
        for binding in binds or []:
            observed.append({
                "container": container_port,
                "host": binding["HostIp"],
                "port": str(binding["HostPort"]),
            })
    return sorted(observed, key=lambda b: (b["container"], b["host"], b["port"]))


def verify(containers: list[dict]) -> dict:
    if len(containers) != 4:
        raise ValueError("Expected four containers: edge, api, worker, vault")
    by_service: dict[str, dict] = {}
    for info in containers:
        name = info["Config"]["Labels"].get("com.docker.compose.service")
        if name in by_service or name not in EXPECTED_NETWORKS:
            raise ValueError("Duplicate or unknown Compose service")
        by_service[name] = info
    if set(by_service) != set(EXPECTED_NETWORKS):
        raise ValueError("Missing Compose service")

    output = {}
    for name, info in sorted(by_service.items()):
        if info["State"]["Running"] is not True:
            raise ValueError(f"{name}: container not running")
        if info["HostConfig"].get("NetworkMode") == "host" or info["HostConfig"].get("Privileged"):
            raise ValueError(f"{name}: forbidden host/privileged mode")
        attached = set(info["NetworkSettings"]["Networks"])
        if attached != EXPECTED_NETWORKS[name]:
            raise ValueError(f"{name}: unexpected network attachment")
        actual = published(info, "actual")
        requested = published(info, "requested")
        if actual != requested:
            raise ValueError(f"{name}: requested vs live port bindings differ")
        expected = [{"container": "443/tcp", "host": "127.0.0.1", "port": "443"}] if name == "edge" else []
        if actual != expected:
            raise ValueError(f"{name}: forbidden host binding")
        output[name] = {"networks": sorted(attached), "published_ports": actual}

    return output


def require_negative(containers: list[dict], service: str, port: str, bind: dict) -> None:
    mutation = copy.deepcopy(containers)
    target = next(x for x in mutation if x["Config"]["Labels"]["com.docker.compose.service"] == service)
    target["NetworkSettings"]["Ports"][port] = [bind]
    target["HostConfig"]["PortBindings"][port] = [bind]
    try:
        verify(mutation)
    except ValueError:
        return
    raise RuntimeError(f"Mutation incorrectly accepted: {service} {port}")


def main() -> None:
    try:
        data = json.load(sys.stdin)
        evidence = verify(data)
        require_negative(data, "api", "8080/tcp", {"HostIp": "0.0.0.0", "HostPort": "8080"})
        require_negative(data, "edge", "443/tcp", {"HostIp": "0.0.0.0", "HostPort": "443"})
        print(json.dumps({
            "Witness": "RUNNING_DOCKER_INSPECT_PRIVATE_ONLY",
            "Services": evidence,
            "UnsafeApiPortMutation": "REJECTED",
            "EdgePublicPortMutation": "REJECTED",
            "Scope": "GITHUB_HOSTED_CI_ONLY_NOT_PUBLIC_INTERNET_SCAN",
        }, sort_keys=True))
    except (KeyError, TypeError, ValueError, RuntimeError) as exc:
        print("CI_RUNTIME_PORTS_FAIL: " + str(exc), file=sys.stderr)
        raise SystemExit(1) from exc


if __name__ == "__main__":
    main()

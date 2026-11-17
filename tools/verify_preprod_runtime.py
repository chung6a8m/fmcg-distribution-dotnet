#!/usr/bin/env python3
"""CI/host structural invariants; Docker config alone is NOT real PREPROD evidence."""
import json
import os
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
deployment = root / "deploy" / "preprod"
def require(condition, message):
    if not condition:
        raise SystemExit("RUNTIME_STATIC_FAIL: " + message)

env = os.environ.copy()
env.update({
    "PREPROD_FQDN": "preprod.example.invalid",
    "PREPROD_BIND_IP": "127.0.0.1",
    "VAULT_ADDR": "https://vault.private.example.invalid:8200",
    "TLS_CERT_FILE": "/tmp/preprod-fixture.crt",
    "TLS_KEY_FILE": "/tmp/preprod-fixture.key",
    "VAULT_API_TOKEN_FILE": "/tmp/preprod-fixture-api.token",
    "VAULT_WORKER_TOKEN_FILE": "/tmp/preprod-fixture-worker.token",
})
proc = subprocess.run(
    ["docker", "compose", "-f", str(deployment / "compose.yaml"),
     "config", "--format", "json"],
    cwd=deployment, env=env, capture_output=True, text=True, check=False)
require(proc.returncode == 0, "compose config did not parse")
compose = json.loads(proc.stdout)
services = compose["services"]
require(set(services) == {"edge", "api", "worker"}, "unexpected service/DB or Vault deployment")
require(set(compose["networks"]) == {"public", "private", "outbound"}, "network boundary altered")
require(compose["networks"]["private"].get("internal") is True, "private network not internal")
require(not compose["networks"]["outbound"].get("internal", False), "Vault egress network missing")

edge = services["edge"]
ports = edge.get("ports", [])
require(len(ports) == 1, "only a single public TLS port is permitted")
port = ports[0]
require(int(port.get("published", -1)) == 443 and int(port.get("target", -1)) == 443
        and port.get("protocol") == "tcp", "edge must only publish HTTPS/443")
require(not any("ports" in services[n] or "network_mode" in services[n]
                for n in ("api", "worker")), "private workload publishes a port")
require(not any("privileged" in s and s["privileged"] for s in services.values()),
        "privileged container")
require(set(edge["networks"]) == {"public", "private"}, "edge attached to outbound")
for name in ("api", "worker"):
    service = services[name]
    require(set(service["networks"]) == {"private", "outbound"}, name + " network scope")
    require(service.get("read_only") is True, name + " filesystem must be read-only")
    require(service.get("user") == "1654:1654", name + " user must be nonroot")
    require("ALL" in service.get("cap_drop", []), name + " capabilities not dropped")
    require("no-new-privileges:true" in service.get("security_opt", []),
            name + " no-new-privileges missing")
    cfg = service.get("environment", {})
    require(cfg.get("Runtime__Mode") == "FOUNDATION_ONLY"
            and cfg.get("Runtime__Admission") == "NO_GO"
            and cfg.get("Runtime__Role") == name,
            name + " runtime admission altered")
    require(cfg.get("Vault__ProbePath") == "preprod/data/runtime/" + name,
            name + " Vault path not scoped")
    require(not any("TOKEN" in key and key != "Vault__TokenFile"
                    or "PASSWORD" in key or "SIGNINGKEY" in key.upper()
                    for key in cfg), name + " secret-like environment variable")
require(edge["networks"]["private"].get("ipv4_address") == "172.30.50.10",
        "trusted proxy IP differs from compose")
require(services["api"]["environment"]["Runtime__TrustedProxyIp"] == "172.30.50.10",
        "runtime proxy trust differs from edge")
def mounted_source(name):
    mounts = services[name]["volumes"]
    matches = [m for m in mounts if m["target"] == "/run/secrets/vault_" + name + "_token"]
    require(len(matches) == 1 and matches[0]["read_only"], name + " secret mount missing")
    return matches[0]["source"]
require(mounted_source("api") != mounted_source("worker"), "shared workload token source")
caddy = (deployment / "Caddyfile").read_text()
require("admin off" in caddy and "auto_https disable_redirects" in caddy,
        "Caddy control endpoint/HTTP redirect changed")
require("@health path /health/live /health/ready" in caddy and "respond \"Not Found\" 404" in caddy,
        "Caddy route allowlist removed")
for name in ("api", "worker"):
    policy = (deployment / ("vault-policy-" + name + ".hcl")).read_text()
    require('path "preprod/data/runtime/' + name + '"' in policy
            and 'capabilities = ["read"]' in policy
            and 'capabilities = ["list"]' not in policy,
            "Vault policy not least-privilege for " + name)
print("RUNTIME_STATIC_PASS: topology/TLS/deny-by-default/Vault workload separation (CI only)")

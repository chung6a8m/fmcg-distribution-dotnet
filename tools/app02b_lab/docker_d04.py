"""D04 fixed SQL deployment model and protected planning inputs."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
from uuid import UUID, uuid5

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab import docker_d02 as d02, docker_d03 as d03
from tools.app02b_lab.contract import LabError, canonical_json, digest, require, strict_json
from tools.app02b_lab.preflight import beneath, existing_local_input, output_path, source_info
from tools.app02b_lab.docker_d02_host import assert_private

IMAGE = "mcr.microsoft.com/mssql/server@sha256:ee45df93117986941f5e47e1b074897b115c62d6733f2f0b24b1d51259a77723"
VERSION = "17.0.5065.9"
POLICY = "APP02B-LAB-DOCKER-SQL-PLAN-V1"
FILES = ROOT / "deploy/lab/docker-wsl2/sql"
ROLES = ("apiA", "apiB", "observer")


def scope(project, nonce):
    require(type(project) is str and re.fullmatch(r"[a-z][a-z0-9_-]{2,56}", project)
            and type(nonce) is str and d02.NONCE.fullmatch(nonce) and nonce != "0" * 32, "D04_INVALID_SCOPE")
    uid = UUID(hex=nonce)
    tenants = {slot: str(uuid5(uid, "tenant-" + slot)) for slot in ("A", "B")}
    databases = {slot: "LabD04_" + slot + "_" + nonce for slot in ("identity", "registry", "fence")}
    databases.update({"tenant" + slot: "FmcgPpT_" + UUID(tenant).hex for slot, tenant in tenants.items()})
    bindings = {slot: str(uuid5(uid, "binding-" + slot)) for slot in tenants}
    logins = {role: "labd04_" + role.lower() + "_" + nonce for role in ROLES}
    return {"project": project, "nonce": nonce, "instance": "d04-" + nonce,
            "databases": databases, "tenants": tenants, "bindings": bindings, "logins": logins}


def compose(spec, certificate, key, secret):
    project, nonce = spec["project"], spec["nonce"]
    require(spec == scope(project, nonce), "D04_SCOPE_DRIFT")
    owner = {"io.fmcg-dms.lab-d04.project": project, "io.fmcg-dms.lab-d04.nonce": nonce}
    bindings = ((FILES / "start.sh", "/run/lab-sql/start.sh"), (certificate, "/run/lab-sql/leaf.pem"),
                (key, "/run/lab-sql/leaf.key"), (secret, "/run/lab-sql/bootstrap"),
                (FILES / "mssql.conf", "/var/opt/mssql/mssql.conf"))
    mounts = [{"type": "bind", "source": str(path), "target": target, "read_only": True,
               "bind": {"create_host_path": False}} for path, target in bindings]
    mounts += [{"type": "volume", "source": role, "target": target} for role, target in
               (("data", "/var/opt/mssql"), ("backup", "/var/opt/mssql-backup"))]
    return {"name": project + "-d04",
            "services": {"sql": {"image": IMAGE, "platform": "linux/amd64", "pull_policy": "never",
                                  "hostname": spec["instance"], "container_name": project + "-d04-sql", "labels": owner,
                                  "user": "10001:0", "entrypoint": ["/bin/sh", "/run/lab-sql/start.sh"], "command": [],
                                  "environment": {"ACCEPT_EULA": "Y", "MSSQL_PID": "Developer",
                                                  "MSSQL_MEMORY_LIMIT_MB": "2048"},
                                  "networks": {"data": {"aliases": ["sql"]}}, "volumes": mounts,
                                  "cap_drop": ["ALL"], "security_opt": ["no-new-privileges:true"],
                                  "restart": "no", "mem_limit": "3g", "pids_limit": 512,
                                  "healthcheck": {"disable": True}}},
            "networks": {"data": {"external": True, "name": project + "_data"}},
            "volumes": {"data": {"external": True, "name": project + "_sql"},
                        "backup": {"name": project + "_d04_backup", "labels": owner}}}


def require_trust(peer, stage_sha, predecessor):
    require(type(peer) is dict and peer.get("schema") == "APP02B-LAB-DOCKER-VAULT-PEER-RESULT-V1"
            and peer.get("vantage") == "OPERATOR_ENROLLED_PRIVATE_PEER"
            and peer.get("auditVantage") == "OPERATOR_PROTECTED_AUDIT_READBACK"
            and peer.get("status") == "ROLE_AUDIT_OBSERVED" and peer.get("sourceHead") == predecessor
            and peer.get("originalGateStatus") == "NOT_VERIFIED" and peer.get("stageReceiptSha256") == stage_sha,
            "D04_D03_TRUST_REQUIRED")
    expected = {f"{role}_read_{owner}": {"httpStatus": 200 if role == owner else 403, "audit": "OBSERVED"}
                for role in ("api", "worker", "operator") for owner in ("api", "worker", "operator")}
    require(peer.get("checks") == expected, "D04_D03_ROLE_MATRIX_REQUIRED")


def plan(config_path, output, pwsh):
    _, raw = d03.protected_file(config_path, pwsh)
    config = strict_json(raw)
    d02.object_fields(config, "schema d01Plan d02Plan d02Apply d03Plan d03Stage d03Peer sqlCertificate sqlKey "
                      "sqlCa bootstrapFile licenseSha256 evidenceRoot", "D04_INVALID_CONFIG")
    require(config["schema"] == "APP02B-LAB-DOCKER-SQL-CONFIG-V1"
            and d03.d01.nonzero_digest(config["licenseSha256"]), "D04_LICENSE_REQUIRED")
    source, state = source_info()
    require(state == "CLEAN", "D04_SOURCE_NOT_CLEAN")
    predecessor, result, hashes = d03.d02_receipts(config, pwsh, source)
    d01_path, d01_bytes = d03.protected_file(config["d01Plan"], pwsh)
    baseline = strict_json(d01_bytes)
    d03.d01.validate_plan(baseline)
    require(digest(d01_bytes) == predecessor["d01PlanSha256"]
            and any(item["role"] == "SQL" and item["reference"] == IMAGE for item in baseline["images"]),
            "D04_SQL_IMAGE_NOT_ENROLLED")
    baseline_evidence = existing_local_input(baseline["evidenceRoot"], directory=True)
    d03.d01.validate_publisher_receipts(d01_path, baseline_evidence, baseline)
    _, d03_plan_raw = d03.protected_file(config["d03Plan"], pwsh)
    _, d03_stage_raw = d03.protected_file(config["d03Stage"], pwsh)
    _, d03_peer_raw = d03.protected_file(config["d03Peer"], pwsh)
    d03_plan, stage, peer = map(strict_json, (d03_plan_raw, d03_stage_raw, d03_peer_raw))
    spec = scope(predecessor["spec"]["project"], predecessor["spec"]["nonce"])
    require(type(d03_plan) is dict and type(stage) is dict and stage.get("status") == "STAGED"
            and stage.get("schema") == "APP02B-LAB-DOCKER-VAULT-STAGE-V1"
            and stage.get("planSha256") == digest(d03_plan_raw) and stage.get("nonce") == spec["nonce"]
            and d03_plan.get("schema") == d03.PLAN and d03_plan.get("engineRef") == predecessor["engineRef"]
            and d03_plan.get("nonce") == spec["nonce"] and d03_plan.get("project") == spec["project"]
            and stage.get("sourceHead") == d03_plan.get("sourceHead")
            and all(d03_plan.get(name) == sha for name, sha in hashes.items()), "D04_D03_STAGE_MISMATCH")
    require_trust(peer, digest(d03_stage_raw), stage.get("sourceHead"))
    require(type(stage.get("sourceHead")) is str and re.fullmatch(r"[a-f0-9]{40}", stage["sourceHead"]),
            "D04_D03_STAGE_MISMATCH")
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    ancestry = subprocess.run(["git.exe", "merge-base", "--is-ancestor", stage["sourceHead"], source],
                              cwd=ROOT, env=env, capture_output=True, timeout=15)
    require(ancestry.returncode == 0, "D04_D03_SOURCE_NOT_MERGED")

    cert, cert_raw = d03.protected_file(config["sqlCertificate"], pwsh)
    key, key_raw = d03.protected_file(config["sqlKey"], pwsh, confidential=True)
    _, ca_raw = d03.protected_file(config["sqlCa"], pwsh)
    bootstrap = existing_local_input(config["bootstrapFile"], directory=False)
    assert_private(pwsh, [bootstrap.parent, bootstrap], confidential=True)
    evidence = existing_local_input(config["evidenceRoot"], directory=True)
    require(not beneath(key, evidence) and not beneath(bootstrap, evidence)
            and len({cert, key, bootstrap}) == 3 and not any("$" in str(path) for path in (cert, key, bootstrap, FILES)),
            "D04_UNSAFE_SECRET_PATH")
    leaf_sha = d03.validate_pem(cert_raw, key_raw, ca_raw)
    d03.verify_tls_inputs(cert, key, ca_raw, hostname="sql")
    model = compose(spec, cert, key, bootstrap)
    docker, host = d03.snapshot(pwsh, baseline)
    require(d03.d01.identity_refs(host) == (predecessor["hostRef"], predecessor["engineRef"])
            and d03.d01.architecture(host["engine"]["Architecture"]) == "X64"
            and d03.d01.image_check({"role": "SQL", "reference": IMAGE}, host["images"].get("SQL")),
            "D04_DOCKER_OR_IMAGE_MISMATCH")
    inventory = docker.state(spec["project"])
    d02_spec = predecessor["spec"]
    labels = d03.topology.labels(d02_spec)
    data = [row for row in inventory["volumes"] if row["Name"] == model["volumes"]["data"]["name"]]
    network = [row for row in inventory["networks"] if row["Name"] == model["networks"]["data"]["name"]]
    require(len(data) == len(network) == 1 and isinstance(data[0].get("CreatedAt"), str)
            and all((row.get("Labels") or {}).get(k) == value for row in (*data, *network) for k, value in labels.items())
            and data[0].get("Driver") == "local" and not data[0].get("Options")
            and inventory["volumeUsers"].get(data[0]["Name"]) == []
            and not any(docker.state(model["name"]).values()) and model["name"] not in host["projects"]
            and not any(row["Name"] == model["volumes"]["backup"]["name"] for row in inventory["volumes"]),
            "D04_DATA_NOT_UNUSED_OR_OWNED")
    subnet = next(item["subnet"] for item in d02_spec["networks"] if item["name"] == network[0]["Name"])
    d03.topology._validate_network(network[0], subnet, {})
    vault = [row for row in inventory["containers"] if
             digest(row["Id"].encode("ascii")) == stage.get("containerRef")]
    require(len(vault) == 1 and (vault[0].get("State") or {}).get("Status") == "running"
            and not (vault[0].get("HostConfig") or {}).get("PortBindings"), "D04_D03_LIVE_TARGET_MISSING")
    out = output_path(output)
    require(beneath(out, evidence), "D04_UNSAFE_OUTPUT")
    assert_private(pwsh, [evidence, out.parent])
    d02.publish(out, evidence, "docker-sql-plan.json", {
        "schema": POLICY, "status": "PLAN_ONLY", "apply": "BLOCKED", "image": IMAGE, "version": VERSION,
        "sourceHead": source, "engineRef": predecessor["engineRef"], **hashes, "spec": spec,
        "d03StageSha256": digest(d03_stage_raw), "d03PeerSha256": digest(d03_peer_raw),
        "d03LeafDerSha256": stage.get("leafDerSha256"), "d03ScopeSha256": peer.get("scopeSha256"),
        "sqlLeafDerSha256": leaf_sha, "sqlCaSha256": digest(ca_raw), "configSha256": digest(raw),
        "composeSha256": digest(canonical_json(model)), "licenseSha256": config["licenseSha256"],
        "networkRef": digest(network[0]["Id"].encode("ascii")),
        "dataVolumeRef": digest(canonical_json([data[0]["Name"], data[0]["CreatedAt"], labels])),
    })


def main():
    parser = argparse.ArgumentParser()
    for name in ("config", "output", "pwsh"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    try:
        require(os.name == "nt" and sys.flags.isolated and sys.flags.no_site
                and sys.flags.dont_write_bytecode, "D04_WINDOWS_WRAPPER_REQUIRED")
        plan(args.config, args.output, args.pwsh)
        print("PLAN_ONLY")
        return 0
    except LabError as error:
        print(f"BLOCKED {error.code}")
        return 2
    except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError):
        print("COLLECTION_FAILED D04_PLAN_ERROR")
        return 1


if __name__ == "__main__":
    sys.exit(main())

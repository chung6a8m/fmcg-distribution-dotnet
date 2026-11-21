"""D04 fixed SQL deployment model and protected planning inputs."""
import argparse
from datetime import datetime, timedelta, timezone
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
ENROLLMENT = "APP02B-LAB-DOCKER-SQL-PROBE-ENROLLMENT-V1"
STAGE_ENROLLMENT = "APP02B-LAB-DOCKER-SQL-STAGE-ENROLLMENT-V1"
STAGE = "CREATE_STOPPED_SQL"
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
                                  "cap_drop": ["ALL"], "cap_add": ["NET_BIND_SERVICE"],
                                  "security_opt": ["no-new-privileges:true"],
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
            and peer.get("originalGateStatus") == "NOT_VERIFIED" and peer.get("stageReceiptSha256") == stage_sha
            and d03.d01.nonzero_digest(peer.get("scopeSha256")),
            "D04_D03_TRUST_REQUIRED")
    try:
        observed = datetime.strptime(peer["collectedUtc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except (TypeError, ValueError, KeyError):
        raise LabError("D04_D03_TRUST_TIME_INVALID") from None
    now = d02.utc_now()
    require(d02.timestamp(observed) == peer["collectedUtc"]
            and timedelta() <= now - observed <= timedelta(hours=1), "D04_D03_TRUST_STALE")
    expected = {f"{role}_read_{owner}": {"httpStatus": 200 if role == owner else 403, "audit": "OBSERVED"}
                for role in ("api", "worker", "operator") for owner in ("api", "worker", "operator")}
    require(peer.get("checks") == expected, "D04_D03_ROLE_MATRIX_REQUIRED")


def observe(config_path, pwsh):
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
    image_id = d03.d01.text_result(docker.run("image", "inspect", "--format", "{{.Id}}", "--", IMAGE))
    require(re.fullmatch(r"sha256:[a-f0-9]{64}", image_id), "D04_SQL_IMAGE_ID_MISSING")
    info = bootstrap.stat()
    require(info.st_nlink == 1, "D04_UNSAFE_SECRET_PATH")
    document = {
        "schema": POLICY, "status": "PLAN_ONLY", "operation": STAGE, "image": IMAGE, "version": VERSION,
        "imageId": image_id, "bootstrapFileRef": digest(canonical_json(
            [info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns])),
        "sourceHead": source, "engineRef": predecessor["engineRef"], **hashes, "spec": spec,
        "d03StageSha256": digest(d03_stage_raw), "d03PeerSha256": digest(d03_peer_raw),
        "d03LeafDerSha256": stage.get("leafDerSha256"), "d03ScopeSha256": peer.get("scopeSha256"),
        "sqlLeafDerSha256": leaf_sha, "sqlCaSha256": digest(ca_raw), "configSha256": digest(raw),
        "composeSha256": digest(canonical_json(model)), "licenseSha256": config["licenseSha256"],
        "networkRef": digest(network[0]["Id"].encode("ascii")),
        "dataVolumeRef": digest(canonical_json([data[0]["Name"], data[0]["CreatedAt"], labels])),
    }
    return document, model, docker, evidence


def stage_sql(docker, model, plan, *, ensure_current, env_file=os.devnull):
    ensure_current()
    docker.run("compose", "--project-directory", str(ROOT), "--env-file", env_file, "-p", model["name"],
               "-f", "-", "up", "--no-start", "--no-build", "--no-recreate", "--no-deps", "--pull", "never", "sql",
               input_data=canonical_json(model))
    service = model["services"]["sql"]
    row, = docker._inspect("container", [d03.d01.text_result(docker.run(
        "container", "inspect", "--format", "{{.Id}}", "--", service["container_name"]))])
    host, config = row.get("HostConfig") or {}, row.get("Config") or {}
    settings = row.get("NetworkSettings") or {}
    network = model["networks"]["data"]["name"]
    mounts = {item["Destination"]: item for item in row.get("Mounts") or []}
    expected_capabilities = {cap.removeprefix("CAP_") for cap in host.get("CapAdd") or []}
    require(re.fullmatch(r"[a-f0-9]{64}", row["Id"]) and row.get("Image") == plan["imageId"]
            and (row.get("State") or {}).get("Status") == "created"
            and host.get("NetworkMode") == network and set(settings.get("Networks") or {}) <= {network}
            and not host.get("PortBindings") and not any((settings.get("Ports") or {}).values())
            and host.get("PublishAllPorts") is False and host.get("Privileged") is False
            and host.get("CapDrop") == ["ALL"] and expected_capabilities == {"NET_BIND_SERVICE"}
            and set(host.get("SecurityOpt") or []) == {"no-new-privileges:true"}
            and (host.get("RestartPolicy") or {}).get("Name") in ("", "no")
            and config.get("User") == service["user"] and config.get("Hostname") == service["hostname"]
            and config.get("Entrypoint") == service["entrypoint"] and not config.get("Cmd")
            and all((config.get("Labels") or {}).get(k) == v for k, v in service["labels"].items())
            and not any(item.startswith("MSSQL_SA_PASSWORD=") and item.partition("=")[2]
                        for item in config.get("Env") or [])
            and set(mounts) == {item["target"] for item in service["volumes"]}, "D04_STAGE_DRIFT")
    for item in service["volumes"]:
        mount = mounts[item["target"]]
        require(mount["Type"] == item["type"] and mount["RW"] == (item["type"] == "volume"),
                "D04_STAGE_MOUNT_CHANGED")
        if item["type"] == "bind":
            require(d03.bind_matches(mount.get("Source"), item["source"]), "D04_STAGE_BIND_CHANGED")
        else:
            require(mount.get("Name") == model["volumes"][item["source"]]["name"], "D04_STAGE_VOLUME_CHANGED")
    backup, = docker._inspect("volume", [model["volumes"]["backup"]["name"]])
    require(backup.get("Driver") == "local" and not backup.get("Options")
            and all((backup.get("Labels") or {}).get(k) == v for k, v in service["labels"].items()),
            "D04_STAGE_BACKUP_OWNERSHIP")
    if "networkRef" in plan:
        data, = docker._inspect("volume", [model["volumes"]["data"]["name"]])
        bridge_id = d03.d01.text_result(docker.run("network", "inspect", "--format", "{{.Id}}", "--", network))
        owner = d03.topology.labels(plan["spec"])
        require(digest(bridge_id.encode("ascii")) == plan["networkRef"]
                and all((data.get("Labels") or {}).get(k) == v for k, v in owner.items())
                and digest(canonical_json([data["Name"], data["CreatedAt"], owner])) == plan["dataVolumeRef"],
                "D04_STAGE_TARGET_CHANGED")
    return digest(row["Id"].encode("ascii"))


def run(args):
    require(not any(key.upper().startswith("COMPOSE_") for key in os.environ), "D04_COMPOSE_ENV_OVERRIDE")
    inputs = (args.plan, args.enrollment, args.enrollment_sha256)
    require(all(inputs) if args.apply else not any(inputs), "D04_INVALID_ARGUMENTS")
    current, model, docker, evidence = observe(args.config, args.pwsh)
    out = output_path(args.output)
    require(beneath(out, evidence), "D04_UNSAFE_OUTPUT")
    assert_private(args.pwsh, [evidence, out.parent])
    if not args.apply:
        d02.publish(out, evidence, "docker-sql-plan.json", current)
        print("PLAN_ONLY")
        return

    _, plan_bytes = d03.protected_file(args.plan, args.pwsh)
    _, approval = d03.protected_file(args.enrollment, args.pwsh)
    approved = strict_json(plan_bytes)
    plan_sha = digest(plan_bytes)
    spec = current["spec"]

    def authorization():
        d03.authorize(approval, args.enrollment_sha256, plan_sha, spec["nonce"],
                      STAGE, d02.utc_now(), schema=STAGE_ENROLLMENT)

    def revalidate():
        authorization()
        observed, _, _, _ = observe(args.config, args.pwsh)
        require(approved == current == observed, "D04_STAGE_PLAN_CHANGED")
        authorization()

    authorization()
    receipt = {"schema": "APP02B-LAB-DOCKER-SQL-STAGE-V1", "sourceHead": current["sourceHead"],
               "planSha256": plan_sha, "enrollmentSha256": digest(approval), "operation": STAGE,
               "collectedUtc": d02.timestamp(d02.utc_now()), "d03StageSha256": current["d03StageSha256"]}
    with d02.project_lock(spec, current):
        try:
            ref = stage_sql(docker, model, current, ensure_current=revalidate)
        except LabError as error:
            d02.publish(out, evidence, "docker-sql-stage.json", {**receipt, "status": "BLOCKED", "issue": error.code})
            raise
        d02.publish(out, evidence, "docker-sql-stage.json", {**receipt, "status": "STAGED", "containerRef": ref})
    print("STAGED")


def main():
    parser = argparse.ArgumentParser()
    for name in ("config", "output", "pwsh"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--apply", action="store_true")
    for name in ("plan", "enrollment", "enrollment-sha256"):
        parser.add_argument("--" + name)
    args = parser.parse_args()
    try:
        require(os.name == "nt" and sys.flags.isolated and sys.flags.no_site
                and sys.flags.dont_write_bytecode, "D04_WINDOWS_WRAPPER_REQUIRED")
        run(args)
        return 0
    except LabError as error:
        print(f"BLOCKED {error.code}")
        return 2
    except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError):
        print("COLLECTION_FAILED D04_PLAN_ERROR")
        return 1


if __name__ == "__main__":
    sys.exit(main())

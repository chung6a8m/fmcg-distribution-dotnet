"""Plan and stage the stopped D03 Vault service on a verified D02 sandbox."""
import argparse
from datetime import datetime, timezone
import os
from pathlib import Path
import re
import ssl
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab import docker_d02 as d02, docker_preflight as d01
from tools.app02b_lab import docker_d02_topology as topology
from tools.app02b_lab.contract import LabError, canonical_json, digest, require, strict_json
from tools.app02b_lab.docker_d02_host import assert_private, snapshot
from tools.app02b_lab.preflight import beneath, existing_local_input, output_path, source_info

IMAGE = "hashicorp/vault@sha256:6c77f568e6b6310d5bc68befb5711b9215c574de7da489e7c24332581176888b"
HCL = ROOT / "deploy/lab/docker-wsl2/vault/rehearsal.hcl"
ENROLLMENT = "APP02B-LAB-DOCKER-VAULT-ENROLLMENT-V1"
STAGE = "CREATE_STOPPED_VAULT"
PLAN = "APP02B-LAB-DOCKER-VAULT-PLAN-V1"


def compose(project, nonce, certificate, key):
    require(type(project) is str and topology.PROJECT.fullmatch(project)
            and type(nonce) is str and d02.NONCE.fullmatch(nonce) and nonce != "0" * 32,
            "D03_INVALID_SCOPE")
    owner = {"io.fmcg-dms.lab-d03.project": project, "io.fmcg-dms.lab-d03.nonce": nonce}
    bindings = ((HCL, "rehearsal.hcl"), (certificate, "leaf.pem"), (key, "leaf.key"))
    mounts = [{"type": "bind", "source": str(source), "target": f"/vault/config/{name}",
               "read_only": True, "bind": {"create_host_path": False}} for source, name in bindings]
    mounts += [{"type": "volume", "source": name, "target": target}
               for name, target in (("data", "/vault/file"), ("audit", "/vault/logs"))]
    return {
        "name": project + "-d03",
        "services": {"vault": {
            "image": IMAGE, "platform": "linux/amd64", "pull_policy": "never",
            "container_name": project + "-d03-vault", "labels": owner,
            "entrypoint": ["/bin/vault", "server", "-config=/vault/config/rehearsal.hcl"],
            "command": [], "user": "vault:vault", "volumes": mounts,
            "networks": {"vault": {"aliases": ["vault"]}},
            "cap_drop": ["ALL"], "security_opt": ["no-new-privileges:true"],
            "read_only": True, "restart": "no", "pids_limit": 128,
            "mem_limit": "512m", "healthcheck": {"disable": True},
        }},
        "networks": {"vault": {"external": True, "name": project + "_vault"}},
        "volumes": {"data": {"external": True, "name": project + "_vault"},
                    "audit": {"name": project + "_d03_audit", "labels": owner}},
    }


def authorize(raw, approval_sha, plan_sha, nonce, operation, now):
    require(type(approval_sha) is str and d02.HEX.fullmatch(approval_sha)
            and digest(raw) == approval_sha, "D03_ENROLLMENT_UNAPPROVED")
    approval = strict_json(raw)
    d02.object_fields(approval, "schema operation nonce planSha256 expiresUtc", "D03_INVALID_ENROLLMENT")
    require((approval["schema"], approval["operation"], approval["planSha256"], approval["nonce"])
            == (ENROLLMENT, operation, plan_sha, nonce), "D03_ENROLLMENT_SCOPE_MISMATCH")
    try:
        expiry = datetime.strptime(approval["expiresUtc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except (ValueError, TypeError):
        raise LabError("D03_INVALID_EXPIRY") from None
    require(d02.timestamp(expiry) == approval["expiresUtc"] and now < expiry, "D03_ENROLLMENT_EXPIRED")


def protected_file(name, pwsh, *, confidential=False):
    path = existing_local_input(name, directory=False)
    assert_private(pwsh, [path.parent, path], confidential=confidential)
    return path, d01.bounded_input(path)


def d02_receipts(config, pwsh, source):
    plan_path, plan_raw = protected_file(config["d02Plan"], pwsh)
    result_path, result_raw = protected_file(config["d02Apply"], pwsh)
    require(plan_path != result_path and plan_path.parent != result_path.parent, "D03_D02_RECEIPTS")
    for path, raw, name in ((plan_path, plan_raw, "docker-rehearsal-plan.json"),
                            (result_path, result_raw, "docker-rehearsal-apply.json")):
        _, sums = protected_file(str(path.parent / "SHA256SUMS"), pwsh)
        require(path.name == name and sums == d02.checksum_file({name: raw}), "D03_D02_CHECKSUM")
    plan, result = strict_json(plan_raw), strict_json(result_raw)
    d02.object_fields(plan, "intent observed", "D03_D02_RECEIPTS")
    intent = plan["intent"]
    d02.object_fields(intent, "schema operation sourceHead configSha256 d01PlanSha256 d01ReportSha256 hostRef "
                      "engineRef context endpoint spec images imageIds compose exposure runtimeMode admission",
                      "D03_D02_RECEIPTS")
    require(type(result) is dict and result.get("schema") == d02.RESULT
            and result.get("status") == "STAGED" and result.get("operation") == d02.OPERATION
            and result.get("planSha256") == digest(plan_raw) and d01.nonzero_digest(result.get("enrollmentSha256"))
            and (intent["schema"], intent["operation"], intent["context"], intent["endpoint"],
                 intent["exposure"], intent["runtimeMode"], intent["admission"]) ==
                (d02.PLAN, d02.OPERATION, d01.CONTEXT, d01.ENDPOINT, "NONE", "FOUNDATION_ONLY", "NO_GO"),
            "D03_D02_NOT_STAGED")
    predecessor = intent["sourceHead"]
    require(type(predecessor) is str and re.fullmatch(r"[0-9a-f]{40}", predecessor)
            and result.get("sourceHead") == predecessor and d01.nonzero_digest(intent["hostRef"])
            and d01.nonzero_digest(intent["engineRef"]), "D03_D02_SOURCE")
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    git = subprocess.run(["git.exe", "merge-base", "--is-ancestor", predecessor, source],
                         cwd=ROOT, env=env, capture_output=True, timeout=15)
    require(git.returncode == 0, "D03_D02_SOURCE")
    spec = intent["spec"]
    d02.object_fields(spec, "project nonce networks volumes ports", "D03_D02_SCOPE")
    require(topology.resources(spec, spec["nonce"]) == spec
            and topology.compose(spec, intent["images"]) == intent["compose"], "D03_D02_SCOPE")
    return intent, result, {"d02PlanSha256": digest(plan_raw), "d02ApplySha256": digest(result_raw)}


def stage_model(docker, model, image_id, *, ensure_current):
    """Create only. Do not adopt, overwrite, start, or remove a partial staging."""
    ensure_current()
    docker.run("compose", "--project-directory", str(ROOT), "--env-file", os.devnull,
               "-p", model["name"], "-f", "-", "up", "--no-start", "--no-recreate", "--no-deps",
               "--no-build", "--pull", "never", "vault",
               input_data=canonical_json(model))
    service = model["services"]["vault"]
    identifier = d01.text_result(docker.run("container", "inspect", "--format", "{{.Id}}", "--",
                                           service["container_name"]))
    require(re.fullmatch(r"[a-f0-9]{64}", identifier), "D03_STAGE_ID")
    row, = docker._inspect("container", [identifier])
    mounts = {item.get("Destination"): item for item in row.get("Mounts") or []}
    host = row.get("HostConfig") or {}
    details = row.get("Config") or {}
    require(row.get("Image") == image_id and (row.get("State") or {}).get("Status") == "created"
            and not host.get("Privileged") and not host.get("PortBindings")
            and host.get("ReadonlyRootfs") is True and host.get("CapDrop") == ["ALL"]
            and set(host.get("SecurityOpt") or []) == {"no-new-privileges:true"}
            and details.get("User") == service["user"] and details.get("Entrypoint") == service["entrypoint"]
            and not details.get("Cmd") and all((details.get("Labels") or {}).get(k) == v
                                                for k, v in service["labels"].items())
            and host.get("NetworkMode") == model["networks"]["vault"]["name"]
            and set(mounts) == {m["target"] for m in service["volumes"]},
            "D03_STAGE_DRIFT")
    for mount in service["volumes"]:
        actual = mounts[mount["target"]]
        require(actual.get("Type") == mount["type"] and actual.get("RW") == (mount["type"] == "volume"),
                "D03_STAGE_MOUNT_DRIFT")
        if mount["type"] == "volume":
            require(actual.get("Name") == model["volumes"][mount["source"]]["name"], "D03_STAGE_MOUNT_DRIFT")
    audit, = docker._inspect("volume", [model["volumes"]["audit"]["name"]])
    require(audit.get("Driver") == "local" and not audit.get("Options")
            and all((audit.get("Labels") or {}).get(k) == v for k, v in service["labels"].items()),
            "D03_STAGE_AUDIT_DRIFT")
    return digest(identifier.encode("ascii"))


def run(args):
    require(not any(key.upper().startswith("COMPOSE_") for key in os.environ), "D03_COMPOSE_ENV_OVERRIDE")
    _, raw = protected_file(args.config, args.pwsh)
    config = strict_json(raw)
    d02.object_fields(config, "schema d01Plan d02Plan d02Apply certificate leafKey caCertificate evidenceRoot", "D03_INVALID_CONFIG")
    require(config["schema"] == "APP02B-LAB-DOCKER-VAULT-CONFIG-V1", "D03_INVALID_CONFIG")
    source, status = source_info()
    require(status == "CLEAN", "D03_SOURCE_NOT_CLEAN")
    intent, result, hashes = d02_receipts(config, args.pwsh, source)
    spec = intent["spec"]
    d01_path, d01_raw = protected_file(config["d01Plan"], args.pwsh)
    baseline = strict_json(d01_raw)
    d01.validate_plan(baseline)
    require(digest(d01_raw) == intent["d01PlanSha256"] and baseline["sourceHead"] == intent["sourceHead"]
            and topology.resources(baseline, spec["nonce"]) == spec
            and any(item["role"] == "VAULT" and item["reference"] == IMAGE for item in baseline["images"]),
            "D03_D01_VAULT_NOT_ENROLLED")
    d01_evidence = existing_local_input(baseline["evidenceRoot"], directory=True)
    d01.validate_publisher_receipts(d01_path, d01_evidence, baseline)
    evidence = existing_local_input(config["evidenceRoot"], directory=True)
    out = output_path(args.output)
    require(beneath(out, evidence), "D03_UNSAFE_OUTPUT")
    assert_private(args.pwsh, [evidence, out.parent])
    cert, cert_raw = protected_file(config["certificate"], args.pwsh)
    key, key_raw = protected_file(config["leafKey"], args.pwsh, confidential=True)
    ca, ca_raw = protected_file(config["caCertificate"], args.pwsh)
    require(len({cert, ca, key}) == 3 and b"PRIVATE KEY" not in cert_raw + ca_raw and b"BEGIN CERTIFICATE" in cert_raw
            and b"PRIVATE KEY" in key_raw and not beneath(key, evidence)
            and not any("$" in str(path) for path in (cert, key, HCL)), "D03_INVALID_TLS_INPUT")
    try:
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(cert, key, password=lambda: "")
        tls.load_verify_locations(cafile=ca)
    except ssl.SSLError:
        raise LabError("D03_INVALID_TLS_INPUT") from None
    model = compose(spec["project"], spec["nonce"], cert, key)
    require(len(model["name"]) <= 63, "D03_PROJECT_TOO_LONG")

    def observe():
        docker, host = snapshot(args.pwsh, baseline)
        require(d01.identity_refs(host) == (intent["hostRef"], intent["engineRef"])
                and d01.architecture(host["engine"].get("Architecture")) == "X64", "D03_TARGET_CHANGED")
        budget = baseline["budget"]
        require(d01.image_check({"reference": IMAGE, "role": "VAULT"}, host["images"].get("VAULT"))
                and d01.minimum(d01.mib(host.get("freeRam")), budget["hostFreeRamMiB"])
                and d01.minimum(d01.mib(host.get("diskFree")), budget["diskFreeMiB"]),
                "D03_HOST_BUDGET_OR_IMAGE_BLOCKED")
        ids = docker.image_ids(intent["images"], intent["sourceHead"])
        require(ids == intent["imageIds"], "D03_D02_IMAGE_CHANGED")
        state = docker.state(spec["project"])
        topology.validate_state(spec, intent["compose"], state, ids, complete=True)
        require(result.get("observed") == d02.scoped_inventory(state), "D03_D02_STATE_CHANGED")
        image = d01.json_result(docker.run("image", "inspect", "--format",
                    d01.projection(("Id", ".Id"), ("Os", ".Os"), ("Architecture", ".Architecture"),
                                   ("RepoDigests", ".RepoDigests")), "--", IMAGE), dict)
        image_id = image.get("Id")
        require(image.get("Os") == "linux" and image.get("Architecture") == "amd64"
                and type(image.get("RepoDigests")) is list and IMAGE in image["RepoDigests"]
                and type(image_id) is str and re.fullmatch(r"sha256:[a-f0-9]{64}", image_id),
                "D03_VAULT_IMAGE_UNVERIFIED")
        document = {"schema": PLAN, "operation": STAGE, "sourceHead": source, "nonce": spec["nonce"],
                    "hostRef": intent["hostRef"], "engineRef": intent["engineRef"], **hashes,
                    "configSha256": digest(raw), "certificateSha256": digest(cert_raw), "caSha256": digest(ca_raw),
                    "tlsInputsSha256": digest(canonical_json([digest(cert_raw), digest(key_raw), digest(ca_raw)])),
                    "hclSha256": digest(HCL.read_bytes()), "imageId": image_id,
                    "composeSha256": digest(canonical_json(model)),
                    "project": spec["project"], "image": IMAGE, "exposure": "NONE"}
        return docker, document

    if not args.apply:
        _, plan = observe()
        d02.publish(out, evidence, "docker-vault-plan.json", plan)
        print("PLANNED")
        return

    _, plan_raw = protected_file(args.plan, args.pwsh)
    _, approval = protected_file(args.enrollment, args.pwsh)
    plan_sha = digest(plan_raw)
    approved = strict_json(plan_raw)

    with d02.project_lock(spec, intent):
        docker, current = observe()

        def check():
            authorize(approval, args.enrollment_sha256, plan_sha, spec["nonce"], STAGE, d02.utc_now())
            _, observed = observe()
            require(approved == current == observed and source_info() == (source, "CLEAN")
                    and protected_file(config["certificate"], args.pwsh)[1] == cert_raw
                    and protected_file(config["leafKey"], args.pwsh, confidential=True)[1] == key_raw
                    and protected_file(config["caCertificate"], args.pwsh)[1] == ca_raw,
                    "D03_PLAN_CHANGED")
            authorize(approval, args.enrollment_sha256, plan_sha, spec["nonce"], STAGE, d02.utc_now())

        receipt = {"schema": "APP02B-LAB-DOCKER-VAULT-STAGE-V1", "sourceHead": source,
                   "planSha256": plan_sha, "enrollmentSha256": digest(approval),
                   "collectedUtc": d02.timestamp(d02.utc_now()), "operation": STAGE,
                   "nonce": spec["nonce"], "caSha256": digest(ca_raw)}
        try:
            ref = stage_model(docker, model, current["imageId"], ensure_current=check)
        except LabError as error:
            d02.publish(out, evidence, "docker-vault-stage.json", {**receipt, "status": "BLOCKED", "issue": error.code})
            raise
        d02.publish(out, evidence, "docker-vault-stage.json", {**receipt, "status": "STAGED", "containerRef": ref})
        print("STAGED")


def main():
    if os.name != "nt" or not (sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode):
        print("COLLECTION_FAILED D03_WINDOWS_WRAPPER_REQUIRED")
        return 1
    parser = argparse.ArgumentParser()
    for name in ("config", "output", "pwsh"):
        parser.add_argument("--" + name, required=True)
    for name in ("plan", "enrollment", "enrollment-sha256"):
        parser.add_argument("--" + name)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        fields = (args.plan, args.enrollment, args.enrollment_sha256)
        require(all(fields) if args.apply else not any(fields), "D03_INVALID_ARGUMENTS")
        run(args)
        return 0
    except LabError as error:
        print(f"BLOCKED {error.code}")
        return 2
    except (OSError, TypeError, KeyError, AttributeError, ValueError, subprocess.SubprocessError):
        print("COLLECTION_FAILED D03_RUNTIME_ERROR")
        return 1


if __name__ == "__main__":
    sys.exit(main())

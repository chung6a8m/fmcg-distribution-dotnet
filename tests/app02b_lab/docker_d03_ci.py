"""Real Vault on the disposable GitHub-hosted runner. No live approval or host adapter."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from tools.app02b_lab import docker_d03 as d03
from tools.app02b_lab.contract import LabError, canonical_json, require
from tools.app02b_lab.docker_d03_client import (AUDIT_FILE, AUDIT_HEADER, AUDIT_PATH, POLICIES, ROLE_PATHS,
                                                Vault, probe_roles, rotate, token_for_role)

PROJECT = f"fmcg-lab-d03-{os.environ.get('GITHUB_RUN_ID', '')}-{os.environ.get('GITHUB_RUN_ATTEMPT', '')}"
NONCE = "d03" + "0" * 28 + "1"


def command(*args, input_data=None):
    result = subprocess.run(args, input=input_data, capture_output=True, timeout=120)
    if result.returncode:
        raise LabError("CI_NATIVE_COMMAND_FAILED", " ".join(args[:3]))
    return result.stdout


class CiDocker:
    def __init__(self):
        require(Path("/var/run/docker.sock").is_socket() and not any(
            name.startswith(("DOCKER_", "COMPOSE_")) for name in os.environ), "CI_LOCAL_DOCKER_REQUIRED")
        self.engine = self.identity()

    def identity(self):
        value = command("docker", "--host", "unix:///var/run/docker.sock", "info", "--format", "{{.OSType}} {{.ID}}")
        require(value.startswith(b"linux ") and bool(value.strip()[6:]), "CI_LOCAL_DOCKER_REQUIRED")
        return value

    def run(self, *args, input_data=None):
        require(self.identity() == self.engine, "CI_DAEMON_CHANGED")
        return command("docker", "--host", "unix:///var/run/docker.sock", *args, input_data=input_data)

    def _inspect(self, kind, names):
        return json.loads(self.run(kind, "inspect", "--", *names))


def certificate(folder, name, authority=None):
    key, crt = folder / f"{name}.key", folder / f"{name}.pem"
    if authority is None:
        command("openssl", "req", "-x509", "-newkey", "rsa:2048", "-noenc", "-days", "1",
                "-subj", f"/CN={name}", "-keyout", str(key), "-out", str(crt))
    else:
        csr, ext = folder / f"{name}.csr", folder / f"{name}.ext"
        ext.write_text("subjectAltName=DNS:vault\nextendedKeyUsage=serverAuth\n", encoding="ascii")
        command("openssl", "req", "-newkey", "rsa:2048", "-noenc", "-subj", "/CN=vault",
                "-keyout", str(key), "-out", str(csr))
        command("openssl", "x509", "-req", "-in", str(csr), "-CA", str(authority[0]),
                "-CAkey", str(authority[1]), "-CAcreateserial", "-days", "1",
                "-extfile", str(ext), "-out", str(crt))
        # The directory is mode 0700 on the runner. Only the directly mounted leaf files are visible in Vault.
        key.chmod(0o644)
    return crt, key


def await_health(vault, expected):
    for _ in range(40):
        try:
            status, _ = vault.request("GET", "sys/health")
            if status == expected:
                return
        except LabError as error:
            if error.code != "D03_VAULT_UNREACHABLE":
                raise
        time.sleep(0.35)
    raise LabError("CI_VAULT_HEALTH_TIMEOUT")


def bootstrap(vault):
    init = vault.expect(200, "POST", "sys/init", timeout=30,
                        payload={"secret_shares": 3, "secret_threshold": 2})
    keys, root = init["keys_base64"], init["root_token"]
    unseal(vault, keys)
    vault.expect(204, "PUT", "sys/config/auditing/request-headers/" + AUDIT_HEADER.lower(),
                 token=root, payload={"hmac": False})
    vault.expect(204, "PUT", "sys/audit/" + AUDIT_PATH, token=root,
                 payload={"type": "file", "options": {"file_path": AUDIT_FILE, "log_raw": "false"}})
    vault.expect(204, "POST", "sys/mounts/preprod", token=root,
                 payload={"type": "kv", "options": {"version": "2"}})
    directory = d03.ROOT / "deploy/lab/docker-wsl2/vault"
    policy_root = d03.ROOT / "deploy/preprod"
    source = {"api": policy_root / "vault-policy-api.hcl", "worker": policy_root / "vault-policy-worker.hcl",
              "operator": directory / "operator-probe.hcl"}
    revoke = (directory / "token-self-revoke.hcl").read_text(encoding="utf-8")
    for role, path in ROLE_PATHS.items():
        policy = source[role].read_text(encoding="utf-8") + "\n" + revoke
        vault.expect(204, "PUT", "sys/policies/acl/" + POLICIES[role], token=root, payload={"policy": policy})
        vault.expect(204, "POST", "auth/token/roles/" + POLICIES[role], token=root,
                     payload={"allowed_policies": [POLICIES[role]], "orphan": True, "renewable": False,
                              "token_explicit_max_ttl": "5m", "token_no_default_policy": True})
        vault.expect(200, "POST", path, token=root, payload={"data": {"fixture": "LAB_D03_SYNTHETIC"}})
    policy = (directory / "token-issuer.hcl").read_text(encoding="utf-8")
    vault.expect(204, "PUT", "sys/policies/acl/lab-d03-issuer", token=root, payload={"policy": policy})
    issuer = vault.expect(200, "POST", "auth/token/create", token=root,
                         payload={"policies": ["lab-d03-issuer"], "ttl": "10m", "no_default_policy": True})
    return keys, root, issuer["auth"]["client_token"]


def unseal(vault, keys):
    for key in keys[:2]:
        status = vault.expect(200, "POST", "sys/unseal", timeout=30, payload={"key": key})
    require(status.get("sealed") is False, "CI_VAULT_UNSEAL")
    await_health(vault, 200)


def must_block(fn, expected):
    try:
        fn()
    except LabError as error:
        require(error.code == expected, "CI_WRONG_BLOCK_REASON", error.code)
    else:
        raise LabError("CI_EXPECTED_BLOCK_MISSING")


def run(report, folder):
    ca = certificate(folder, "lab-d03-ca")
    wrong_ca = certificate(folder, "lab-d03-wrong-ca")
    cert, key = certificate(folder, "leaf", ca)
    leaf_sha = d03.validate_pem(cert.read_bytes(), key.read_bytes(), ca[0].read_bytes())
    must_block(lambda: d03.validate_pem(cert.read_bytes(), key.read_bytes() + ca[1].read_bytes(),
                                       ca[0].read_bytes()), "D03_INVALID_TLS_INPUT")
    d03.verify_tls_inputs(cert, key, ca[0].read_bytes())
    must_block(lambda: d03.verify_tls_inputs(cert, key, wrong_ca[0].read_bytes()), "D03_INVALID_TLS_INPUT")
    report["checks"]["offline_certificate_chain"] = "PASS"
    model = d03.compose(PROJECT, NONCE, cert, key)
    docker = CiDocker()
    name = model["services"]["vault"]["container_name"]
    network = model["networks"]["vault"]["name"]
    volume = model["volumes"]["data"]["name"]

    docker.run("pull", "--platform", "linux/amd64", d03.IMAGE)
    image, = json.loads(docker.run("image", "inspect", d03.IMAGE))
    require((image["Os"], image["Architecture"]) == ("linux", "amd64"), "CI_VAULT_IMAGE_ARCH")
    docker.run("network", "create", "--internal", "--label", f"lab-d03-ci={NONCE}", network)
    docker.run("volume", "create", "--label", f"lab-d03-ci={NONCE}", volume)
    d03.stage_model(docker, model, image["Id"], ensure_current=lambda: None)
    report["checks"]["stopped_private_compose"] = "PASS"
    docker.run("start", name)
    row, = docker._inspect("container", [name])
    ip = row["NetworkSettings"]["Networks"][network]["IPAddress"]
    require(bool(ip), "CI_PRIVATE_ADDRESS")
    vault = Vault("https://vault:8200", ca[0], connect_address=ip, leaf_sha=leaf_sha)

    await_health(vault, 501)
    must_block(lambda: Vault("https://vault:8200", wrong_ca[0], connect_address=ip).request("GET", "sys/health"),
               "D03_TLS_REJECTED")
    must_block(lambda: Vault("https://bad-name:8200", ca[0], connect_address=ip).request("GET", "sys/health"),
               "D03_TLS_REJECTED")
    must_block(lambda: Vault("https://vault:8200", ca[0], connect_address=ip, leaf_sha="f" * 64)
               .request("GET", "sys/health"), "D03_TLS_LEAF_CHANGED")
    report["checks"]["untrusted_ca_and_wrong_hostname"] = "PASS"

    report["phase"] = "initialize_and_enroll_roles"
    keys, root, issuer = bootstrap(vault)
    cluster = vault.cluster()
    tokens = {role: token_for_role(vault, role, issuer) for role in ROLE_PATHS}
    vault.expect(403, "POST", "auth/token/create/lab-d03-api", token=tokens["api"], payload={})
    vault.expect(403, "GET", ROLE_PATHS["api"], token=issuer)

    def read_audit():
        raw = docker.run("exec", name, "/bin/cat", AUDIT_FILE)
        require(len(raw) < 8_000_000, "CI_AUDIT_LIMIT")
        return [json.loads(line) for line in raw.splitlines()]

    report["phase"] = "verify_role_audit"
    report["checks"]["role_matrix"] = probe_roles(vault, tokens, read_audit)
    replacement = []
    rotate(vault, "api", issuer, tokens["api"], replacement.append, lambda: None, read_audit)
    tokens["api"], = replacement
    report["checks"]["rotation_and_old_token_denial"] = "PASS"

    expired = vault.expect(200, "POST", "auth/token/create/lab-d03-worker", token=issuer,
                           payload={"ttl": "3s", "policies": [POLICIES["worker"]],
                                    "no_default_policy": True})["auth"]["client_token"]
    vault.expect(200, "GET", ROLE_PATHS["worker"], token=expired)
    for _ in range(36):
        status, _ = vault.request("GET", ROLE_PATHS["worker"], token=expired)
        if status == 403:
            break
        time.sleep(0.25)
    require(status == 403, "CI_EXPIRED_TOKEN_NOT_DENIED")
    report["checks"]["expired_token_denial"] = "PASS"

    report["phase"] = "restart_and_unseal"
    audit_before = read_audit()
    docker.run("restart", name)
    await_health(vault, 503)
    unseal(vault, keys)
    require(vault.cluster() == cluster, "CI_RAFT_CLUSTER_DRIFT")
    require(read_audit()[:len(audit_before)] == audit_before, "CI_AUDIT_NOT_PERSISTED")
    report["checks"]["raft_restart"] = probe_roles(vault, tokens, read_audit)
    # Operate only on the disposable single-node fixture. The retained log reader
    # still sees older records while Vault no longer writes the new probe attempts.
    vault.expect(204, "DELETE", "sys/audit/" + AUDIT_PATH, token=root)
    devices = vault.expect(200, "GET", "sys/audit", token=root)
    require(AUDIT_PATH + "/" not in (devices.get("data") or devices), "CI_AUDIT_STILL_ENABLED")
    must_block(lambda: probe_roles(vault, tokens, read_audit), "D03_AUDIT_WITNESS_MISSING")
    report["checks"]["missing_audit_fails_closed"] = "PASS"


def main():
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and os.environ.get("RUNNER_OS") == "Linux", "CI_DISPOSABLE_RUNNER_REQUIRED")
    root = Path(os.environ["RUNNER_TEMP"])
    head = os.environ["LAB_D03_SOURCE_HEAD"]
    require(command("git", "rev-parse", "HEAD").decode().strip() == head, "CI_SOURCE_MISMATCH")
    report = {"schema": "APP02B-LAB-D03-SYNTHETIC-CI-V1", "sourceHead": head, "image": d03.IMAGE,
              "environment": "GITHUB_HOSTED_SYNTHETIC", "httpsVantage": "LINUX_RUNNER_PRIVATE_BRIDGE_IP",
              "auditVantage": "RUNNER_DOCKER_EXEC_READBACK", "checks": {}, "status": "FAIL",
              "host": "NOT_RUN", "originalGateStatus": "NOT_VERIFIED"}
    try:
        with tempfile.TemporaryDirectory(prefix="lab-d03-", dir=root) as tmp:
            run(report, Path(tmp))
        report["status"] = "PASS"
        return 0
    except LabError as error:
        report["failure"] = str(error)
        print(error)
        return 1
    except Exception as error:
        report["failure"] = type(error).__name__
        print("LAB-D03 synthetic collection failed", type(error).__name__)
        return 1
    finally:
        # The entire disposable hosted VM is destroyed by Actions. No global Docker cleanup.
        (root / "lab-d03-ci.json").write_bytes(canonical_json(report))


if __name__ == "__main__":
    sys.exit(main())

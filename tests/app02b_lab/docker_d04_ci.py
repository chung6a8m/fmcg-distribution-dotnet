"""Real disposable SQL Server and Vault on the hosted runner; no laptop operation."""
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile
import time

from docker_d03_ci import CiDocker, await_health, certificate, command, must_block
from tools.app02b_lab import docker_d03 as d03, docker_d04 as d04
from tools.app02b_lab.contract import LabError, canonical_json, require
from tools.app02b_lab.docker_d03_client import Vault
from tools.app02b_lab.docker_d04_peer import credential_path, load_credentials, sql_run, sql_tls

PROJECT = f"fmcg-lab-d04-{os.environ.get('GITHUB_RUN_ID', '')}-{os.environ.get('GITHUB_RUN_ATTEMPT', '')}"


def private_address(docker, name, network):
    row, = docker._inspect("container", [name])
    host = row["HostConfig"]
    networks = row["NetworkSettings"]["Networks"]
    require(not host["PortBindings"] and not host["Privileged"] and not host["PublishAllPorts"]
            and set(networks) == {network} and networks[network]["IPAddress"], "D04_CI_NETWORK_SCOPE")
    return networks[network]["IPAddress"]


def wait_sql(docker, name):
    for _ in range(120):
        row, = docker._inspect("container", [name])
        logs = docker.run("logs", "--tail", "100", name, include_stderr=True)
        if not row["State"]["Running"]:
            patterns = (b"permission denied", b"operation not permitted", b"read-only file system",
                        b"private key", b"certificate", b"bootstrap", b"master.mdf", b"memory",
                        b"invalid", b"fault", b"failed", b"address", b"administrator password")
            flags = [word.decode("ascii") for word in patterns if word in logs.lower()]
            import re
            errors = sorted(set(re.findall(rb"(?im)(?:Error: |error code: |reason: )([0-9a-fx]{1,12})\b", logs)))
            raise LabError("D04_CI_SQL_STOPPED", f"exit={row['State']['ExitCode']}, "
                           + ",".join(flags + [code.decode("ascii") for code in errors]))
        if b"SQL Server is now ready for client connections" in logs:
            return
        time.sleep(1)
    raise LabError("D04_CI_SQL_NOT_READY")


def issue_roles(vault, spec, credentials):
    init = vault.expect(200, "POST", "sys/init", timeout=30, payload={"secret_shares": 3, "secret_threshold": 2})
    for key in init["keys_base64"][:2]:
        vault.expect(200, "POST", "sys/unseal", timeout=30, payload={"key": key})
    await_health(vault, 200)
    root = init["root_token"]
    vault.expect(204, "PUT", "sys/audit/lab-file", token=root,
                 payload={"type": "file", "options": {"file_path": "/vault/logs/lab.jsonl", "log_raw": "false"}})
    vault.expect(204, "POST", "sys/mounts/preprod", token=root, payload={"type": "kv", "options": {"version": "2"}})
    tokens = {}
    for role in d04.ROLES:
        path = credential_path(spec, role)
        policy = "lab-d04-" + role.lower()
        vault.expect(200, "POST", path, token=root, payload={
            "data": {"login": spec["logins"][role], "password": credentials[role]},
        })
        vault.expect(204, "PUT", "sys/policies/acl/" + policy, token=root,
                     payload={"policy": f'path "{path}" {{ capabilities = ["read"] }}'})
        reply = vault.expect(200, "POST", "auth/token/create", token=root,
                             payload={"policies": [policy], "ttl": "10m", "no_default_policy": True})
        tokens[role] = reply["auth"]["client_token"]
    return load_credentials(vault, spec, tokens)


def run(folder, report):
    docker = CiDocker()
    nonce = secrets.token_hex(16)
    spec = d04.scope(PROJECT, nonce)
    sql_ca = certificate(folder, "d04-sql-ca")
    sql_cert, sql_key = certificate(folder, "sql-leaf", sql_ca, hostname="sql")
    vault_ca = certificate(folder, "d04-vault-ca")
    vault_cert, vault_key = certificate(folder, "vault-leaf", vault_ca)
    d03.verify_tls_inputs(sql_cert, sql_key, sql_ca[0].read_bytes(), hostname="sql")
    must_block(lambda: d03.verify_tls_inputs(sql_cert, sql_key, sql_ca[0].read_bytes(), hostname="wrong"),
               "D03_INVALID_TLS_INPUT")
    passwords = {role: "Aa!9" + secrets.token_hex(24) for role in (*d04.ROLES, "bootstrap", "revoker")}
    secret = folder / "bootstrap"
    secret.write_text(passwords["bootstrap"] + "\n", encoding="ascii")
    # Runner-private 0700 parent. Docker mounts only this file into the UID 10001 container.
    secret.chmod(0o644)

    sql_model = d04.compose(spec, sql_cert, sql_key, secret)
    vault_model = d03.compose(PROJECT, nonce, vault_cert, vault_key)
    sql_name = sql_model["services"]["sql"]["container_name"]
    vault_name = vault_model["services"]["vault"]["container_name"]
    sql_net = sql_model["networks"]["data"]["name"]
    vault_net = vault_model["networks"]["vault"]["name"]
    for image in (d04.IMAGE, d03.IMAGE):
        docker.run("pull", "--platform", "linux/amd64", image)
        row, = json.loads(docker.run("image", "inspect", image))
        require((row["Os"], row["Architecture"]) == ("linux", "amd64"), "D04_CI_IMAGE_ARCH")
    for name in (sql_net, vault_net):
        docker.run("network", "create", "--internal", "--label", f"lab-d04-ci={nonce}", name)
    for name in (sql_model["volumes"]["data"]["name"], vault_model["volumes"]["data"]["name"]):
        docker.run("volume", "create", "--label", f"lab-d04-ci={nonce}", name)
    for model, role in ((sql_model, "sql"), (vault_model, "vault")):
        docker.run("compose", "--project-directory", str(d04.ROOT), "--env-file", "/dev/null", "-p", model["name"],
                   "-f", "-", "up", "--no-build", "--pull", "never", "-d", role, input_data=canonical_json(model))
    report["phase"] = "bootstrap_vault"
    vault = Vault("https://vault:8200", vault_ca[0], connect_address=private_address(docker, vault_name, vault_net))
    await_health(vault, 501)
    sql_secrets = issue_roles(vault, spec, passwords)
    report["checks"]["vault_sql_role_separation"] = "PASS"
    report["phase"] = "sql_server_startup"
    wait_sql(docker, sql_name)
    address = private_address(docker, sql_name, sql_net)
    row, = docker._inspect("container", [sql_name])
    mounts = {mount["Destination"]: mount for mount in row["Mounts"]}
    require(mounts["/var/opt/mssql"]["Name"] == sql_model["volumes"]["data"]["name"]
            and mounts["/var/opt/mssql-backup"]["Name"] == sql_model["volumes"]["backup"]["name"]
            and not any(item.startswith("MSSQL_SA_PASSWORD=") for item in row["Config"]["Env"]),
            "D04_CI_MOUNT_OR_SECRET_DRIFT")
    leaf_sha = d03.validate_pem(sql_cert.read_bytes(), sql_key.read_bytes(), sql_ca[0].read_bytes())
    must_block(lambda: sql_tls(address, vault_ca[0].read_bytes(), leaf_sha), "D04_SQL_TLS_CA_OR_NAME_REJECTED")
    must_block(lambda: sql_tls(address, sql_ca[0].read_bytes(), "f" * 64), "D04_SQL_TLS_LEAF_CHANGED")
    leaf = sql_tls(address, sql_ca[0].read_bytes(), leaf_sha)
    report["checks"]["strict_tls_ca_name_and_leaf"] = "PASS"
    report["phase"] = "sql_migrations"
    report["checks"]["migration_reapply"] = sql_run("bootstrap", address, spec, passwords, leaf)
    report["phase"] = "sql_permission_matrix"
    before = sql_run("verify", address, spec, sql_secrets, leaf)
    report["checks"]["permissions"] = before
    report["checks"]["sql_fence"] = sql_run("fixture-fence", address, spec, passwords, leaf)
    docker.run("restart", sql_name)
    report["phase"] = "sql_persistence"
    wait_sql(docker, sql_name)
    for _ in range(20):
        try:
            restarted_address = private_address(docker, sql_name, sql_net)
            restarted_leaf = sql_tls(restarted_address, sql_ca[0].read_bytes(), leaf_sha)
            after = sql_run("verify", restarted_address, spec, sql_secrets, restarted_leaf)
            break
        except LabError as error:
            if error.code not in ("D04_SQL_ERROR_-2", "D04_SQL_ERROR_10054", "D04_SQL_ERROR_0", "D04_SQL_TLS_UNAVAILABLE"):
                raise
            time.sleep(1)
    else:
        raise LabError("D04_CI_SQL_RESTART_TIMEOUT")
    require(before == after, "D04_CI_RESTART_FINGERPRINT_DRIFT")
    report["checks"]["revoked_fence_restart"] = sql_run("fixture-fence-readback", restarted_address,
                                                      spec, passwords, restarted_leaf)
    report["checks"]["named_volume_restart_readback"] = "PASS"


def main():
    require(sys.platform == "linux" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and os.environ.get("RUNNER_OS") == "Linux", "D04_CI_DISPOSABLE_RUNNER_REQUIRED")
    source = os.environ["LAB_D04_SOURCE_HEAD"]
    require(command("git", "rev-parse", "HEAD").decode().strip() == source, "D04_CI_SOURCE_MISMATCH")
    directory = Path(os.environ["RUNNER_TEMP"])
    report = {"schema": "APP02B-LAB-DOCKER-SQL-SYNTHETIC-CI-V1", "sourceHead": source,
              "environment": "GITHUB_HOSTED_SYNTHETIC", "vantage": "LINUX_RUNNER_PRIVATE_BRIDGE_IP",
              "image": d04.IMAGE, "version": d04.VERSION, "status": "FAIL", "checks": {},
              "originalGateStatus": "NOT_VERIFIED", "host": "NOT_RUN", "tenantLifecycle": "NOT_RUN",
              "b53": "NOT_RUN", "pitr": "NOT_RUN"}
    try:
        with tempfile.TemporaryDirectory(prefix="lab-d04-", dir=directory) as tmp:
            run(Path(tmp), report)
        report["status"] = "PASS"
        return 0
    except LabError as error:
        report["failure"] = str(error)
        print(error)
        return 1
    except Exception as error:
        report["failure"] = type(error).__name__
        print("D04_CI_COLLECTION_FAILED", type(error).__name__)
        return 1
    finally:
        (directory / "lab-d04-ci.json").write_bytes(canonical_json(report))


if __name__ == "__main__":
    sys.exit(main())

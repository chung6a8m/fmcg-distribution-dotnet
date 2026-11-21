"""Read D04 role credentials from enrolled Vault and run the bounded SQL verifier."""
import argparse
from base64 import b64encode
from pathlib import Path
import re
import socket
import ssl
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab import docker_d03_peer as peer, docker_d04 as d04
from tools.app02b_lab.contract import LabError, canonical_json, digest, require, strict_json
from tools.app02b_lab.docker_d03_client import Vault

PROJECT = ROOT / "tools/lab-d04-sql/FmcgDms.Lab.D04.Sql.csproj"


def credential_path(spec, role):
    return f"preprod/data/lab-d04/{spec['nonce']}/{role.lower()}"


def load_credentials(vault, spec, tokens):
    require(set(tokens) == set(d04.ROLES) and len(set(tokens.values())) == len(d04.ROLES),
            "D04_DISTINCT_VAULT_ROLES_REQUIRED")
    credentials = {}
    for role, token in tokens.items():
        value = vault.expect(200, "GET", credential_path(spec, role), token=token)
        secret = value.get("data", {}).get("data", {})
        require(type(secret) is dict and set(secret) == {"login", "password"}
                and secret["login"] == spec["logins"][role] and type(secret["password"]) is str
                and 12 <= len(secret["password"]) <= 128, "D04_VAULT_SQL_ROLE_MISMATCH")
        credentials[role] = secret["password"]
        for other in tokens:
            if role != other:
                vault.expect(403, "GET", credential_path(spec, other), token=token)
    require(len(set(credentials.values())) == len(credentials), "D04_DISTINCT_SQL_ROLES_REQUIRED")
    return credentials


def sql_tls(endpoint, ca, leaf_sha):
    """Verify the actual TDS 8 TLS handshake without sending a SQL credential."""
    require(d04.d03.d01.nonzero_digest(leaf_sha) and b"PRIVATE KEY" not in ca, "D04_SQL_TRUST_INPUT")
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    tls.minimum_version = ssl.TLSVersion.TLSv1_2
    tls.hostname_checks_common_name = False
    tls.load_verify_locations(cadata=ca.decode("ascii"))
    try:
        with socket.create_connection((endpoint, 1433), timeout=5) as transport:
            with tls.wrap_socket(transport, server_hostname="sql") as encrypted:
                leaf = encrypted.getpeercert(binary_form=True)
    except ssl.SSLCertVerificationError:
        raise LabError("D04_SQL_TLS_CA_OR_NAME_REJECTED") from None
    except OSError:
        raise LabError("D04_SQL_TLS_UNAVAILABLE") from None
    require(leaf and digest(leaf) == leaf_sha, "D04_SQL_TLS_LEAF_CHANGED")
    return leaf


def sql_run(mode, endpoint, spec, credentials, leaf, *, cli=None):
    cli = cli or ["dotnet", "run", "--no-build", "-c", "Release", "--project", str(PROJECT), "--"]
    result = subprocess.run([*cli, mode], input=canonical_json({
        "endpoint": endpoint, "spec": spec, "credentials": credentials,
        "certificateDer": b64encode(leaf).decode("ascii"),
    }), capture_output=True, timeout=250, cwd=ROOT)
    if result.returncode:
        # The C# entry point emits one bounded error code only. Never pass through native text.
        code = result.stderr.strip().decode("ascii", errors="replace")
        code = code if re.fullmatch(r"D04_SQL_ERROR_-?[0-9]{1,12}|D04_[A-Za-z0-9_]{1,100}", code) else "D04_SQL_TOOL_FAILED"
        raise LabError(code)
    value = strict_json(result.stdout)
    require(type(value) is dict, "D04_INVALID_SQL_RESULT")
    return value


def run(args):
    plan_raw, plan = _file(args.plan)
    sql_stage_raw, sql_stage = _file(args.sql_stage)
    stage_raw, stage = _file(args.stage)
    scope_raw, scope = _file(args.vault_scope)
    trust_raw, observation = _file(args.trust)
    require(plan.get("schema") == d04.POLICY and plan.get("status") == "PLAN_ONLY"
            and plan.get("sourceHead") == peer.checkout() and plan.get("d03StageSha256") == digest(stage_raw)
            and plan.get("d03ScopeSha256") == digest(scope_raw) and plan.get("d03PeerSha256") == digest(trust_raw)
            and plan.get("d03LeafDerSha256") == stage.get("leafDerSha256")
            and d04.d03.d01.nonzero_digest(stage.get("leafDerSha256"))
            and scope.get("schema") == "APP02B-LAB-DOCKER-VAULT-PEER-V1"
            and scope.get("stageReceiptSha256") == digest(stage_raw)
            and scope.get("sourceHead") == stage.get("sourceHead")
            and scope.get("nonce") == stage.get("nonce"), "D04_PEER_SCOPE_MISMATCH")
    d04.require_trust(observation, digest(stage_raw), stage.get("sourceHead"))
    require(sql_stage.get("schema") == "APP02B-LAB-DOCKER-SQL-STAGE-V1"
            and sql_stage.get("status") == "STAGED" and sql_stage.get("sourceHead") == plan["sourceHead"]
            and sql_stage.get("operation") == d04.STAGE and sql_stage.get("planSha256") == digest(plan_raw)
            and sql_stage.get("d03StageSha256") == digest(stage_raw)
            and d04.d03.d01.nonzero_digest(sql_stage.get("containerRef")), "D04_SQL_STAGE_MISMATCH")
    spec = plan["spec"]
    require(spec == d04.scope(spec["project"], spec["nonce"]) and spec["nonce"] == scope["nonce"],
            "D04_PEER_SCOPE_MISMATCH")
    ca = peer.read_file(args.vault_ca)
    require(digest(ca) == scope.get("caSha256") == stage.get("caSha256") and b"PRIVATE KEY" not in ca,
            "D04_VAULT_CA_MISMATCH")
    sql_ca = peer.read_file(args.sql_ca)
    require(digest(sql_ca) == plan.get("sqlCaSha256"), "D04_SQL_CA_MISMATCH")
    peer.custody_parent(args.output.parent)
    permit = peer.read_file(args.enrollment)

    def authorize():
        d04.d03.authorize(permit, args.enrollment_sha256, digest(plan_raw), spec["nonce"],
                          "D04_PROBE_ROLLBACK_ONLY", d04.d02.utc_now(), schema=d04.ENROLLMENT)

    authorize()
    leaf = sql_tls("sql", sql_ca, plan["sqlLeafDerSha256"])
    vault = Vault("https://vault:8200", ca, leaf_sha=stage["leafDerSha256"])
    require(vault.cluster() == scope.get("clusterId"), "D04_VAULT_CLUSTER_CHANGED")
    credentials = load_credentials(vault, spec, {role: peer.secret(f"D04 {role} Vault token") for role in d04.ROLES})
    require(vault.cluster() == scope.get("clusterId"), "D04_VAULT_CLUSTER_CHANGED")
    authorize()
    value = sql_run("verify", "sql", spec, credentials, leaf)
    peer.output_file(args.output, canonical_json({
        "schema": "APP02B-LAB-DOCKER-SQL-PEER-RESULT-V1", "planSha256": digest(plan_raw),
        "sqlStageSha256": digest(sql_stage_raw), "sourceHead": plan["sourceHead"], "vantage": "OPERATOR_ENROLLED_PRIVATE_PEER",
        "originalGateStatus": "NOT_VERIFIED", "checks": value, "lifecycle": "NOT_RUN", "pitr": "NOT_RUN",
    }))
    print(value["status"])


def _file(path):
    raw = peer.read_file(path)
    value = strict_json(raw)
    require(type(value) is dict, "D04_INVALID_PEER_INPUT")
    return raw, value


def main():
    parser = argparse.ArgumentParser()
    for name in ("plan", "sql-stage", "stage", "trust", "vault-scope", "vault-ca", "sql-ca", "enrollment", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--enrollment-sha256", required=True)
    try:
        require(sys.platform == "linux" and sys.flags.isolated and sys.flags.no_site
                and sys.flags.dont_write_bytecode, "D04_ISOLATED_LINUX_PEER_REQUIRED")
        run(parser.parse_args())
        return 0
    except LabError as error:
        print(f"BLOCKED {error.code}")
        return 2
    except (OSError, ValueError, KeyError, AttributeError, TypeError, subprocess.SubprocessError):
        print("COLLECTION_FAILED D04_PEER_ERROR")
        return 1


if __name__ == "__main__":
    sys.exit(main())

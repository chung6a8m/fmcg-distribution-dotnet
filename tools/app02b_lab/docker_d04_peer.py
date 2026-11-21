"""Read D04 role credentials from enrolled Vault and run the bounded SQL verifier."""
import argparse
import json
from pathlib import Path
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


def sql_run(mode, endpoint, spec, credentials, *, cli=None):
    cli = cli or ["dotnet", "run", "--no-build", "-c", "Release", "--project", str(PROJECT), "--"]
    result = subprocess.run([*cli, mode], input=canonical_json({
        "endpoint": endpoint, "spec": spec, "credentials": credentials,
    }), capture_output=True, timeout=250, cwd=ROOT)
    if result.returncode:
        # The C# entry point emits one bounded error code only. Never pass through native text.
        code = result.stderr.strip().decode("ascii", errors="replace")
        import re
        code = code if re.fullmatch(r"D04_[A-Za-z0-9_]{1,100}", code) else "D04_SQL_TOOL_FAILED"
        raise LabError(code)
    value = strict_json(result.stdout)
    require(type(value) is dict, "D04_INVALID_SQL_RESULT")
    return value


def run(args):
    plan_raw, plan = _file(args.plan)
    stage_raw, stage = _file(args.stage)
    scope_raw, scope = _file(args.vault_scope)
    _, observation = _file(args.trust)
    require(plan.get("schema") == d04.POLICY and plan.get("status") == "PLAN_ONLY"
            and plan.get("sourceHead") == peer.checkout() and plan.get("d03StageSha256") == digest(stage_raw)
            and plan.get("d03ScopeSha256") == digest(scope_raw) and plan.get("d03PeerSha256") == digest(peer.read_file(args.trust))
            and plan.get("d03LeafDerSha256") == stage.get("leafDerSha256")
            and d04.d03.d01.nonzero_digest(stage.get("leafDerSha256"))
            and scope.get("schema") == "APP02B-LAB-DOCKER-VAULT-PEER-V1"
            and scope.get("stageReceiptSha256") == digest(stage_raw)
            and scope.get("sourceHead") == stage.get("sourceHead")
            and scope.get("nonce") == stage.get("nonce"), "D04_PEER_SCOPE_MISMATCH")
    d04.require_trust(observation, digest(stage_raw), stage.get("sourceHead"))
    spec = plan["spec"]
    require(spec == d04.scope(spec["project"], spec["nonce"]) and spec["nonce"] == scope["nonce"],
            "D04_PEER_SCOPE_MISMATCH")
    ca = peer.read_file(args.vault_ca)
    require(digest(ca) == scope.get("caSha256") == stage.get("caSha256") and b"PRIVATE KEY" not in ca,
            "D04_VAULT_CA_MISMATCH")
    peer.custody_parent(args.output.parent)
    vault = Vault("https://vault:8200", ca, leaf_sha=stage["leafDerSha256"])
    require(vault.cluster() == scope.get("clusterId"), "D04_VAULT_CLUSTER_CHANGED")
    credentials = load_credentials(vault, spec, {role: peer.secret(f"D04 {role} Vault token") for role in d04.ROLES})
    require(vault.cluster() == scope.get("clusterId"), "D04_VAULT_CLUSTER_CHANGED")
    value = sql_run("verify", "sql", spec, credentials)
    peer.output_file(args.output, canonical_json({
        "schema": "APP02B-LAB-DOCKER-SQL-PEER-RESULT-V1", "planSha256": digest(plan_raw),
        "sourceHead": plan["sourceHead"], "vantage": "OPERATOR_ENROLLED_PRIVATE_PEER",
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
    for name in ("plan", "stage", "trust", "vault-scope", "vault-ca", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
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

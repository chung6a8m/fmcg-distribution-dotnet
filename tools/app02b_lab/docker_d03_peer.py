"""Operator-side verification and bounded rotation from an enrolled private peer."""
import argparse
from getpass import getpass, GetPassWarning
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import warnings

ROOT = Path(__file__).resolve().parents[2]
if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))

from tools.app02b_lab.contract import LabError, canonical_json, digest, require, safe_path, strict_json
from tools.app02b_lab import docker_d03 as d03, docker_d02 as d02
from tools.app02b_lab.docker_d03_client import ROLE_PATHS, Vault, probe_roles, rotate

LIMIT = 8_000_000


def custody_parent(parent):
    parent = safe_path(parent)
    info = parent.stat()
    require(parent.is_dir() and info.st_uid == os.getuid() and not info.st_mode & 0o077
            and not any((item / ".git").exists() or item == ROOT for item in (parent, *parent.parents)),
            "D03_UNSAFE_PEER_CUSTODY")
    return parent


def read_file(path):
    path = safe_path(path)
    custody_parent(path.parent)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
                and not info.st_mode & 0o077 and info.st_nlink == 1, "D03_UNSAFE_PEER_CUSTODY")
        raw = stream.read(LIMIT + 1)
        require(len(raw) <= LIMIT, "D03_PEER_INPUT_LIMIT")
        return raw


def output_file(path, data):
    parent = custody_parent(path.parent)
    descriptor = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        fd = os.open(path.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=descriptor)
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def checkout():
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    def git(*args):
        return subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false", *args],
                              cwd=ROOT, env=env, check=True, capture_output=True, timeout=15).stdout
    head = git("rev-parse", "HEAD").decode("ascii").strip()
    require(re.fullmatch(r"[0-9a-f]{40}", head) and not git("status", "--porcelain"), "D03_SOURCE_NOT_CLEAN")
    return head


def secret(label):
    require(sys.stdin.isatty() and sys.stderr.isatty(), "D03_PRIVATE_TTY_REQUIRED")
    with warnings.catch_warnings():
        warnings.simplefilter("error", GetPassWarning)
        return getpass(label + ": ")


def run(args):
    raw = read_file(args.config)
    config = strict_json(raw)
    d02.object_fields(config, "schema sourceHead nonce clusterId caSha256 stageReceiptSha256", "D03_PEER_CONFIG")
    source = checkout()
    require(config["schema"] == "APP02B-LAB-DOCKER-VAULT-PEER-V1" and config["sourceHead"] == source
            and type(config["nonce"]) is str and d02.NONCE.fullmatch(config["nonce"])
            and config["nonce"] != "0" * 32 and type(config["clusterId"]) is str
            and re.fullmatch(r"[a-f0-9-]{36}", config["clusterId"])
            and d03.d01.nonzero_digest(config["stageReceiptSha256"]), "D03_PEER_CONFIG")
    stage = read_file(args.stage_receipt)
    require(digest(stage) == config["stageReceiptSha256"], "D03_PEER_STAGE_MISMATCH")
    receipt = strict_json(stage)
    require(type(receipt) is dict and receipt.get("schema") == "APP02B-LAB-DOCKER-VAULT-STAGE-V1"
            and receipt.get("status") == "STAGED" and receipt.get("sourceHead") == source
            and receipt.get("nonce") == config["nonce"] and receipt.get("caSha256") == config["caSha256"],
            "D03_PEER_STAGE_MISMATCH")
    ca = read_file(args.ca)
    require(digest(ca) == config["caSha256"] and b"PRIVATE KEY" not in ca, "D03_CA_MISMATCH")
    vault = Vault("https://vault:8200", ca)

    def identity():
        require(vault.cluster() == config["clusterId"], "D03_CLUSTER_CHANGED")

    if args.role:
        operation = "ROTATE_" + args.role.upper()
        approval = read_file(args.enrollment)

        def authorization():
            require(checkout() == source and read_file(args.config) == raw and read_file(args.ca) == ca
                    and read_file(args.stage_receipt) == stage and read_file(args.enrollment) == approval,
                    "D03_ROTATION_SCOPE_CHANGED")
            identity()
            d03.authorize(approval, args.enrollment_sha256, digest(raw), config["nonce"], operation, d02.utc_now())

        custody_parent(args.replacement.parent)
        require(not os.path.lexists(args.replacement), "D03_REPLACEMENT_EXISTS")
        authorization()
        issuer = secret("Scoped issuer token")
        previous = secret("Current role token")
        result = rotate(vault, args.role, issuer, previous,
                        lambda value: output_file(args.replacement, (value + "\n").encode("ascii")), authorization)
        print(result)
        return

    identity()
    custody_parent(args.output.parent)
    tokens = {role: secret(f"{role} probe token") for role in ROLE_PATHS}

    def audit_records():
        return [strict_json(line) for line in read_file(args.audit).splitlines()]

    checks = probe_roles(vault, tokens, audit_records)
    identity()
    output_file(args.output, canonical_json({
        "schema": "APP02B-LAB-DOCKER-VAULT-PEER-RESULT-V1", "sourceHead": source,
        "scopeSha256": digest(raw), "stageReceiptSha256": config["stageReceiptSha256"],
        "collectedUtc": d02.timestamp(d02.utc_now()), "status": "ROLE_AUDIT_OBSERVED",
        "vantage": "OPERATOR_ENROLLED_PRIVATE_PEER", "auditVantage": "OPERATOR_PROTECTED_AUDIT_READBACK",
        "checks": checks, "faults": "NOT_RUN", "originalGateStatus": "NOT_VERIFIED",
    }))
    print("ROLE_AUDIT_OBSERVED")


def main():
    parser = argparse.ArgumentParser()
    for name in ("config", "ca", "stage-receipt"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("audit", "output", "enrollment", "replacement"):
        parser.add_argument("--" + name, type=Path)
    parser.add_argument("--role", choices=ROLE_PATHS)
    parser.add_argument("--enrollment-sha256")
    args = parser.parse_args()
    try:
        require(sys.platform == "linux" and sys.flags.isolated and sys.flags.no_site
                and sys.flags.dont_write_bytecode, "D03_ISOLATED_LINUX_PEER_REQUIRED")
        rotation = (args.enrollment, args.enrollment_sha256, args.replacement)
        observation = (args.audit, args.output)
        require((all(rotation) and not any(observation)) if args.role else
                (all(observation) and not any(rotation)), "D03_PEER_ARGUMENTS")
        run(args)
        return 0
    except LabError as error:
        print(f"BLOCKED {error.code}")
        return 2
    except (OSError, ValueError, TypeError, AttributeError, GetPassWarning, subprocess.SubprocessError):
        print("COLLECTION_FAILED D03_PEER_ERROR")
        return 1


if __name__ == "__main__":
    sys.exit(main())

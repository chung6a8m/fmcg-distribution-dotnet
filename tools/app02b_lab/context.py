"""Freeze the exact checkout and the redacted, externally retained inputs."""

from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import os
from pathlib import Path
import re
import subprocess

from .catalog import FIXTURE, REPO, parse_catalog
from .contract import LabError, canonical_json, digest, read_bytes, read_json, require, safe_path
from .schema import validate_document


@dataclass(frozen=True)
class LabRunContext:
    runId: str
    environmentId: str
    sourceHeadSha: str
    fixtureSha256: str
    configurationSha256: str
    startedUtc: str
    hostSha256: str
    mode: str = "LOCAL_OPERATOR_LAB"

    def document(self) -> dict:
        return {"schema": "APP02B-LAB-RUN-CONTEXT-V1", **asdict(self)}

    def to_bytes(self) -> bytes:
        return canonical_json(self.document())


@dataclass(frozen=True)
class RunInputs:
    context: LabRunContext
    fixture: bytes
    manifest: bytes
    host: bytes
    admission: bytes


_SHA = re.compile(r"[a-f0-9]{40}")
_RUN_ID = re.compile(r"b-lab-([0-9]{8}T[0-9]{6}Z)-[a-f0-9]{8,32}")
ZERO_SHA = "0" * 64


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def validate_run_id(run_id: str) -> None:
    match = _RUN_ID.fullmatch(run_id)
    require(match is not None, "INVALID_RUN_ID")
    try:
        stamp = match[1]
        require(datetime.strptime(stamp, "%Y%m%dT%H%M%SZ").strftime(
            "%Y%m%dT%H%M%SZ") == stamp, "INVALID_RUN_ID_UTC")
    except ValueError:
        raise LabError("INVALID_RUN_ID_UTC") from None


def git(*arguments: str) -> bytes:
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    try:
        return subprocess.run(["git", "--no-replace-objects", "-C", str(REPO), *arguments], check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              env=env, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        raise LabError("GIT_CHECK_FAILED") from None


def checkout_sha() -> str:
    return git("rev-parse", "--verify", "HEAD^{commit}").decode("ascii").strip()


def validate_source(source_sha: str) -> None:
    require(_SHA.fullmatch(source_sha) is not None, "INVALID_SOURCE_SHA")
    require(checkout_sha() == source_sha, "CHECKOUT_HEAD_MISMATCH")
    flags = git("ls-files", "-v", "-z").split(b"\0")
    require(not any(row[:1].islower() or row.startswith(b"S ") for row in flags),
            "CHECKOUT_HAS_HIDDEN_TRACKED_FILES")
    require(not git("status", "--porcelain=v1", "-z", "--untracked-files=all",
                    "--ignore-submodules=none"), "DIRTY_CHECKOUT")
    require(checkout_sha() == source_sha, "CHECKOUT_HEAD_MISMATCH")


def validate_outside_checkout(path: Path) -> None:
    worktrees = git("worktree", "list", "--porcelain", "-z").split(b"\0")
    roots = [REPO, *(Path(os.fsdecode(row[9:])) for row in worktrees
                     if row.startswith(b"worktree "))]
    for root in roots:
        require(not path.is_relative_to(root.resolve()), "PATH_INSIDE_REPOSITORY")
    # Also reject other repositories that aren't registered worktrees of this one.
    for folder in (path, *path.parents):
        marker = folder / ".git"
        require(not marker.exists() and not marker.is_symlink(), "PATH_INSIDE_REPOSITORY")


def approved_root(path: Path) -> Path:
    path = safe_path(path)
    require(os.name != "nt" or re.fullmatch(r"[A-Za-z]:", path.drive) is not None,
            "EVIDENCE_ROOT_REQUIRES_LOCAL_DRIVE")
    path = path.resolve(strict=True)
    require(path.is_dir() and path != path.parent, "EVIDENCE_ROOT_NOT_DIRECTORY")
    validate_outside_checkout(path)
    return path


def validate_manifest(manifest, admission, host: bytes) -> None:
    validate_document(manifest, "environment")
    validate_document(admission, "environment", "admission")
    require(bool(host.strip()) and manifest["hostSha256"] != ZERO_SHA
            and digest(host) == manifest["hostSha256"], "HOST_FINGERPRINT_MISMATCH")
    require(manifest["authorizationRecord"]["sha256"] != ZERO_SHA, "AUTHORIZATION_RECORD_PIN")
    refs = [item["reference"] for item in manifest["secretReferences"]]
    require(len(refs) == len(set(refs)), "DUPLICATE_SECRET_REFERENCE")
    a, b = manifest["targets"]["A"], manifest["targets"]["B"]
    require(a["targetRef"] != b["targetRef"]
            and a["identitySha256"] != b["identitySha256"]
            and ZERO_SHA not in (a["identitySha256"], b["identitySha256"]),
            "DISTINCT_ADMITTED_TARGETS_REQUIRED")
    require(admission["environmentId"] == manifest["environmentId"]
            and admission["hostSha256"] == manifest["hostSha256"], "ADMISSION_HOST_OR_ENVIRONMENT_DRIFT")
    require(admission["targets"] == manifest["targets"], "TARGET_LINEAGE_NOT_ADMITTED")


def validate_admission_root(admission: dict, root: Path) -> None:
    require(Path(admission["evidenceRoot"]).is_absolute() and
            Path(admission["evidenceRoot"]) == root, "EVIDENCE_ROOT_NOT_ADMITTED")


def validate_lineage_receipt(receipt: object) -> None:
    """Typed receipt shape for LAB-I08; this increment does not admit transitions."""
    validate_document(receipt, "environment", "lineage")
    require(receipt["oldBinding"] != receipt["newBinding"]
            and receipt["newBinding"]["registryVersion"] > receipt["oldBinding"]["registryVersion"]
            and receipt["approvalRecord"]["sha256"] != ZERO_SHA, "INVALID_LINEAGE_RECEIPT")
    validate_run_id(receipt["runId"])


def collect_inputs(config_path: Path, evidence_root: Path, source_sha: str, run_id: str) -> RunInputs:
    validate_run_id(run_id)
    validate_source(source_sha)
    root = approved_root(evidence_root)
    config = safe_path(config_path)
    validate_outside_checkout(config)
    require(not config.is_relative_to(root), "CONFIG_INSIDE_EVIDENCE_ROOT")
    manifest = read_json(config)
    validate_document(manifest, "environment")
    try:
        host = read_bytes(config.parent / "host-fingerprint.bin")
        admission = read_json(config.parent / "lab-admission.json")
    except LabError as error:
        if error.code == "PATH_MISSING":
            raise LabError("PROTECTED_HOST_OR_ADMISSION_MISSING") from None
        raise
    validate_manifest(manifest, admission, host)
    validate_admission_root(admission, root)
    fixture = read_bytes(FIXTURE)
    parse_catalog(fixture)
    public = canonical_json(manifest)
    context = LabRunContext(run_id, manifest["environmentId"], source_sha, digest(fixture),
                            digest(public), utc_now(), manifest["hostSha256"])
    validate_document(context.document(), "run-context")
    validate_source(source_sha)
    return RunInputs(context, fixture, public, host, canonical_json(admission))


def create_context(config_path: Path, evidence_root: Path, source_sha: str,
                   run_id: str) -> LabRunContext:
    """Return a validated context for a new run. Persistence is owned by the bundle."""
    inputs = collect_inputs(config_path, evidence_root, source_sha, run_id)
    directory = approved_root(evidence_root) / run_id
    require(not os.path.lexists(directory), "RUN_ALREADY_EXISTS")
    return inputs.context

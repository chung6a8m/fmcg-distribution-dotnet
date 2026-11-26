"""Produce the source-bound APP-00A classification for the current checkout."""

from hashlib import sha1
import json
import os
from pathlib import Path
import re
import subprocess
from threading import Timer
from time import monotonic


# DOC-CI-02B: exact existing descriptive documents, no authority/plan paths.
ALLOWED_DOC_PATHS = frozenset({
    "docs/status/app-05-status.md",
    "docs/receipts/app-05-receipts.md",
})
RECEIPT_VERSION = 2
MAX_DIAGNOSTIC_RECORDS = 8
MAX_PATH_PREVIEW = 240
DIAGNOSTIC_PHASES = frozenset({"event", "context", "whole-pr", "integration", "comparisons", "blob-verification", "complete"})

_CHANGE_KEYS = {"path", "status", "old_mode", "new_mode"}
# Stable existing documents only; even add/delete between these names is Full.
_PROSE_MODES = {"M": ("100644", "100644")}
_SHA = re.compile(r"[0-9a-f]{40}")
_RAW_HEADER = re.compile(rb":([0-7]{6}) ([0-7]{6}) ([0-9a-f]{40}) ([0-9a-f]{40}) ([A-Z])")
_BLOB_HEADER = re.compile(rb"([0-9a-f]{40}) blob (0|[1-9][0-9]{0,6})\n")
_ZERO_OID = b"0" * 40
_GIT_TIMEOUT_SECONDS = 20
_GIT_MAX_BYTES = 1024 * 1024


class _ComparisonUnavailable(Exception):
    """An expected uncertainty that requires full CI."""


def is_docs_only_change(change) -> bool:
    if not isinstance(change, dict) or change.keys() != _CHANGE_KEYS:
        return False
    if any(not isinstance(value, str) for value in change.values()):
        return False
    path = change["path"]
    # Independent category fence: an accidental allowlist entry must not let
    # a classifier, gate, workflow, authority or instruction exempt itself.
    # This is NOT a wildcard allowlist; exact membership is still mandatory.
    return (
        path in ALLOWED_DOC_PATHS
        and re.fullmatch(r"docs/(?:status|receipts)/[a-z0-9][a-z0-9-]*\.md", path) is not None
        and path.rsplit("/", 1)[-1] != "agents.md"
        and _PROSE_MODES.get(change["status"]) == (change["old_mode"], change["new_mode"])
    )


def _git(repo, *args, input_data=None):
    # Limit both elapsed time and the bytes read even if Git never exits cleanly.
    with subprocess.Popen(
        ["git", "--no-pager", "--no-replace-objects", *args],
        cwd=repo, stdin=subprocess.PIPE if input_data is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        env={**os.environ, "GIT_NO_LAZY_FETCH": "1"},
    ) as process:
        deadline = monotonic() + _GIT_TIMEOUT_SECONDS
        timer = Timer(_GIT_TIMEOUT_SECONDS, process.kill)
        timer.start()
        try:
            if input_data is not None:
                with process.stdin:
                    process.stdin.write(input_data)
            output = process.stdout.read(_GIT_MAX_BYTES + 1)
            if len(output) > _GIT_MAX_BYTES:
                raise _ComparisonUnavailable("git_output_too_large")
            if process.wait(timeout=max(0, deadline - monotonic())) != 0:
                raise _ComparisonUnavailable("git_comparison_unavailable")
            return output
        except (BrokenPipeError, subprocess.TimeoutExpired) as error:
            raise _ComparisonUnavailable("git_comparison_unavailable") from error
        finally:
            timer.cancel()
            if process.returncode is None:
                process.kill()
            timer.join()


def _changes_between(repo, base_sha, head_sha):
    raw = _git(
        repo, "diff", "--raw", "-z", "--no-abbrev", "--no-renames", "--no-ext-diff",
        "--no-textconv", "--no-relative", "--no-color", "--ignore-submodules=none",
        base_sha, head_sha, "--",
    )
    if not raw:
        return [], set()
    records = raw.split(b"\0")
    if records.pop() or len(records) % 2:
        raise _ComparisonUnavailable("malformed_diff")

    changes = []
    object_ids = set()
    seen_paths = set()
    for header, raw_path in zip(records[::2], records[1::2]):
        match = _RAW_HEADER.fullmatch(header)
        if not match or not raw_path or raw_path in seen_paths:
            raise _ComparisonUnavailable("malformed_diff")
        seen_paths.add(raw_path)
        old_mode, new_mode, old_oid, new_oid, status = match.groups()
        if (
            (old_mode == b"000000") != (old_oid == _ZERO_OID)
            or (new_mode == b"000000") != (new_oid == _ZERO_OID)
            or (old_mode == new_mode and old_oid == new_oid)
        ):
            raise _ComparisonUnavailable("malformed_diff")
        object_ids.update((old_oid, new_oid))
        try:
            path = raw_path.decode("utf-8")
        except UnicodeDecodeError as error:
            raise _ComparisonUnavailable("malformed_diff") from error
        changes.append({
            "path": path,
            "status": status.decode("ascii"),
            "old_mode": old_mode.decode("ascii"),
            "new_mode": new_mode.decode("ascii"),
        })
    return changes, object_ids


def _verify_blobs(repo, object_ids):
    # Eligible comparisons have at most two object IDs per allowlisted path each.
    blob_ids = sorted(object_ids - {_ZERO_OID})
    batch = b"".join(oid + b"\n" for oid in blob_ids)
    output = _git(repo, "cat-file", "--batch", input_data=batch)
    offset = 0
    for oid in blob_ids:
        header = _BLOB_HEADER.match(output, offset)
        if not header or header[1] != oid:
            raise _ComparisonUnavailable("prose_blob_unavailable")
        size = header[2]
        start = header.end()
        end = start + int(size)
        if output[end:end + 1] != b"\n":
            raise _ComparisonUnavailable("prose_blob_unavailable")

        digest = sha1(b"blob " + size + b"\0", usedforsecurity=False)
        content = output[start:end]
        digest.update(content)
        if digest.hexdigest().encode("ascii") != oid:
            raise _ComparisonUnavailable("prose_blob_unavailable")
        # Regular Git mode is not proof of a text document. Check both sides
        # without parsing Markdown as instructions or logging its contents.
        try:
            content.decode("utf-8")
        except UnicodeDecodeError as error:
            raise _ComparisonUnavailable("prose_blob_not_text") from error
        if b"\0" in content:
            raise _ComparisonUnavailable("prose_blob_not_text")
        offset = end + 1
    if offset != len(output):
        raise _ComparisonUnavailable("prose_blob_unavailable")


def path_preview(path):
    # Preserve no raw command, control or markup delimiters. Lossy display only.
    escaped = json.dumps(path, ensure_ascii=True)[1:-1]
    escaped = "".join(
        f"\\u{ord(c):04x}" if c in ':<>&|[]*_~!' or ord(c) in (96, 127) else c
        for c in escaped
    )
    return escaped if len(escaped) <= MAX_PATH_PREVIEW else escaped[:MAX_PATH_PREVIEW - 3] + "..."


def diagnostics(phase, records=()):
    return {
        "phase": phase,
        "offending_count": len(records),
        "offenders": [
            {"phase": item_phase, "path_preview": path_preview(change["path"]),
             "status": change["status"], "old_mode": change["old_mode"],
             "new_mode": change["new_mode"]}
            for item_phase, change in records[:MAX_DIAGNOSTIC_RECORDS]
        ],
    }


def classify(repo, *, event_name, base_sha, source_head_sha, workflow_sha) -> dict:
    receipt = {
        "version": RECEIPT_VERSION,
        "mode": "full",
        "event_name": event_name,
        "base_sha": base_sha,
        "source_head_sha": source_head_sha,
        "workflow_sha": workflow_sha,
        "merge_base_sha": "",
        "reason": "non_pull_request",
        "changes": [],
        "diagnostics": diagnostics("event"),
    }
    if event_name != "pull_request":
        return receipt

    phase = "context"
    try:
        revisions = (base_sha, source_head_sha, workflow_sha)
        if not all(isinstance(sha, str) and _SHA.fullmatch(sha) for sha in revisions):
            raise _ComparisonUnavailable("invalid_event_sha")
        if _git(repo, "rev-parse", "--is-shallow-repository") != b"false\n":
            raise _ComparisonUnavailable("history_not_complete")
        if _git(repo, "rev-parse", "--verify", "HEAD") != f"{workflow_sha}\n".encode("ascii"):
            raise _ComparisonUnavailable("checkout_mismatch")
        parents = _git(repo, "rev-list", "--parents", "--max-count=1", workflow_sha, "--")
        if parents != f"{workflow_sha} {base_sha} {source_head_sha}\n".encode("ascii"):
            raise _ComparisonUnavailable("merge_parent_mismatch")

        raw_merge_base = _git(repo, "merge-base", "--all", base_sha, source_head_sha)
        if not re.fullmatch(rb"[0-9a-f]{40}\n", raw_merge_base):
            raise _ComparisonUnavailable("merge_base_not_unique")
        merge_base_sha = raw_merge_base[:-1].decode("ascii")
        receipt["merge_base_sha"] = merge_base_sha

        phase = "whole-pr"
        whole_pr, whole_pr_objects = _changes_between(repo, merge_base_sha, source_head_sha)
        phase = "integration"
        integration, integration_objects = _changes_between(repo, base_sha, workflow_sha)
        phase = "comparisons"
        changes = whole_pr + integration
        if not changes:
            raise _ComparisonUnavailable("empty_diff")
        offenders = [(label, change) for label, items in (("whole-pr", whole_pr), ("integration", integration))
                     for change in items if not is_docs_only_change(change)]
        if offenders:
            receipt["reason"] = "change_not_allowlisted_prose"
            receipt["diagnostics"] = diagnostics(phase, offenders)
            return receipt
        phase = "blob-verification"
        _verify_blobs(repo, whole_pr_objects | integration_objects)
    except _ComparisonUnavailable as error:
        receipt["reason"] = str(error)
        receipt["diagnostics"] = diagnostics(phase)
    else:
        # Identical public records may refer to different objects in the two comparisons.
        distinct_changes = list({tuple(change.values()): change for change in changes}.values())
        receipt.update(mode="docs-only", reason="allowlisted_prose", changes=distinct_changes,
                       diagnostics=diagnostics("complete"))
    return receipt


def main():
    receipt = classify(
        Path.cwd(),
        event_name=os.environ.get("APP00A_EVENT_NAME", ""),
        base_sha=os.environ.get("APP00A_BASE_SHA", ""),
        source_head_sha=os.environ.get("APP00A_SOURCE_HEAD_SHA", ""),
        workflow_sha=os.environ.get("APP00A_WORKFLOW_SHA", ""),
    )
    payload = json.dumps(receipt, ensure_ascii=True, separators=(",", ":"))
    artifact = Path("TestResults/app00a-classification.json")
    artifact.parent.mkdir(exist_ok=True)
    artifact.write_text(f"{payload}\n", encoding="utf-8")

    mode = receipt["mode"]
    reason = receipt["reason"]
    if output_path := os.environ.get("GITHUB_OUTPUT"):
        with Path(output_path).open("a", encoding="utf-8") as output:
            output.write(f"mode={mode}\nreceipt={payload}\n")
    detail = receipt["diagnostics"]
    lines = [f"Phase: {detail['phase']}; offending comparison records: {detail['offending_count']}."]
    lines.extend(
        f"{item['phase']}: {item['status']} {item['old_mode']} -> {item['new_mode']} path={item['path_preview']}"
        for item in detail["offenders"]
    )
    if detail["offending_count"] > len(detail["offenders"]):
        lines.append("Additional records omitted (bounded diagnostic).")
    if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with Path(summary_path).open("a", encoding="utf-8") as summary:
            summary.write(f"### APP-00A classification\n\nMode **{mode}** (`{reason}`).\n")
            summary.write("\n" + "".join("    " + line + "\n" for line in lines))
    print(f"APP-00A classification: {mode} ({reason})")
    for line in lines:
        print("APP-00A diagnostic: " + line)


if __name__ == "__main__":
    main()

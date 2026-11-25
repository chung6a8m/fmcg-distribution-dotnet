"""Verify the source-bound APP-00A classification and execution job results."""

import json
import os
import re

if __package__:
    from .classify_app00a_pr import is_docs_only_change
else:
    from classify_app00a_pr import is_docs_only_change


WORKER_JOBS = ("core_sql", "web_sales", "app03", "distribution")
REQUIRED_JOBS = ("classify", *WORKER_JOBS)
RECEIPT_FIELDS = {
    "version", "mode", "event_name", "base_sha", "source_head_sha", "workflow_sha",
    "merge_base_sha", "reason", "changes",
}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key {key!r}")
        result[key] = value
    return result


def reject_non_json_literal(literal):
    raise ValueError(f"unexpected literal {literal}")


def load_json(raw_json):
    return json.loads(
        raw_json,
        object_pairs_hook=unique_object,
        parse_constant=reject_non_json_literal,
    )


def job_status(needs, job_id):
    if job_id not in needs:
        return "missing job"
    job = needs[job_id]
    if not isinstance(job, dict):
        return "job entry must be an object"
    if "result" not in job:
        return "missing result"
    result = job["result"]
    if result not in ("success", "failure", "cancelled", "skipped"):
        return f"invalid result {result!r}"
    return result


def is_commit_sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) is not None


def verify_receipt_identity(receipt, context):
    event_name = context["event_name"]
    if not isinstance(event_name, str) or not event_name.strip():
        raise ValueError("event_name must be present in the current workflow context")
    for field, value in context.items():
        if receipt[field] != value:
            raise ValueError(f"{field} must match the current workflow context")

    base_sha = context["base_sha"]
    if event_name == "pull_request":
        if not is_commit_sha(base_sha):
            raise ValueError("base_sha must be a full commit SHA for a pull_request")
    elif base_sha != "":
        raise ValueError("base_sha must be empty outside a pull_request")

    for field in ("source_head_sha", "workflow_sha"):
        if not is_commit_sha(context[field]):
            raise ValueError(f"{field} must be a full commit SHA")
    if event_name == "push" and context["source_head_sha"] != context["workflow_sha"]:
        raise ValueError("source_head_sha must equal workflow_sha on push")


def verified_mode(outputs, context):
    if not isinstance(outputs, dict):
        raise ValueError("outputs must be an object")
    mode = outputs.get("mode")
    if mode not in ("full", "docs-only"):
        raise ValueError("mode must be full or docs-only")
    raw_receipt = outputs.get("receipt")
    if not isinstance(raw_receipt, str):
        raise ValueError("receipt output must be a JSON string")

    try:
        receipt = load_json(raw_receipt)
    except (ValueError, RecursionError) as error:
        raise ValueError(f"receipt contains invalid JSON ({error})") from error
    if not isinstance(receipt, dict) or receipt.keys() != RECEIPT_FIELDS:
        raise ValueError("receipt must be an object with exactly the version 1 fields")
    if type(receipt["version"]) is not int or receipt["version"] != 1:
        raise ValueError("receipt version must be integer 1")
    if receipt["mode"] != mode:
        raise ValueError("receipt mode must match the classifier output mode")
    verify_receipt_identity(receipt, context)

    reason = receipt["reason"]
    merge_base = receipt["merge_base_sha"]
    changes = receipt["changes"]
    if mode == "docs-only":
        if context["event_name"] != "pull_request":
            raise ValueError("docs-only mode requires a pull_request event")
        if reason != "allowlisted_prose":
            raise ValueError("docs-only reason must be allowlisted_prose")
        if not is_commit_sha(merge_base):
            raise ValueError("docs-only merge_base_sha must be a full commit SHA")
        if not isinstance(changes, list) or not changes or not all(map(is_docs_only_change, changes)):
            raise ValueError("docs-only changes must be a nonempty list of eligible prose records")
    else:
        if not isinstance(reason, str) or not reason.strip() or reason == "allowlisted_prose":
            raise ValueError("full mode reason must describe the fallback")
        if merge_base != "" and not is_commit_sha(merge_base):
            raise ValueError("full mode merge_base_sha must be empty or a full commit SHA")
        if changes != []:
            raise ValueError("full mode changes must be an empty list")
    return mode


def evaluate_needs(
    raw_needs: str | None,
    *,
    event_name: str | None = None,
    base_sha: str | None = None,
    source_head_sha: str | None = None,
    workflow_sha: str | None = None,
) -> tuple[bool, list[str]]:
    if raw_needs is None:
        return False, ["APP00A_NEEDS_JSON is missing"]

    try:
        needs = load_json(raw_needs)
    except (ValueError, RecursionError) as error:
        return False, [f"APP00A_NEEDS_JSON contains invalid JSON ({error})"]

    if not isinstance(needs, dict):
        return False, ["APP00A_NEEDS_JSON must be a JSON object keyed by job ID"]

    messages = []
    extra_jobs = sorted(needs.keys() - set(REQUIRED_JOBS))
    if extra_jobs:
        messages.append(f"Unexpected jobs: {', '.join(repr(job) for job in extra_jobs)}")

    classifier_status = job_status(needs, "classify")
    messages.append(f"classify: {classifier_status}")
    mode = None
    if classifier_status == "success":
        context = {
            "event_name": event_name,
            "base_sha": base_sha,
            "source_head_sha": source_head_sha,
            "workflow_sha": workflow_sha,
        }
        try:
            mode = verified_mode(needs["classify"].get("outputs"), context)
        except ValueError as error:
            messages.append(f"classify: {error}")

    passed = not extra_jobs and mode is not None
    expected_result = "skipped" if mode == "docs-only" else "success"
    for job_id in WORKER_JOBS:
        status = job_status(needs, job_id)
        messages.append(f"{job_id}: {status}")
        if status != expected_result:
            passed = False
            if mode is not None:
                messages.append(f"{job_id}: expected {expected_result} in {mode} mode")

    if passed and mode == "docs-only":
        messages.append("APP-00A docs-only checks passed with runtime workers NOT_RUN.")
    return passed, messages


def main() -> int:
    passed, messages = evaluate_needs(
        os.environ.get("APP00A_NEEDS_JSON"),
        event_name=os.environ.get("APP00A_EVENT_NAME"),
        base_sha=os.environ.get("APP00A_BASE_SHA"),
        source_head_sha=os.environ.get("APP00A_SOURCE_HEAD_SHA"),
        workflow_sha=os.environ.get("APP00A_WORKFLOW_SHA"),
    )
    for message in messages:
        print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())

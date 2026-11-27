"""Fail-closed DOC-CI-02C gates. Reuse APP-00A receipt v2, not an independent allowlist."""

import os
import sys

if __package__:
    from .verify_app00a_jobs import job_status, load_json, verified_mode
else:
    from verify_app00a_jobs import job_status, load_json, verified_mode


# workflow filename -> unique check prefix, baseline public check names, exact
# direct dependency inventory. Full workers keep their original commands.
# "always" includes policy/security suites retained on Light; "light" denotes
# an additional static/operator-only substitute, never runtime evidence.
COMPANIONS = {
    "app02b2-runtime.yml": (
        "b2",
        ("runtime-foundation",),
        {
            "run-runtime-foundation": "full",
            "light-runtime-foundation": "light",
        },
    ),
    "app02b3-identity.yml": (
        "b3",
        ("identity-b3", "identity-sql-windows"),
        {
            "run-identity-b3": "always",
            "run-identity-sql-windows": "full",
        },
    ),
    "app02b4-tenants.yml": (
        "b4",
        ("tenant-b4-unit", "tenant-b4-sql"),
        {
            "run-tenant-b4-unit": "always",
            "run-tenant-b4-sql": "full",
        },
    ),
    "app02b4-web-regressions-with-known-debt.yml": (
        "b4web",
        ("web-regressions-with-known-audit-debt",),
        {
            "run-web-regressions-with-known-audit-debt": "full",
            "light-web-regressions-with-known-audit-debt": "light",
        },
    ),
    "app02b5-authorization.yml": (
        "b5",
        ("b5-source", "b5-two-tenant-sql"),
        {
            "run-b5-source": "always",
            "run-b5-two-tenant-sql": "full",
        },
    ),
    "app02b6-policy-governance.yml": (
        "b6",
        ("b6-source", "b6-localdb"),
        {
            "run-b6-source": "always",
            "run-b6-localdb": "full",
        },
    ),
    "app02b7-recovery.yml": (
        "b7",
        ("b7-source-safety", "b7-localdb-restore"),
        {
            "run-b7-source-safety": "always",
            "run-b7-localdb-restore": "full",
            "light-b7-localdb-restore": "light",
        },
    ),
    "app02b8-audit-operations.yml": (
        "b8",
        ("b8-source-contract", "b8-two-tenant-localdb"),
        {
            "run-b8-source-contract": "always",
            "run-b8-two-tenant-localdb": "full",
        },
    ),
    "security-picomatch-issue59.yml": (
        "security",
        ("web-glob-input-boundary", "picomatch-lock-security", "full-npm-security-audit", "candidate-metadata-only", "candidate-parse-source-diff"),
        {
            "run-web-glob-input-boundary": "always",
            "run-picomatch-lock-security": "always",
            "run-full-npm-security-audit": "always",
            "run-candidate-metadata-only": "always",
            "run-candidate-parse-source-diff": "always",
        },
    ),
}


def expected_status(route, mode):
    if route == "always":
        return "success"
    if route == "full":
        return "success" if mode == "full" else "skipped"
    if route == "light":
        return "success" if mode == "docs-only" else "skipped"
    raise ValueError("unknown worker route")


def context_from_env():
    return {field: os.environ.get("APP00A_" + field.upper()) for field in
            ("event_name", "base_sha", "source_head_sha", "workflow_sha")}


def evaluate_companion(raw_needs, filename, context):
    if not isinstance(filename, str) or filename not in COMPANIONS:
        return False, ["Unknown companion workflow"]
    _, _, workers = COMPANIONS[filename]
    try:
        needs = load_json(raw_needs)
    except (TypeError, ValueError, RecursionError):
        return False, ["Missing or invalid DOC_CI_NEEDS_JSON"]
    if not isinstance(needs, dict):
        return False, ["Needs must be an object"]
    expected = {"classify", *workers}
    if set(needs) != expected:
        return False, ["Exact dependency inventory mismatch"]
    result = job_status(needs, "classify")
    if result != "success":
        return False, [f"classify: {result}; no companion gate PASS"]
    try:
        mode = verified_mode(needs["classify"].get("outputs"), context)
    except (ValueError, TypeError, KeyError, RecursionError):
        return False, ["Classifier receipt or context rejected"]
    messages = [f"classify: success ({mode})"]
    passed = True
    for job, route in workers.items():
        result = job_status(needs, job)
        required = expected_status(route, mode)
        messages.append(f"{job}: {result}; expected {required}")
        if result != required:
            passed = False
    if passed:
        if mode == "docs-only":
            messages.append("DOC-CI Light: eligible full runtime suites NOT_RUN; retained checks passed.")
        else:
            messages.append("DOC-CI Full: all original suites passed; Light-only jobs skipped.")
    return passed, messages


def main():
    context = context_from_env()
    if sys.argv[1:] == ["--classification-only"]:
        try:
            mode = verified_mode({"mode": os.environ.get("DOC_CI_MODE"),
                                  "receipt": os.environ.get("DOC_CI_RECEIPT")}, context)
        except (ValueError, TypeError, KeyError, RecursionError):
            print("Classifier receipt rejected before routing.")
            return 1
        print(f"Verified classification receipt: {mode}")
        return 0
    if sys.argv[1:]:
        print("Unexpected arguments")
        return 1
    filename = os.environ.get("DOC_CI_COMPANION")
    # Bind the inventory selector to the actual workflow file, not PR text.
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    workflow_ref = os.environ.get("GITHUB_WORKFLOW_REF", "")
    if not repo or workflow_ref.partition("@")[0] != f"{repo}/.github/workflows/{filename}":
        print("Companion workflow identity mismatch")
        return 1
    passed, messages = evaluate_companion(os.environ.get("DOC_CI_NEEDS_JSON"), filename, context)
    print("\n".join(messages))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())

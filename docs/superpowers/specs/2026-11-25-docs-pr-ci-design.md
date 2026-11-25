# Documentation-only APP-00A CI design

Baseline: `bcdc5f54dc51e7201ce265fcb900b582ebdc8500` (merged PR #148).

## Goal and scope

Reduce the wait for a narrowly defined prose-only PR by skipping APP-00A's four
Windows execution jobs. Preserve the `validate` check, immutable business/link/
E-SIM checks, the nine companion workflows, and full execution on every main
push. This is APP-05.0 CI reliability work, not runtime or business acceptance.

The user requested implementation of item 6 on the new HEAD. Routine technical
choices are authorized by that request and the repository's APP-05 guidance.

## Options considered

1. Workflow-level `paths-ignore`: unsuitable because a required workflow check
   can remain pending, and a broad Markdown filter misses executable inputs.
2. **A classification job, conditional workers, and an unconditional aggregate:**
   selected because it changes the dominant APP-00A wait with a small scope and
   leaves every existing execution step intact.
3. Classification across all workflows: deferred. The companion security and
   runtime checks remain useful and finish much sooner than APP-00A.

## Path policy

Use a finite, case-sensitive set of 16 paths, defined in
`tools/classify_app00a_pr.py`:

- Exact root `README.md`.
- Four existing CI notes: `docs/roadmap/app-00a-parallel-ci.md`,
  `app-00a-build-cache.md`, `localdb-ci-bootstrap.md`, `pr-ci-concurrency.md`.
- The nine implementation plans present at the baseline under
  `docs/superpowers/plans/`, named explicitly in the classifier.
- This change's operator note `docs/roadmap/docs-pr-ci.md` and implementation
  plan `docs/superpowers/plans/2026-11-25-docs-pr-ci.md`.

Unknown or newly introduced paths require full CI until explicitly audited.
There are no directory globs, labels, commit-message rules, manual skip inputs,
or privileged PR events. All changed entries must be ordinary `100644` blobs
on each side that exists; additions and deletions may have an absent `000000`
side. Executable files, symlinks, gitlinks, type changes, and unknown statuses
require full CI. Before a positive decision, a bounded batch query verifies
that every nonzero old/new object ID from both comparisons exists and has
type `blob`; unavailable historical content also requires full CI. An empty
diff requires full CI.

The input audit found Markdown read/hash checks in C.6 and D.1–D.4 acceptance
matrices/contracts, E-SIM tables and security decisions. `docs/ui-ux/` also
contains runtime assets and executable tools. `business/`, locks, instructions,
security/authority/evidence documents, specs, code, tests, scripts, tools,
configuration, and every workflow stay outside the exception. Future code that
starts reading an allowlisted document must remove that path from the policy
in the same full-CI change.

## Complete comparison and source identity

The `classify` Ubuntu job checks out the normal event commit with complete Git
history. It receives the event name, event base SHA, reviewed source head SHA,
and workflow SHA through environment variables. Only `pull_request` may qualify.

For a PR, validate all three SHA values, require actual checkout HEAD to equal
the workflow SHA, and require its two parents to be the event base and source
head in that order. Find exactly one best merge base using `git merge-base
--all`. Read the union of:

1. merge base to source head (the whole PR), and
2. event base to actual workflow merge commit (integration result).

Use `git diff --raw -z --no-abbrev --no-renames --no-ext-diff --no-textconv`
with explicit immutable revisions and `--`. Parse NUL-delimited records,
including old/new Git modes. Renames become deletion plus addition, so a code
file moved into a prose name remains a full-CI change. Do not normalize paths
or replace invalid UTF-8. Use a bounded Git timeout and output-size check;
unavailable, malformed, oversized, shallow, ambiguous, or inconsistent data
selects full CI. Do not accept a partial path listing.

## Classification interface

`tools/classify_app00a_pr.py` provides:

- `ALLOWED_DOC_PATHS`: the exact immutable Python path set.
- `is_docs_only_change(change) -> bool`: validates an exact record with keys
  `path`, `status`, `old_mode`, `new_mode`. Only `A` (000000/100644), `D`
  (100644/000000), or `M` (100644/100644) and an allowlisted path qualify.
- `classify(repo, *, event_name, base_sha, source_head_sha, workflow_sha) -> dict`.

The receipt has exactly `version` (integer 1), `mode` (`full` or `docs-only`),
`event_name`, `base_sha`, `source_head_sha`, `workflow_sha`, `merge_base_sha`,
`reason`, and `changes`. Unavailable `merge_base_sha` is an empty string. A docs receipt requires `reason=allowlisted_prose`, a
valid merge-base SHA, and the complete nonempty union of eligible records
(deduplicating identical records is permitted). A full receipt uses an empty
`changes` list because it makes no skip proof; its reason describes the fallback.

The CLI reads `APP00A_EVENT_NAME`, `APP00A_BASE_SHA`,
`APP00A_SOURCE_HEAD_SHA`, `APP00A_WORKFLOW_SHA`, writes
`TestResults/app00a-classification.json`, emits single-line `mode` and JSON
`receipt` outputs through `GITHUB_OUTPUT`, and writes a concise job summary.
JSON escapes any untrusted characters. Logs and summaries must not interpolate
untrusted paths as workflow commands. A conservative full decision succeeds;
unexpected infrastructure/write failures fail the job.

## Workflow and aggregate

Add `classify` on `ubuntu-24.04`, timeout 5 minutes, full-history checkout.
Retain these existing lightweight commands in this job on both routes:
`./scripts/Verify-Business.ps1`, `python tools/validate_business.py`,
`python tools/validate_e_sim_01.py`. Preserve the original copies in `core_sql`.
Upload the classification receipt with an attempt-specific name.

Each Windows job adds `needs: [classify]` and runs unless classification is a
successful, explicit docs-only decision. Use a cancellation-aware status
condition so a classifier infrastructure failure cannot silently count as a
docs success. Expected comparison failures produce a successful `full`
decision and run all workers. A failed classifier can still run workers but
must leave the aggregate red.

`validate` keeps its existing name and `always()` condition, depends on all
five jobs, and still runs the Python CI test suite before the gate. The gate
requires exactly those five job entries and a successful classifier with
strictly parsed outputs. Its receipt must match the current event/base/head/
workflow environment; reject missing fields, duplicate keys, non-JSON numeric
literals, unsupported versions, unknown modes, and mismatched identity.

- Full route: all four workers must be `success`.
- Docs route: event is `pull_request`, complete positive proof passes the path
  and mode policy, and all four workers must be exactly `skipped`.

No mixed worker results are accepted. Docs output states **runtime workers
NOT_RUN**. It is never evidence for SQL, browser, LocalDB, business acceptance,
or closeout. The main push always has its own full execution receipt.

## Verification and evidence

Use actual temporary Git repositories to test whole-PR ranges, merge results,
renames, modes, malformed/unavailable history, and main push behavior. Retain
all existing strict aggregate regressions and extend them for docs proof,
source identity, classifier failures, and mixed worker outcomes. Check all
original worker steps and every companion workflow remain unchanged.

The implementation PR must take full CI. After its reviewed candidate is
frozen, create a temporary draft child PR targeting the implementation branch
with only one eligible prose edit. Observe real successful lightweight checks
and `validate`, four `skipped` workers in the Jobs API, the receipt, and the
unchanged companion jobs. Close the probe without merging it and preserve its
source/run/attempt/artifact and timing evidence in the implementation PR body.
No dispatch override or skip label is added to production.

References: [required checks](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks),
[PR events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows),
[checkout history](https://github.com/actions/checkout#fetch-all-history-for-all-tags-and-branches),
[Git diff](https://git-scm.com/docs/git-diff),
[merge bases](https://git-scm.com/docs/git-merge-base).

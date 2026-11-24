# Superseded PR CI Runs Implementation Plan

> Execute the Owner-authorized item 3 from the CI improvement sequence. Use native implementation in this session with an independent source review. This request does not authorize merging the new PR.

**Goal:** Stop obsolete in-progress PR validation when a replacement run arrives for the same workflow and PR, reducing redundant runner usage.

**Architecture:** Add workflow-level GitHub Actions concurrency to all 16 existing PR workflows. Group by workflow name, event name and PR number; use the unique run ID for non-PR events. Cancel in-progress work only for `pull_request`. Keep the four APP-00A workers parallel inside each run.

**Tech Stack:** GitHub Actions YAML and existing verification tools; no application or runner-script changes.

**Spec:** Owner instruction on 25 November 2026: implement item 3, cancel superseded PR runs. Baseline is merged PR #145, `dfa59378c871cbe8fcfcdaaf92c5f3bbdd5bcefd`.

## Design and constraints

- Use native workflow concurrency rather than a custom API cancellation script or a shared branch queue. Native concurrency requires no new token permissions; a shared main queue could replace pending main runs even with cancellation disabled.
- Exact group: `${{ github.workflow }}-${{ github.event_name }}-${{ github.event.pull_request.number || github.run_id }}`.
- Exact cancellation condition: `${{ github.event_name == 'pull_request' }}`.
- Keep workflow names, event/path filters, permissions, jobs, timeouts, SDK/runtime pins, source checks, tests, artifacts, scratch cleanup and APP-00A `validate` unchanged.
- Scope is 10 regular PR/main workflows plus 6 path-filtered PR/manual workflows. Modifying those six workflow files invokes their existing synthetic/source checks; this does not authorize LAB host Apply or physical acceptance.
- This is arrival-based run cancellation, not commit-age comparison. Do not rerun a stale PR SHA while a newer head is being validated. Preserve the old run history and distinguish cancellation from assertion failure.

## Review focus

1. Same PR/workflow shares a group across commits; different PRs and workflows have different groups, including fork PRs with the same branch name.
2. Non-PR runs have unique groups, preventing both running cancellation and replacement of pending main/manual runs.
3. Concurrency is at workflow scope; it must not serialize or cancel sibling APP-00A workers or the LAB matrix jobs.
4. All pre-existing jobs and triggers remain byte-for-byte unchanged. Cancellation may retain partial evidence and execute existing `always()` finalization; no cancelled run is accepted as final-source verification.
5. Reruns keep the same run ID; rerunning a stale PR run is outside the newest-head contract. GitHub scheduling is not a commit-order guarantee.

## Implementation and verification

- [ ] Add the block before `permissions` in every PR workflow; record the implementation as commit A.
- [ ] Add the operational guide and link it from the APP-00A CI guide as meaningful documentation commit B.
- [ ] Compare all 16 parsed workflows with baseline after removing only the new top-level concurrency mapping; verify names are unique case-insensitively and every original job, trigger and permission is preserved.
- [ ] Run actionlint, `git diff --check` and the existing 15 APP-00A gate tests. Do not add tests that merely duplicate the YAML literals.
- [ ] Obtain independent review of the complete A+B tree before publication.
- [ ] Publish A and open the PR. Once its CI is active, fast-forward to reviewed B. Observe GitHub automatically cancelling superseded runs; do not manually cancel a run and present that as proof.
- [ ] Verify final-head CI on B: all 16 triggered workflows and 32 effective jobs, including the six path-filtered workflows. Keep any infrastructure failure/retry history explicit.
- [ ] Record exact SHAs, old/new run IDs, cancellation evidence and final-head checks in the PR body, without a recursive receipt-only source commit.

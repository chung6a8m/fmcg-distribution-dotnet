# APP-03 read diagnostics implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the pending HTTP/SQL phase observable when APP-03.4/.5 browser assertions fail, then complete the authorized review, verification and merge of PR #148.

**Architecture:** An opt-in Vite observer records allowlisted proxy lifecycle fields. An opt-in Playwright reporter starts and reaps a bounded PowerShell SQL observer for the already-created, nonce-owned tenant-a run database. Existing tests and application behavior remain authoritative; telemetry is diagnostic evidence with explicit availability and correlation limits.

**Tech Stack:** Existing Vite/Playwright/Node 26, PowerShell, Windows LocalDB; no package additions.

**Spec:** `docs/roadmap/app-00a-build-cache.md`, section “Bổ sung khi review PR #148: quan sát request chậm”.

## Global Constraints

- SYNTHETIC_NONPRODUCTION / CI. User authorizes review, fixes, verification and merge; no new operational acceptance.
- Preserve SDK 10.0.404, Node >=26.13.1 <27, all witnesses, assertion/HTTP/step timeouts, retries 0, validate, concurrency and bootstrap.
- Do not change application C#, business snapshots, identity/tenant authority, transaction/locking semantics, host target or test scenario assertions.
- Enable only with FMCG_APP03_READ_DIAGNOSTICS=1 in .4/.5. SQL requires Windows GitHub Actions and exact run/evidence identity.
- Allowlisted telemetry only; no raw SQL, headers, URLs/query values, body, credentials, arbitrary exception messages or other-database metadata.
- Fixed LocalDB tenant-a database, owner/nonce/session/binding verified before sampling; no SQL writes or permission changes.
- Preserve failure results and historic negative evidence. No further unchanged blind reruns; final receipts belong in the existing PR body.

## Review Focus

- Slow/aborted proxy requests must retain their original status, body and termination behavior.
- Unexpected URL/error text and environment values must not enter diagnostic records.
- Invalid run/path/ownership must stop observation, never widen the database or file boundary.
- Failed/unavailable/overlong observer must dispose its connection/process without masking test failure; interrupted redaction must leave raw reports outside both upload roots.
- Incomplete/capped samples and overlapping requests must not support claims of no contention or unique request-to-session causality.

### Task 1: HTTP lifecycle observation

**Files:** `web/ci/app03-read-http.mjs`, `web/vite.config.ts`, `web/tests/app03-read-http.test.mjs`, `web/tests/app03-read-vite.test.mjs` and the module type declaration.

**Interfaces:** Consume only the opt-in flag; return Vite middleware/proxy configure hooks. Emit prefixed JSON with fixed route/event labels, local request ID, UTC/elapsed and numeric status. Keep the existing localhost:5116 proxy target.

- [ ] Add real-HTTP regression cases for success, upstream error/client cancellation, unknown paths and sensitive input; assert unchanged delivery and absence of secrets in observer output. Verify actual Vite configurations preserve the upstream Host as well as URL/body with diagnostics on/off.
- [ ] Implement observation without mutation or raw exception logging; contain diagnostic writer errors.
- [ ] Run the focused tests, record results and review the diff independently.

### Task 2: Bounded SQL observation and artifact redaction

**Files:** `scripts/Watch-App03SqlReads.ps1`, `web/ci/app03-read-reporter.mjs`, `web/playwright.app03-4.config.ts`, `web/playwright.app03-5.config.ts`, focused regression files under `web/tests` / `tests/ci` where needed.

**Interfaces:** Consume exact APP02C1_RUN_ID and APP03_EVIDENCE_DIR supplied by the existing harness, plus the CI opt-in. Write NDJSON beneath TestResults/app03-4 or app03-5 for that run. Record SQL request/session IDs, wait type/time/blocker, bounded counters and fixed statement/resource families. No direct HTTP-to-SQL ID mapping is claimed.

- [ ] Test invalid identity/path, child stop/reap, diagnostic failure independence and exact-known-token redaction preserving XML/assertion text and PNG bytes. Test interrupted/failed publication keeps raw reports outside both artifact selections.
- [ ] Start one observer with a restricted child environment; fixed integrated-auth LocalDB connection and existing DemoRun ownership predicates.
- [ ] Sample around 250ms with one command at a time, 1s command timeout, 240s deadline, bounded rows/output, parent-stop handling and finally disposal. Include current-database idle blockers and explicitly mark unavailable samples. Find the first SqlException in a wrapped chain; retain bounded continuation for timeout -2 on a usable connection and cover a nested native cause in Windows PowerShell 5.1.
- [ ] Keep list/JUnit reporters; stage raw browser output under the validated runner temporary directory outside upload selections, clean up onEnd, then publish only sanitized known-token text and unchanged screenshot bytes into canonical evidence paths. An interrupted/failed publication must not make raw output uploadable. Do not change returned test status.
- [ ] Parse PowerShell and execute focused platform-independent tests; review the SQL/ownership boundary. Actual LocalDB acceptance remains hosted CI.

### Task 3: Integration and authorized completion

**Files:** `.github/workflows/app00a.yml`, this plan and the owning roadmap; final receipts in PR body.

**Interfaces:** Set the opt-in for exactly APP-03.4/.5. Retain existing TestResults artifact uploads. Consume complete source-bound CI results before merge.

- [ ] Verify original workflow commands/gates and targets are unchanged; run actionlint and existing aggregate-gate tests. Run focused observer tests, actual Vite integration and the Windows PowerShell 5.1 error-classification regression on CI as part of APP-03 preparation.
- [ ] Independent review of the diagnostic delta, fix material findings and verify the final source tree before publishing.
- [ ] Run full CI once on the substantive new HEAD. Inspect .4/.5 HTTP records and SQL availability, plus all original .NET/SQL/browser/audit gates and reuse calls. If failed, diagnose from the new evidence before choosing any further action.
- [ ] If all final-head checks pass, merge with expected head lease, verify actual merge/tree and all expected push/main workflows. Keep failures, measurements and main receipts in the existing PR; no receipt-only commits.

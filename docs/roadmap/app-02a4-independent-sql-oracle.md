# APP-02A.4 — Independent AC-11 C→P two-tenant physical SQL oracle

**State (2026-11-17): IMPLEMENTED_ON_FEATURE_BRANCH / FINAL_HEAD_CI_PENDING / INDEPENDENT_REVIEW_PENDING.** Do not claim AC11_SYNTHETIC_VERIFIED until all gates pass on the exact reviewed HEAD. Parent: [APP-02 roadmap](app-02.md); [8 Owner choices](../decisions/owner-app02-2026-11-17.md); [A.1 contract](app-02a1-scope-contract.md), [A.2 schema](app-02a2-schema-contract.md), [A.3 reviewed worker](app-02a3-worker-transactional-recovery.md).

## Isolation and authority

APP-02A.4 is SYNTHETIC_NONPRODUCTION only; it changes neither worker/schema nor the immutable business/ baseline and business.lock.json. Independently provisioned, nonce-owned physical SQL Server LocalDB tenant-a/tenant-b hold actual witnesses. Incoming HTTP statuses are scenario drivers, not economic evidence or numerical expected values. Synthetic source APP-01D offer is nonposting; accepted C writes exactly one APP-01D source and two balanced ledger legs (CUSTODY-OUT −200.0000; FUND-IN +200.0000). Transactional outbox and separately committed synthetic receiver/ACK remain distinct. Unit SYNTHETIC_UNITLESS; invoiceEligible=false; no live financial/provider/invoice; no offline business writes; B_NO_CHANNEL blocked; EV-08 and P0 NOT_VERIFIED; APP-02B–E/pilot/go-live NOT_AUTHORIZED.

## Frozen independent expected SQL matrix

- Fixed versioned fixture: [app02a4-expected.json](../../tests/FmcgDms.Api.Tests/Fixtures/app02a4-expected.json), SHA-256 recorded in reports and rechecked by an independent validator. Decimal amount 200.0000 and expected counts/states frozen without consulting implementation API response.
- [Physical SQL observer](../../tests/FmcgDms.Api.Tests/App02a4IndependentSqlOracle.cs) separately queries persisted P/C receipts, progress, exact P→C reference, APP-01D acceptance/source, two ledger effects, outbox/consumer/delivery ACK, retry and CASE/disposition/correction lineage, orphan/invalid hash/ACK sentinels, status history, and full user-table SQL fingerprint.
- 24 named independent metric cells per checkpoint: Expected, Actual, signed Difference = Actual - Expected, Match. There are 28 checkpoints per tenant = 672 cells per tenant, **1344 across two tenant databases**.
- Each of 24 tests writes a tenant+group-named report to TestResults/APP-02A4 even after a fault. Every report includes group/tenant, SQL DB name, TestRunId, final source HEAD SHA, workflow SHA, fixture SHA, SQL status history, full SQL digest and opposite tenant physical DB SHA before/after.
- [Verify-App02A4.ps1](../../scripts/Verify-App02A4.ps1) independently validates all cells and signed differences against the checked-in fixture, 24 complete report files, exactly 56 checkpoints, SQL names/nonce boundaries, source SHA, and zero peer writes. C# test-reported PASS alone does not qualify.

## A11 group-by-group evidence (two tenants each)

| Group | Named SQL checkpoints | Fault and negative witness |
|---|---|---|
| A11-01 | applied → ack | P offer nonposting, C APP-01D source with two 200-unit legs, durable outbox/inbox/ACK |
| A11-02 | pending → applied | C before correctly named P, zero premature source/ledger/outbox |
| A11-03 | pending → case | Absent/foreign P opaque, malformed input rejected, owned 24h CASE without phantom offer |
| A11-04 | applied → replay → second-c | Same key/hash does not mutate SQL, second C for same P rejected without extra acceptance |
| A11-05 | applied → conflict | Changed canonical content under same event key HTTP 409, full SQL digest unchanged |
| A11-06 | compete | Two concurrent C/worker calls, one acceptance/outbox and one rejected |
| A11-07 | rolled-back → recovered | Injected pre-COMMIT SQL ledger fault, atomic rollback and safe same-ID retry |
| A11-08 | intake-lost → outbound-lost → recovered | Suppressed real HTTP 202, owner lookup/same-ID replay, consumer COMMIT then lost ACK, stable MessageId |
| A11-09 | os-pending → os-recovered | Real distinct Kestrel OS PIDs and SQL pending→APPLIED/outbound ACK recovery |
| A11-10 | revoked | Current SQL grant revoked, wrong role forbidden, peer lookup opaque, no peer SQL writes |
| A11-11 | missing-case → late-case → resumed → applied → corrected → six-retries | CASE owner, late P holds, independent Maker/Checker, self-approval denied, immutable nonposting correction, retry exhaustion; separate Trip for six-retry fault |
| A11-12 | applied → reconciled | Full source/offer/acceptance/legs/outbox/consumer lineage, AR and COGS unchanged, no correction posting, peer SQL unchanged |

Groups are scenario categories, not merely 12 nominal PASS labels. Exact independent expected/actual/diff assertions run on both physical tenant DBs.

## Strict exit gates

1. Windows GitHub Actions pinned .NET SDK 10.0.404, Node 26 LTS, two nonce-marked scratch LocalDBs, migrations; no skipped tests; minimum **296** .NET tests (historical 272 + 24 A4 tests).
2. Both tenants: **12/12 A11 groups each**, **28/28 checkpoints each**, **672/672 SQL metric cells each**; no negative sentinel, broken source/ledger/outbox lineage, or opposite tenant full-table mutation; new oracle artifact + TestRunId bound to exact PR source HEAD and workflow SHA.
3. Preserve historical APP-01E.5 182/182 cells and 518/518 equations, prior APP-01B/C/D/APP-01E regressions, auth, immutable business baseline, frontend/PWA and Playwright gates.
4. Independent review of source, numeric fixtures, SQL queries, mutation sentinels and validator; fix findings and reverify on **final HEAD**. Never carry over an earlier green run after changing the source.
5. Only after all previous gates pass may AC11_SYNTHETIC_VERIFIED be recorded (strictly synthetic). Actual EV-08 and five production P0 still NOT_VERIFIED; APP-02B–E, production integration and pilot remain NOT_AUTHORIZED.

**Closeout evidence pending:** final source commit SHA, CI run+attempt, 24 report artifacts, database nonce names, independent reviewer verdict and explicit unchanged business lock.

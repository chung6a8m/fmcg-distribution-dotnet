# APP-02C.6 — LocalDB execution report

**22/11/2026 (Asia/Bangkok) · MATRIX_V0.1_APPROVED / EXECUTION_BLOCKED**

Owner approved [matrix v0.1 and fixture](../roadmap/app-02c6-localdb-acceptance-matrix.md) and reserved acceptance signoff after reviewing SQL. The 11-case acceptance run remains `NOT_RUN`. The C.1–C.5 baseline below ran against actual Windows LocalDB and supplies partial evidence for the approved matrix.

## Run identity and SQL comparison

- Clean checkout `34b24d8d09ef76f155e7b5f4e85c0f1d354ab191` (PR #109 merge). [GitHub Actions](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions?query=branch%3Amain+event%3Apush) reported 10 successful workflows when queried on this exact SHA. API SHA-256 `99D2B2F79FD8F2EE4E0635B8C3753EEDF7D36172A5F5DDF6F5A3794CAC182D9D`.
- Host `LAPTOP-DLPF4MIR`, instance `(localdb)\MSSQLLocalDB`, operated by Codex through the current Windows session. Each launcher allocated its own nonce-bound pair `Fmcg_App00_<runId>_[AB]`. Guarded cleanup completed and a read-only master catalog check returned zero remaining names for the five pairs.
- The existing launchers query SQL directly after browser execution under the launcher account. Results below are baseline readbacks. A C.6 run still needs the matrix fixture, a separate read-only comparison identity and evidence mapped to the full case × tenant scope.
- Regression evidence on the same revision: solution build passed, .NET 339/339, web unit 15/15, dependency checks 3/3 and PWA build checks 5/5. Local browser regression had 50 pass and one installed standalone PWA skip under the existing local-environment guard. Business snapshot and model validation passed.
- The local evidence directory is `%LOCALAPPDATA%/FmcgDmsC6-20261122-195156`. It contains the approved matrix snapshot, baseline manifest and logs, `baseline-sql-review.json` with all 61 matched expected/actual rows and empty diffs, exact-source CI receipts, browser results and raw SQL JSON under `source/TestResults/app02c{1..5}/`. Each JSON includes expected and actual rows and a match flag.

| Baseline | Run ID | SQL rows matched | Observed SQL |
|---|---|---|---|
| C.1 | `8f3720b2aed24070be05e5240ec48aa2` | 12/12 | Customer/Outlet update, direct selection, two registered ShipTo revisions and stale-version denial. |
| C.2 | `05c726c85b1a465fb6319b03336f4ecc` | 11/11 | Confirm 40 and reserve 15 + 25. MAIN stays 100/value 1000 and AR/COGS remain zero. Over-reserve denied. |
| C.3 | `2902683595cc41b4850a88e6be316748` | 12/12 | Load 10 + 15. MAIN 75/value 750, TRIP 25/value 250, 15 remaining warehouse allocation and zero AR/COGS. |
| C.4 | `7be59a99793145aba9cf76e32045846c` | 13/13 | Load 40, deliver 25, held 15. MAIN 60/value 600, TRIP 15/value 150, AR 375, COGS 250. Delivery 22/11 and due 29/11. Zero counted business effects in B. |
| C.5 | `d2b39b5d139b4d7f887d374dd50b0aa8` | 13/13 | Load 40.001, deliver 25, held 15.001. MAIN 59.999/value 599.9900, TRIP 15.001/value 150.0100, AR 375 and COGS 250. Pending 13 had zero postings. Zero counted business effects in B. |

## Coverage and gaps by approved case

| Case | Evidence from the baseline | Outstanding for C.6 |
|---|---|---|
| DEV-01 | C.1 direct/registered ShipTo and C.4/C.5 tenant-A delivery and AR | Complete valid flow and prescribed denials in both A and B. |
| DEV-02 | C.1 master revisions and stale-version denial | Integrated post-Confirm edit plus eligible and rejected amendment sequence and snapshot fingerprints within the same case. |
| DEV-03 | C.2 SQL confirms 15 + 25 and no accounting effects | Run the approved policy-denial and blocked Load variants and collect complete A/B evidence. |
| DEV-04 | C.2 SQL reconciles pool and reservation rights | Seed protected occupancy 30 and run the two competing A allocations at 40 each. |
| DEV-05 | C.3 partial manifests and rollback on its own fixture | 15 + 25 Load checkpoints in each tenant and the prescribed full rollback fingerprints. |
| DEV-06 | C.4 standard 40 and C.5 fractional 40.001 SQL matched in A | Those fixtures in B as well, with the approved denials and checkpoint evidence. |
| DEV-07 | C.4/C.5 seller-local due date plus seven days at the actual run date | Approved fixed UTC instants, second policy, historical order and independent readback. |
| DEV-08 | C.5 posted/pending coverage 1/1 → 1/2 with 375 balance | Posted allocation 100 across `asOf` and due-day → overdue boundary. |
| DEV-09 | C.5 role, revocation, trip assignment and peer report probes | Two-way read/write coverage, target refusal subcases and case-specific peer business-row fingerprints. |
| DEV-10 | C.5 lost ACK, late SQL failure and lookup in a fresh browser session | Restart the actual API inside the same case, race two clients and reconcile stable outcomes and effects against the original command IDs. |
| DEV-11 | C.5 workspace state, navigation recovery and disabled offline write action | Case-specific browser/HTTP/SQL record on the approved fixture, including the offline attempt and regression receipts. |

## Execution blockers

1. The checked-in DEV launcher supports C.1–C.5 and seeds C.3/C.4 trip and writer data only for A. Add a C.6 runner that seeds both slots with the approved fixture, performs the required phases and captures case-scoped SQL evidence. See [launcher](../../scripts/Run-App02C1.ps1) and [fixture and orchestration](../../src/FmcgDms.Demo/App02c1Dev.cs).
2. The approved historical Delivery instants and report cutoffs require a deterministic time mechanism scoped to scratch DEV. Delivery evidence currently defaults to SQL `SYSUTCDATETIME()` and the report uses SQL time to reject future cutoffs. The actual baseline dates therefore differ from DEV-07 and DEV-08. See [Delivery schema](../../src/FmcgDms.Api/Persistence/App02c4Schema.cs) and [report clock](../../src/FmcgDms.Api/Persistence/App02c5Report.cs).
3. Prepare the approved allocation and API-restart probes and a separate read-only SQL comparator before running for acceptance. Pin the runner, fixture and actual compared outputs. The matrix still requires the tested source/build, required CI and traceable diffs for all 11 cases.

**Physical gates:** `C-PREPROD BLOCKED / NOT_VERIFIED`, physical `C-E2E NOT_RUN`. The original conditions are in the [C roadmap](../roadmap/app-02c-a-online-e2e.md#3-development-and-physical-gates).

# APP-02 — Integrated Event Processing & Product Verification Foundation

**State (2026-11-17): APP-02A.1–A.4 OWNER_AUTHORIZED / SYNTHETIC_NONPRODUCTION; APP-02A.1 DOCUMENTATION_REVIEW_PENDING / NOT_IMPLEMENTED.** [OWN-APP02-01..08](../decisions/owner-app02-2026-11-17.md) explicitly authorize only the staged synthetic AC-11 C→P scope. A.2 starts only after independent A.1 contract review/merge, with A.3/A.4 separately gated. **APP-02B–E remain NOT_AUTHORIZED.** No production actor/cash/invoice/accounting, Product Verification, EV/P0 or pilot/go-live approval is implied. Earlier [OWN-SQL04-06](../decisions/owner-app-2026-11-15.md) only covered APP-00/APP-01A–E.

**Starting baseline:** APP-01E.1–E.5 have been independently reviewed and merged in their bounded scopes. [APP-01E.5/#37](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/37) used final source HEAD `1acd407f7fc3c8c8f980786c5c83b84f759df6d9` and [Windows CI #44002175853](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44002175853): **247/247 tests, 182/182 AC-03 cells, 518/518 cross-transaction equations**, two independent nonce-owned LocalDB tenants. [Documentation closeout/#38](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/38) merged on `main` `3432241d3ca3cbbb6a87756bb3fa4a8b28d919e6`. These results do **not** establish full AC-11/C→P, production auth/policy, actual payments/invoices, other business ACs, EV-01..15, five P0 gates or pilot acceptance.

## 1. Goal and non-negotiable boundaries

- First complete **AC-11: durable processing of an event C that depends on P**, including out-of-order arrival, same-ID replay, changed-payload conflict, missing dependency, lost ACK, process restart and two-client/two-worker contention. [AC-11](../../business/acceptance.md), [BR-16](../../business/business-rules.md), [POL-16](../../business/policies.md) and EV-08 are normative; Owner chose P=`CashHandoverOffered` (Sales), C=`CashierAcceptanceRequested` (Cashier), and **at most one successful C acceptance per P (0..1)**; intake can precede P, but business acceptance remains pending and nonposting until verified P and count evidence. See [frozen Owner choices](../decisions/owner-app02-2026-11-17.md).
- Then design the **production runtime boundary**, an A_ONLINE vertical E2E slice, B_ONLINE + Mixed E2E, and evidence-driven Product Verification/Pilot Readiness. Advance each separately; passing a synthetic gate cannot upgrade a real mode.
- **Never modify** the locked `business/` snapshot or `business.lock.json` as an implementation shortcut; retain independent baseline SHA validation. The canonical business and SQL repositories are evidence/reference, not runtime dependencies.
- Keep `SYNTHETIC_UNITLESS` and `invoiceEligible=false` in synthetic paths; signed synthetic JWT and nonce-owned LocalDB must never become production credentials/routing. **Offline business writes disabled, B_NO_CHANNEL BLOCKED.**
- A/B_ONLINE/MIXED remain **NO_GO / PENDING** for production until mode-specific approval and tests. Real credit/terms/thresholds are **UNSET** until separately approved; no invented policy defaults. The 125% browser scaling exception stays `EXCEPTION_ACCEPTED / NOT_TESTED`, outside the roadmap.

## 2. Sequential increments and acceptance gates

| Increment | Proposed scope | Exit condition (before the next dependent increment) | State |
|---|---|---|---|
| **APP-02A.1** | [Owner-approved C/P scope, identity, ACK, recovery, CASE, outbox and evidence contract](app-02a1-scope-contract.md) | [Eight Owner decisions](../decisions/owner-app02-2026-11-17.md) recorded; independent docs review, exact final-head CI, no unresolved findings; then `CONTRACT_APPROVED` | **OWNER_DECISIONS_APPROVED / DOC_REVIEW_PENDING** |
| **APP-02A.2** | Durable Inbox/Outbox, P/C dependency and synthetic consumer schema, tenant constraints and migrations | Fresh/replay on two nonce-owned physical tenant LocalDBs; event unique key, per-offer acceptance, hash/FK/immutability and tenant negatives; no posting | **OWNER_AUTHORIZED_IN_SCOPE / BLOCKED_ON_A1_REVIEW** |
| **APP-02A.3** | C→P worker, APP-01D source bridge, Maker/Checker CASE, outbox dispatch and consumer recovery | Receipt COMMIT before ACK, apply + two ledger legs + outbox atomic; P-triggered/15m scan, 6 bounded technical retries, 24h CASE; two-worker/OS-restart/lost-ACK | **OWNER_AUTHORIZED_IN_SCOPE / BLOCKED_ON_A2** |
| **APP-02A.4** | Independent AC-11 SQL oracle and C→P synthetic closeout | **A11-01..A11-12** groups, independent two-tenant SQL expected/actual/diff, source/ledger and inbox/outbox/case lineage, no peer writes, exact final-head CI and review; only then `AC11_SYNTHETIC_VERIFIED` | **OWNER_AUTHORIZED_IN_SCOPE / BLOCKED_ON_A3** |
| **APP-02B** | Production runtime foundation (authorization, policy boundary, tenant lifecycle, audit/operations) | Real actor/seller/tenant identity and role revocation; no synthetic privilege escalation; policy approvals fail closed; deployment/backup/restore/security evidence and Owner decisions for hosting, retention, RPO/RTO | **PROPOSED; requires separate authority** |
| **APP-02C** | A_ONLINE vertical E2E: Customer/ShipTo → A Confirm → reserve/load → partial deliveries → due-dated AR → reporting, with UI | AC-02/08 and relevant AC-03 A cases backed by actual integrated services/UI, real approvals in permitted environment, independent SQL and operator acceptance; no production mode admission implied | **PROPOSED; after B prerequisites** |
| **APP-02D** | B_ONLINE + Mixed E2E: B quota/credit, stock/cash/custody, real channel-validated invoicing, accounting reconciliation, and MVP rule coverage | AC-03/09/10/14/15 and relevant additional ACs verified with external evidence, source-bound outcomes and no double-post; B_NO_CHANNEL remains blocked; Mixed only after A and B both qualify | **PROPOSED; gated by external contracts** |
| **APP-02E** | Product Verification + EV/P0 and pilot-readiness dossier | EV-01..15 collected as applicable with actual build/environment/actors; all five P0 passed; whole-warehouse cutover, opening signed, real recovery and mode-specific go/no-go evidence; **separate Owner/Tenant acceptance** | **PROPOSED; pilot remains NOT_AUTHORIZED** |

**PR rule:** one independently reviewable increment per PR; first agree on contracts/expected values, then migration, then processor, then independent oracle. For each code PR: clean immutable business lock, same source-head revision under review and CI, two physical tenant databases where applicable, no skipped required tests, source/actor/tenant lineage, negative/fault tests and persisted artifacts with TestRunId. A changed HEAD invalidates earlier review/CI approval. Do not preset new pass counts before the scenario matrix is approved; keep all existing APP-01E/AC-03 and frontend/PWA/Playwright gates.

## 3. APP-02A approved acceptance coverage (NOT_RUN; 12 scenario groups)

Owner selected the [A11-01..A11-12 independent SQL matrix](app-02a1-scope-contract.md#8-approved-a11-01a11-12-acceptance-groups-not_run). It includes Offer P then Cashier C; out-of-order durable pending; 24h CASE and Maker/Checker with late-P same-ID resume; immutable correction, 6 technical retries, tenant/revocation negatives, atomic acceptance + two balanced custody/fund legs, transactional outbox + durable synthetic consumer, lost intake/apply/outbound ACK, process restart and two-worker races. Each group may expand into multiple test cases; **no run/pass count is claimed**.

The summary below remains an overview, **not an executed oracle**. Exact expected cells, correlation IDs and versioned fixtures are to be independently reviewed before implementation.

| Scenario | Durable expectation | Negative assertion |
|---|---|---|
| P then C | Each accepted with source/actor/tenant identity; C applied once after P is durable | No second source/effect under replay |
| C before P | C durably RECEIVED/PENDING with explicit dependency; resumes when valid P is available | Never silently drop C or post an effect before P |
| Missing/invalid/foreign P | Pending/case or terminal rejection according to approved contract | No cross-tenant dependency resolution, phantom P or guessed default |
| Same identity + same payload | Return stable persisted outcome across retry, process restart or lost ACK | Zero new business effects |
| Same identity + different payload | Durable conflict and no new effect | Do not overwrite prior identity/hash/outcome |
| Two concurrent workers | Transactional source/effect uniqueness with permitted linearization | No double posting or lost pending |
| Failure before commit | All related SQL effects rollback; retry preserves identity | No partial command, source, dependency or ledger effect |
| Failure after commit / lost ACK | Query existing outcome; same-identity retry recovers stable result | Never convert UNKNOWN into REJECTED or mint new ID |
| Cross-tenant actor/replay | Hard-fail access or isolation; inspect both physical DBs | Zero peer-tenant write or visibility |
| Dependency stuck/expired | Observable queue age, asOf, owner/next action and audited case as decided by Owner | No silent deletion, endless unobservable retry or zero-as-unknown |

The Owner has fixed the C/P envelope, identity, state/ACK rules, retry/case/retention and 12-group acceptance *scope* in APP-02A.1. Engineering must still freeze concrete versioned fixtures, expected numeric SQL cells and per-group negative cases at the independent A.1 review/A.2 handoff. Business source IDs and exact decimal/lineage requirements must never be weakened to match code.

## 4. APP-02B–E dependencies and business completeness

1. **Runtime/production gate (B):** decide production hosting/environment, secret & identity administration, tenant provisioning and actor permissions, restore/security testing, retention/RPO/RTO and audit operations. APP-01 synthetic JWT or LocalDB guard is deliberately not reused as production authorization.
2. **A_ONLINE gate (C):** independently approved actual price/credit/terms policy, immutable ShipTo/terms snapshot, partial delivery AR and proof of source-bound stock/cost; provide application UI and an integrated scenario rather than only API/SQL tests.
3. **B_ONLINE/Mixed gate (D):** real verified cash/bank evidence, approved credit/grant policy, invoice provider validity with UNKNOWN lookup/same identity, accounting RECEIVED→POSTED→RECONCILED states, cashier custody/fund and mixed-trip non-overlap. No real B invoice/channel is presumed available.
4. **Full MVP AC coverage register (D/E, not silently satisfied):** AC-05/06/07 (lot/FEFO, return/quarantine, moving-average cost and gifts), AC-12/13 (offline events/revoke/restore), AC-17 (visits), AC-19 (tax/price/return), AC-20/21 (employee advances, allocation, custody), and any other not yet executed AC require dedicated design/implementation/verification or explicit Owner-approved deferral *without waiving P0*. APP-02D implementation must be further split into reviewable PRs once these dependencies are resolved.
5. **Production/Pilot gate (E):** G-STOCK, G-CUT, G-IDEMP, G-LEDGER and G-INV are non-waivable P0 gates. AC-04/16 require authority for **all** movements of Kho-01 after cutover, including outside the pilot wave; once a real first effect exists, use forward recovery rather than assumed rollback. EV-01..15 and real provider/legal review remain evidence-pending. NPP-01's blueprint (3 A + 3 B_ONLINE + 2 Mixed per day for at least 5 days) is **not** authorization to run the pilot.

## 5. Evidence and decision ledger

| Gate / evidence | At roadmap creation | Required action |
|---|---|---|
| APP-01E synthetic two-tenant T0–T6 | **MERGED / SYNTHETIC_VERIFIED** | Preserve regressions and original evidence links |
| APP-02A.1 Owner decisions | **OWNER_APPROVED (8/8, 2026-11-17)** | [Decision ledger](../decisions/owner-app02-2026-11-17.md); independent documentation review/final-head CI remain pending |
| Full AC-11/C→P | **NOT_VERIFIED / NOT_RUN** | A.2 only after A.1 reviewed; A.3/A.4 independently gated. `AC11_SYNTHETIC_VERIFIED` only if A.4 full matrix passes |
| APP-02B production auth/policy/hosting | **NOT_AUTHORIZED / NOT_VERIFIED** | Distinct architecture/security, business policy and operations decisions |
| Production A/B_ONLINE/Mixed, cashier, invoices/accounting | **NO_GO / NOT_VERIFIED** | Integrated product evidence and external acceptance by mode |
| EV-01..15, G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV | **EVIDENCE_PENDING / NOT_VERIFIED** | Actual environment/build/source, measured outcomes and reviewers |
| Pilot / Tenant Go-Live | **NOT_AUTHORIZED** | Signed cutover/opening, P0, pilot thresholds, mode-specific Owner/Tenant decision |

**Next gate:** Independent review and merge of the [APP-02A.1 Owner-approved documentation contract](app-02a1-scope-contract.md) with final-head CI/immutable business lock and the full 12-group SQL witness handoff. Then, and only then, begin separately reviewed **APP-02A.2** under existing **synthetic-only** Owner scope. This documentation does not implement or verify C→P, enable a runtime endpoint/consumer, deploy production, change `business/`, or authorize EV/P0/pilot.

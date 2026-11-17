# APP-02 — Integrated Event Processing & Product Verification Foundation

**State (2026-11-17): PROPOSED / OWNER_AUTHORIZATION_PENDING / NOT_IMPLEMENTED.** This is a roadmap and review input, **not** a new Owner decision, permission to implement, Product Verification, EV/P0 acceptance, or tenant go-live approval. The only existing scope authorization is [OWN-SQL04-06](../decisions/owner-app-2026-11-15.md) for APP-00 and APP-01A–E in synthetic/nonproduction mode. Request a separate, explicit Owner disposition before starting any APP-02 code, migration or external integration.

**Starting baseline:** APP-01E.1–E.5 have been independently reviewed and merged in their bounded scopes. [APP-01E.5/#37](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/37) used final source HEAD `1acd407f7fc3c8c8f980786c5c83b84f759df6d9` and [Windows CI #44002175853](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44002175853): **247/247 tests, 182/182 AC-03 cells, 518/518 cross-transaction equations**, two independent nonce-owned LocalDB tenants. [Documentation closeout/#38](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/38) merged on `main` `3432241d3ca3cbbb6a87756bb3fa4a8b28d919e6`. These results do **not** establish full AC-11/C→P, production auth/policy, actual payments/invoices, other business ACs, EV-01..15, five P0 gates or pilot acceptance.

## 1. Goal and non-negotiable boundaries

- First complete **AC-11: durable processing of an event C that depends on P**, including out-of-order arrival, same-ID replay, changed-payload conflict, missing dependency, lost ACK, process restart and two-client/two-worker contention. [AC-11](../../business/acceptance.md), [BR-16](../../business/business-rules.md), [POL-16](../../business/policies.md) and EV-08 are normative; the concrete meaning/schema of C and P is **not inferred** from synthetic labels and must be resolved in APP-02A.1.
- Then design the **production runtime boundary**, an A_ONLINE vertical E2E slice, B_ONLINE + Mixed E2E, and evidence-driven Product Verification/Pilot Readiness. Advance each separately; passing a synthetic gate cannot upgrade a real mode.
- **Never modify** the locked `business/` snapshot or `business.lock.json` as an implementation shortcut; retain independent baseline SHA validation. The canonical business and SQL repositories are evidence/reference, not runtime dependencies.
- Keep `SYNTHETIC_UNITLESS` and `invoiceEligible=false` in synthetic paths; signed synthetic JWT and nonce-owned LocalDB must never become production credentials/routing. **Offline business writes disabled, B_NO_CHANNEL BLOCKED.**
- A/B_ONLINE/MIXED remain **NO_GO / PENDING** for production until mode-specific approval and tests. Real credit/terms/thresholds are **UNSET** until separately approved; no invented policy defaults. The 125% browser scaling exception stays `EXCEPTION_ACCEPTED / NOT_TESTED`, outside the roadmap.

## 2. Sequential increments and acceptance gates

| Increment | Proposed scope | Exit condition (before the next dependent increment) | State |
|---|---|---|---|
| **APP-02A.1** | [Owner authorization + AC-11/C→P dependency, source/identity, ACK and evidence contract](app-02a1-scope-contract.md) | Explicit Owner scope disposition; mapping AC-11/BR-16/POL-16/EV-08; C/P semantics and state machine approved; independent expected/actual test matrix and schema proposal reviewed | **PROPOSED / NOT_AUTHORIZED** |
| **APP-02A.2** | Durable Inbox/Outbox and dependency schema, per-tenant constraints and migrations | Fresh/replay migrations on two nonce-owned LocalDB tenants; uniqueness, dependency FK/source lineage, immutability and negative tenant/duplicate tests; no production transport | **BLOCKED on A.1** |
| **APP-02A.3** | Transactional dependency processor and safe worker recovery | Durable receipt before ACK; C-before-P retained pending, resume after P; same identity one business effect, different payload conflict; bounded retry/case escalation; two-worker/restart/lost-ACK tests | **BLOCKED on A.2** |
| **APP-02A.4** | Independent AC-11 SQL oracle and C→P closeout | Direct SQL expected/actual/diff with source/effect lineage; both tenant DBs, reordered/duplicate/conflicting/deferred events, lost ACK, failure/restart, peer-tenant zero writes; exact final-source CI and independent review | **BLOCKED on A.3** |
| **APP-02B** | Production runtime foundation (authorization, policy boundary, tenant lifecycle, audit/operations) | Real actor/seller/tenant identity and role revocation; no synthetic privilege escalation; policy approvals fail closed; deployment/backup/restore/security evidence and Owner decisions for hosting, retention, RPO/RTO | **PROPOSED; requires separate authority** |
| **APP-02C** | A_ONLINE vertical E2E: Customer/ShipTo → A Confirm → reserve/load → partial deliveries → due-dated AR → reporting, with UI | AC-02/08 and relevant AC-03 A cases backed by actual integrated services/UI, real approvals in permitted environment, independent SQL and operator acceptance; no production mode admission implied | **PROPOSED; after B prerequisites** |
| **APP-02D** | B_ONLINE + Mixed E2E: B quota/credit, stock/cash/custody, real channel-validated invoicing, accounting reconciliation, and MVP rule coverage | AC-03/09/10/14/15 and relevant additional ACs verified with external evidence, source-bound outcomes and no double-post; B_NO_CHANNEL remains blocked; Mixed only after A and B both qualify | **PROPOSED; gated by external contracts** |
| **APP-02E** | Product Verification + EV/P0 and pilot-readiness dossier | EV-01..15 collected as applicable with actual build/environment/actors; all five P0 passed; whole-warehouse cutover, opening signed, real recovery and mode-specific go/no-go evidence; **separate Owner/Tenant acceptance** | **PROPOSED; pilot remains NOT_AUTHORIZED** |

**PR rule:** one independently reviewable increment per PR; first agree on contracts/expected values, then migration, then processor, then independent oracle. For each code PR: clean immutable business lock, same source-head revision under review and CI, two physical tenant databases where applicable, no skipped required tests, source/actor/tenant lineage, negative/fault tests and persisted artifacts with TestRunId. A changed HEAD invalidates earlier review/CI approval. Do not preset new pass counts before the scenario matrix is approved; keep all existing APP-01E/AC-03 and frontend/PWA/Playwright gates.

## 3. APP-02A acceptance matrix (proposed, not yet executed)

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

Approval of APP-02A.1 must fix the actual C/P envelope, legal state transitions, retry/case retention policy and expected oracles **before** converting this table into implementation acceptance. Business source IDs and exact decimal/lineage requirements are not to be weakened to match code.

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
| APP-02A.1 approval | **OWNER_AUTHORIZATION_PENDING** | Record Owner-selected scope, C/P semantics, exclusions and actor/tenant boundary in a new decision; this document alone is not approval |
| Full AC-11/C→P | **NOT_VERIFIED** | Implement/verify A.2–A.4 independently only after approval |
| APP-02B production auth/policy/hosting | **NOT_AUTHORIZED / NOT_VERIFIED** | Distinct architecture/security, business policy and operations decisions |
| Production A/B_ONLINE/Mixed, cashier, invoices/accounting | **NO_GO / NOT_VERIFIED** | Integrated product evidence and external acceptance by mode |
| EV-01..15, G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV | **EVIDENCE_PENDING / NOT_VERIFIED** | Actual environment/build/source, measured outcomes and reviewers |
| Pilot / Tenant Go-Live | **NOT_AUTHORIZED** | Signed cutover/opening, P0, pilot thresholds, mode-specific Owner/Tenant decision |

**Next actionable planning task:** APP-02A.1 is the [first proposed documentation increment](app-02a1-scope-contract.md), **not** permission to implement A.2, enable any runtime endpoint, deploy a production tenant, change `business/`, or run EV/P0/pilot. Track remaining decisions as explicit blockers, not assumed defaults.

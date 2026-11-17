# APP-01E.2 — Cross-command two-client races (synthetic only)

**Base:** independently reviewed APP-01E.1 merged in [PR #33](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/33), `main` `cc3f3d38f1ee1341e544824fc35c33d335f421e2`. **Status: MERGED / E2_RACES_VERIFIED** after independent [PR #34](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/34) review; exact source HEAD `37906d6b7c69fd195cf45c7ca2c755ec2ad3edeb`, [Windows CI #43956610679](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43956610679) SUCCESS 124/124 tests, 16 reports/368 SQL cells, TestRunId `app01e2-43956610679-1`. E.3–E.5 remain separate/unverified. No production authorization or business policy modification.

Read-only canonical contract: [APP-01E.1 recovery specification](app-01e1-recovery-harness.md) and `business/reference-data/expected-results/golden-oracles.json`. Locked `business/` and `business.lock.json` are unchanged. Synthetic unit `SYNTHETIC_UNITLESS`, `invoiceEligible=false`.

## Contention contract

Each test uses **two separately constructed JWT-authenticated HttpClient transports** with an admission barrier; that barrier alone is not a SQL race. A separate guarded LocalDB **SERIALIZABLE transaction** deliberately holds a shared resource while both requests arrive. A third SQL connection must observe **at least two distinct waiting sessions** in `sys.dm_tran_locks` before releasing the gate. For a Trip app-lock scenario, at least one must be waiting on `APPLICATION`; the other may be waiting on an upstream SERIALIZABLE `BusinessCommand` key-range, which is a genuine second SQL wait (not evidence that both reached the app-lock). Only a nonce-owned `Fmcg_App00_<nonce>_A|B` database with matching `App00ScratchMarker` and `AppTenantBinding` is eligible. A negative test blocks `master`, a non-LocalDB host and cross-tenant connection.

Targets: `StockPool` row update lock for A/B stock entitlements; `Trip` row update lock for manifest vs B20; the exact production-synthetic `APP01C:TRIP:{tenant}:{TripId:N}` SQL application lock for B20/A25/cashier/unload. Test infrastructure lives only in `tests/FmcgDms.Api.Tests`; no migration, production DI hook, runtime fault endpoint or different business posting contract.

**Limit:** lock-wait observation forces a common SQL contention point but cannot claim all possible intra-transaction schedules or deterministic FIFO winner order. Outcomes are checked against a deliberately small *allowed linearization set*, not asserted according to HTTP request start order. A timeout/no observed waiters is test failure, never a synthetic PASS.

## Scenario matrix (each of two physical tenants)

| Scenario | Opposing authenticated commands | Legitimate committed outcome |
|---|---|---|
| `rights-pool` | A70 reservation vs B50 quota over shared stock100 | Exactly one wins. The loser durably rejects `STOCK_INSUFFICIENT`; allocation never >100 |
| `manifest-vs-b20` | T1 atomic `LoadManifest` vs B20 posted using already issued rights | Manifest loads once; B20 either posts after load or rejects `B_LOADED_RIGHT_NOT_FOUND` before load |
| `a25-vs-b20` | A25 vs B20 on same loaded vehicle/trip | Both apply, A/B rights remain independent, no lost COGS/AR/cash |
| `a25-vs-unload` | A25 vs T6 unload (zero custody) | Unload posts once; A25 either precedes unload and succeeds or rejects `TRIP_NOT_ELIGIBLE` after unload |
| `b20-vs-unload` | B20 vs T6 unload (initial custody zero) | If B20 wins, unload rejects `CUSTODY_NOT_SETTLED`; if unload wins, B20 rejects `TRIP_NOT_ELIGIBLE` |
| `cashier-accept-vs-unload` | Independently witnessed cashier T5 acceptance vs T6 unload while custody200 | Cashier acceptance succeeds once; unload either follows and succeeds or rejects `CUSTODY_NOT_SETTLED` beforehand |
| `cashier-accept-vs-accept` | Two independent IDs accepting the same offered200 and verified cash witness | Exactly one T5 acceptance; other durably rejects `OFFER_ALREADY_ACCEPTED`; no double `FUND-IN` |
| `cashier-offer-vs-accept` | Second T5 OFFER200 (non-posting) vs first offer's T5 acceptance | Acceptance succeeds; second offer either succeeds before settlement (no ledger) or rejects `CUSTODY_INSUFFICIENT` afterward |

Each pair uses **different (ProducerId, CommandId)** and a unique source number. The replay portion reuses each original command identity and actor, then checks `Replayed=true`, unchanged reply fields, source identity, entire trip lineage fingerprint (including source/line records with zero ledger effects such as cashier OFFER), physical, rights, AR, COGS, custody/fund and cash allocation. A serialization regression test independently ensures that source fields and amount changes alter the SHA-256 digest. Rejected business commands persist a terminal `BusinessCommand` but never `BusinessSource` or `LedgerEffect`.

## Evidence and safety gate

- **16 parameterized LocalDB tests** (eight races × two tenants) + one guard-negative test + one lineage-hash regression, added to prior **106**, hence **124 minimum**, no skipped/failed tests.
- **16 required reports:** `tests/**/TestResults/APP-01E2/{tenant}-{scenario}-race.json`, schema `APP01E2_RACE_V1`, `TestRunId=app01e2-<run-id>-<attempt>`, separate actual PR source HEAD and workflow synthetic merge SHA, nonce DB name, SQL waiters, per-command actor/source/line/effect provenance, complete trip source/line/effect SHA-256. Replay evidence includes both HTTP replies, `Replayed=true` and a before/after lineage + physical-state sentinel. SQL expected/actual/signed difference across **23 columns/report** => **368/368 checked cells**. Every cell must have zero difference; the CI independently validates report cardinality, field names, source linkage, replay sentinel and provenance.
- SQL state invariants: quantity `warehouse + vehicle + delivered = 100`, cost `warehouse + vehicle + COGS = 1000`, `AConsumed <= AGranted`, `BConsumed+BReleased <= BGranted`, nonnegative stock, capped allocated pool, no invoices. Check duplicate sources/effects through row lineage and replay-stable hash.
- Preserve physical SQL APP-01B.3 **39/39**, APP-01C.4 **65/65**, APP-01D **91/91** per tenant; retain APP-01E.1 six reports (60 cells), immutable business lock, backend build, MTP/TRX/Cobertura, frontend/PWA/Playwright at 100%.
- **Acceptance only after independent PR review and green CI on exact final source HEAD**; rerun after each code change. Record workflow run attempt, artifact IDs/URLs and TestRunId before merge.

## Strict boundaries

This is an **APP-01E.2 SQL interleaving acceptance**, not complete AC-11/C→P: E.3 owns the every-meaningful-fault-point transaction rollback matrix, E.4 owns real lost TCP/ACK, unknown durable outcome lookup and process restart, and E.5 owns independent cross-command reconciliation closeout. Real B/offline, actual payment/invoice/accounting, EV/P0, 125%-scale display (Owner accepted `EXCEPTION_ACCEPTED/NOT_TESTED`) and tenant go-live remain **NOT_VERIFIED / NOT_AUTHORIZED**.

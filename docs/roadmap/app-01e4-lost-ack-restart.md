# APP-01E.4 — Lost ACK, Outcome Lookup & Process Restart Recovery

**State: IMPLEMENTED / INDEPENDENT REVIEW AND EXACT-HEAD WINDOWS CI PENDING.** Synthetic/nonproduction only. This is an isolated increment after [E.3 merged PR #35](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/35), not approval for production offline writes, real cash/invoices, full AC-11/C→P, EV/P0 or go-live. No migration, business rule change, production fault switch or locked Business Baseline modification.

## Durable protocol and HTTP surface

`GET /api/synthetic/app01e/commands/outcome?producerId=...&commandId=...`

- Exists **only** when `SyntheticAuth:Enabled` is true in `Development`/`IntegrationTest`. Reads the server-routed, nonce-marked, tenant-bound LocalDB directly through `TenantConnections.OpenSyntheticScratchAsync`. Never accepts tenant/database/actor in request parameters.
- Requires validated synthetic JWT and a warehouse/sales/cashier role; auditor-only and anonymous requests fail authorization. SQL lookup requires exact **(JWT TenantId, JWT ActorId, ProducerId, CommandId)** with byte-exact actor/command identity comparisons even under case-insensitive SQL collation. Other actor or tenant receives the same `UNKNOWN/NOT_FOUND` as a missing identity, with no source/outcome data.
- Successful response is HTTP 200 with `BusinessCommandRecovery`:
  - `State=COMPLETED`, `Status=APPLIED|REJECTED`, exact persisted `OutcomeCode`, `ReasonCode` and nullable `SourceId`.
  - `State=UNKNOWN`, all terminal fields null; `ReasonCode=NOT_FOUND` when no visible row, or `NOT_FINAL` if a visible row is incomplete/inconsistent. **Neither UNKNOWN answer means a command was rejected or safely absent**. A concurrent in-flight command may not yet have committed.
- The lookup checks that a terminal command has `CompletedAtUtc`; APPLIED has exactly one matching `BusinessSource`, nonempty outcome and no rejection code; REJECTED has zero sources, code REJECTED and nonempty reason. Incomplete or inconsistent rows fail closed to `UNKNOWN/NOT_FINAL`.
- GET neither starts a SQL transaction that updates data nor writes a `BusinessCommand`, source, physical movement, receipt, right or ledger. It does **not** compare or change the canonical payload hash. Only a same-identity POST can test replay or mismatch.
- Invalid/missing/duplicate `producerId` or `commandId` query values (max 128, no controls/empty values) return 400 `COMMAND_IDENTITY_INVALID`.

### Client crash/lost-ACK algorithm

1. **Before the first POST**, durably retain tenant/producer/command identity, signed actor, `SourceNumber`, command kind and **exact canonical payload** on the client. E.4 tests intentionally use the same signed actor after a fresh client/process creation.
2. On HTTP timeout, EOF/RST, cancellation, connection close or any result without a parsed terminal `BusinessCommandOutcome`, mark client outcome **UNKNOWN**. Never generate a new `CommandId`/`SourceNumber` to replace an unknown write; never mark transport failure REJECTED.
3. When connected/authenticated again, GET by original producer/command identity. A `COMPLETED` outcome is durable; do not POST it again to make another business write. `REJECTED` is terminal business rejection, not a network error.
4. On `UNKNOWN` (including `NOT_FOUND` or `NOT_FINAL`), **keep the original identity/actor/payload** and retry/poll under bounded, explicit operator/client policy. Under the existing `SERIALIZABLE` and SQL application lock, either that original command will commit/replay or a previous pre-COMMIT rollback permits a fresh apply under the same key. Changed payload returns 409 `CONFLICT/IDEMPOTENCY_PAYLOAD_MISMATCH` and no new effects.
5. Offline business POSTs and blind automatic retries of arbitrary 4xx, failed policies or changed actors are **not** introduced. A truly ambiguous/inconsistent row is a manual investigation condition, not permission to post a replacement.

## Deterministic implementation boundaries

- **Generic lookup over all command types**: `ReserveAStock`, `GrantBQuota`, `ConfirmAOrder`, `GrantBCredit`, T1 load, T3 B20, T4 A25, T5 cashier OFFER/ACCEPT and T6 unload. The 11th matrix scenario proves that a business `REJECTED/QUANTITY_INVALID` is persisted and recovered without a source.
- **22 TestServer lost-response cases** (11 × two tenants) extend the original E.1 after-server-response suppression; dispose the test host, create a new host/client, GET and retry. These do **not** masquerade as an OS restart.
- **10 real loopback TCP and process cases** (quota, B20, cashier ACCEPT, unload and a rejected quota × two physical tenants): spawn the previously built `FmcgDms.Api.dll` directly as an external ASP.NET Kestrel process with explicit `--urls 127.0.0.1:<ephemeral-port>` using only the fixture's signed synthetic configuration and nonce-owned LocalDB. A test-only TCP reverse proxy forwards the full POST to Kestrel, reads the upstream `HTTP/1.1 200`, then sends an RST to the caller **without forwarding any response bytes**. The client receives `HttpRequestException`, and the first server process is terminated. A **different OS process** starts against the same physical tenant DB; a new authenticated client recovers the exact SQL-backed outcome.
- **Security/unknown negative test**: invalid/missing/duplicate keys 400; unauthenticated 401; auditor-only 403; cross-tenant/actor UNKNOWN; intentionally persisted nonterminal row never returns completed; synthetic-disabled/deployed-Production routes absent (404).
- Real process crash **during a transaction before COMMIT** is not claimed: E.3 already proves deterministic 42-point pre-COMMIT SQL rollback. E.4 tests the post-COMMIT/no-ACK window and restart, with a real socket reset but not an external WAN, mobile network or OS power loss.

## SQL oracle and CI acceptance

- Each real restart case independently reads `BusinessCommand`, `BusinessSource` and actor/source/line/effect lineage from SQL, plus E.2's 23 financial/physical/rights probes and E.3's **all-user-tables, all-rows/columns canonical SHA-256**. The committed fingerprint must differ from before, be identical after GET, same-identity replay, changed-payload conflict, process restart, and hidden-actor/tenant lookups. Durable rejected command must have **zero** business sources/lines/effects.
- Each of **10** `TestResults/APP-01E4/{tenant}-{scenario}-recovery.json` reports uses `APP01E4_RECOVERY_V1`, exact test run/source HEAD/workflow SHA, LocalDB ownership, distinct process IDs, TCP reset status, recovered outcome, SQL lineage and full fingerprints. Two independent `REPLAY/` and `CONFLICT/` sets of **23 actual/expected/signed-diff cells** yield **46 per report, 460/460 in total**. `scripts/Verify-App01E4.ps1` requires every specific file, schema, provenance, stage, 23 named SQL fields, signed zero-diffs and security/actor/source semantics; never trust report PASS alone.
- New cases: **33** (22 TestServer + 10 real-process + 1 negative), raising Windows MTP/`dotnet test` floor from **209 to 242**. No failed/skipped tests. Preserve E.3 84 fault reports/5,796 cells, E.2 16 SQL races/368 cells, E.1 six harness reports/60 cells, B.3/C.4/D 39/65/91 per-tenant oracle, business lock, xUnit v3/MTP/TRX/Cobertura, frontend/PWA and Playwright 100%-only Owner exception.
- Mark `E4_DURABLE_RECOVERY_VERIFIED` **only after** independent review, exact final source-HEAD Windows CI evidence (record both PR source HEAD and GitHub workflow's synthetic merge SHA), then guarded merge. Any source change invalidates prior CI/review claims.

**Next:** APP-01E.5 independent cross-transaction reconciliation and closeout. No C→P, production B, real cashier/payment, legal invoices, offline writes or EV/P0 authorization is implied.

# APP-01A — Integrated model kickoff (AUTHORIZED / APP-01A.1 SCHEMA_VERIFIED)

**Sequencing:** APP-00A is merged and APP-00B/C browser/PWA Owner acceptance OWN-APP00B-13-03 is complete (Issue #13 closed). APP-01A.1 schema and LocalDB negative tests are implemented in [PR #21](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/21); APP-01A as a whole is **NOT_VERIFIED** and remains synthetic/nonproduction only.

Define one transaction/source model per tenant DB: Customer, ShipToSnapshot, SKU, StockLocation, Trip, AOrder/AOrderLine, AReservation, BQuota, BCreditGrant, Command, SourceLine and ledger postings. Add deterministic payload hash/version and `(tenantId,producerId,commandId)` idempotency; same payload replay returns durable outcome, altered payload yields CONFLICT without effect.

Implement `ConfirmAOrder/ReserveAStock` without AR/COGS and grant/quota command guards with no overlap of rights. Enforce actor/tenant, source ownership, decimal exactness and Dapper explicit transaction boundaries. Tests must include two-tenant read/write negative, replay, changed payload, wrong Customer/ShipTo and same-resource concurrency.

APP-01B–E cover manifest/sale/delivery/cash and 91/91 AC-03 checkpoints, not this first slice. Do not lift SQL scratch stored procedures as app persistence.

## Increment boundary

1. **APP-01A.1 — schema + migrations + physical LocalDB negatives:** tables and source/command/effect links, shared A/B rights, FK/unique/CHECK guards, tenant A/B migration replay and RowVersion. See [schema contract](roadmap/app-01a1-integrated-schema.md). No command execution or outcome verification in this increment.
2. **APP-01A.2 — business commands (implemented / CI and review pending):** explicit Dapper Serializable transactions, A/B shared stock + B credit pools, canonical SHA-256 payload hash v1, durable replay/CONFLICT, and reserve/confirm/quota/grant. See [command contract](roadmap/app-01a2-command-transactions.md).
3. **APP-01A.3 — behavior hardening:** rollback after partial effects, two-client contention, Customer/ShipTo/pool mismatches, query-asOf audit, 2-tenant HTTP/API authorization, final-HEAD CI evidence.

Do not mark original Issue #2 checkboxes as complete from schema constraints alone. In particular, **physical stock availability** and **no A/B overlap across different source lines** require APP-01A.2 atomic allocation logic, not merely a database CHECK.

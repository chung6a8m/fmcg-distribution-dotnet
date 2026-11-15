# APP-01A — Integrated model kickoff (AUTHORIZED / NOT_STARTED)

**Sequencing:** Start coding after APP-00A foundation review/merge. This is an implementation backlog, not evidence that APP-01A is done.

Define one transaction/source model per tenant DB: Customer, ShipToSnapshot, SKU, StockLocation, Trip, AOrder/AOrderLine, AReservation, BQuota, BCreditGrant, Command, SourceLine and ledger postings. Add deterministic payload hash/version and `(tenantId,producerId,commandId)` idempotency; same payload replay returns durable outcome, altered payload yields CONFLICT without effect.

Implement `ConfirmAOrder/ReserveAStock` without AR/COGS and grant/quota command guards with no overlap of rights. Enforce actor/tenant, source ownership, decimal exactness and Dapper explicit transaction boundaries. Tests must include two-tenant read/write negative, replay, changed payload, wrong Customer/ShipTo and same-resource concurrency.

APP-01B–E cover manifest/sale/delivery/cash and 91/91 AC-03 checkpoints, not this first slice. Do not lift SQL scratch stored procedures as app persistence.

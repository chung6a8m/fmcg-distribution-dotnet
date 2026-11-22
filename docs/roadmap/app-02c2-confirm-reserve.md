# APP-02C.2 — A Confirm & Reservation

## DEV contract and implementation plan

- **R01.** Create a durable, immutable draft with one or more distinct SKUs in base UoM. The draft freezes each SKU's base unit and rejects late line insertion. Confirm expects that draft ID and its immutable contents, including a base unit that still matches current SKU master data. Confirm materializes the same ID as an integrated AOrder in one SQL commit and presents `CONFIRMED_UNRESERVED`. Warehouse Reserve is a separate command for one line and an explicit positive quantity. It requires the exact frozen Confirm line, policy and matching base unit. Reserve failure preserves the order and its settings. Compute order state from all lines as unreserved, partially reserved, or fully reserved. Preserve historical APP-01 Reserve-before-Confirm commands. Reject C.2 orders in the legacy Load and Delivery paths until their owning increments can consume C.2 provenance.
- **R02 consumption.** Confirm checks Customer, active status, the tenant/Customer ShipTo selection, payload hash and validator version. A superseded registered revision fails; a direct selection needs no Outlet. A unique binding prevents reuse of a selection across Orders. Copy to the existing immutable ShipToSnapshot and retain the C.1 selection ID as provenance.
- **R03.** The LocalDB adapter implements the B.6 decision boundary for trusted synthetic L1 approval fixtures. One seller per synthetic tenant, exact Customer/SKU scope and explicit versions. The newest started version governs. Missing, expired, pending, incomplete or self-approved data denies. Use one SQL UTC instant, a half-open effective interval and an explicit approved seller timezone. DEV fixture v1 uses `Asia/Ho_Chi_Minh`, represented by SQL Server's `SE Asia Standard Time`. Use SKU base UoM/factor 1. Freeze approved unit price, per-unit discount, tax rate, terms days, per-order credit cap, policy record/source version and source hash per line. Round line totals to four decimals with midpoint away from zero. Require one consistent timezone, payment term and per-order cap across lines. The cap is a synthetic per-order validation input. Confirm records zero receivable, revenue, cost or physical movements. Reserve depends on the frozen proof and current warehouse permission, so policy changes alone do not reprice or invalidate an existing order. C.4/C.5 own delivery business date, due-date consumption and reports.
- **R04 in C.2.** For each order line, reserved is the sum of grant less consumed and released across its held A rights. At this increment consumed and released start at zero. Each Reserve commits an independent StockRight, AReservation, `RIGHT_A` source effect and a positive increase in the existing shared StockPool allocation. Allocated A/B occupancy cannot exceed on-hand. Reserve neither moves stock nor consumes delivery entitlement. C.3 owns warehouse occupancy-to-loaded custody and C.4 owns actual delivery consumption and held/authorized release.
- **Authority and recovery.** Sales creates drafts and confirms. Warehouse reserves. Reads and recovery apply current actor grants within the nonce/tenant-bound SQL transaction and recovery is actor-scoped. The loopback DEV launcher provides explicit synthetic access with existing signed bearer auth. Command identity is server actor namespace plus client GUID. Same ID and same input replay the original outcome. Payload conflicts, stale Confirm sources and revoked actors fail closed. Receipts and business writes use the existing BusinessCommand and BusinessSource transaction.

The C.2 launcher activates a dedicated synthetic composition with C.1 and C.2 routes, using the reviewed Vite proxy on loopback port 5116. It issues a short-lived DEV identity with explicit Sales and Warehouse grants to let one browser demonstrate both roles. The server still checks each grant separately. APP-01 effect and outcome routes are fenced from that composition. The DEV policy store starts empty. Only the fixture provisions the approved Customer/SKU combination, maker/checker witness and financial inputs.

## Run the flow

```powershell
npm --prefix web ci
./scripts/Run-App02C2.ps1
```

Open the printed URL and paste the short-lived DEV credential. Use Customer `C2-FIXTURE` to choose a ShipTo from the C.1 screen. In `/sales/orders`, create a draft, Confirm, then hold stock in warehouse `C2-MAIN`. Order and line state show the exact committed reservation. Ctrl+C frees the loopback ports and clears the owned synthetic databases.

```powershell
Push-Location web
npx playwright install chromium
Pop-Location
./scripts/Run-App02C2.ps1 -Verify
```

The automated run uses a full browser, Kestrel and two nonce-owned LocalDB tenants. It writes SQL evidence under `TestResults/app02c2` and JUnit/browser evidence under `web/test-results-app02c2/<run-id>`.

## DEV acceptance

1. Verify API shapes on guarded LocalDB. Check Confirm without reserve, policy failures and ShipTo freshness, then partial and full reservation, insufficient/over-reservation, actor/tenant denial, replay/conflict, transactional rollback and no legacy Load bypass.
2. Reuse the C.1 launcher and browser harness. From the actual web app, create a Customer and ShipTo, create a draft, Confirm and reserve in two steps. Observe each state and independently compare both tenant databases, frozen values and zero posting effects. Fixtures and CI evidence are synthetic only.
3. Exercise full regression, retain execution and artifact IDs and submit one C.2 PR.

# APP-02C.3 Atomic Load Manifest

C0-R04 for Load applies to the dedicated APP-02C DEV runtime. Warehouse Load uses a current Warehouse role and a current writer assignment for the prepared Trip. Each submitted batch posts a distinct immutable manifest. A right can appear once per batch and can be loaded in later batches while the Trip is prepared.

| Quantity | Load rule |
| --- | --- |
| Warehouse-held reservation | Granted less cumulative loaded for an eligible, unconsumed and unreleased C.2 right. A partial batch keeps the remainder allocated in MAIN. |
| Physical custody | Move the explicitly submitted quantity and its current warehouse carrying cost from MAIN to the assigned TRIP location. |
| Delivery entitlement | Preserve the C.2 right and its counters. Delivery, release and trip completion belong to C.4. |

The command compares the operator's observed warehouse-held quantity against the current quantity for every right. A stale line, mixed warehouse batch, insufficient balance or invalid right rejects the entire batch. MAIN stock-pool on-hand and allocated quantities decrease together. Load requires the MAIN pool and physical on-hand quantities to agree. Cost is allocated per SKU using the existing warehouse carrying-cost and rounding rule. The manifest, source, custody ledger, balances and command outcome commit in a single transaction. An exact replay returns the original outcome. A changed payload under the same command ID conflicts. A request with more than 100 lines fails the transport size limit before command acceptance.

DEV acceptance uses a nonce-owned LocalDB fixture with synthetic ShipTo, C.2 Confirm and Reservation. Exercise two partial loads and verify remaining reservation, unchanged entitlement and balanced custody postings in SQL. Force a SQL error during movement posting and check that command, manifest and accounting writes from that attempt roll back. Check competing submissions and exact replay through the API. Physical acceptance and tenant go-live remain separate.

Run `./scripts/Run-App02C3.ps1 -Verify` from the repository root for the Chromium and SQL comparison. The interactive launcher serves `/sales/load` and the prerequisite C.1 and C.2 screens on loopback. The warehouse fixture grants a writer assignment for one synthetic Trip. The verification trigger is installed only in the owned tenant A database when running `-Verify`.

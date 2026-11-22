# APP-02C.1 — Customer, Outlet & Flexible ShipTo

## C0-R02 contract for synthetic DEV

- The existing Customer identity remains the debtor. Outlet belongs to one Customer and is optional on every address. Outlet edits keep the owner fixed. A new owner gets a new association and identity.
- A registered ShipTo is an address identity under one Customer with append-only revisions. Each revision can reference an Outlet belonging to that Customer. Selection requires the current registered revision. A one-off address is entered under a specific Customer with optional Outlet and stored as a single intended order selection. No registered master is created for it.
- The DEV validator v1 explicitly supports country `VN` and synthetic region catalog `HCM`, `HN`. The browser sends country and region explicitly. Address line, locality and recipient are required. Their limits are 200, 120 and 120 UTF-16 code units. Server validation rejects control characters and invalid Unicode, applies NFC, trims and collapses whitespace. Country and region codes must be explicit catalog members. The catalog checks supported scope and field structure. It carries no claim of postal deliverability or live address coverage.
- DEV writes, reads and recovery require a signed sales identity with a current sales grant in the tenant database. Customer and Outlet edits compare SQL rowversion. Registered edits and selections compare revision numbers. The server records tenant, actor, command, UTC capture time, origin, normalized v1 payload and its SHA-256 (UTF-8) on the immutable selection. A master edit leaves previous revisions and selections intact.
- The selection is a C.1 handoff record. At C.2 Confirm, bind it transactionally to at most one tenant/Customer Order and freeze a distinct immutable order ShipToSnapshot with selection provenance. If the selected registered revision has since been superseded, require a fresh selection before Confirm and retain the original selection as history. C.4 consumes the frozen version. A post-Confirm amendment must be a separately authorized, reasoned source with a current version, scoped to identified undelivered quantities and checked against actual delivery inside the transaction. Reject missing approval, stale versions and already-delivered scope. C.1 exposes no post-Confirm amendment command or Order/Delivery transitions. An eligible future amendment uses a new snapshot/version and retains all original sources.

## Implementation and acceptance plan

Use a forward-only FluentMigrator migration, existing tenant and sales grant checks, and a single SQL transaction for each command and its outcome. Extend the existing React app at `/sales/customers` using shared native controls and theme. Provide a guarded LocalDB launcher and independently query SQL after browser writes. Use one increment for this scope.

All routes are under `/api/synthetic/app02c1`. `GET /session` returns the authenticated tenant, actor and owned DEV session ID. `GET /customers` and `GET /customers/{id}` serve list/detail. `POST /customers/save`, `/outlets/save`, `/registered-ship-tos/save` and `/ship-to-selections` take a GUID command ID, typed content and the applicable version. Read `GET /commands/{id}` for the durable actor-owned outcome. Reusing an ID with a changed typed payload conflicts. Applied and business-rejected outcomes are durable. The browser can look up an unknown command or retry the original payload within the same browser tab and owned DEV database session. Offline writes are disabled.

At wide viewports the list sits beside the detail. At narrow viewports the workspace stacks them in one column and uses 48px controls. The edit form uses native labels, keyboard navigation and shared Light/Dark/System tokens. It preserves the submitted request while the outcome is uncertain and keeps the credential in page memory. A recorded selection shows address provenance, selected revision and UTC capture time. Its Outlet label, when present, comes from current master data.

| Case | Expected result |
|---|---|
| C1 without Outlet, direct address with excess whitespace | One Customer and one immutable DIRECT selection in A. Normalized data in SQL. No Outlet required and no registered master created. |
| Customer with Outlet and registered address | Outlet scoped to its owner. Select registered revision 1, update to 2, and retain both versions and the original selected address. |
| Wrong Customer, wrong tenant, revoked or wrong role, invalid address, invalid/expired token, unsafe target | No forbidden SQL changes. Reject stale rowversion/revision and attempted mutation of frozen SQL rows. |
| Duplicate, changed payload, concurrent edit and unknown response | One committed effect for an identical identity, conflict for changed content, and one winner for two edits of the same version. Recover the durable outcome for a lost response. |
| Scoped UI | Load SQL-backed list/detail, save and reread Customer, Outlet and ShipTo. Verify empty, conflict and offline states, theme and narrow view. |
| Confirm and Delivery | C.2 and C.4 own order binding, amendment state, and delivered-scope negatives. AC-02 physical witness belongs to physical acceptance. |

## Run locally

```powershell
npm --prefix web ci
./scripts/Run-App02C1.ps1
```

Open the loopback URL printed by the script and paste the short-lived tenant-a credential from the clipboard. Ctrl+C stops the processes, clears that value if it is still in the clipboard, and removes the two owned DEV databases. Each launch starts fresh. A cleanup attempt that cannot verify instance, database name, both ownership markers and tenant binding fails closed and reports the session ID.

```powershell
Push-Location web
npx playwright install chromium
Pop-Location
./scripts/Run-App02C1.ps1 -Verify
```

The verification run builds the API, provisions new synthetic tenant databases and starts Kestrel and Vite on loopback. Playwright signs in against the server and drives the existing React app. An independent SQL query verifies both databases and records before/after expectations, exact API build identity, browser fixture hash and JUnit hash under `TestResults/app02c1`. Browser JUnit and sanitized screenshots are under `web/test-results-app02c1`. Runs marked `+working-tree` are exploratory and do not attest a clean source commit.

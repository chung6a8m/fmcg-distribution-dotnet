# APP-02C — DEV implementation handoff

> **Standing scope.** The [Owner DEV-first decision, 22/11/2026](../decisions/owner-app02c-dev-first-2026-11-22.md) authorizes C.1–C.5 source, forward DB migrations, UI and dedicated synthetic DEV integrated runs. Current C.1 source, commands and limits are in the [local DEV closeout](../closeout/2026-11-22-app-02c1-dev.md). The source inventory and C.1 sequence below describe the starting baseline. Physical PREPROD retains its own acceptance requirements.

[C roadmap](app-02c-a-online-e2e.md) · [C.0 contract](app-02c0-a-online-e2e-contract.md) · [Operations debt](app-02c-operations-debt.md).

## 1. Start here

1. Read the new Owner decision and this handoff before applying historical C.0 timing restrictions. C.1–C.5 do not require a fresh blanket Owner authorization per increment within the approved DEV scope.
2. Read [AGENTS.md](../../AGENTS.md), the locked `business/` baseline and `business.lock.json`. Preserve tenant isolation, server authority, transaction integrity, source identity and idempotency; never edit the baseline to pass a test.
3. Read [C.0 contract §2.1 and §8](app-02c0-a-online-e2e-contract.md): existing APP-01 behavior is not automatically the target C behavior. Map each applicable C0-R item into its owning increment.
4. Read [UI/UX README](../ui-ux/README.md), [decisions](../ui-ux/decisions.md), [Owner UI baseline](../decisions/owner-uiux-2026-11-20.md), [components](../ui-ux/components.md) and [screen template](../ui-ux/templates/screen-spec.md). Reuse adopted UI-01–03 foundations and interaction patterns.
5. Prepare a small C.1 implementation plan, contract decisions and acceptance matrix before code. Routine schema/API/component choices belong to the implementer and reviewer. Ask the Owner only about a meaningful unresolved business choice; identify the affected behavior and continue independent work.

Use the existing repository and `web/` application. UI-04 [PR #86](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/86) is an **OPEN docs-only proposal**, may need rebasing, and is not a start prerequisite or merge dependency for C.1. Do not wait for it or assume it supplies implemented business screens.

## 2. Existing source to inspect

Paths below were inspected at `main` `6041cb3`; re-read the relevant code at the implementation branch HEAD.

| Area | Exact existing paths | What is available / what remains |
|---|---|---|
| Synthetic API | `src/FmcgDms.Api/Program.cs`, `Authentication/SyntheticAuth.cs`, `appsettings.json` under that project | Signed synthetic roles and actor/tenant claims; effect routes exist only in Development/IntegrationTest with synthetic auth enabled. Default auth is disabled. There are no C.1 Customer/Outlet/ShipTo management routes. |
| SQL target and migration guards | `src/FmcgDms.Api/Tenancy/TenantConnections.cs`; `src/FmcgDms.Api/Persistence/ScratchMigrator.cs` | Only `tenant-a`/`tenant-b`, LocalDB, generated `Fmcg_App00_<nonce>_[AB]`, matching ownership marker and tenant binding are admitted. |
| Existing source model | `src/FmcgDms.Api/Persistence/AppSchema.cs`, `IntegratedSchema.cs`, `BusinessCommandContracts.cs` | Customer and a basic ShipToSnapshot already exist; no full Outlet/registered ShipTo lifecycle. Extend through reviewed forward migrations, preserving existing references and tests. |
| Command integrity | `src/FmcgDms.Api/Persistence/App01a2CommandService.cs`, `App01e4OutcomeLookupService.cs` | Server identity, durable command outcome, canonical payload hash, transaction-owned command lock and serializable transaction patterns. Review reuse; do not copy old Confirm/Reserve semantics into C unchanged. |
| SQL fixture and real HTTP process | `tests/FmcgDms.Api.Tests/SqlScratchFixture.cs`, `App00aTests.cs`, `App01e4TcpRestartHarness.cs` | Two owned scratch databases, signed test tokens, refusal/isolation tests, and an actual loopback API process/restart harness. Browser orchestration for C.1 still needs implementation. |
| Existing demonstration | `scripts/Start-Demo.ps1`; `src/FmcgDms.Demo/Program.cs`, `DemoRun.cs`, `DemoScenario.cs` | A separate Windows/LocalDB AC-03 demonstration uses real SQL and server-rendered pages. Its run ownership/lifecycle is a reference; it does not supply the requested React C.1 module. |
| React composition | `web/src/main.tsx`, `web/vite.config.ts`, `web/playwright.config.ts` | Existing health and UI Lab routes; Vite DEV proxies `/api` and `/health` to port 5116. Current Playwright configuration starts preview only, not the API or SQL fixture lifecycle. |
| Shared UI | `web/src/ui/controls.tsx`, `LookupField.tsx`, `EditableGrid.tsx`, `theme.ts`; `web/src/style.css`; `web/public/theme-runtime.js` | Reuse controls, lookup accessibility, tokens, self-hosted Noto Sans and Light/Dark/System. UI Lab fixtures are examples, not server authority. |
| Current UI recovery examples | `web/src/ui/SourceCommandDemo.tsx`, `command-recovery.ts`; `web/e2e/ui-command-recovery.spec.ts`, `ui-data-entry.spec.ts` | Deliberate client-side simulations. Their sessionStorage, permission selections and synthetic results cannot certify durable C commands. |
| PREPROD boundary | `src/FmcgDms.Preprod.Api/Program.cs`, `src/FmcgDms.Preprod.Worker/Program.cs`, `src/FmcgDms.Preprod.Runtime/PreprodStartupGuard.cs` | Separate binaries remain `FOUNDATION_ONLY / NO_GO`; startup requires HTTPS Vault and rejects synthetic/direct tenant DB configuration and business activation. Do not repurpose them to avoid DEV work. |

## 3. C.1 implementation sequence

### Step A — close the applicable ShipTo contract inside C.1

Treat **C0-R02 as the first contract task of C.1**, not a blanket prohibition on starting C.1. Record the reviewed data/API/UI contract before implementing the behavior that depends on it:

- Customer remains the debtor; Outlet is optional, with 0..n outlets per Customer.
- Distinguish editable master records, registered ShipTo, validated order-specific address and immutable source snapshots.
- Resolve minimum address fields, country/region validation, normalization, identity/provenance and which authorized actor may enter a one-off address.
- Define version/concurrency checks, authorized amendment eligibility, and rejection of stale or already-delivered amendments. Historical snapshots must not be rewritten.
- Preserve downstream Confirm snapshot obligations without adding C.2 Confirm or C.4 Delivery prematurely. Specify C.1's independently testable versioning behavior and the later consumer tests.

Do not invent address defaults, amendment rights, commercial policies, credit limits, tax values, timezone or payment terms. A real ambiguity limits its affected subtask; it does not reinstate physical B acceptance as the DEV entry gate. Record open decisions and the exact dependent cases.

### Step B — implement the smallest authoritative API/SQL slice

- Add forward migrations for the reviewed Customer/Outlet/ShipTo model and immutable versions; preserve tenant-scoped foreign keys and existing source references.
- Add actual queries and commands for the accepted C.1 scope, with server-side validation, role checks, server-derived tenant/actor and optimistic concurrency where applicable.
- Define command identity, payload-conflict, retry and durable outcome behavior for each write. Keep protected mutations and their audit/outcome records atomic; a failed command must not leave a partial master/version update.
- Add tenant A/B positive and refusal tests using actual SQL. Read SQL independently after HTTP results; an `OK` response or browser state is insufficient.
- Make API/DTO names part of the reviewed C.1 contract. This document intentionally does not advertise routes or signatures that have not been implemented.

### Step C — compose a runnable, dedicated DEV session

The initial execution target is **Windows SQL Server LocalDB with newly generated, nonce-owned synthetic databases**, using the existing guards. No production or live tenant data is permitted.

- Own the create → migrate → seed → start API → start existing React app → test → stop → guarded cleanup lifecycle. Supply a documented repeatable launcher and failure cleanup in C.1; no working C.1 launcher exists at this baseline.
- Reuse the lifecycle/signing patterns in `SqlScratchFixture` and the loopback process patterns in `App01e4TcpRestartHarness`; signed, short-lived synthetic identities must still pass server authentication/authorization. Provision interactive DEV credentials outside source; do not adopt the existing static test signing key as an interactive runtime secret. Keep signing material out of browser bundles and redact credentials/tokens from evidence.
- Define the browser's controlled synthetic session bootstrap and API origin/proxy configuration. Do not trust a browser-selected tenant, role, actor or connection string as authorization. Test anonymous, invalid/expired token, wrong-role and cross-tenant requests.
- Keep instance, exact generated database name, ownership nonce and `AppTenantBinding` checks for migration, open, reset and DROP. Never reuse an unowned database or broaden a regex/allowlist merely to connect successfully.
- A separate DEV host/composition is an implementation option only after review of its admission, identity and target boundaries, with negative target-refusal tests. It must preserve the existing synthetic fences; an environment label alone is insufficient.
- Docker SQL, VPS and PREPROD are not drop-in targets for `OpenSyntheticScratchAsync`. Do not remove its LocalDB restriction or disable auth. Any later target adapter needs its own reviewed design and the appropriate scoped authorization.

**Vault and physical B artifacts are not required to start this LocalDB DEV work.** The existing synthetic host does not use Vault. Start the Worker only when a feature needs it. Conversely, the PREPROD host's Vault/TLS/admission guards are not optional: leave them intact. B.3/B.4/B.5/B.6 libraries do not automatically provide a composed C runtime.

### Step D — deliver the minimum real C.1 screen

- Add a Customer list/detail and the scoped Outlet/ShipTo editing/selection flow to the existing React app, using shared controls and the C.1 screen contract.
- Load and save through the actual C.1 API and SQL databases. Show server validation, loading, empty, forbidden, conflict and failure states; do not substitute local fixtures for the accepted integrated path.
- Demonstrate Customer without Outlet and registered/direct ShipTo according to the closed contract. Display source/version context needed to distinguish master changes from frozen snapshots.
- Disable business writes while offline and provide safe recovery of an uncertain outcome with the original command identity. Reuse UI-03's interaction principles, not its simulated outcome authority.
- Cover keyboard/focus, narrow layout and Light/Dark behavior within the adopted UI scope. Preserve the scoped 125% exception; do not claim physical device acceptance.

C.1 cannot close with API/DB-only work or UI contract stubs. C.5 expands and unifies the workspace/reporting; it does not postpone all usable UI until the end.

## 4. Baseline verification commands

These commands are instructions for an authorized Windows DEV/CI host, **not a claim that they were run for C.1**. Use .NET SDK `10.0.404` and Node `>=26.13.1 <27`; inspect `global.json`, `web/package.json` and `.github/workflows/app00a.yml` for the pinned baseline. From repository root in PowerShell:

```powershell
git status --short
git rev-parse HEAD
dotnet --version
node --version
./scripts/Verify-Business.ps1
python tools/validate_business.py
sqllocaldb start MSSQLLocalDB
sqlcmd -S '(localdb)\MSSQLLocalDB' -E -Q 'SELECT @@VERSION AS Version'
$env:APP00A_SQL_SERVER = '(localdb)\MSSQLLocalDB'
dotnet restore FmcgDms.slnx
dotnet build FmcgDms.slnx -c Release --no-restore
dotnet test --solution FmcgDms.slnx -c Release --no-build --minimum-expected-tests 296 -- --report-trx --coverage --coverage-output-format cobertura
```

The workflow includes additional run/source identity variables and SQL evidence verifiers; retain them for CI acceptance. Update expected coverage/counts for new C tests rather than using the historical minimum as proof of completeness. `scripts/Run-App00A.ps1` is a convenience baseline runner, not the complete C.1 acceptance suite.

```powershell
Push-Location web
try {
    npm ci --no-audit --no-fund
} finally { Pop-Location }
node --test web/tests/theme.test.mjs web/tests/ui-02.test.mjs web/tests/ui-03.test.mjs
node docs/ui-ux/tools/sync-tokens.mjs --check
node docs/ui-ux/tools/check-tailwind.mjs
Push-Location web
try {
    npm audit --omit=dev --audit-level=low
    npm audit --audit-level=moderate
    npm run test:dependencies
    npm run test
    npm run build
    npx playwright install chromium
    npm run test:e2e
    npm run test:pwa-build
} finally { Pop-Location }
```

Stop and report any nonzero exit; a pasted sequence is not a fail-fast runner. Existing browser tests remain baseline regressions: they do not certify C.1 UI→API→SQL integration. C.1 must add its dedicated runtime orchestration and integrated browser cases, record the exact new commands, and execute them against the same reviewed source HEAD.

For LocalDB startup failures, use `scripts/Collect-LocalDbDiagnostics.ps1` and record `BLOCKED` with the actual cause; do not substitute a remote SQL target or fabricate a passing SQL run.

## 5. Acceptance evidence for each DEV increment

Before counting an acceptance run, review and freeze that increment's applicable C0-R contracts, synthetic fixture definitions, independent expected SQL values, negatives and fixture hashes. Apply C0-R06 to the increment's cases; do not require the full C.6 physical matrix merely to begin development or exploratory runs.

For C.1, require reviewed cases for:

- Customer without Outlet; scoped Outlet/ShipTo associations; accepted registered/direct address paths and rejected invalid addresses.
- Unauthorized, wrong-role and A→B read/write attempts, forged tenant selection and misbound/unsafe SQL targets, with no forbidden SQL changes.
- Master changes versus immutable snapshots; stale version, disallowed/already-delivered amendment and direct snapshot mutation negatives for the behavior covered by C.1.
- Replay without duplication, changed payload under the same identity, concurrent conflicting edits, rollback and uncertain-response recovery.
- Actual React interaction → authenticated HTTP → committed SQL, followed by independent SQL readback and peer-tenant checks; offline write refusal.

Record source HEAD, build identity, run ID, environment, synthetic tenant/database identity, fixture hash, expected/actual/difference, commands and outcomes, SQL before/after, browser/HTTP evidence, review disposition and artifact hashes. Never place PII, signing keys, bearer tokens or connection secrets in evidence.

Preserve relevant APP-01/02A, APP-00A, security audit, UI/PWA and B-boundary regressions. Required failures/skips remain failures/skips with a scoped disposition, not inferred PASS. Distinguish `IMPLEMENTED`, scoped `DEV_INTEGRATION_VERIFIED` and physical acceptance. C.1 verification does not award C.2–C.6, `PREPROD_FOUNDATION_VERIFIED`, EV/P0 or go-live status.

## 6. Keep the next increments executable

| Owner increment | Required contract/composition work before its dependent behavior and acceptance |
|---|---|
| C.1 | C0-R02; controlled browser synthetic identity/session, real API/SQL lifecycle, Customer/Outlet/ShipTo persistence and minimum working UI. |
| C.2 | C0-R01/R03 and applicable R04; approved Confirm→Reserve states, policy selection/frozen commercial snapshots and authority/tenant checks in the protected transaction. Define synthetic DEV policy fixtures explicitly; required `UNSET` denies. |
| C.3 | C0-R04; reservation→load source binding, partial/repeated load constraints, warehouse versus loaded remainder, concurrency and atomic stock/value movement with no AR/COGS. |
| C.4 | Applicable R02/R03/R04; one delivery consumption, held remainder, immutable Confirm terms, seller business-date/due calculation and source-bound AR/COGS. Current actor/tenant authority still applies. |
| C.5 | Extend real screens and SQL-authoritative reports, `asOf`/pending/source coverage, permission filtering and recovery; do not create a second ledger or treat missing data as zero. |

Each increment retains review, applicable frozen expectations, exact-source tests and an honest closeout under the standing DEV authorization. Runtime auth/tenant/policy bridges must be delivered by the owning increment before its integrated acceptance, not silently assumed from B library availability. Track outstanding deployment, Vault/real identity, operational and physical witnesses in the [operations debt register](app-02c-operations-debt.md); these retain their own future gates without blocking this scoped DEV start.

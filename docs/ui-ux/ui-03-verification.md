# UI-03 — Source/command recovery verification matrix

**Status: `MERGED / UI03_100_PERCENT_CI_VERIFIED` — `SYNTHETIC_NONPRODUCTION / 100_PERCENT_BROWSER_CI_ONLY`.** [Implementation PR #84](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84) merged with source `dcd2870b5e6b4f4f90ee579ec9ec30f4267ac2c9` and merge `f17397e4de0912c0d6551eb9bba0ea1f0a557b62`. This is evidence for an isolated UI Lab, **not** production source-bound acceptance. See [post-merge closeout](../closeout/2026-11-20-ui-03.md).

## Reproduce (Node 26.13.1, repository lockfile)

From repository root:
- node docs/ui-ux/tools/sync-tokens.mjs --check
- node docs/ui-ux/tools/check-tailwind.mjs

From web/ after npm ci:
- npm test (includes UI-02 parser and UI-03 reducer)
- npm run build
- npm run test:pwa-build
- npx playwright test e2e/ui-command-recovery.spec.ts
- npm run test:e2e (all UI-01/02/theme/health/PWA tests)

Existing workflows also run .NET/SQL and security gates. Record actual source SHA, workflow/check run IDs, failures and artifacts; do not inherit previous PR success.

| Gate | Required observable behavior |
|---|---|
| UI03-F01 | Link and isolated Lab route, zero business fetch/XHR/write |
| UI03-S01 | Type/scope/asOf, known zero within 2/3, missing source and partial total |
| UI03-S02 | Complete known 0, no-data, N unknown and stale snapshot maintain distinct meanings |
| UI03-P01 | Read-only/denied permissions redact source values, command phase/outcome labels, scenario choices and history; block unauthorized actions while keeping only opaque request ID |
| UI03-P02 | Revoking read after a simulated POSTED outcome hides its business status and history while keeping only the opaque request ID |
| UI03-C01 | Preflight invalidates on quantity/version/permission/network; never implicitly submits |
| UI03-C02 | Frozen identity and payload; duplicate send rejected; PENDING/UNKNOWN cannot mint alternative ID |
| UI03-C03 | ACK not POSTED; waiting not complete; no inferred invoicing/ledger/financial balance |
| UI03-C04 | Lost ACK to original-ID lookup; no-result stays UNKNOWN; same-tab session recovery |
| UI03-C05 | Rejected/conflict and blocking of new-ID bypass; no silent retry/offline queue |
| UI03-C06 | Invalid present session (including contradictory UNKNOWN + ACK) cannot silently mint a new request; preserve invalid snapshot and block send |
| UI03-C07 | Browser denies session storage writes: warn explicitly, keep current in-memory fixture only |
| UI03-C08 | Contradictory UNKNOWN/intake ACK persisted snapshot cannot be restored as valid |
| UI03-C09 | Client-only checked/preflight state is invalidated after reload; draft buffer/ID remain, explicit recheck required |
| UI03-V01 | Dark, desktop 1440 and Field 320 without overflow and field target sizing |
| UI03-V02 | Light/System, OS theme changes, keyboard Tab/focus and Enter in input does not submit |
| UI03-R01 | UI-01/02, PWA, SQL/.NET, tenant/security regressions and independent final-HEAD review |

**Observed final-source verification (not inherited from round 1):** Reviewed source `dcd2870b5e6b4f4f90ee579ec9ec30f4267ac2c9`; [APP-00A foundation run #44769327935](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44769327935), [bounded web run #44769328083](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44769328083), [security run #44769327982](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44769327982), and seven other applicable workflow runs: **10/10 SUCCESS, 20/20 jobs SUCCESS, 0 pending/failed**. Playwright **51/51 PASS** (including UI-03 permission, UNKNOWN/ACK, session restore and keyboard cases), Node unit tests, TypeScript/Vite, Tailwind, PWA and applicable .NET/SQL regressions PASS. Merge `f17397e4de0912c0d6551eb9bba0ea1f0a557b62` has **zero changed files compared with reviewed source**; this is source-HEAD CI, not a separate merge-SHA run. [Review round 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84#pullrequestreview-5995817580) · [post-merge closeout](../closeout/2026-11-20-ui-03.md).

**Explicitly unverified without further evidence:** physical devices/IME/screen reader, browser zoom 125% for UI-03, actual PREPROD, API, server permission/identity, production P0, durable inbox/outbox, invoice/accounting and recovery across devices. The APP-00B/C 125% historical exception is not extended to UI-03.

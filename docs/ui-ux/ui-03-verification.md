# UI-03 — Source/command recovery verification matrix

**Status:** IMPLEMENTATION_PR_REMEDIATION / FINAL_HEAD_CI_RECHECK_PENDING. This is synthetic/nonproduction UI Lab evidence only, not source-bound acceptance. Do not claim success until checks have completed on the exact final PR source HEAD.

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
| UI03-P01 | Read-only/denied permissions redact source values, scenario labels and history; block unauthorized actions |
| UI03-C01 | Preflight invalidates on quantity/version/permission/network; never implicitly submits |
| UI03-C02 | Frozen identity and payload; duplicate send rejected; PENDING/UNKNOWN cannot mint alternative ID |
| UI03-C03 | ACK not POSTED; waiting not complete; no inferred invoicing/ledger/financial balance |
| UI03-C04 | Lost ACK to original-ID lookup; no-result stays UNKNOWN; same-tab session recovery |
| UI03-C05 | Rejected/conflict and blocking of new-ID bypass; no silent retry/offline queue |
| UI03-C06 | Invalid present session cannot silently mint a new request; preserve invalid snapshot and block send |
| UI03-C07 | Browser denies session storage writes: warn explicitly, keep current in-memory fixture only |
| UI03-V01 | Dark, desktop 1440 and Field 320 without overflow and field target sizing |
| UI03-V02 | Light/System, OS theme changes, keyboard Tab/focus and Enter in input does not submit |
| UI03-R01 | UI-01/02, PWA, SQL/.NET, tenant/security regressions and independent final-HEAD review |

**Explicitly unverified without further evidence:** physical devices/IME/screen reader, browser zoom 125% for UI-03, actual PREPROD, API, server permission/identity, production P0, durable inbox/outbox, invoice/accounting and recovery across devices. The APP-00B/C 125% historical exception is not extended to UI-03.

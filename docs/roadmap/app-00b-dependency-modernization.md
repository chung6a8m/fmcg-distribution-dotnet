# APP-00B — Dependency Modernization & Toolchain Alignment

**Owner decision:** approved 2026-11-16, before APP-01A. Scope is strictly tooling, test infrastructure and CSS baseline for synthetic/nonproduction application foundation. **Do not modify** immutable `business/`, `business.lock.json`, tenant routing, scratch guard, production authorization or business oracles.

## Order, branches and acceptance criteria

| PR | Branch | Scope | Exit criteria |
|---|---|---|---|
| A | `feature/app-00b-a-toolchain` | Pin SDK 10.0.404, update SqlClient 6.1.8 and PKCS 10.0.15; Node 24 + Node24-compatible GitHub Actions, Dependabot weekly without auto-merge | .NET restore/build warnings-as-errors; 9/9 xUnit v2 LocalDB tests; frontend 4/4 Node tests and PWA build; unchanged business tree; CI PASS on HEAD |
| B | `feature/app-00b-b-xunit-v3` | xunit.v3 4.0.3, MTP v2, executable test project, fixture lifecycle to ValueTask, TRX + coverage upload | No VSTest-only flags/packages, test discovery >= 9, same isolation and guard tests PASS, TRX + Cobertura artifacts present, CI PASS on HEAD |
| C | `feature/app-00b-c-tailwind-v4` | Tailwind CSS v4 Vite plugin, CSS-first compact styles, Node 24 lockfile alignment; retain React 19/Vite 8 online-first PWA | `npm ci` from committed lock, 4/4 health contract tests, TS/Vite build, visual check of compact layout + PWA update prompt, CI PASS on HEAD |

Branches B and C may be stacked against A and B during development. **Merge sequentially A → B → C only after review and verified final HEAD of each PR.** Block APP-01A implementation until all three are reviewed and merged. Do not infer production, EV/P0 or tenant go-live acceptance.

## Version policy

- Runtime family remains `net10.0` LTS; SDK fixed to `10.0.404` with `latestPatch` to avoid unreviewed feature-band changes.
- Align Microsoft framework packages with `10.0.x`; do not move to .NET 11 packages. Keep Dapper, FluentMigrator, React, Vite and TypeScript major versions unless a confirmed issue requires a change.
- SqlClient stays on supported `6.1.x` patch line for this round rather than jumping to major 7, to minimize database transport risk.
- xUnit v3 uses its native Microsoft Testing Platform v2 integration; preserve LocalDB A/B isolation assertions, negative tests and test evidence; do not silently run zero tests.
- Frontend package versions must be exact and `web/package-lock.json` committed. Tailwind v4 is a new CSS dependency, not a v3 migration. Retain online-first behavior and user-confirmed service-worker updates.
- GitHub Actions use current Node24-compatible major releases (checkout v7, setup-dotnet v6, setup-node v7, upload-artifact v7); upgrade is part of PR A rather than retaining deprecated Node20 action runtimes.
- Dependabot proposes weekly grouped patch updates; manual review and CI are still required. Never auto-merge dependency PRs.

## Verification / evidence

For each PR, store the last-HEAD CI URL, runtime/package versions, test counts, artifact names and any blockers in its PR description. Local checks must not be recorded as PASS unless run. Runtime SQL scratch tests use **LocalDB only** with the existing nonce/ownership guard; do not target real tenant DBs. APP-00B closure requires resolving review findings and recording final evidence separately from APP-01A work.

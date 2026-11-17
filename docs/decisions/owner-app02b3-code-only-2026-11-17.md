# OWN-APP02B3-CI-01 — APP-02B.3 Code-only Merge Exception

**Owner decision: APPROVED, 2026-11-17.** The Owner explicitly authorized **code-only merge** for APP-02B.3 in PR #51, requested the maximum useful GitHub-hosted CI coverage, and directed that missing real PREPROD infrastructure evidence be tracked with GitHub Issues. This is a **separate B.3 exception**, not an extension of B.2's EPHEMERAL_CI_RUNTIME_TESTED exception. It permits integrating bounded source, **not** verification of physical PREPROD or authorization to deploy it.

## Authorized source integration

- Merge the independently reviewed, PREPROD-only Identity/JWT/opaque refresh/session feature behind default-OFF Identity, FOUNDATION_ONLY / NO_GO, with no business/admin/synthetic routes, tenant/role grants or provider access.
- Include versioned, transactional, idempotent SQL Server Identity DDL as reviewed **source**, with explicit **operator/DBA-approved** PREPROD migration required separately.
- Use disposable, nonce-owned Windows GitHub-hosted SQL Server LocalDB, and Linux TestServer / Vault / Docker CI as legitimate *engineering* evidence, with source SHA, TestRunId, workflow SHA, negative expected/actual, redacted artifact and retained regressions.

## Exact code-only merge gates

1. Zero unresolved blocking **source findings**, independent technical review of complete final diff and B.1 threat/acceptance obligations.
2. PR #51 APP-02B.3 workflow **both** jobs SUCCESS at the exact same source HEAD: Ubuntu HTTP JwtBearer/origin/cookie/JWT/replay negatives + B.2 runtime tests, and Windows **actual SQL Server engine (LocalDB)** migration apply/idempotent replay/partial-or-unknown-schema denial + independent two-client concurrent refresh and authoritative SQL readback. Both artifacts must bind to final source HEAD.
3. PR #51 APP-02B.2 Linux ephemeral Compose/Vault/TLS negative workflow SUCCESS at the exact same source HEAD.
4. PR #51 APP-00A Windows LocalDB T0–T6 APP-01E.5 / APP-02A.4 synthetic SQL oracle and PWA regressions SUCCESS at the exact same source HEAD. Business Baseline files remain unchanged.
5. Record final provenance, code review and open infrastructure debt in the PR. Any new source commit invalidates earlier review/CI acceptance.

**Maximum post-merge label:** MERGED / B3_GITHUB_HOSTED_CI_TESTED / REAL_PREPROD_IDENTITY_NOT_VERIFIED. B.2 stays MERGED / EPHEMERAL_CI_RUNTIME_TESTED / REAL_PREPROD_RUNTIME_NOT_VERIFIED.

## Not accepted or authorized

- No IDENTITY_VERIFIED, PREPROD_RUNTIME_VERIFIED, PREPROD_FOUNDATION_VERIFIED, B.1 G-B01..05/30-case PASS, production/Pilot/Go-Live, APP-02C/D/E, real AC-11/EV-08 or five original production P0 gate PASS.
- No physical/operator PREPROD SQL Identity, migration execution, key trust chain, B.4 provisioning, B.5 independent monotonic epoch, B.7 restore fence, B.8 immutable audit or tenant business grants.
- AI code review does not assert human Security Approver independence or PREPROD operator attestation. GitHub-hosted LocalDB is **SQL Server CI only**, never physical PREPROD.

## Outstanding PREPROD issues (must not be auto-closed by CI)

- [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52) — Identity SQL/migration/operator provisioning and physical refresh witness.
- [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53) — independent security epoch + restore-safe fence.
- [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54) — real-host RUNTIME-01/02, TLS/Vault/firewall.
- [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55) — immutable security audit, key governance and MFA.
- [#56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56) — physical B.1 30-case G-B01..05 gates.

Original authority: [10/10 Owner decisions](owner-app02b-2026-11-17.md), [B.1 architecture](../roadmap/app-02b1-architecture-security-contract.md), [threat model](../security/app-02b1-threat-model.md), [30-case contract](../security/app-02b1-acceptance-evidence.md). This decision changes **only** the code merge acceptance threshold for PR #51.

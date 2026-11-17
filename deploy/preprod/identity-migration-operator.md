# APP-02B.3 — Operator-run Identity SQL migration (PREPROD only)

**Status: SOURCE IMPLEMENTED; OPERATOR INSTALLATION NOT AUTHORIZED OR VERIFIED.**
The Owner's 2026-11-17 code-only merge approval is **not** authorization to run this script against a real environment. This document is an execution contract for a later approved B.4 operator change.

## Artifact and boundaries

- Reviewed source: src/FmcgDms.Preprod.Identity/Migrations/20261117_001_identity_control_plane.sql
- Version ledger row: B3_20261117_001 in dbo.__PreprodIdentitySchemaHistory.
- SQL Server control-plane Identity database only; **not** a tenant DB or synthetic LocalDB. Script has no seed users, token material, admin principals, Vault secrets or tenant membership.
- API startup verifies all nine expected Identity tables and the exact version ledger; it never runs schema creation, applies migrations, seeds users or elevates SQL permissions.
- The migration is an **atomic one-batch transaction** with fail-closed refusal of partial, unknown-version, or system database; applying it again with a matching version is an idempotent checked no-op. The SQL Server engine/DDL compatibility is tested in disposable Windows LocalDB GitHub-hosted CI, not operator PREPROD.

## Required change approval / execution (not performed in PR #51)

1. Obtain a named DBA/security approval with change ID and authorized private PREPROD control SQL target. Record immutable source HEAD, migration script SHA-256, planned DB name/schema, backup/rollback method, trusted certificate, privileged migration principal, expected key/secret versions and an independent reviewer. **Do not** enable Identity routes.
2. Ensure a newly provisioned control-plane database, isolated from tenant databases, is bound in the approved Registry and backed up. Use an operator-held least-privilege DDL credential only for the migration, never the API runtime account. Public SQL/Worker/Vault ports remain denied.
3. From a securely controlled machine with private SQL connectivity, review the script hash and use a SQL Server-aware batch tool such as:
   - sqlcmd -S <approved-private-server> -d <approved-control-database> -E -b -i src/FmcgDms.Preprod.Identity/Migrations/20261117_001_identity_control_plane.sql
   - Substitute the approved operator-owned principal/secure credential method, not GitHub Actions environment variables.
   - Never run on master/model/msdb/tempdb; never run under API startup or a tenant worker.
4. Independently verify exactly one ledger entry B3_20261117_001, nine intended dbo Identity tables, unique refresh-token hash index, actual schema/permission fingerprint, zero seeded identities and denial when the same DB has mismatched version/partial schema. Re-run the same script to demonstrate idempotent behavior only with approval.
5. Set the dedicated API-scoped Vault SQL connection with genuine TLS trust (no TrustServerCertificate or Encrypt downgrade), ensure Worker cannot read the secret, confirm readiness/fail-closed tests, and only then consider separately approved PREPROD Identity enablement after B.4/B.5 gates. **No tenant business action** follows from successful auth.
6. Record change UTC, exact source and image SHA/digests, migration hash, SQL expected/actual and negative outcome, independent reviewer, redacted audit and incident/rollback decision. Attach results to [Issue #52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52).

## What GitHub-hosted CI proves (and does not)

Windows LocalDB job applies this script twice against a newly created random database, rejects a pre-existing partial schema and unknown ledger version, uses two concurrent SQL connections to refresh the same grant and reads back the committed family revoke/epoch. A source-bound redacted witness is uploaded. This is a genuine SQL Server **CI engineering gate**, not a PREPROD operator deployment, not independently monotonic restore fencing and not a G-B01..05/30-case PASS.

Outstanding issues: [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52), [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53), [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54), [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55), [#56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56).

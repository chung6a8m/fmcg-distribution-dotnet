# APP-02B.5 — PREPROD Authorization DBA Operator Runbook (SOURCE ONLY)

**STOP / NO-GO:** This is not an instruction to deploy to a real PREPROD host today. Three migrations exist as un-applied source. Issue #53 (monotonic revocation source after restore), independently approved RBAC enrollment and #59 audit remain blocking; existing API/Worker are NO_GO. No business routes, production secrets or pilot.

## Preconditions for a future Owner-authorized operator exercise

1. Record independent DBA/Security approval, operator identity, actual PREPROD environment IDs, expected SQL server name, source HEAD, exact three migration SHA256 digests, B.3/B.4 applied schema version and approved Vault ACLs. Confirm B.3 Identity, B.4 Registry and the independent B.5 fence all live on an explicitly approved SQL Server instance for same-connection cross-database locks. No LocalDB for actual PREPROD.
2. Select a distinct, separately recovered and protected **B.5 fence authority database**. Never restore its monotonic high watermark from any tenant or Control Plane PITR snapshot. Deny startup if the external fence cannot be checked or integrity is unknown.
3. DBA applies 001 independent_fence to the new fence database, 002 identity_guard only to the B.3 Identity database, 003 registry_guard only to the B.4 Registry database. Reapply is idempotent, unknown versions deny. No API boot DDL or automatic migration.
4. Only after explicit approved runtime principal decisions, grant b5_runtime EXEC-only on fence and b5_session_runtime/b5_registry_runtime EXEC-only on their respective host databases. Grant b5_revoker procedures to a **distinct security login**, never to runtime/Worker. Neither runtime principal gets direct SELECT/UPDATE on B.5 memberships, B.3 Identity or B.4 Registry. Inspect effective SQL principals and Vault grants directly.
5. **Do not seed ACTIVE, tenant memberships, role grants or enable Worker/API routes** until the full REV-01 external authority synchronization and ADMIN-01 independent role approval lifecycle have been reviewed and proven. An operator-supplied SQL username or JWT claim is not an authoritative tenant grant.

## Mandatory physical negative witnesses before admitting business work

- Capture two real physical tenant database IDs, B.3/B.4/fence database identities, current binding and epoch, owner/SQL effective grants, build/image and migration SHA, reviewer, UTC/TestRunId.
- Validate B01-01..06 with real actor/SQL claims, wrong tenant/resource, System Admin without membership, current JWT+refresh/replay, Maker/Checker, revoke under API/Worker/CASE commit race.
- Revoke B.3 session and suspend B.4 tenant; atomically advance independent B.5 high-watermark (REV-01) in the same authoritative commit, never a best-effort asynchronous cache update. Inject failed SQL/partial write and prove fail-closed.
- Restore older tenant/Identity/Registry snapshots (B.7 controlled drill) and prove old signed tokens, member grants, Worker retry and pending CASE cannot pass the newer independent fence. Leave reactivation SUSPENDED until independent approval and current binding/epoch check. Record actual RPO/RTO and peer no-write; do not treat a fast database restore as safe return.
- Disable/revoke Vault grants and test network/secret fault negatives. Require independent expected/actual/diff, SQL hashes before/after, source/test artifact hashes and Security/DBA disposition. Follow B.1 G-B01..05 / B.9 contract.

Until these requirements pass, status is SOURCE_CI_TESTED only, actual PREPROD authorization and G-B01..05 remain NOT_VERIFIED/NOT_RUN. Owner B.4 DEBT-01 is not a waiver for B.5.

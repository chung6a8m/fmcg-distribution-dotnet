# Issue #53 — Revocation fence operator handoff (NOT an approved deployment)

**This is a PREPROD-only review checklist. Default = DENY / NO_GO.** Do not run these scripts against actual PREPROD or shared SQL until the Owner separately approves the change ticket, environment, independent DBA/Security reviewer, backup and rollback/restore plan. GitHub CI uses nonce-owned disposable LocalDB **only**.

## Preconditions

1. Freeze exact reviewed source SHA, SQL script checksums, target host fingerprint and DBA change ticket. Verify source `business/` against `business.lock.json`; never modify either.
2. Verify three *different* approved SQL databases on the **same SQL Server instance**: B.3 Identity Control Plane, B.4 Tenant Registry Control Plane and B.5 independently durable revocation fence. B.5 must not be restored from tenant, Identity or Registry PITR snapshots and must have a separate audited backup/key/retention boundary. Fail closed if this separation cannot be proven.
3. Confirm B.3 identity migration `B3_20261117_001`, B.4 registry migration `B4_20261118_001` and all original B.5 `B5_20261118_001` migrations are approved and versioned. Pin independent `B53_20261118_001` source migration and verify no unknown or partially applied schema exists.
4. Assign narrow **separate** SQL server logins/DB users to B53 Identity bridge, Registry bridge, independent activation approver and B53 runtime assert. These are database-specific EXEC permissions only. Do not use sysadmin, broad database owner, shared cross-tenant DML, direct event/epoch table writes or attach private credentials to API/Worker. Verify effective inherited SQL/role grants, not only direct grants.
5. Require approved, cryptographically evidenced safe baseline of any existing B.3 sessions, B.4 tenants and B.5 fence rows. **Do not silently backfill old ACTIVE sessions, map restored epochs or grant any real tenant membership.** If the baseline cannot be independently reproduced, remain SUSPENDED/NO_GO.

## Source application sequence (operator-owned, not automatic)

- Apply `20261118_004_monotonic_revocation.sql` ONLY to the independently durable B.5 fence database after the original B.5 migration. Inspect history, role separation and exact stored proc definitions; repeat to prove idempotence.
- DBA renders the exact `__B53_FENCE_DB__` token in `20261118_005_identity_revocation_bridge.sql` and `20261118_006_registry_revocation_bridge.sql` to one frozen approved database identifier (ASCII letters/digits/underscore, leading letter; not system DB). Re-hash rendered scripts and record both original and rendered hashes. **Never** use raw HTTP tenant hints or SQL connection strings to construct database object names. An unresolved token MUST fail to apply.
- Apply 005 to the approved B.3 database and 006 to the approved B.4 database, using dedicated approved operator SQL credentials. Validate historical migration rows, installed enabled triggers, narrow bridge assertion stored procs, no unintended table DML grants, exact cross-DB role assignments and same-instance commit behavior. Repeat scripts to prove versioned replay.
- The independent activation approver may issue a short-lived permit **only** for an already approved tenant/binding and next Registry epoch. The B.4 original approval, verification, finalizer and retirement gates remain independent prerequisites. No real tenant ACTIVATE in this source-only step.
- If any migration, SQL bridge, Vault ACL, metadata or permission witness is missing, halt and retain a non-routable state. Never rescue a failing session or tenant by resetting epochs, disabling triggers or force-changing B5 records.

## Independent negative witnesses before any future physical acceptance

| Test | Expected independent evidence |
| --- | --- |
| B.3 login and revoke-all / consumed-token-family reuse | Every created session has independent watermark; all revocations atomically increment it. Fence SQL outage/denied EXEC forces original B.3 mutation ROLLBACK |
| B.4 suspend/revoke and attempted ACTIVATE | Independently approved one-use activation permit required; original B.4 mutation and B5 watermark commit/rollback together |
| B.3/B.4 lost commit ACK and retries | Same source identity; old session/epoch cannot be reissued or resurrected |
| B.3/B.4 old-snapshot actual PITR restore | B5 authority must remain newer and independent; old JWT/refresh and tenant routing deny; missing restored bridge/trigger denies |
| API & Worker concurrent command/CASE resume | Lock current B.3/B.4/B5 rows through protected SQL COMMIT, no stale post-revoke effect, no peer A/B writes |
| Failed key, disabled bridge, wrong physical tenant, cross-tenant callback | Fail closed with source-bound witness; no runtime secrets leaked |
| B.7 safe service re-entry | Tenant suspended, current external authority and immutable external outcome reconciled, separate reactivation approval and real measured RPO/RTO |

Record independent actual-vs-expected/diff, negative fault IDs, A/B SQL before/after fingerprints, independent Security/DBA signoffs, TestRunId, UTC timestamps, SQL migration SHA and reviewed final source HEAD. Execute B.9's original B01/B03/B05 physical acceptance before any `PREPROD_FOUNDATION_VERIFIED`; SQL source tests by themselves cannot satisfy those gates.

**Current disposition:** `SOURCE_IN_REVIEW / PHYSICAL_PREPROD_NOT_VERIFIED / G-B01..05 NOT_RUN / NO_GO`. The #53 blocker and #59 HIGH full npm audit remain open. B.3 Identity-only HTTP authorization/refresh must additionally require the independent watermark before operational admission; this source-only package does not yet add an admitted HTTP path. The optional internal B.5 test guard is not itself a production API, and no workload/runtime registration is authorized. APP-02C/D/E, production/pilot and actual EV-08/P0 remain blocked.

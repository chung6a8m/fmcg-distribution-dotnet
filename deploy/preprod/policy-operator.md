# APP-02B.6 — policy registry operator / security hold (PREPROD)

**Operator status:** NO_GO. This runbook describes SOURCE-ONLY migrations and CI evidence. No real PREPROD/Vault/SQL role grants, Owner-approved global L0 values, CASE integration or B.5 restore-safe authority have been demonstrated.

## Prerequisites for future operator review

- Independent approved Control Plane SQL database and private SQL/TLS access, named DBA and Security reviewers, approved B.3/B.4/B.5 principal isolation and external revocation high-watermark, all from real PREPROD. **Never** use synthetic API/LocalDB or a user-supplied database name.
- Migration `B6_20261118_001` is operator-applied manually and transactionally; no app-startup migration. Verify database identity, embedded resource source hash and change approval. Reapply must detect unknown history or absent critical schema, and abort; never silently repair.
- The `B6L0Policy` table **starts empty**. A separate Owner/legal approval is needed for each policy code, numeric floor/ceiling, tier-permission bits, frozen `DefinitionHash` and `AuthorityRef`. No real price/credit/terms thresholds or policy defaults are provided by APP-02B.6.
- Independent named SQL logins must be granted only their exact EXEC-only roles and bound to **one tenant** in `B6OperatorScope` by a privileged security operator. Revoke access in SQL and independent B.5 epoch authority; never treat SQL scope alone as a restore-safe revocation mechanism. A System Administrator/DBA connection is not a business tenant identity.
- Make/approve source or CASE changes only under own independently authenticated SQL login. SQL freezes `SUSER_SNAME()`, not supplied maker/checker IDs. Verify upstream APP-02A CASE/source reference and immutable hash **outside this package** before any real L2 action. B.6 alone does not implement the upstream verifier.
- `b6_ResolveCurrent` is an EXEC-only operator source proof, not an enabled business authorization endpoint. Its `@ExpectedCaseId` must match the frozen approved L2 CASE; other CASE overrides cannot shadow L1. **This raw SQL parameter is NOT authenticated upstream CASE authority** and must never be taken directly from a browser or untrusted event. Bind actual CASE/source/actor to B.3/B.4/B.5 and independently verified APP-02A before admitting business decisions. Do not wire it to API, Worker, outbox, CASE resume or tenant write path until same-transaction checks, restore-safe fencing and independent integration tests exist.
- Audit/backup and legal-hold lifecycle are deferred to APP-02B.7/B.8; a local immutable trigger cannot replace off-host audit or PITR validation.

## SQL fixture grant policy

| Principal | Intended EXEC only | Never grant |
|---|---|---|
| Maker | `b6_Propose` | Checker/CASE approval/direct table DML |
| Checker | `b6_Approve` | Maker/direct table DML |
| CASE Maker | `b6_OpenCase` | CASE approval, SQL audit alteration |
| CASE Checker | `b6_ApproveCase` | CASE source creation |
| Reader | `b6_ResolveCurrent` | any schema or proposal write |
| DBA/security | distinct signed L0/source/operator-scope changes with audit | unreviewed business admission |

This table is a prospective physical acceptance design, **not** proof these grants exist in PREPROD. In GitHub CI, all logins, L0 fixture definitions and physical SQL databases are disposable LocalDB only.

## Recovery rule

Fail closed on missing/current-version mismatch, current tenant/actor binding loss, revoke, expired policy, revoked checker, unknown external effect or unavailable independent control authority. Preserve immutable SourceId, CaseId, ProposalId, source SHA-256 and approval snapshot; never replay under a new ID to evade policy/CASE rejection. Following restore, recheck B.5 external high-watermark and B.7 reconciliation before any release; this B.6 increment does not claim that test passed.

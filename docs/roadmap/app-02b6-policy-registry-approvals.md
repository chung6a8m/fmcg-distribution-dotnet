# APP-02B.6 — Policy Registry & Approvals (PREPROD-only, inert source PR)

**State:** SOURCE_IMPLEMENTED / CI_FINAL_HEAD_PENDING / INDEPENDENT_REVIEW_PENDING / PHYSICAL_PREPROD_NOT_VERIFIED. This PR does **not** earn `POLICY_GOVERNANCE_VERIFIED` or G-B02 PASS. Owner design scope: OWN-APP02B-06 and OWN-APP02B-10, with B.1 TM/B01/B02 threats and the [30-case acceptance contract](../security/app-02b1-acceptance-evidence.md). B.5 conditional CODE_ONLY merge is **not** an authorization or debt waiver for B.6.

## Boundary and delivered change

- New independent **operator-applied** SQL Server Control Plane store `B6_20261118_001` in `src/FmcgDms.Preprod.Policy/Migrations/`; no automatic migration, API route, Worker handler, tenant business posting, production admission or runtime registration.
- `B6L0Policy` is append-only global **bounds/allowed tiers**, initially **EMPTY**. Only separately authorized DBA/security source provisioning can insert policy definitions; no initial price, credit, terms, legal retention, approval threshold, or value has been approved or seeded. AFTER UPDATE/DELETE immutable trigger denies policy-history rewrites. L0 constraints checked both on proposal and at approval.
- `B6OperatorScope` maps separately granted SQL login + tenant + action, in addition to EXEC-only roles `b6_maker`, `b6_checker`, `b6_case_maker`, `b6_case_checker`, `b6_reader`. Authority is `SUSER_SNAME()`, **never caller-supplied MakerId/CheckerId**. No role memberships, tenant grants or ACTIVE policies are shipped.
- L1 is tenant-scoped, only when L0 explicitly permits the tier. L2 additionally demands a two-person immutable `B6CaseSource` + `B6CaseApproval` chain whose tenant, CaseId, source ID/hash and distinct principals agree with the policy proposal. L2 cannot exceed L0 bounds, and cannot replace immutable L0 definitions. **Different CASEs may carry overlapping L2 windows, but a single CASE cannot approve overlapping versions.**
- `B6PolicyProposal`, `B6PolicyApproval` and `B6PolicySnapshot` append-only sources, approvals and approved frozen value/version/effective period. Unique `(TenantId, PolicyCode, Tier, SourceId, SourceVersion)` blocks replaying one source revision under a fresh ProposalId. The approved decision and snapshot are inserted in **one SERIALIZABLE-lock-compatible SQL transaction**; `sp_getapplock` serializes approvals per tenant/policy/tier, denying overlapping windows **within L1 or within the same L2 CASE** and single-use ProposalId replay.
- `UNSET` is explicitly represented by a distinct null state; a maker can record an UNSET proposal but checker **cannot approve** it. An explicitly approved numeric `0` remains `SET/0` in the immutable snapshot and is not silently converted to missing. **No proposal or L0 definition** means fail-closed.
- `b6_ResolveCurrent` trusts only **SQL server UTC**, requires tenant-scoped reader, exact expected ProposalId/source SHA-256 and an effective snapshot. L2 wins over L1 **only for an explicitly supplied matching CaseId**; unrelated CASE overrides never shadow tenant L1, and two unrelated CASEs can resolve distinct effective overrides concurrently. SQL and typed .NET snapshot expose/validate the frozen CaseId. The `@ExpectedCaseId` supplied to this **operator-only proof** is not proof of upstream CASE entitlement: integration must bind it to independently verified APP-02A/B.3–B.5 authority. If absent, expired or stale, deny with an opaque code; a caller cannot choose a historical/future time for a live decision. Half-open interval `[FromUtc,UntilUtc)`.

## Non-negotiable control gaps before real PREPROD or production

1. **B.5 revocation high-watermark #53:** B.3 session revoke, B.4 suspend, external epoch and restore-safe barrier are NOT atomically connected. B.6 cannot admit or dispatch business writes, Worker retries or CASE resumes while that debt remains. B.6 approval is **not** a substitute for B.5 actor/session membership assertions.
2. **Operator Identity bridge:** Current B.6 SQL roles are an isolated operator-only source proof; actual B.3 signed actor/session → B.4 registry binding → B.5 permission/resource/epoch and B.6 checks on one protected same-transaction effect **are not integrated**. Neither API nor Worker uses this schema. SQL direct privileges, two physical PREPROD tenants, Vault role separation, role assignment/revocation evidence and independent DBA review remain NOT_VERIFIED.
3. **CASE and upstream source:** CI-only `B6CaseSource` binds an immutable SHA-256 and distinct SQL checker, but it is **not yet a verified APP-02A durable CASE/source identity** with immutable upstream journal, signed/oracle-verified source canonicalization, current Maker/Checker B.3 authorization or resume. Source hash is accepted from an authorized SQL maker, not independently attested. Prohibit real L2 without that bridge.
4. **L0 authorization:** Global definitions need separately signed Owner business/policy content, source identity and legal review. The migration contains no values or grants. SQL system administrator can administratively alter objects/triggers; actual independent audit/DBA tamper-evidence remains B.8 / G-B04, not proven by CI trigger negatives.
5. **Security debt #59 / B.7 / B.8 / B.9:** npm audit HIGH open, no PREPROD PITR restore drill, independent audit sink, physical two-tenant B02 acceptance, final-HEAD independent review or G-B01..05 PASS. Do not infer production/Pilot/APP-02C/D/E approval.

## Reproducible CI-only evidence matrix

| Gate | Executed source / disposable Windows LocalDB assertion | Real PREPROD |
|---|---|---|
| CI-B6-01 | Install and replay `B6_20261118_001`, reject unknown/partial and dropped procedure despite history | NOT_VERIFIED |
| CI-B6-02 | L0 is empty by default, bounded immutable test-only definition, reject UPDATE/DELETE/override/unregistered key | NOT_VERIFIED |
| CI-B6-03 | Tenant-scoped L1 operator, cross-tenant denial, direct SQL SELECT denial, independent two tenant SQL DB IDs/hashes | NOT_VERIFIED |
| CI-B6-04 | L2 CASE origin/hash, separate CASE Maker/Checker and proposal Maker/Checker, distinct CASE concurrent intervals, same-CASE overlap rejection, reject missing/wrong CASE/replay/self | NOT_VERIFIED |
| CI-B6-05 | `UNSET` proposed but denied; explicit `SET/0` approved/resolved without fallback | NOT_VERIFIED |
| CI-B6-06 | Server-UTC effective interval, CASE-scoped L2 priority with L1 fallback, stale expected hash/ID/CASE and overlap approval rollback | NOT_VERIFIED |
| CI-B6-07 | Frozen snapshot and approval/source immutable, no backdating, no history rewrite after approval | NOT_VERIFIED |
| CI-B6-08 | SQL policy database + two distinct physical tenant LocalDB instances; peer sentinel hash before/after unchanged | NOT_VERIFIED |
| CI-B6-09 | **Exact source-HEAD checkout assertion** (not synthetic PR merge SHA), SHA-256 migration, TestRunId, actual before/after SQL peer hashes, JSON witness, separate Ubuntu/Windows jobs; no API/Worker runtime activation | NOT_VERIFIED |

**Evidence class:** `GITHUB_HOSTED_LOCALDB_ONLY`; exact-HEAD CI and independent review are still required. B.1 **B02-04..06** remain `NOT_RUN` in actual PREPROD; no `POLICY_GOVERNANCE_VERIFIED` claim is permitted until source gaps above are resolved and PREPROD witnesses independently signed. Read [operator runbook](../../deploy/preprod/policy-operator.md).

## Exit/merge rule

Separate B.6 PR. Verify current HEAD, whole diff/negative cases and all required CI workflows. Only mark *source CI tested* when the exact HEAD passes both B.6 jobs. **Without additional Owner authorization to merge source-only despite B.5/B.6 physical debts, do not merge** or claim full B.6 verification. Never modify `business/`, `business.lock.json`, synthetic oracle baselines or npm audit gates to manufacture a PASS.

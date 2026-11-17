# APP-02B.1 — Independent Security, Fault, Restore and Evidence Contract

**Contract state: CONTRACT_APPROVED (DOCS_ONLY); 30/30 acceptance cases SPECIFIED, 0 EXECUTED; all G-B01..G-B05 NOT_RUN.** This contract freezes a **minimum** of six named cases per gate (30 total) for later independent PREPROD evaluation in APP-02B.2–B.9. B.1 is **documentation only**. No case in this document is evidence of a PASS. Sources: [Owner Decisions](../decisions/owner-app02b-2026-11-17.md), [architecture/security contract](../roadmap/app-02b1-architecture-security-contract.md), [threat register](app-02b1-threat-model.md) and [B.9 roadmap exit](../roadmap/app-02b-preprod-foundation.md).

## 1. Frozen 30-case acceptance matrix

**Case result vocabulary:** NOT_RUN, BLOCKED, FAIL, PASS. An empty or missing result is NOT_RUN, never PASS. A gate PASS requires **all six minimum cases** with independently observed positive and negative witnesses, plus all corresponding increment-specific checks. Two physical tenant databases (TF-TEN-A/B) are mandatory for tenant/security/SQL observations; evidence must state where a case was applied to A, B or both, and whether peer database fingerprints changed. Never fabricate numeric business/policy values, legal retention deadlines or availability metrics.

### G-B01 — Security and tenant isolation (B01-01..06)

| ID | Preconditions and fault injection | Independent expected result (including negative assertion) | Mandatory witness; implementing PR |
|---|---|---|---|
| **B01-01** | Valid A actor attempts read/search/export/update/metadata for B and supplies B resource/tenant/connection hints | Deny/opaque peer response; **no A→B data disclosure and no new tenant B row/write**; A identity cannot be silently rebound to B | HTTP denial + independent A/B SQL full-table hashes before/after, registry binding; **B.4/B.5** |
| **B01-02** | Invalid/expired/wrong-issuer JWT, wrong session, tampered tenant claim; concurrent refresh and reuse of rotated opaque token | 401/403, no token/family elevation; refresh reuse revokes that family; no business SQL effect | JWT validation and session/family/epoch SQL reads, redacted audit, peer DB no-write; **B.3/B.5** |
| **B01-03** | Valid access token and queued Worker job then revoke actor or increment epoch before apply/CASE resume; race revoke at COMMIT | New API write and stale Worker/CASE action **cannot commit after authority fence**; no privileged partial effect | Current-epoch/control-store witness + tenant SQL before/after + injected barrier timeline; **B.5** |
| **B01-04** | Control Plane System Admin without tenant membership requests tenant read/export/business write | All tenant data access denied despite provisioning privilege, including direct SQL/service path | Distinct admin identity, SQL permission denial, API evidence, peer hash; **B.4/B.5** |
| **B01-05** | Maker=Checker or Checker=initiator; check after approval revocation; mutate version/policy | Deny self/expired/non-current approval and leave immutable source/approval audit intact | Actor/role IDs, current permission version and attempted approval source queries; **B.5/B.6** |
| **B01-06** | Restore tenant A snapshot predating revocation while B stays live; attempt old JWT, Worker retry and queued action; reject reactivation verification with stale epoch/Registry binding or concurrent revoke | Restored rights never revive; B remains unchanged; A stays SUSPENDED/non-routable on failed reactivation and never enters initial provisioning retry; ACTIVE requires fresh approval and current binding/epoch CAS | Independent control-fence snapshot/SQL hashes, restore ID, lifecycle transition/CAS denial, B pre/post; **B.4/B.5/B.7** |

### G-B02 — Secrets and policy governance (B02-01..06)

| ID | Preconditions and fault injection | Independent expected result (including negative assertion) | Mandatory witness; implementing PR |
|---|---|---|---|
| **B02-01** | API attempts Worker-only/Vault-admin/tenant-B secret; wrong workload/SQL identity | Scope-denied, audited and no secret leak/fallback; wrong SQL principal cannot query another tenant | Vault policy audit, masked secret ID/version, SQL deny/peer hash; **B.2/B.4** |
| **B02-02** | Vault unavailable, expired lease or key version absent at startup/apply/restore | Startup or operation fail closed/hold; **no plaintext/config key fallback or business effect** | Failover/fault timeline, redacted logs, zero SQL effect, health admission; **B.2/B.7** |
| **B02-03** | Rotate signing/decryption key, retire old version and revoke active session family | Permitted historical verification/decryption retained per documented window; revoked family and non-current authority still denied | Vault version ledger, sample encrypted archived data, jwt/refresh negatives, no raw key; **B.2/B.3/B.7** |
| **B02-04** | Tenant policy attempts override/edit/delete L0 and rewrite historical L0 effective date | All prohibited changes rejected; L0 hash/history unchanged | Versioned policy SQL pre/post hashes, audit, expected vs observed; **B.6** |
| **B02-05** | Tenant A L1 edit and backdated/expired L1 against B or wrong effective time | Enforce scope and approved bounds; disallowed/backdated/expired version fail closed, tenant B untouched | Registry/approval/current policy snapshot, date/zone, two tenant SQL diff; **B.6** |
| **B02-06** | L2 CASE Maker/Checker distinct; try self-approve, unknown policy and UNSET vs explicitly APPROVED=0 | Valid scoped approval immutable; self approval and UNSET rejected; approved zero remains a distinct durable value; source snapshot unchanged on replay | SQL case/approval/source lineage, actor and policy versions, status/diff, two tenant negative; **B.6** |

### G-B03 — Backup, recovery and outcome safety (B03-01..06)

| ID | Preconditions and fault injection | Independent expected result (including negative assertion) | Mandatory witness; implementing PR |
|---|---|---|---|
| **B03-01** | Inspect continuously generated FULL/DIFF/LOG chain and off-host encrypted copies | SQL FULL recovery, daily FULL, differential ≤6h, transaction logs ≤5m, valid chain/checksums and separate accessible decryption path; gap = FAIL | Backup job schedule **actual timestamps**, LSN chain, off-host object hashes/encryption/key IDs; **B.7** |
| **B03-02** | Force PREPROD tenant A failure with seeded committed witness sequence, perform tenant-scoped PITR and safe resumption | Measured **RPO ≤15 min and RTO ≤4 h**, restored state consistent, no blind outgoing side effects | UTC fault/start/end, monotonic elapsed, recovered max witness ID/UTC, chain/LSNs, independently signed drill report; **B.7** |
| **B03-03** | Restore A with wrong Registry binding, changed DB identity or peer database identifier | Refuse activation; no peer data read/write; B's entire user-table fingerprint unchanged | Registry binding version, SQL DB marker, A/B hashes, denial reason; **B.4/B.7** |
| **B03-04** | Restore outdated Control Plane/tenant snapshot predating revoked session, tenant suspension and Vault/key rotation | Compare external current epoch/key version/approval authority; obsolete snapshot cannot restore rights; failed reactivation preserves SUSPENDED and no API/Worker claims pending independently verified reconciliation | Before/after authoritative external fence, restore runbook, key IDs and activation barrier; **B.5/B.7** |
| **B03-05** | Durable external surrogate outcome is COMMITTED, ACK lost or UNKNOWN; restore/retry queued outbox item | Same immutable MessageId/SourceId lookup; no second effect, no new ID. UNKNOWN without witness => HOLD/CASE and incident | Independent receiver journal + inbox/outbox SQL, hashes and send attempt IDs, negative duplicate counter; **B.7** |
| **B03-06** | Corrupt log backup, withhold decryption key, fail Control Plane restore or unauthorized restore request | Deny/abort cleanly; no ACTIVE tenant/dispatch, no claimed RPO/RTO PASS; incident and next action recorded | Corruption injection hash, restore error/denial, Registry lifecycle, incident/audit reference; **B.7/B.8** |

### G-B04 — Immutable audit and operations (B04-01..06)

| ID | Preconditions and fault injection | Independent expected result (including negative assertion) | Mandatory witness; implementing PR |
|---|---|---|---|
| **B04-01** | Attempt SQL mutation/delete of archived audit and alter previous audit hash | Denied or tamper detected by independent verifier, preserved original chain | Before/after immutable sink hash/signature chain, verifier error and actor audit; **B.8** |
| **B04-02** | API intake → durable Worker → inbox/outbox/CASE simulated flow with replay and failed ACK | Correlated trace/span/metrics and source/event IDs without tokens/PII secrets; durable ACK and outcome distinguishable | TraceId/SpanId, SQL receipt/source/inbox/outbox and redacted central telemetry; **B.8** |
| **B04-03** | Inject cross-tenant deny, abnormal refresh reuse, Worker backlog and restore alert threshold breach | Alerts fire and arrive at an acknowledged operations sink; missing delivery is visible and FAIL | Alert config/version, trigger/receive/ACK UTC timestamps, correlation and responsible function; **B.8** |
| **B04-04** | Simulate incident severity and escalation timeout, assign incident and execute runbook | Immutable IncidentId, owner, timed escalation, mitigation/rollback-safe decisions and reviewed closeout | Independent incident timeline + handoff log, runbook version and reviewer; **B.8** |
| **B04-05** | Legal hold applies to an evidence class, then unauthorized actor requests purge | Purge denied; all protected objects/hashes still exist, hold preserved across restore | Hold authority/version, full manifest hash before/after, denied purge audit; **B.8** |
| **B04-06** | Approved archive/purge of *eligible* classified data; change diagnostic telemetry TTL while durable APP-02A history exists | Legal-hold-free manifest/approvals/verification required; diagnostic TTL never deletes durable receipt/CASE/source/outbox or audit; no unsupported retention default | Class-specific policy approval, per-object hashes, no-change durable SQL fingerprints, archival proof; **B.8** |

### G-B05 — Independent final-HEAD acceptance (B05-01..06)

| ID | Preconditions and fault injection | Independent expected result (including negative assertion) | Mandatory witness; implementing PR |
|---|---|---|---|
| **B05-01** | Run G-B01..04 on two fresh physically distinct PREPROD tenant DBs and reproducible fixtures | Complete TestRunId/build/container digest/source HEAD/migration/fixture hash, every applicable case/tenant reported; no missing required case | Source/fixture manifest, two SQL DB IDs, report index, CI artifact links; **B.9** |
| **B05-02** | Independent security+DB reviewer examines actual code, permissions, witnesses and unresolved findings | Different reviewer from implementer, zero unresolved blocking findings and signed review of **exact final source SHA** | Review identity/time/commit, blocking finding ledger; **B.9** |
| **B05-03** | Run required .NET, SQL, APP-01E.5/APP-02A.4, frontend/PWA/Playwright and PREPROD suites | All required checks PASS, **0 failed/skipped required tests**; no previous regression removed or marked optional | CI run URL/attempt/final source HEAD, count and artifact names; **B.9** |
| **B05-04** | Mutate expected cell, actual cell, signed difference, match flag, fixture hash, peer state and source reference independently | Every single mutation makes independent oracle **FAIL**; an implementation response cannot define its own expected values | Frozen fixture SHA, validator mutation transcript, expected/actual/diff report; **B.9** |
| **B05-05** | Forge positive API status while tenant SQL/alert/restore/fence evidence is missing | Gate remains FAIL/NOT_VERIFIED; physical peer no-write and real drill/alert evidence are authoritative | Independent SQL/backup/telemetry/restore artifacts with expected/actual/diff, no self-attestation; **B.9** |
| **B05-06** | Push a new HEAD after green CI/review or after B.9 witness collection | Invalidate earlier sign-off; rerun review and CI on new exact source; **no production/pilot promotion** via B gate | Source SHA comparison, new CI run and independent review, final mode/status ledger; **B.9** |

**Coverage accounting:** **G-B01=6; G-B02=6; G-B03=6; G-B04=6; G-B05=6; TOTAL=30.** In addition, [threat model](app-02b1-threat-model.md#4-test-fixture-and-account-ownership-design-not-existing-provisioned-users) requires two dedicated **RUNTIME-01/RUNTIME-02** B.2 port/startup hardening probes. Increment teams may add cases, **never silently delete or mark required cases N/A**. A justified change to these frozen minimum obligations requires a versioned contract amendment and independent approval.

## 2. Evidence schema, independent oracle and ownership

At B.1 every case is SPECIFIED/NOT_RUN. When executed later, store one append-only evidence record **per CaseId × TestRunId × applicable TenantId/Scope**, plus separate negative fault and before/after peer witnesses. The minimum record format (field names normative for later automation):

| Field | Required meaning |
|---|---|
| CaseId, GateId, Status, ExecutionUtc | Named case and result; only witnessed outcomes become PASS |
| TestRunId, EnvironmentId, PreprodMode, Repository, SourceHeadSha, WorkflowSha, WorkflowRunUrl, RunAttempt | Distinguish PREPROD, exact code and CI attempt; no production assertion |
| BuildArtifactSha, ApiImageDigest, WorkerImageDigest, SchemaMigrationSetSha, FixtureSha | Reproducible binary/schema/expected witness; **not** an unverified artifact filename |
| TenantId, RegistryBindingVersion, DatabaseId, DatabasePhysicalName, DatabaseSchemaVersion | Server-authoritative identity; no token-selected SQL target |
| ActorId, Role/PermissionVersion, PermissionEpoch, TenantSecurityEpoch, SessionIdRedacted, PolicyVersion | Current security decision lineage, without tokens/passwords |
| FaultId, FaultInjectedAtUtc, PreconditionsSha, EvidenceObserverId, IndependentReviewerId | Independent fault witness and reviewer separation; reviewer cannot approve own implementation |
| Expected, Actual, Difference, Match, NegativeInvariant | For numeric cells: signed Difference=Actual−Expected. For structured values: canonical hash and exact match. Absence cannot be PASS |
| TenantSqlHashBefore/After, PeerSqlHashBefore/After, RegistryHashBefore/After, RevocationFenceVersion | Physical isolated SQL/read/write fingerprint, including peer zero-write assertions |
| DurableSourceId, EventId, MessageId, ExternalOutcomeId, CorrelationId, AuditHash, TraceId, IncidentId | Immutable lineage; absent values may be explicit NOT_APPLICABLE only when not relevant and independently reviewed |
| BackupChainId, RecoveryPointUtc, FailureUtc, SafeRestoreUtc, RpoSeconds, RtoSeconds, RestoreLogSha | Required for B03-01..06; measured, never asserted from planned schedule |
| ArtifactPath, ArtifactContentSha256, RecorderIdentity, ReviewerDisposition, FindingIds | Reproducible evidence and finding trace; secret material/redacted subject data never attached |

**Expected-value ownership:** Fixture Owner authors a versioned expected table **before** execution; independent Reviewer checks expected values against Owner decisions/business oracle and hashes the fixture. Runtime/HTTP return values may drive the scenario but **cannot populate the expected fields**. Independent SQL observer/backup/telemetry verifier produces Actual; a separate validator computes Difference and Match and checks scenario/state/peer mutations. A changed fixture hash or source HEAD invalidates earlier evidence. Missing artifact, contradictory row, skipped required case, unreviewed exception or false no-write assertion = FAIL/NOT_VERIFIED.

**Required report layout (future contract, no files claimed to exist yet):** artifacts/APP-02B/TestRunId/manifest.json, gates/G-B0x/CaseId/TenantScope.json, sql/tenant-A-and-B-fingerprints.json, security/current-fences.json, vault/key-version-witnesses.json, recovery/backup-chain-and-drill.json, ops/alerts-incidents-audit.json, oracle/independent-expected-actual-diff.json, oracle/mutations.json, review/final-head-signoff.json. Record relative paths + immutable content hashes in manifest, with redaction for personal/credential details. CI artifact retention location is not legal-record retention policy.

**Ownership at execution:** each B.2–B.8 implementing PR names a responsible engineer/function and creates the scoped evidence; security reviewer reviews access/keys; separate DB reviewer validates physical tenant/LSN/restore/oracle; B.9 independent reviewer signs exact-head matrix. The Owner's 10 decisions authorize the design, **not** any particular named person as approver. Maker/Checker separation must also be enforced in the application, not only review notes.

## 3. Recovery experiment protocol

**Preconditions:** Provision fresh TF-TEN-A and TF-TEN-B with different physical SQL databases; separate independent Registry/epoch source; Vault-managed versioned test encryption key, restore credential and protected outcome surrogate. No live external provider. Seed a paced series of committed immutable **test-only** witness rows with UTC commit timestamps, LSN/sequence and signed observer receipt; include before-fault revoked session, queued MessageId, and an acknowledged/lost-ACK external surrogate effect.

1. **Before fault (T0):** Independently snapshot authoritative current epoch, tenant binding, A/B SQL hashes and surrogate effect count. Verify FULL recovery and actual FULL/DIFF/LOG **timestamps and contiguous LSN chain**; confirm encrypted off-host copies can be decrypted with a distinct approved restore identity. Inject one nonproduction tenant A outage or point-in-time scenario and record first unavailable UTC instant plus monotonic clock anchor.
2. **Perform restore:** Restore ONLY A to the selected point; record each backup file ID/UTC/LSN, SQL RESTORE command/result, key VersionId, Registry/Vault and separate revocation fence evidence. Deny all tenant A API/Worker writes until validation. B remains online, and its initial/final fingerprint must be equal.
3. **Reconcile:** Recheck A marker and Registry binding, migration versions, current authoritative revocation epochs and sessions; enumerate durable source/outbox/inbox/dependency/CASE and external surrogate outcomes. If an outcome is UNKNOWN, hold the original identity/CASE and block dispatch, **never resend using a new ID**. Recovered state must reflect independent current suspension/revocation; obsolete snapshot state cannot grant access.
4. **Safe restoration (T1):** An independent actor validates signed permission probes, SQL invariant queries, Vault key access, zero cross-tenant read/write, durable outcome reconciliation and Worker safe re-entry; only then record first safe API/Worker write-ready UTC and monotonic elapsed. SQL online alone **is not T1**.
5. **Measure:** RPO_seconds = T0_UTC − LatestRecoveredCommittedWitnessUtc; require **0 ≤ RPO_seconds ≤900** and a witness sufficiently near T0 to measure actual potential lost data. RTO_seconds = monotonic elapsed between T0 unavailable and T1 safe; require **0 ≤ RTO_seconds ≤14400**. UTC clock sync/offset and sequence evidence must be captured; clock skew, no recovered witness, broken log chain, missing key, incomplete reconciliation or no successful safe-open probe **invalidates** the measurement even if a stopwatch number is small.
6. **Fault and sign-off:** Repeat with corrupt log, missing key, stale Registry binding, restored stale revoke, wrong tenant database and lost external ACK. Publish expected/actual/diff, denied paths, actual RPO/RTO, signed witness hashes and independent reviewer disposition. Failing either target or an invariant is **G-B03 FAIL**, not a waiver or a synthetic PASS.

**No artificial claims:** RPO/RTO are targets here. Actual measured results appear only when B.7/B.9 drill and its independent evidence have executed in a real PREPROD environment.

## 4. B.1 review gate vs B.9 security acceptance

| Stage | Required evidence | Maximum permitted status |
|---|---|---|
| B.1 PR | This contract, diagrams/threat model, 30 cases and owners, open-decision ledger; independent documentation/security review, exact final-HEAD Windows CI regression pass | **CONTRACT_APPROVED** (after review/CI), never a PREPROD gate PASS |
| B.2..B.8 PRs | Implement only their scoped controls and negative/fault evidence; independently review & rerun CI after final HEAD | Increment-specific **VERIFIED** only with actual implementation/witness |
| B.9 PR | Re-execute and independently verify **all 30 minimum** cases plus increment checks, actual two tenant physical PREPROD evidence, 5 gate proofs, strict mutation failures, zero blocking findings and exact source+CI | **PREPROD_FOUNDATION_VERIFIED**, conditional on G-B01..G-B05 all PASS |
| APP-02C/D/E or production | Separately authorized real business/mode/cutover/legal/provider/pilot acceptance | **NOT_AUTHORIZED / NOT_VERIFIED** by APP-02B |

**Preserved historical CI minima (not new PREPROD evidence):** APP-02A.4 final-head Windows CI previously exercised at least **296/296 .NET tests**, two LocalDB tenants, 24/24 A11 group instances, 60/60 checkpoints, 1440/1440 SQL metric cells and 5/5 mutation sentinels; APP-01E.5 has historical 182/182 AC-03 and 518/518 reconciliation cells, plus existing frontend/PWA/Playwright coverage. Later PRs **must not drop these regressions** or cite their synthetic PASS as actual B or production proof. B.1 must validate the final PR HEAD using current CI and record any failure honestly.

**Status at authored contract:** **SPECIFIED ONLY / 30 CASES NOT_RUN / G-B01..G-B05 NOT_RUN**. Real AC-11/EV-08, production P0 G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV, APP-02C/D/E, Pilot and Go-Live remain NOT_VERIFIED or NOT_AUTHORIZED as applicable.

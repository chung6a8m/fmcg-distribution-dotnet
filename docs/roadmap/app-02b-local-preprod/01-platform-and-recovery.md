# APP-02B LAB — Platform and Recovery Increments

**State:** PLAN_ONLY / IMPLEMENTATION_NOT_STARTED. Baseline `16d96c71a7967c216ebfa599693807a2b54e7435`, 2026-11-19.

**Goal:** Prepare a guarded Windows-hosted lab and execute recoverable, measurable SQL fault drills.

**Architecture/stack:** [Design and contracts](design.md), .NET 10/SQL Server, PowerShell/Linux and Python as assigned below. **Global constraints:** [Design sections 1–7](design.md); immutable business baseline, retained guards, source-bound admission and no automatic physical acceptance. **Review focus:** [five cross-cutting risks](design.md#8-five-review-risks-and-required-ownership). **Execution/commands:** [Codex handoff](codex-handoff.md). A `Create/New` path or API below is proposed, not a claim that it already exists.

Every increment must add meaningful regression/negative tests for its owned invariant, verify relevant retained suites, preserve failed evidence, and record source-only versus live results separately. Commands for new components apply only after that increment implements them.

## Shared implementation decisions

- Windows is the operator/observer workstation. Three Linux VMs are APP, DATA and VAULT. An OBSERVER VM is optional; it does not create an independent human reviewer. The laptop/hypervisor/admin remains a common failure domain.
- Keep B3 Identity, B4 Registry, B5 fence and any tenant target participating in the current cross-database transaction on **one supported SQL Server instance**. Separate database principals and independently managed recovery policies are required; cross-instance distributed transactions are not assumed.
- The base application stack stays `deploy/preprod/compose.yaml`; SQL and Vault remain outside that stack. Preserve FOUNDATION_ONLY/NO_GO until the separately reviewed runtime-admission increment exists. Existing synthetic API/LocalDB credentials cannot become admitted PREPROD credentials.
- All tools accept the shared `LabRunContext`: `runId`, `environmentId`, `sourceHeadSha`, `fixtureSha256`, `configurationSha256`, `startedUtc`, `hostSha256` and `mode`. Raw collectors append context to every artifact. A new source/config/fixture hash starts a new run; a retry does not overwrite a failed artifact.
- **New configuration** `deploy/lab/windows/environment.example.json` describes names, addresses, approved target identifiers, resource limits, TLS trust references, image/version locks, Vault secret references and evidence directories. It contains no credentials, private keys, root/unseal material or invented approval flags. Real environment enrollment/approval records are held outside Git and verified separately; signing a local config does not establish independent custody.
- SQL Server Developer 2022 with an approved CU is a **proposed baseline**, not an existing frozen project decision. Freeze the exact package/image digest and license mode before installation. SDK `10.0.404` and Node `>=26.13.1 <27` follow the existing source baseline. Linux images, Docker, Vault and certificates need authenticated version/checksum records; avoid mutable `latest` inputs.
- Existing physical obligations remain unchanged: RUNTIME-01/02, 30 B.1 cases, distinct reviewers, off-host recoverable copies, RPO ≤900 s and RTO ≤14400 s. Local evidence uses a separate lab schema/class. No PR may make the B9 physical validator accept solo/local evidence as physical independence.

<a id="lab-i01"></a>
## LAB-I01 — Preflight Windows và toolchain

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-E05. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** a read-only capability report and validated environment configuration, usable before any administrator action.

**Files:** new `scripts/lab/Test-LabPrerequisites.ps1`, `scripts/lab/Lab.Common.psm1`, `deploy/lab/windows/environment.example.json`, `deploy/lab/windows/toolchain.lock.json`, `tools/app02b_lab/environment.py`, `tests/app02b_lab/test_environment.py`; read existing `global.json`, `web/package.json`, `AGENTS.md`.

**Interfaces:** `Test-LabPrerequisites.ps1 -EnvironmentFile <path> -OutputDirectory <path>` returns exit 0 for a complete inventory and machine-readable readiness; exit 2 when requirements are missing, exit 1 on collection error. It never installs features, changes firewall policy or creates VMs. `environment.py: validate_environment(document: dict) -> list[str]` rejects malformed or inconsistent targets. The report distinguishes `READY_FOR_LAB_SETUP` from authorization to execute setup.

- [ ] Capture Windows edition/build, x64/ARM, virtualization/SLAT, Hyper-V availability, RAM/disk, hostname, current VM/NAT/WSL/Docker subnets, relevant tools and their versions, .NET SDK, Node, Git, SSH, Python/OpenSSL, SQL tools and clock state. Inspect facts without dumping all environment variables or credential stores.
- [ ] Validate absolute evidence directories outside Git, absence of path traversal, distinct APP/DATA/VAULT identities, nonoverlapping network ranges and distinct tenant A/B identifiers. Do not assume Windows Home provides the Hyper-V role or ARM can run a supported SQL Linux image.
- [ ] Emit precise installation prerequisites and a proposed resource budget. RAM 32/64 GB are planning profiles, not claims that the current machine passed a performance gate.
- [ ] Freeze installers/image identities with release version, download origin, SHA-256 and signer/digest verification procedure. A checksum supplied by the same untrusted replacement file is insufficient.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_environment.py`; fixtures cover subnet overlap, identical A/B targets, unsupported CPU, missing tools, wrong SDK, invalid evidence path and secret-bearing configuration. Run the PowerShell inventory on an actual Windows host and retain redacted output. CI cannot attest to that host. **Dependencies:** roadmap/evidence contracts only.

<a id="lab-i02"></a>
## LAB-I02 — VM Hyper-V và network

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I01. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** reproducible APP/DATA/VAULT machines and measured network boundaries, without enabling business handlers.

**Files:** new `scripts/lab/New-LabVirtualMachines.ps1`, `scripts/lab/Test-LabNetwork.ps1`, `deploy/lab/windows/cloud-init/*.yaml`, `deploy/lab/windows/network-policy.md`; new Linux `scripts/lab/Install-LabGuest.sh`, `scripts/lab/Deploy-LabApplication.sh`. Reuse `deploy/preprod/compose.yaml`, `Dockerfile.api`, `Dockerfile.worker`, `Caddyfile` and `scripts/Collect-App02B2PreprodEvidence.sh`.

**Interfaces:** `New-LabVirtualMachines.ps1 -EnvironmentFile ... -Plan` emits an exact change set; `-Apply -EnrollmentFile ...` accepts only enrolled, nonce-owned lab VM names/VHD roots and expected host fingerprint. Repeated apply converges; it does not recreate or reset existing storage. `Test-LabNetwork.ps1` emits observations with explicit vantage labels such as `WINDOWS_HOST`, `OUTSIDE_APP_BRIDGE`, `PRIVATE_NONPROXY_PEER`; it never labels those `EXTERNAL_INTERNET`.

- [ ] Create Generation-2 Linux VMs from authenticated images, using a dedicated lab switch and an explicitly planned NAT arrangement. Detect existing WinNAT/WSL networks and fail on conflicts rather than deleting them. Preserve VM/volume ownership manifests.
- [ ] Install Docker Engine/Compose from pinned trusted sources. Set guest time synchronization. Record kernel/package versions and effective firewall rules. Test the container-published-port path; an allowlist in UFW alone is not evidence that Docker traffic is filtered.
- [ ] Permit only APP HTTPS into the client network. Permit SQL/Vault access solely from enrolled workload/operator addresses and role paths. Keep API 8080, Worker, Docker API/socket and management paths private. Keep Docker daemon access out of app containers.
- [ ] Build once at the frozen source, record API/Worker image digests, distribute the same images to the run, and keep secrets as out-of-repo protected files. Never resolve `latest` during evidence replay.
- [ ] Exercise direct API bypass, forged forwarded headers, blocked SQL/Vault ingress and forbidden synthetic/dev switches. Preserve original RUNTIME denial codes and the distinction between readiness and admission.

**Verification:** first apply, second idempotent apply, subnet collision, wrong host, foreign VM name and attempted reuse of a data directory must have asserted outcomes. Run positive HTTPS and negative socket probes from both named observation points. **Dependencies:** LAB-I01. This PR owns VM/network/bootstrap and the application deployment wrapper, not successful Vault/application readiness. LAB-I03 supplies certificates/Vault later; LAB-S04/S06 add separate lab hosts. Readiness is checked then. This breaks the VM/Vault dependency cycle; state exactly which tier executed.

<a id="lab-i03"></a>
## LAB-I03 — Vault bền, TLS và credentials

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I02. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** durable single-node lab Vault with scoped workload/operator identities, trusted endpoint certificates and tested recovery of Vault itself.

**Files:** new `deploy/lab/windows/vault/raft.hcl`, `deploy/lab/windows/vault/README.md`, `scripts/lab/Initialize-LabVault.sh`, `scripts/lab/Test-LabVault.sh`, `scripts/lab/Rotate-LabWorkloadToken.sh`. Reuse `deploy/preprod/vault-policy-api.hcl`, `vault-policy-worker.hcl`, and existing B4 role-specific policies; no duplication of their permissions.

**Interfaces:** bootstrap takes certificate/key **paths**, persistent data path, enrollment record and externally supplied operator authentication, never secret values in arguments. It exports only policy hashes, key/cert versions, audit event references and readiness facts. The app continues to read separate API/Worker token files as implemented today. CA root private keys and Vault root/unseal material are outside app mounts and evidence bundles.

- [ ] Use normal server mode with persistent Raft storage; no `-dev`, in-memory storage or automatically unsealed CI configuration. Enable Vault audit before issuing test workload credentials.
- [ ] Issue distinct, narrowly scoped, revocable workload/operator tokens. Revoke bootstrap root privileges after setup. Keep unseal/recovery custody explicit and honestly `SOLO_OWNER_SELF_ATTESTED` when one person holds all material.
- [ ] Provision a private lab CA and separate valid certificates for APP, Vault and SQL names; import public trust into Windows, Linux and relevant containers. Retain existing Caddy external-cert file mounts; never grant application access to the CA signing key. Missing trust must fail rather than using `curl -k` or SQL trust overrides.
- [ ] Test real Vault 403 plus audit evidence for API→Worker, Worker→API and operator-role crossovers. Revoke/expire a token, rotate its mounted file safely and prove expected API readiness/Worker termination.
- [ ] Save a Raft snapshot and restore in an isolated Vault recovery target using appropriate historical recovery material; wait for post-unseal readiness. Do not rewind the active Vault along with tenant restore.

**Verification:** reboot persistence, sealed/unavailable Vault, untrusted certificate, stale token, wrong role and missing audit sink have explicit denied outcomes. Existing B2 CI remains synthetic evidence. **Dependencies:** VM/network, operator enrollment; per-role secrets for later increments are issued only when those roles exist.

<a id="lab-i04"></a>
## LAB-I04 — SQL thật và operator transport

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I03, LAB-S01. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** a real SQL instance with TLS, versioned migration receipts and a secret-safe operator connection path compatible with a Windows workgroup and Linux SQL.

**Files:** new `deploy/lab/windows/sql/README.md`, `scripts/lab/Install-LabSql.sh`, `src/FmcgDms.Preprod.OperatorTool/FmcgDms.Preprod.OperatorTool.csproj`, `Program.cs`, `OperatorSqlConnectionFactory.cs`; new `tests/FmcgDms.Preprod.OperatorTool.Tests/`. Reuse central package versions and all existing B3/B4/B5 migration files, including reviewed #53 files only after their source dependency is available.

**Interfaces:** proposed CLI `sql-probe --environment <path> --credential-ref <Vault reference> --output <path>` supports named inspection operations, not arbitrary SQL. `OpenAsync(ApprovedSqlTarget target, OperatorRole role, CancellationToken ct)` reads an allowed Vault secret in process and returns `Microsoft.Data.SqlClient.SqlConnection` with encryption enabled and certificate validation required. It does not accept caller-chosen server/database/password fields.

- [ ] Freeze server name, certificate SAN, edition/CU, instance identity and allowed DB names. Perform one-time bootstrap under an operator-controlled channel; API/Worker never receive that credential. Create separate migration/backup/restore/observer identities.
- [ ] Keep B3/B4/B5 and same-transaction tenant effects on one instance. Review narrow cross-DB bridges/module signatures and inherited grants. Require `TRUSTWORTHY OFF` and cross-database ownership chaining off; do not solve permission errors with shared sysadmin/db_owner.
- [ ] Default to SQL Authentication for the workgroup lab, with scoped credentials resolved from Vault in memory. Do not assume existing `sqlcmd -E` will authenticate to Linux. No passwords in `-P`, process arguments, environment connection strings, scripts, logs or artifacts; any diagnostic output must use a redacted error code.
- [ ] Apply versioned migrations via a dedicated identity, recording source/rendered hashes, exact target, history and verification reads. Reject unknown/partial schema, unresolved database-name placeholders and unsafe preexisting state.

**Verification:** trusted TLS succeeds; hostname mismatch/untrusted CA/wrong Vault role fail. Show API cannot DDL, observer cannot DML, absent bridge permission rolls back the protected source mutation, and secrets never appear in process command lines or serialized results. **Dependencies:** Vault/TLS; #53 source migration increment for B53 install. This tooling does not authorize a runtime handler.

<a id="lab-i05"></a>
## LAB-I05 — Tenant lifecycle và privilege probes

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I04. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** two nonce-owned tenant databases and reproducible real-principal permission observations, using the existing lifecycle implementation.

**Files:** new `scripts/lab/Invoke-LabTenantProvisioning.ps1`, `tools/app02b_lab/sql_permissions.py`, `deploy/lab/windows/tenant-fixtures.example.json`, tests in `tests/app02b_lab/`; reuse `src/FmcgDms.Preprod.TenantTool/Program.cs`, `SqlTenantFinalizer.cs`, `SqlTenantPrivilegeRetirer.cs`, `TenantProbeVerifier.cs` and their policies/migrations. Do not create duplicate lifecycle command implementations.

**Interfaces:** orchestrator invokes existing `request`, `approve`, `provision`, `finalize`, `retire-finalizer`, `verify`, `activate`, `suspend`, `revoke`, `start-reactivation` and `abort-reactivation` verbs under the correct role credentials. It emits a `TenantEnrollmentReceipt` with context, TenantId/BindingId/Version, physical DB identity, SQL principal SID/role hashes, actual lifecycle state and migration hashes. An ACTIVE registry row still does not enable business routing.

- [ ] Use separate identities for tenant A/B, provisioning, owner, finalizer, retirer, approver, verifier and runtime. Inspect effective inherited grants and cross-role credential reuse, not just direct GRANT statements.
- [ ] Collect direct real-SQL denial evidence for A→B, System Admin without tenant membership, finalizer ownership retake/impersonation after retirement, stale/expired leases, reused activation permits and crash after ownership transfer before ACK. Confirm no unintended peer table change.
- [ ] Keep incomplete tenants nonroutable; independently verify retirement receipt before activation. Never reset epochs or disable bridge triggers to make setup pass.
- [ ] Preserve all denied actions, credentials' nonsecret identities and raw SQL permission results for #52/#60/#64/#65. One operator playing test roles proves technical separation only.

**Verification:** provision A and B, repeat idempotently, interrupt/retry finalization, deny stale privilege use and test malicious A-valid-context→B SQL. Existing standalone tests remain untouched as their own evidence class. **Dependencies:** private SQL, Vault roles, #53 source where activation requires the new permit, named command/authority integration for runtime cases.

**Effective-privilege contract:** add `EffectivePrivilegeProbe` to the proposed OperatorTool only for approved read/deny probes, reusing existing lifecycle verbs. `InspectEffectivePrivilegesAsync(ApprovedSqlTarget target, OperatorSession session, CancellationToken ct) -> Task<PrivilegeWitness>` records principal SID, physical DB, inherited grants/denies, live sessions and observation UTC. Test revoked role at COMMIT and fresh/stale membership during LAB-S04/S07 integration; source enrollment can merge before those runtime scenarios. Never duplicate the lifecycle state machine or infer tenant authority from a System Admin JWT.

<a id="lab-i06"></a>
## LAB-I06 — Backup schedule và retained copies

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I05, LAB-E05. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** actual completed FULL/DIFF/LOG backups, durable transfer receipts and an inventory consumable by `B7BackupChain.Inspect`.

**Files:** extend `src/FmcgDms.Preprod.OperatorTool/` with `BackupCommand.cs` and `BackupInventoryReader.cs`; new `scripts/lab/Export-LabBackup.ps1`, `deploy/lab/windows/backup/schedule.json`, tests for inventory/transport. Reuse safety checks in `scripts/Invoke-App02B7Backup.ps1` and `src/FmcgDms.Preprod.Recovery/BackupChain.cs`; preserve the existing script's Windows-integrated path while adding the separately tested Vault-backed operator path.

**Interfaces:** `backup --environment ... --tenant <enrolled alias> --kind Full|Differential|Log --change-ref <ref> --output ...`; no caller-controlled SQL paths/DB names. Output `B7BackupInventory` plus original SQL header/msdb artifacts and transfer receipts: object identity/version, ciphertext SHA-256, key version, source/target failure domain and timestamps. An uploaded object is not marked decrypt-verified before an actual restore-principal read/decrypt test.

- [ ] Retain unique filename/approved staging root/server/binding/checksum guards, FULL recovery and AES-256 SQL backup encryption. Schedule regular FULL at least daily, DIFF ≤6h and LOG ≤5m; collect actual completion times and LSN/fork/base metadata.
- [ ] Separate backup, transfer and restore/decrypt identities. Preserve recoverable encryption material independently of the instance being restored. Keep B5 fence, Control Plane, Vault and receiver journal recovery policies separate.
- [ ] Target a configured external retained store when available; local Windows copies are reported as local-host storage. Do not fabricate off-host or WORM qualification for another folder/VM on the laptop. Capture retention settings without inventing a legal period.
- [ ] Treat late/missing logs, corrupted transport, absent key and refused reads as failures with alert/incident evidence. `RESTORE VERIFYONLY` is an early check, not the final recovery test.

**Verification:** actual chain across a full/diff/log cycle, missing interval, altered hash, wrong base/fork, filename reuse, unavailable transfer and wrong decrypt principal. **Dependencies:** SQL/operator credentials, tenant enrollment, evidence and alert contracts. A future actual schedule run must provide its own measured timestamps; synthetic accelerated time cannot satisfy physical scheduling proof.

**Dependency boundary:** persist a durable redacted `BackupFault` record and expose its schema in this PR. LAB-O01 consumes those records for real alert/incident delivery; do not wait for O01 to implement basic backup transport and do not claim alert delivery before O01 runs. Key receipt means the required material is recoverable under the restore role, not that key bytes belong in evidence.

<a id="lab-i07"></a>
## LAB-I07 — Durable outcome receiver

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I03, LAB-E05. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** an isolated receiver whose original-ID outcomes survive application/tenant restore and are independently queryable.

**Files:** new `tools/app02b_lab/receiver.py`, `tools/app02b_lab/receiver_schema.sql`, `tests/app02b_lab/test_receiver.py`; add narrowly scoped setup to observer configuration. Use Python standard-library SQLite with a durable transaction configuration (WAL plus synchronous=FULL, verified on the actual storage) for a low-volume lab journal on the observer, outside APP/DATA rollback; it is an explicit non-provider surrogate.

**Interfaces:** `POST /effects` accepts immutable MessageId, SourceId and payload hash under an authenticated lab client. A transaction durably commits the journal/effect before ACK. Replay with identical identity/payload returns the original receipt; changed payload or SourceId→new MessageId returns conflict. `GET /outcomes/{MessageId}` returns journal epoch/sequence, identity, outcome and receipt hash. A protected operator-only fault hook closes the connection after COMMIT for lost-ACK testing.

- [ ] Enforce unique MessageId and source/identity binding inside the transaction, not an in-memory cache. Persist committed effect count, request hash and receipt; restart must retain them.
- [ ] Support COMMITTED, UNKNOWN and independently proven NOT_SENT semantics. Absence/404 alone is UNKNOWN; NOT_SENT requires authoritative complete-journal and sender-dispatch evidence. No automatic new-ID retry.
- [ ] Authenticate and scope the receiver and fault endpoint; record safe fault IDs and contexts. Do not expose the fault controller on application ingress or place observer credentials in Worker.
- [ ] Map raw observations to existing `B7ExternalOutcome` only after verification. Its `IndependentReceiverJournalVerified` field cannot be set by trusting an app response or by pretending a solo observer is an independent human.

**Verification:** simultaneous duplicate sends, process restart, payload conflict, ACK lost after durable COMMIT, unavailable outcome lookup and sender restore/retry produce effect count ≤1; unknown remains HOLD/CASE. **Dependencies:** evidence/observer/TLS, Worker integration for end-to-end scenarios; implementation can be reviewed independently before that integration.

**Dispatch boundary:** this receiver is an independently journaled lab surrogate under the actual solo custodian. It proves durable dedup/outcome mechanics, not independent human review. Sender precheck cannot guarantee no remote COMMIT after revoke; LAB-S06 must either implement and test an agreed receiver-side permit/fence or leave that stronger assertion BLOCKED. This PR does not invent an exactly-once external protocol.

<a id="lab-i08"></a>
## LAB-I08 — Restore/cutover và đo safe return

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-I06, LAB-I07, LAB-S03, LAB-S07, LAB-S08, LAB-O01, LAB-E06. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Deliverable:** an operator-driven restore protocol and lab drill report; no unconditional activation command or physical approval claim.

**Files:** new `src/FmcgDms.Preprod.OperatorTool/RestoreCommand.cs`, `RestorePlan.cs`, `scripts/lab/Invoke-LabRestoreDrill.ps1`, `tools/app02b_lab/recovery_measurements.py`; tests for restore state transitions and raw clock/witness reconciliation. Reuse `SafeRestore.cs`, B4 lifecycle and mandatory B53 authority; never change their false-by-design acceptance properties into deployment permissions.

**Interfaces:** typed `RestorePlan` binds context, incident/change reference, exact SQL instance, TenantId, original/staging/canonical target names, expected binding/fence versions, ordered backup hashes/LSNs, recovery point, approved staging paths and key references. Plan→execute→verify→reactivation-request are distinct operations. The observer owns failure UTC/monotonic anchors and safe-return observations.

- [ ] Suspend A and stop its claims/dispatch; capture full A/B fingerprints, current B3/B4/B5 authority, Vault versions and receiver journal. Restore backups into an isolated nonroutable staging database; do not overwrite a live target or peer.
- [ ] Define and review the same-instance canonical-name/binding cutover explicitly. Existing `B7SafeRestore` requires the canonical `FmcgPpT_<TenantId:N>` name and exact BindingId/Version. A staging name is not accepted by relaxing that check. Only a typed suspended cutover with fresh authority and CAS may establish the intended canonical target; uncertainty leaves A SUSPENDED. LAB-I08 owns the narrow B4 cutover extension and its crash/CAS tests as specified below; no unnamed follow-up may supply an unreviewed rename.
- [ ] Apply FULL/DIFF/LOG with validated fork/base and STOPAT, run CHECKDB and independent reconciliation, then validate revoked JWT/refresh/Worker/CASE denial. Restore old Identity/Registry snapshots without rewinding B5. Never restore the whole DATA VM or use row rewrites as PITR evidence.
- [ ] Reconcile original external MessageIds; COMMITTED dedups, UNKNOWN remains HOLD/CASE. Request fresh reactivation approval only after verification; a concurrently advanced fence invalidates that request.
- [ ] End RTO only after safe API **and** Worker re-entry with current authority. Compute RPO from the latest independently committed recovered witness. Require 0≤RPO≤900 and 0≤RTO≤14400. A host/observer reboot that loses the monotonic anchor invalidates the measurement instead of resetting its stopwatch.

**Verification:** A-only PITR with B unchanged, wrong target/binding, corrupt/missing log, lost key, bridge/fence outage, concurrent revoke, stale approval and lost-ACK replay. Keep raw restore logs and failed attempts. **Dependencies:** reviewed LAB-S01/S02 source and runtime/Worker/CASE authority; #53 physical closure is not a prerequisite for its own drill, tenant lifecycle, backup/receiver, audit/alerts and evidence collector. Physical closeout additionally requires original external witnesses/reviewers; the local drill alone cannot close #56.


**Current limitation:** B4 exposes lifecycle transitions but no restore/rebind API. Its canonical name is `FmcgPpT_<TenantId:N>`; B7 requires exact TenantId, BindingId and BindingVersion. PR #74 also rejects Registry BindingId/DatabaseName mutation. I08 must implement the following narrow protocol, not invoke a nonexistent rebind or weaken those checks.

**New I08 source:** `TenantRestoreCutover.cs`, Tenancy migration `20261119_002_restore_cutover.sql`, `RestoreCutoverSqlTests.cs`; tables `PreprodRestoreOperation` and `PreprodTenantPhysicalBinding`. Add `BeginRestoreCutoverAsync(RestoreCutoverRequest, CancellationToken)` and `CompleteRestoreCutoverAsync(RestoreCutoverCompletion, CancellationToken)` returning `RestoreCutoverReceipt`; authenticate separate restore/verifier SQL roles. No public HTTP endpoint or arbitrary database-name input.

**Identity:** pin OperationId, TenantId, canonical/staging/quarantine names, BindingId/BindingVersion, Registry rowversion/security epoch, B5 fence epoch, backup-chain hash and approved target. Preserve TenantId/BindingId/BindingVersion exactly; mismatched snapshots fail, without general rebinding. Record both physical fingerprints: enrolled instance, database_id, database_guid, recovery_fork_guid, owner SID and operation-bound file inventory. One GUID alone need not change or uniquely identify a clone. Preserve original lineage.

**Durable protocol:**

1. Acquire a session-owned per-tenant restore application lock. Begin under Registry/B5 compare-and-swap, requiring SUSPENDED, matching current binding and epochs. Advance the suspension/security barrier through the reviewed B53 bridge, invalidate prior verification, and persist `PREPARED`. Drain/deny tenant execution; retain B fingerprints. Restore and validate only the enrolled staging target, with runtime access denied.
2. Persist `STAGE_VERIFIED` after CHECKDB, marker/schema, owner/grants, restored source/outcome reconciliation and independent readback. Do not reuse old provisioning verification as proof for the restored physical instance. Recheck current authority before each destructive name operation.
3. Persist intent, then rename old canonical A to its operation-specific quarantine name; observe SQL catalog and record `OLD_QUARANTINED`. Rename the verified stage to canonical A; observe and record `CANONICAL_INSTALLED`. These database DDL steps are **not claimed atomic with Registry SQL**. Both databases remain nonroutable; quarantine is retained without automatic deletion.
4. Completion atomically locks Registry/B5/current physical binding, verifies expected versions and canonical fingerprint, and installs the physical-binding receipt. I08 also makes the lab named-command guard require that receipt and actual target fingerprint. Return `SUSPENDED / CUTOVER_VERIFIED`. Fresh `START_REACTIVATION → verify → ACTIVATE`, current B53 permit, privilege retirement and key/outcome checks remain mandatory.

**Restore/activation exclusion, owned by I08:** a restore-only applock is insufficient. All non-DBA lifecycle activation paths, B53 permit issuance/consumption, verification completion and lab command/Worker admission must reject a pending restore operation (`PREPARED` through canonical-install/receipt reconciliation). Update the relevant B4/B53 procedures/guards with one documented lock order and current pending-operation check inside their existing CAS transaction. START_REACTIVATION cannot race the name transition; an old physical receipt cannot authorize the quarantined/current-old DB. Only verified completion or explicitly verified compensation with a current physical receipt releases the restore barrier, while tenant state remains SUSPENDED. SUSPEND/REVOKE remain available during restore; a newer revocation invalidates the completion CAS. Test concurrent start-reactivation/verify/activate/permit replay at every durable phase, including after restore process crash.

**Source-provenance lineage, owned by I08:** the physical-binding receipt includes immutable old→new incarnation lineage, OperationId, unchanged TenantId/BindingId/BindingVersion, backup-chain hash and actual source/target fingerprints. Extend the LAB-S05 mapping consumers in Commands, Worker and CASE to check this receipt under current Registry/B5 authority. Original `App02LabIdentityBinding`/`App02ReceiptAuthority` rows remain byte-immutable. Valid lineage permits reading the preserved source history on the approved incarnation; it grants no permission or renewed session. Test legitimate restore, arbitrary clone, wrong lineage/chain, old-incarnation replay and attempted provenance rewrite.

**Crash/retry:** reconcile journal against actual names/fingerprints before further DDL. Same OperationId is idempotent; conflicting bytes fail. Lost ACK, failed rename or concurrent revoke leaves SUSPENDED; changed epochs require fresh approval. Before canonical installation, explicit compensation can return unchanged quarantine to canonical under fresh authority, still suspended. After installation require a new reviewed restore operation; never rollback-to-ACTIVE or rewind epochs.

**Mandatory tests:** crash before/after each rename and receipt COMMIT; stale fence/rowversion; foreign stage/peer name; wrong binding/version/owner; duplicate operation; resumed lost ACK; denied old physical incarnation; B unchanged; no activation without fresh verification/permit.

**Solo lab admission:** introduce a separate `LabRestoreTechnicalInspection` reporting mechanical checks and `humanIndependence=false`. Never change or satisfy original `B7SafeRestore` independent-human fields with test-role aliases. Under an explicitly approved lab-only admission contract, separate test-role permits can authorize mechanical reactivation of enrolled test tenants through the lab entrypoint; measure `labRpoSeconds/labRtoSeconds` at safe API/Worker return while physical status stays NOT_VERIFIED. Without that scoped contract—or where the applicable contract still demands independent humans—reactivation is BLOCKED, A stays suspended and technical RTO remains incomplete. SQL-online is not safe return.

**Fault scope:** A-only PITR must demonstrate B data/service preservation; verify resource contention/availability rather than assume it. Shared B3/B4 restore is a separate coordinated fault: affected admission fails closed and B tenant data must be preserved; B service availability is not assumed. Old-B3/B4 scenarios for B01-06/B03-04 are denial-only and earn no shared-Control-Plane safe-return/RTO claim.

### LAB-I08 shared-Control-Plane fault cleanup boundary

An A-only cutover cannot reconcile a restored Registry epoch below the retained B5 watermark. Its restore journal/physical-binding table also lives inside the restorable B4 database. Never use those rewound rows alone as current authority.

I08 owns a guarded **exact-current checkpoint cleanup** for the denial drill: before injecting the old B3/B4 snapshot, quiesce the lab and retain native current B3/B4 backups plus an external checkpoint manifest outside their rollback boundary. Bind their backup hashes/LSNs, schema/bridge definitions, current session/tenant fence digest, physical bindings, source and operator approval. Keep B5 unchanged throughout. After observing stale-rights denial, restore that recorded current checkpoint only if external current B5 authority and approved physical lineage still match exactly; verify enabled bridges, all relevant epochs/session/target identities, durable audit and outcome continuity before any reactivation request. This is recovery of the known current checkpoint, not lowering epochs or synthesizing reconciliation.

Test an extra revoke/fence advance between checkpoint and cleanup: comparison must deny return. Missing checkpoint, rewound journal, partial B3/B4 restore, bridge mismatch or new authority keeps the environment quarantined/BLOCKED; preserve evidence and retire/re-enroll disposable lab targets through the approved lifecycle if needed. Do not disable triggers, edit epoch rows, reset B5 or restore a whole DATA VM. General shared-Control-Plane recovery after arbitrary concurrent authority changes is not implemented by this bounded cleanup and remains BLOCKED pending a separately reviewed procedure. Tenant A-only PITR is the scenario that measures lab safe-return RPO/RTO in this roadmap.

## Source anchors

- `deploy/preprod/compose.yaml`: separate API/Worker, no SQL/Vault in app stack, non-dispatching foundation and NO_GO.
- `scripts/Invoke-App02B7Backup.ps1`: plan-only default, exact server/binding/staging guards, encrypted backup, `sqlcmd -E -N`, VERIFYONLY only.
- `src/FmcgDms.Preprod.Recovery/BackupChain.cs`: `B7BackupInventory`, actual schedule/LSN/fork/key checks, no PREPROD admission.
- `src/FmcgDms.Preprod.Recovery/SafeRestore.cs`: exact canonical DB/binding, distinct reviewer, external outcomes, actual RPO/RTO, no activation permission.
- `src/FmcgDms.Preprod.TenantTool/Program.cs`: existing lifecycle verbs and Vault role dispatch.
- PR #74 `aaa32dbaff987494190e628592588317a46e3ce0`: B3/B4/B5 same-instance SQL transaction and remaining runtime/physical gaps.

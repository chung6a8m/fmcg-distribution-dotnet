# APP-02B Local PREPROD Lab — Design and Technical Contracts

**State:** DESIGN_PROPOSED / IMPLEMENTATION_NOT_STARTED. Prepared 2026-11-19 from `main` `16d96c71a7967c216ebfa599693807a2b54e7435`. The user requested an implementation roadmap, not deployment, automatic merge, a staffing waiver or physical acceptance. The recommendations below are implementation defaults for that roadmap; they are not new Owner decisions.

**Read with:** [roadmap](../app-02b-local-preprod-acceptance.md), [original B.1 cases](../../security/app-02b1-acceptance-evidence.md), [B.9 physical contract](../app-02b9-independent-preprod-oracle.md), [Owner decisions](../../decisions/owner-app02b-2026-11-17.md), and [Codex handoff](codex-handoff.md).

## 1. Intended result and limits

Build a reproducible operator-controlled Linux lab on a Windows workstation, then make all original 30 case IDs executable or explicitly blocked with an identified missing witness. Use actual SQL Server transactions, real HTTP/TLS, durable Vault, real process/VM faults, native SQL backup/PITR, observable Worker/CASE execution, immutable audit and a durable external-effect surrogate. The observer reads original subsystem evidence rather than accepting application success flags.

Two different deliverables are intentional:

1. **Local engineering rehearsal:** a solo developer can reproduce, diagnose and retain evidence. Output is `LOCAL_OPERATOR_LAB_TESTED` only after actual measurements, with `SOLO_OWNER_SELF_ATTESTED` where applicable. These are new proposed lab labels, not aliases accepted by the existing physical oracle.
2. **Original physical closeout:** requires the unchanged B.1/B.9 obligations, actual approved environment, independent human/key custody, external witnesses and all required final-source checks. A laptop is not automatically disqualified as an operator-controlled VM host, but shared physical failure domains and solo custody do not satisfy all obligations. Issue #56 remains OPEN until the applicable original contract is met. An alternative solo acceptance contract needs a separate explicit versioned Owner disposition and a distinct result label; this plan does not create it.

The original `PREPROD_FOUNDATION_VERIFIED` label remains unearned. APP-02C/D/E, production/Pilot/Go-Live, real posting, real AC-11/EV-08 and production P0 are not authorized or verified by this plan.

## 2. Architecture decision and alternatives

**Recommended implementation default:** Windows operator/observer plus three Linux guests: APP, DATA, VAULT. Optional fourth OBSERVER guest when memory permits. This preserves the approved Linux VM + Docker Compose deployment model.

| Option | Useful purpose | Disposition |
|---|---|---|
| Hyper-V APP/DATA/VAULT; Windows observer | Explicit VM/service identities, restart and network boundaries, compatible operator scripts | Recommended planning baseline; preflight checks host eligibility before mutations |
| Docker Desktop/WSL2 rehearsal | Faster Compose experimentation on a smaller laptop | Optional development fixture; record its actual isolation/failure domain, never relabel it as independent physical evidence |
| Separate off-laptop PREPROD host and independent operators | Removes some laptop failure/custody limitations and supports original external witnesses | Future deployment of the same automation; not required to author or unit-test the kit |

Windows-native IIS is not a target of this track. No Kubernetes, message broker, external OIDC server or domain controller is needed for the proposed baseline. Introduce additional services only for an identified acceptance obligation and a separately reviewed change.

### Topology and resource assumptions

| Boundary | Responsibility | Initial sizing estimate, not a verified requirement |
|---|---|---|
| Windows | Git/Codex, PowerShell, SSH, Python/OpenSSL, LocalDB regressions, observer/telemetry | Preserve enough free RAM for build and IDE; 32 GB total is constrained, 64 GB more comfortable |
| APP | Caddy, separate API/Worker containers, private outbound paths | 4 GB RAM |
| DATA | One supported SQL Server instance; separate A/B, B3 Identity, B4 Registry, B5 fence databases; separately permissioned auxiliary stores | 8–12 GB RAM; cap SQL memory explicitly |
| VAULT | Persistent Vault, HTTPS, workload/operator policy isolation, audited key lifecycle | 2–4 GB RAM |
| Optional OBSERVER | Driver, evidence collector, alert/receiver journal and telemetry | 4 GB RAM |

Plan approximately 200–300 GB free SSD and validate real usage before long runs. CPU x64/virtualization/SLAT, Windows edition, disk, NAT and subnet conflicts are preflight outputs, not assumptions about the user's machine.

### SQL atomicity is a deployment constraint

Current B.5/PR #74 uses one SQL connection/transaction with cross-database effects. **B3, B4, B5 and the tenant database involved in that protected transaction stay on the same supported SQL Server instance. B6 policy/source checks and any audit-intent store participating in the transaction must also be on that instance; LAB-S07 owns the B6 installation and actor-to-SQL-principal bridge.** Do not silently move the fence or tenant effect to another instance, introduce linked servers or rely on MSDTC. Any such change needs a new atomicity design and tests.

Separate databases and SQL principals establish scoped engine access, not independent physical failure domains. B5 must never be restored with tenant/B3/B4 PITR. Keep a separately audited fence backup/key/retention policy and current high-watermark witnesses. A whole DATA VM checkpoint rewind is not an accepted restore procedure. SQL sysadmin and host administrator remain privileged across this shared instance and must be disclosed as such.

Keep `TRUSTWORTHY` and unrestricted cross-database ownership chaining off. Review narrowly granted bridge/module permissions and effective inherited rights. Never repair a missing cross-database permission by granting workload sysadmin/db_owner.

### Network and TLS

- An explicit private lab subnet and management path; inspect existing WSL/Docker/Hyper-V NAT before creating one. No broad router forwarding or public binding is implied.
- Publish only approved HTTPS on APP into the lab client network. SQL/Vault/Worker/Docker management sockets have explicit private allowlists; container networks alone are not an egress ACL.
- Observer evidence identifies its vantage: outside app bridge, host/LAN, private peer or genuine external origin. These are not interchangeable.
- Use trusted TLS for Caddy, Vault and SQL separately, including negative wrong-name/expired/untrusted-chain probes. Preserve the existing mounted certificate flow. No `curl -k`, SQL trust-server-certificate bypass or root-token fallback.
- Single-node persistent Vault is a lab simplification; no HA claim. Its state and unseal/recovery material must not follow APP/DATA restore operations.

## 3. Existing source and integration boundaries

The base PREPROD API currently references Runtime and Identity only. Its Worker is intentionally non-dispatching. `PreprodStartupGuard` enforces `FOUNDATION_ONLY / NO_GO` and rejects synthetic/direct DB/business switches. `SqlAuthorizationFence.ExecuteAsync` remains an internal LocalDB-only arbitrary-callback test API. Neither guard may be removed to make the lab run.

The security workstream defines a separate explicitly scoped lab entrypoint over a typed authorization/execution kernel. LAB-S05/S06 extract the existing APP02 intake/processing kernel without changing canonical bytes or journal semantics. Real outbox/CASE witnesses require explicit admission for the original synthetic unitless fixture-ledger effects, with invoiceEligible=false; absent that scope they remain BLOCKED. Real money/provider posting remains outside the lab. Real SQL effects are bounded to approved test-only data and use current authenticated B3/B4/B5 authority. New lab execution is not a renamed synthetic API or a self-attesting success endpoint. Existing foundation-only binaries retain their original negative tests.

Default activation is disabled. Before a mutating run, an operator supplies an approved, externally retained environment manifest identifying exact hosts, databases, permitted operations, source/images and fault window. The preflight/runner can author and validate a proposal but cannot manufacture approval signatures or grant itself a broader target. Already authorized operations may proceed without repeated approval prompts; scope changes require a new concrete disposition.

Tenant SQL provisioning, B3 accounts and B5 membership/RBAC are separate responsibilities. I05 owns B53 permit issuance before initial activation/reactivation; S09 owns the bounded fixture account and authority enrollment lifecycle before S04 can admit a positive command. Existing CI-only DBA inserts and B6 policy actors do not satisfy B5 ADMIN-01. S09 prepares an explicit lab-only approval candidate and leaves original independent-human/physical ADMIN-01 open; an environment manifest alone is not implicit permission to create users or grants.

## 4. Common file ownership and interfaces

All paths in this table are **proposed new paths**, unless explicitly marked existing. Their owner increment is assigned in the roadmap. Existing source paths cited by individual tasks are verified against the baseline tree.

| Path | Responsibility |
|---|---|
| `deploy/lab/windows/` | Nonsecret topology/version/configuration examples and Linux guest provisioning assets |
| `scripts/lab/` | Thin PowerShell operator commands and guarded guest lifecycle operations |
| `tools/app02b_lab/` | Python driver, immutable run context, collectors, case registry and lab reporting |
| `tools/app02b_lab/schemas/` | Versioned environment/run/witness/result contracts |
| `tests/app02b_lab/` | Parser/collector/runner/oracle negative tests without privileged deployment |
| `src/FmcgDms.Preprod.OperatorTool/` | Narrow secret-safe SqlClient operator operations when existing tools cannot perform them |
| `src/FmcgDms.Preprod.TenantTool/` (existing) | Existing lifecycle verbs; reuse rather than duplicate provisioning/activation state machines |
| `tools/app02b9/expected-v1.json` (existing) | Original frozen 30-case expected fixture; read unchanged |
| `tools/app02b9/oracle.py` (existing) | Original physical validator; retain strict evidence class, external trust and reviewer separation |

### Lab environment and run identity

Environment configuration contains addresses, database bindings, approved roots, tool pins and **secret references only**. Raw credentials are never repository/config/report fields. Pin host SSH identity and TLS trust independently; first-seen discovery is not automatically trusted.

LAB-E05 owns the exact JSON schemas and Python types; LAB-S09 owns the matching shared C# LabRunContext DTO and serialization contract tests, consumed by LAB-S04/S06. Common semantic fields are:

`LabRunContext = { runId, environmentId, sourceHeadSha, fixtureSha256, configurationSha256, startedUtc, hostSha256, mode }`

- `mode` for this local profile is `LOCAL_OPERATOR_LAB`; not the physical oracle's evidence class.
- `runId` uses a portable timestamp plus random identifier, contains no path separators or Windows-invalid colon, and is immutable after creation.
- `sourceHeadSha` is the full checked-out commit; a dirty worktree or differing deployed image provenance blocks collection.
- `fixtureSha256` covers byte-identical original expected bytes before the run.
- Host/DB IDs and exact private addresses remain in protected evidence; public summaries use stable redacted references.
- A deliberate SQL restore may change local database identifiers; retain both identities, immutable approved old→new lineage and Registry transition. This declared transition may stay in the same run; arbitrary target identity drift cannot. Preserve original source identity mappings and verify lineage rather than rewriting them. Never rewrite the baseline fingerprint to conceal a binding mismatch.

Each case result has exact canonical `caseId`, scope, `technicalResult` (`NOT_RUN|BLOCKED|FAIL|PASS`), expected/actual/diff, positive and negative witness references, fault ID, timestamps and missing requirements. For local runs, `originalGateStatus` remains `NOT_VERIFIED`. B05-02 is BLOCKED while independent human review is absent, even if AI source review and separate solo review passes exist.

Use content hashes and append-only attempt history; no symlink/path traversal outside the configured evidence root. A manifest references original raw captures, not newly generated summaries pretending to be raw data. Secrets/redaction tests cover failure paths as well as successful output.

## 5. Credentials, keys and operator transport

Reuse dedicated B4 Vault/SQL roles for requester, approver, provisioner, finalizer, retirer and verifier; use actual separate principals during deny probes. SQL roles, Vault tokens and human independence are different things. The same human operating two test identities demonstrates technical role enforcement only.

The existing backup script invokes `sqlcmd -E -N`. A Windows workgroup talking to Linux SQL must not assume Kerberos/Integrated Authentication exists. The proposed baseline uses narrowly scoped SQL Authentication obtained from Vault inside `Microsoft.Data.SqlClient` operator execution, with encrypted connection and validated certificate. Do not put passwords/connection strings in command-line arguments, process environment, transcripts or artifact JSON. Existing approved integrated-auth deployments may keep their working path; the new adapter must not weaken it.

Keep the acceptance signing roots, Vault root/unseal material and restore credentials under their actual custodians. The application has no observer signing key, and observer SQL permissions cannot create the outcomes it is validating. Redaction preserves audit correlation without leaking tokens/key bytes. A failed required audit record must deny the protected effect or follow an explicitly specified durable atomic-outbox design; best-effort logging is not immutable evidence.

## 6. Recovery and outcome invariants

- Native SQL FULL recovery: daily FULL, differential interval <=6 hours, LOG interval <=5 minutes; verify actual job times, checksums and continuous LSNs, not just configured schedules.
- Encrypted staging backup alone is not off-host proof. Transfer receipts bind bytes/key/version/target; restore from retained copies and retain original decryption continuity evidence. A second local folder or VM disk is not proof against laptop loss.
- Restore into a fenced, nonroutable staging target first. Existing canonical-name/binding checks remain; LAB-I08 owns and reviews the typed canonical cutover under suspension before activation. No bypass by labeling a staging DB as the approved live binding.
- RPO <=900 seconds and RTO <=14400 seconds are measured from committed/recovered witnesses and actual safe API **and Worker** return. Observer monotonic anchors must survive the injected fault; restarting the observer/host invalidates that measurement unless an approved independent anchor exists.
- Compare current independent fence, approvals, keys and outcome journal before reactivation. Missing authority, wrong binding, corrupt log or unknown effect keeps the tenant suspended/held.
- External surrogate records stable MessageId/SourceId and durable outcomes. COMMITTED with lost ACK is lookup/replay of the same identity; UNKNOWN requires HOLD/CASE, never a new ID resend. No real provider is introduced.
- Preserve peer B full-table fingerprints and direct permission denial. A controlled A-only drill cannot mutate B to make an oracle pass.

## 7. CI, security disposition and final-source rules

`FmcgDms.slnx` and `Run-App00A.ps1` do not enumerate every dedicated B suite. Build/test the actual dedicated projects and reuse authoritative workflow requirements. MTP commands use `--project` or `--solution` as supported by the pinned SDK, with runner arguments after `--`; do not assume VSTest `--filter` syntax.

The physical oracle requires ten named checks: APP00A_FULL, APP02B2_RUNTIME, APP02B3_IDENTITY, APP02B4_TENANTS, APP02B4_WEB, APP02B5_AUTHORIZATION, APP02B6_POLICY, APP02B7_RECOVERY, APP02B8_OPERATIONS, APP02B9_ORACLE. Security/#59, adopted B53 and new lab checks are additional requirements when applicable, never substitutes. A path-filtered B9 run missing on the accepted SHA is still missing even if ten other workflows are green.

Issue #59 is CLOSED / `WITHDRAWN_ADVISORY_SCOPED_RISK_ACCEPTED / NOT_PATCHED`, with a source/ephemeral-CI-only policy expiring 2026-12-19T00:00:00Z. No automatic renewal or frontend publication is authorized. This lab initially exercises backend HTTP/SQL directly; keep web/PWA regressions in their authorized CI scope. Introducing deployed frontend artifacts or changing the reviewed dependency/input graph needs a concrete new assessment/disposition. Both npm audits remain mandatory.

Source code can be developed and reviewed while physical issues remain open, but a prior PR-specific code-only exception is not transferable. In particular PR #74 is not merge-eligible merely because this roadmap references it. Obtain an applicable source-merge disposition if its existing gate still demands missing physical evidence; otherwise leave the candidate as a reviewed draft. This avoids falsely closing #53 to unlock the code needed to test #53.

Freeze the runtime source before final collection. Keep final raw bundles/signatures and acceptance records outside Git. A later documentation commit may describe the tested SHA as historical evidence; it is not itself a tested deployment. New source/fixture/image changes invalidate stale review/CI and affected runtime evidence according to the original contract. Do not create an endless cycle of committing a new current SHA into the very file whose SHA it changes.

## 8. Five review risks and required ownership

1. Wrong host/database, stale binding or whole-VM rewind: platform/restore tasks must demonstrate rejection without any target mutation.
2. Revocation between authorization and COMMIT/ACK: Identity/named-command/Worker tasks need deterministic barriers and atomic rollback witnesses.
3. Missing secret/key/audit/observer after partial success: Vault, audit, outcome and collector tasks must retain a denied/held state and preserve original evidence.
4. Re-signed fabricated or replayed evidence, self-review or changed HEAD: evidence/oracle tasks reject wrong class, paths, hashes, role provenance and source mismatch.
5. Ambiguous CI retry and recovery timing: CI/restore tasks retain failed attempts, distinguish bootstrap retry from business retry, and validate observer continuity.

Each risk is mapped to tests in the individual increments. These are review targets, not a claim that the tests or controls have already been implemented.

## 9. Official platform references

Version choice is confirmed and pinned during preflight; these references support platform mechanics, not approval of an untested version:

- [Hyper-V overview](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview)
- [Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/)
- [SQL Server Linux installation](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/setup?view=sql-server-ver16)
- [SQL Server Linux container support](https://learn.microsoft.com/en-us/sql/linux/containers/deploy?view=sql-server-ver17)
- [SQL Server 2022 Developer edition](https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2022?view=sql-server-ver16)
- [Vault persistent Raft storage](https://developer.hashicorp.com/vault/docs/configuration/storage/raft)
- [SQL backup/restore guidance](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/back-up-and-restore-of-sql-server-databases?view=sql-server-ver17)
- [OpenTelemetry Collector](https://opentelemetry.io/docs/collector/)
- [Seq telemetry capabilities](https://datalust.co/docs/an-overview-of-seq)
- [Codex native Windows and WSL guidance](https://learn.chatgpt.com/docs/windows/windows-sandbox)

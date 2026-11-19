# APP-02B LAB — Operations Increments

**State:** PLAN_ONLY / IMPLEMENTATION_NOT_STARTED. Baseline `16d96c71a7967c216ebfa599693807a2b54e7435`, 2026-11-19.

**Goal:** Produce real delivered alerts, correlated durable traces, incident actions and guarded diagnostic retention.

**Architecture/stack:** [Design and contracts](design.md), .NET 10/SQL Server, PowerShell/Linux and Python as assigned below. **Global constraints:** [Design sections 1–7](design.md); immutable business baseline, retained guards, source-bound admission and no automatic physical acceptance. **Review focus:** [five cross-cutting risks](design.md#8-five-review-risks-and-required-ownership). **Execution/commands:** [Codex handoff](codex-handoff.md). A `Create/New` path or API below is proposed, not a claim that it already exists.

Every increment must add meaningful regression/negative tests for its owned invariant, verify relevant retained suites, preserve failed evidence, and record source-only versus live results separately. Commands for new components apply only after that increment implements them.
<a id="lab-o01"></a>
## LAB-O01 — Telemetry, alert và incident

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-S06, LAB-S08, LAB-I06. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Dependencies:** protected API/Worker/CASE integration; durable audit writer; LabRunContext; separate observer and TLS. **Scope:** one reviewable operations pipeline for B04-02..04. No business/provider admission.

**Modify:** `src/FmcgDms.Preprod.Operations/B8Operations.cs`, new Lab.Api/Lab.Worker `Program.cs` and project references, `Directory.Packages.props`. **Create:** `src/FmcgDms.Preprod.Operations/OperationsRegistration.cs`, `AlertDispatchService.cs`, `IncidentService.cs`, `Migrations/20261119_002_operations_delivery.sql`; `deploy/lab/windows/observability/{compose.yaml,otel-collector.yaml}`; `tools/app02b_lab/observer/alerts.py`; source tests `OperationsDeliveryTests.cs` and SQL tests `OperationsDeliverySqlTests.cs` in existing B8 projects.

**Interfaces:** `AddPreprodOperations(IServiceCollection, IConfiguration)` registers explicitly enabled lab wiring. `QueueAlertAsync(B8AlertCommand command, CancellationToken ct)` returns stable AlertId. `B8AlertCommand` binds tenant, correlation, type, source/message IDs and approved configuration version. `DispatchPendingAsync(CancellationToken ct)` reads a durable SQL delivery queue; `AcknowledgeAlertAsync(Guid alertId, Guid authenticatedOperatorId, CancellationToken ct)` records a separate human/operator action. `AppendIncidentActionAsync(Guid incidentId, B8IncidentAction action, Guid authenticatedActorId, CancellationToken ct)` appends immutable events. Use existing B8 enums/verifiers, not parallel definitions.

- [ ] Add failing tests for lost delivery ACK/restart, duplicate AlertId, receiver unavailable, forged ACK timestamp, absent authority/config, missed escalation, secret-bearing telemetry and broken trace propagation.
- [ ] Register OpenTelemetry SDK exporters for existing `B8Telemetry.InstrumentationName`, ASP.NET/HTTP/SQL activity and structured logs. Pin compatible packages; retain declared telemetry tag allowlist and low-cardinality metric labels. Persist validated W3C trace context beside durable source/message metadata before ACK so Worker restart retains lineage; never change canonical business event hashes or trust baggage as authorization. Do not persist arbitrary baggage/header contents.
- [ ] Configure Collector + Seq on observer boundary with TLS/authenticated ingestion and export of raw spans/logs/metrics for evidence. Missing collector must appear in health/alert evidence, not fabricate delivered spans. Security audit durability follows its separate audit contract, not telemetry buffer success.
- [ ] Add SQL queue, attempt history and incident journal with tenant-scoped EXEC-only procedures. Alert IDs remain stable across at-least-once retries; source/audit transition and queue intent share the approved transaction. Dispatcher authenticates with a distinct Vault credential. Receiver stores AlertId/payload digest/ReceivedUtc in its own durable SQLite journal before returning receipt; changed content under same ID conflicts. Receiver ACK is a distinct authenticated action, never an HTTP-200 delivery synonym.
- [ ] Configure test-only alert rules for cross-tenant deny, refresh reuse, Worker backlog and recovery-window breach. Values, severity, owner, ACK deadline, escalation deadline and runbook version come from a versioned `LAB_TEST_ONLY` fixture. Real operational values remain UNSET/BLOCKED until approved. Record lab human custody truthfully.
- [ ] Implement incident Opened→Assigned→HandoffAcknowledged→Escalated/Mitigated→Closed transitions; deadline processing persists due times across restart. Closure requires separate authorized reviewer role; solo actors can test role enforcement but cannot establish independent human review. Feed read-back journal events into existing verifiers; do not construct receipt/ACK times from driver expectations.
- [ ] Verify actual API→Worker→SQL/inbox/outbox trace with replay/lost ACK; force receiver outage, restart dispatcher, observe one durable alert plus multiple attempts, perform operator ACK, and inspect incident escalation/raw observer journal. Missing receiver/ACK is FAIL/BLOCKED. Run existing B8 source/SQL suites and full affected final-head regressions.

**Exit:** operational mechanisms produce real lab telemetry, receipts and incident history; B04 physical review remains NOT_VERIFIED.

**Consumes:** LAB-I06 `BackupFault` receipts. Add an idempotent ingestion adapter keyed by fault ID and context; preserve original backup artifact hashes. This completes the live backup-alert path without a circular dependency. The observer alert receiver and I07 outcome receiver have separate stores/credentials and no shared privileged control endpoint.

<a id="lab-o02"></a>
## LAB-O02 — Legal hold và archive/purge

**Status:** PLANNED. **PR boundary:** one independently reviewed PR. **Source/API dependencies:** LAB-O01, LAB-S07, LAB-E06. Live setup/approval requirements are additional and do not convert source merge into acceptance.

**Dependencies:** immutable audit/hold source, O01 incident path, independent object manifest collector, approved lab-owned storage root. **Scope:** B04-05..06 mechanism; no invented legal duration, hold release, or durable-record deletion.

**Create:** `src/FmcgDms.Preprod.Operations/RetentionExecutor.cs`, `RetentionAuthority.cs`, `Migrations/20261119_003_retention_operations.sql`, operator-only `src/FmcgDms.Preprod.OperationsTool/{FmcgDms.Preprod.OperationsTool.csproj,Program.cs}`, `tests/FmcgDms.Preprod.Operations.Tests/RetentionExecutorTests.cs`, SQL/fixture integration tests. **Modify:** `deploy/preprod/audit-operations-operator.md`; extend hold procedure locking through the new migration, preserving existing append-only hold records.

**Interfaces:** `PlanAsync(RetentionScope scope, CancellationToken ct) -> RetentionPlan`; `ApplyAsync(Guid planId, string expectedPlanSha256, CancellationToken ct) -> RetentionExecutionReceipt`. Scope is an allowlisted tenant/class/object inventory identifier, never an arbitrary path or SQL predicate. Receipt records per-object outcomes, hashes, authority version and audit/incident references. CLI defaults to plan; explicit apply requires unchanged hash and named operator credentials. Runtime API/Worker cannot register this tool.

- [ ] Test hold before/after planning, concurrent hold vs purge, stale plan/hash, wrong tenant/root, symlink escape, unavailable authority, UNSET retain-until, partial storage failure and replay. Reject every purge class except `DiagnosticTelemetry`; APP02A receipts/CASE/inbox/outbox, ledger/source, audit, backup and legal evidence categorically remain protected.
- [ ] Read authority, Maker/Checker/reviewer identities and explicit retain-until from authoritative records. Missing real legal approval yields BLOCKED before mutation. Meaningful tests use only disposable, ownership-marked diagnostic files and a signed/scoped `LAB_TEST_ONLY` authority fixture; never label it legal approval. Keep existing `B8RetentionGate` unchanged as an additional validator.
- [ ] Archive stages copies without deleting originals, verifies the complete object manifest and independent archive receipt, then evaluates the gate and records completion. This ordering satisfies the existing gate's archive-receipt prerequisite without self-certifying an archive before creation.
- [ ] Apply revalidates authority, latest hold and unchanged manifest under a tenant/class operation fence also acquired by `b8_PlaceHold`; serialize hold against destructive work. Persist intent before per-object deletion and retain the verified archive. SQL plus filesystem is not one atomic transaction: interrupted/unknown outcomes stay resumable and incident-visible, with hash-based reconciliation rather than blind rerun or fabricated COMMITTED.
- [ ] Default to archive execution and purge planning/denial. Actual irreversible deletion of eligible lab diagnostic objects is permitted only when the storage-enforced hold/delete protocol below is implemented and verified; otherwise purge remains BLOCKED. Capture full protected SQL/peer fingerprints unchanged. Restore the retention/hold store in a drill and require external current hold authority before apply; missing comparison blocks. Diagnostic TTL changes must have no path to durable SQL history.

**Exit:** operator executor and negative witnesses exist; actual legal authority, independent custody/review and physical acceptance remain separately blocked where absent.

**Live verification prerequisites:** use LAB-I08 controlled restore for the restore/hold drill after source implementation; never rewind the full DATA VM. The operation-fence protocol must define a lock owner and crash recovery across SQL intent/filesystem work, with legal hold winning before any new deletion. An unavailable current hold authority leaves apply blocked; a local test-only policy is not legal approval.


### LAB-O02 enforceable hold/delete boundary

A SQL transaction or applock alone cannot fence filesystem deletion after its connection is lost. The baseline deliverable executes **archive** and evaluates/denies purge; irreversible purge stays BLOCKED without a deletion backend that atomically enforces current hold/fencing state at the storage boundary. B04-06's positive archive scenario does not require enabling irreversible deletion.

If this increment introduces such a backend, it must define the single lock/lease owner, linearization of hold admission versus each destructive operation, generation/fencing token enforcement by storage, restart/reconciliation and hold-ack protocol. A stale executor cannot delete after losing its fence; hold admission must not acknowledge enforcement before the storage backend enforces it. SQL-only flags plus an unfenced filesystem loop do not meet the contract.

Mandatory deterministic tests: disconnect SQL immediately before deletion; hold arrives with deletion pending; crash before/after storage mutation; old worker continues after its fence expires; new hold/restart races. Without that verified mechanism, return `PURGE_BACKEND_NOT_FENCED / BLOCKED` before destructive work. Archive and the negative legal-hold/diagnostic-retention tests can still complete. SQL rollback never restores deleted files.

# APP-02B.8 — independent source-only acceptance checks

**No physical PREPROD gate is satisfied by these CI checks.** Required classification for successful dedicated CI is `GITHUB_HOSTED_CI_TESTED / PHYSICAL_PREPROD_NOT_VERIFIED / G-B04 NOT_RUN`; no applied PREPROD migration, real immutable off-host anchor, alert sink or approved legal retention. The separate B.1 physical B04-01..06 remain required.

| ID | Required deterministic check |
|---|---|
| CI-B8-01 | Exact final PR HEAD and .NET 10.0.404; standalone source library and API/Worker build; six xUnit B04 source tests, 6/6 with zero skipped/failed |
| CI-B8-02 | Windows SQL Server LocalDB creates isolated audit Control Plane DB plus two physically distinct, disposable tenant DBs; peer A/B sentinels fingerprint unchanged |
| CI-B8-03 | SQL append-only trigger, role-scope deny, wrong tenant, direct table read, duplicate EntryId rollback without sequence gap; no DELETE endpoint |
| CI-B8-04 | SQL/C# identical SHA256 canonical chain, tamper/truncated-tail and wrong signer rejected; temporary CI ECDSA signature only, off-host actual NOT_PERFORMED |
| CI-B8-05 | Dedicated legal hold role, unauthorized hold and SQL DELETE denied; retention planner denies active hold, missing legal authority/explicit time, self-approval and protected durable purge |
| CI-B8-06 | Allowlisted OTel-compatible ActivitySource/Meter, no secret string fields; alarm delivery/ACK failure, late escalation and incident independent closeout negatives |
| CI-B8-07 | Migration repeat intact succeeds; disabled or missing guard/trigger or unknown schema version fails closed; no B.8 API/Worker activation, source SQL DROP DATABASE, TTL or privileged DML |
| CI-B8-08 | Dedicated JSON/TRX artifacts, exact `SourceHeadSha`, TestRunId, source/SQL SHA and two tenant peer hashes, explicit `RealPreprod=NOT_VERIFIED`, `G_B04=NOT_RUN`, independent diff/security review and prior regressions; no false G-B04/production label |

**Acceptance rule:** An incomplete CI, unreviewed final source SHA, failed full APP-00A or blocking security finding means NO_MERGE unless a new explicit B.8-specific Owner decision establishes a narrow source-only exception with compensating evidence. Earlier B.4/B.5/B.6/B.7 exceptions are nontransferable; #59 npm HIGH audit cannot be reported as PASS. B.9 independently checks real physical PREPROD, actual-vs-expected, two tenants, legal key/hold/retention owners, alerts, source/inbox/outbox traces, peer no-write and final-head provenance. No source-only artifact is G-B04 physical proof.

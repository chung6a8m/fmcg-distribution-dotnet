# LAB-D04 SQL rehearsal

LAB-D04 fixes the Microsoft SQL Server 2025 CU11 Windows-Docker/WSL2 lab model
to Linux amd64, Developer Edition, engine build `17.0.5065.9`, and the
published OCI manifest in `docker_d04.py`. Developer Edition is scoped to
development and testing. The operator retains the image publisher proof and
reviewed license receipt before any separate live approval.

The SQL service has no published port and attaches only to the D02 DATA
bridge. It uses the unused D02 SQL data volume and a distinct backup target.
The backup volume is storage layout only. The scripts perform no backup,
restore, drop, cleanup, whole-volume rewind or business admission. The
bootstrap file is mounted from protected custody. Its value never appears in
the model, environment file, command line or report. The container entry
process reads it locally and supplies it to SQL Server during startup. The
daemon/laptop still shares custody. SQL Server presents the leaf certificate
for `sql`, TLS is forced, and SqlClient uses Strict encryption with
hostname validation against the verifier's enrolled OS trust store.

## Read-only Windows planning

Use a clean checkout. Provide independently retained D01/D02 plans and a
successful D02 staging receipt, the D03 staging plan and receipt plus a
current D03 Vault peer role/audit observation from the same daemon and
namespace. The D01 plan must include the pinned SQL publisher receipt.
The D04 plan checks the actual local context, SQL image, unused labeled
data volume and bridge, running D03 container identity, TLS files, and
distinct backup namespace. Keep output and inputs outside Git. Restrict
the private key and bootstrap file and their parent directories to the
operator, SYSTEM and Administrators. Confirm service UID read access on the
eventual separately authorized target and keep the CA signing key off the host.

~~~json
{
  "schema": "APP02B-LAB-DOCKER-SQL-CONFIG-V1",
  "d01Plan": "C:\\LabPrivate\\d01.json",
  "d02Plan": "C:\\LabEvidence\\d02-plan\\docker-rehearsal-plan.json",
  "d02Apply": "C:\\LabEvidence\\d02-apply\\docker-rehearsal-apply.json",
  "d03Plan": "C:\\LabEvidence\\d03-plan\\docker-vault-plan.json",
  "d03Stage": "C:\\LabEvidence\\d03-stage\\docker-vault-stage.json",
  "d03Peer": "C:\\LabEvidence\\d03-audit\\peer-result.json",
  "sqlCertificate": "C:\\LabPrivate\\sql\\leaf.pem",
  "sqlKey": "C:\\LabPrivate\\sql\\leaf.key",
  "sqlCa": "C:\\LabPrivate\\sql\\ca.pem",
  "bootstrapFile": "C:\\LabPrivate\\sql\\bootstrap",
  "licenseSha256": "<digest of operator-reviewed license receipt>",
  "evidenceRoot": "C:\\LabEvidence"
}
~~~

~~~powershell
.\scripts\lab\Install-DockerLabSql.ps1 -Plan -ConfigFile C:\LabPrivate\d04.json -OutputDirectory C:\LabEvidence\d04-plan-01
~~~

The planner emits a redacted plan and checksum with the exact source,
manifest, scope and resource fingerprints. `-Apply` returns
`D04_LIVE_INSTALL_NOT_ENROLLED`. No automated live SQL bootstrap or DDL is
exposed by the Windows entry point. Before a live setup, an operator must
approve the exact deployment and vault-to-SQL workload paths, and the DBA
must separately enroll the instance and the reviewed B.3/B.4/B.5 operating
contracts. Use the existing B.4 Tenant Tool for any eventual full registry,
provisioning and finalization workflow. Do not use the disposable CI fixture
as a substitute for live enrollment.

## SQL verification on an enrolled private peer

After separately enrolled provision, the peer must be able to reach Vault
as `vault` and SQL as `sql` over the approved private topology. Enroll
the SQL CA into that peer's OS trust store, do not reuse its SSL trust store
for Vault secret acquisition. The tool uses D03's pinned Vault leaf/CA and
checks the enrolled cluster. Each distinct Vault token may read only
`preprod/data/lab-d04/<nonce>/<role>` for `apia`, `apib` or `observer`.
The KV field set is `login` and `password`. The expected SQL login, SQL
instance name, derived B.4 tenant names and binding IDs come from the scope.
Tokens are read without echo and passwords travel to SqlClient in process
memory via standard input. Keep the plan, D03 stage and peer evidence, D03
scope and Vault CA as 0600 files under owner-only 0700 directories. The
redacted result is written once to a new path.

~~~powershell
dotnet build tools/lab-d04-sql/FmcgDms.Lab.D04.Sql.csproj -c Release
./scripts/lab/Test-DockerLabSql.ps1 -PlanFile /private/docker-sql-plan.json -VaultStageReceipt /private/docker-vault-stage.json -VaultTrustReceipt /private/peer-result.json -VaultScope /private/vault-scope.json -VaultCa /private/vault-ca.pem -OutputFile /private/d04-roles-01.json
~~~

The verifier checks the same SQL Server identity, Developer engine build,
current database and authenticated login on each connection. It checks
distinct identities and the B.3/B.4/B.5 migration histories via the
observer. The synthetic probe table has one independently read marker
row. Positive insert and all denial assertions use transactions with an
explicit rollback, including if DDL or observer DML succeeds unexpectedly.
The independent observer compares both tenant and control-plane witnesses
before and after.

## What synthetic CI exercises

CI uses disposable SQL Server and Vault containers with no published
ports. A unique CI instance name, exact developer build, empty inventory,
instance/nonce marker and fixed database allowlist gate fixture creation.
It reuses the B.3 Identity, B.4 Registry/tenant-marker and B.5 SQL migrations
without changing their source. The fixture checks a second migration pass
and readback, real scoped Vault credentials, strict SQL TLS, same-instance
A/B isolation, observer write denial, API DDL denial, B.5 SQL fence
stale/revoked denial, and restart with no fingerprint drift. SQL fence
checks exercise the existing stored procedures. Runtime authority remains
`FOUNDATION_ONLY / NO_GO`.

Full B.4 activation and DBA finalization, B53/PITR, live D03 trust, live
D04 Apply, independently witnessed restore and original B.1/B.9 gates
remain unverified by CI. Restore must preserve current fence authority
outside a tenant restore path.

[Microsoft image listing](https://mcr.microsoft.com/artifact/mar/mssql/server/tags) ·
[SQL Server 2025 builds](https://learn.microsoft.com/en-us/troubleshoot/sql/releases/sqlserver-2025/build-versions) ·
[SQL Linux TLS configuration](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-configure-mssql-conf/)

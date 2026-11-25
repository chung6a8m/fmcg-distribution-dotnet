[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('app03-4', 'app03-5')][string]$ProfileName,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][int]$ReporterPid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:FMCG_APP03_READ_DIAGNOSTICS -cne '1' -or $env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_OS -cne 'Windows' -or [Environment]::OSVersion.Platform -ne 'Win32NT') { exit 2 }

# Fail closed before opening any file or database. The caller cannot supply a server,
# connection string, database, SQL text or free-form destination.
if ($RunId -cnotmatch '\A[0-9a-f]{32}\z' -or $ReporterPid -le 0) { exit 2 }
$directory = Split-Path -Parent $PSScriptRoot
foreach ($part in @('TestResults', $ProfileName, $RunId)) {
    $directory = Join-Path $directory $part
    if (![IO.Directory]::Exists($directory) -or
        [IO.File]::GetAttributes($directory).HasFlag([IO.FileAttributes]::ReparsePoint)) { exit 2 }
}
if (![string]::Equals($directory, $env:APP03_EVIDENCE_DIR, [StringComparison]::OrdinalIgnoreCase)) { exit 2 }

# Keep SQL values, client identity, full batch text and free-form wait_resource at the server.
# Only known statement families and object names may cross the diagnostic connection.
. (Join-Path $PSScriptRoot 'Get-App03SqlReadFailure.ps1')
$sampleSql = @'
SELECT TOP (33) r.session_id AS spid, r.request_id AS requestId,
    COALESCE(r.wait_type, '') AS waitType, r.wait_time AS waitMs, r.total_elapsed_time AS elapsedMs,
    r.cpu_time AS cpuMs, r.logical_reads AS logicalReads,
    CASE WHEN COALESCE(r.blocking_session_id,0)=0 THEN 'none'
         WHEN b.session_id IS NULL THEN 'unresolved' ELSE 'sameDatabase' END AS blockerScope,
    b.session_id AS blockerSpid, b.open_transaction_count AS blockerTransactions,
    CASE WHEN b.status='sleeping' THEN 1 ELSE 0 END AS blockerSleeping,
    CASE WHEN txt.text LIKE '%SELECT t.Id AS TripId,t.Code AS Label,%'
              AND txt.text LIKE '%dbo.C4DeliveryWriter%' THEN 'D2TripList'
         WHEN txt.text LIKE '%SELECT t.Id,t.Code,CASE WHEN @Field=1%' AND txt.text LIKE '%dbo.C4DeliveryWriter%' THEN 'C4TripList'
         WHEN txt.text LIKE '%FROM dbo.App02ActorRoleGrant WITH(UPDLOCK,HOLDLOCK)%' THEN 'RoleCheck'
         WHEN txt.text LIKE '%SELECT Nonce FROM dbo.App00ScratchMarker%' AND txt.text LIKE '%dbo.C3TripWriter%' THEN 'D1Catalog'
         ELSE 'Other' END AS family,
    COALESCE(l.kind, 'none') AS lockKind, COALESCE(l.obj, 'none') AS lockObject
FROM sys.dm_exec_requests r
LEFT JOIN sys.dm_exec_sessions b ON b.session_id=r.blocking_session_id AND b.database_id=DB_ID()
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) txt
OUTER APPLY (
    SELECT TOP (1)
        CASE WHEN locks.resource_type IN ('KEY','RID','PAGE','OBJECT','APPLICATION') THEN locks.resource_type ELSE 'other' END AS kind,
        CASE COALESCE(OBJECT_NAME(p.object_id,DB_ID()),
                CASE WHEN locks.resource_type='OBJECT' THEN OBJECT_NAME(TRY_CONVERT(int,locks.resource_associated_entity_id),DB_ID()) END)
            WHEN 'App02ActorRoleGrant' THEN 'App02ActorRoleGrant' WHEN 'Trip' THEN 'Trip' ELSE 'other' END AS obj
    FROM sys.dm_tran_locks locks
    LEFT JOIN sys.partitions p ON locks.resource_type IN ('KEY','RID','PAGE')
        AND p.hobt_id=locks.resource_associated_entity_id
    WHERE locks.resource_database_id=DB_ID() AND locks.request_session_id=r.session_id
        AND locks.request_status IN ('WAIT','CONVERT','LOW_PRIORITY_WAIT','LOW_PRIORITY_CONVERT')
    ORDER BY locks.resource_type
) l
WHERE r.database_id=DB_ID() AND r.session_id<>@@SPID AND r.session_id>0
ORDER BY CASE WHEN r.blocking_session_id<>0 THEN 0 ELSE 1 END, r.total_elapsed_time DESC, r.session_id;
'@

$writer = $null
$db = $null
$command = $null
$parent = $null
$inputPipe = $null
$exitCode = 1
$phase = 'initialization'
$clock = [Diagnostics.Stopwatch]::StartNew()

function Write-Record([hashtable]$Data) {
    $Data.utc = [DateTime]::UtcNow.ToString('O')
    $Data.observerMs = $clock.ElapsedMilliseconds
    $writer.WriteLine(($Data | ConvertTo-Json -Depth 4 -Compress))
    $writer.Flush()
}

try {
    $parent = [Diagnostics.Process]::GetProcessById($ReporterPid)
    $inputPipe = [Console]::OpenStandardInput()
    $pipeRead = $inputPipe.BeginRead((New-Object byte[] 1), 0, 1, $null, $null)
    $file = [IO.File]::Open((Join-Path $directory 'sql-reads.ndjson'), 'CreateNew', 'Write', 'Read')
    $writer = [IO.StreamWriter]::new($file, [Text.UTF8Encoding]::new($false))
    Write-Record @{ event='start'; profile=$ProfileName; runId=$RunId }

    $phase = 'connection'
    Add-Type -AssemblyName System.Data
    $database = "Fmcg_App00_${RunId}_A"
    $db = [System.Data.SqlClient.SqlConnection]::new(
        "Server=(localdb)\MSSQLLocalDB;Database=$database;Integrated Security=true;Pooling=false;Connect Timeout=2;ConnectRetryCount=0;Application Name=APP03 read observer")
    $db.Open()
    $command = $db.CreateCommand()
    $command.CommandTimeout = 1
    $phase = 'ownership'
    # These are the existing DemoRun.Verify(tenant-a, app03Session:true) markers.
    $command.CommandText = @'
SELECT CASE WHEN DB_NAME()=@database
 AND (SELECT COUNT(*) FROM dbo.LocalDemoOwner o JOIN dbo.App00ScratchMarker m ON m.Nonce=o.RunId WHERE o.RunId=@run)=1
 AND (SELECT COUNT(*) FROM dbo.LocalApp03Session WHERE RunId=@run AND TenantId='tenant-a')=1
 AND (SELECT COUNT(*) FROM dbo.AppTenantBinding WHERE Slot=1 AND TenantId='tenant-a')=1 THEN 1 ELSE 0 END;
'@
    $null = $command.Parameters.Add('@database', [System.Data.SqlDbType]::NVarChar, 128)
    $command.Parameters['@database'].Value = $database
    $null = $command.Parameters.Add('@run', [System.Data.SqlDbType]::VarChar, 32)
    $command.Parameters['@run'].Value = $RunId
    if ([int]$command.ExecuteScalar() -ne 1) { throw 'ownership' }
    $phase = 'visibility'
    $command.Parameters.Clear()
    $command.CommandText = @'
SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR
    CASE WHEN TRY_CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))>=16
         THEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE')
         ELSE HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE') END=1 THEN 1 ELSE 0 END;
'@
    if ([int]$command.ExecuteScalar() -ne 1) { throw 'visibility' }

    $phase = 'sample'
    $command.CommandText = $sampleSql
    $reason = 'deadline'
    Write-Record @{ event='available'; sampleIntervalMs=250; commandTimeoutMs=1000 }
    while ($clock.Elapsed.TotalSeconds -lt 240) {
        if ($parent.HasExited -or $pipeRead.IsCompleted) { $reason='parentEnded'; break }
        # The reserved space accommodates one maximum-sized sample and the final status.
        if ($file.Position -ge (2MB - 64KB)) { $reason='sizeLimit'; break }
        $started = $clock.ElapsedMilliseconds
        $rows = [Collections.Generic.List[object]]::new()
        $overflow = $false
        $reader = $null
        try {
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                if ($rows.Count -eq 32) { $overflow=$true; break }
                $waitType = [string]$reader['waitType']
                if ($waitType -cnotmatch '\A[A-Z0-9_]{0,80}\z') { $waitType='other' }
                $row = @{ spid=$reader['spid']; requestId=$reader['requestId']; family=$reader['family']
                    waitType=$waitType; waitMs=$reader['waitMs']; elapsedMs=$reader['elapsedMs']
                    cpuMs=$reader['cpuMs']; logicalReads=$reader['logicalReads']; blockerScope=$reader['blockerScope']
                    lockKind=$reader['lockKind']; lockObject=$reader['lockObject'] }
                if (!$reader.IsDBNull($reader.GetOrdinal('blockerSpid'))) {
                    $row.blockerSpid=$reader['blockerSpid']
                    $row.blockerTransactions=$reader['blockerTransactions']
                    $row.blockerSleeping=[bool]$reader['blockerSleeping']
                }
                $rows.Add($row)
            }
        } catch {
            $failure = Get-App03SqlReadFailure $_.Exception $db.State
            if ($failure.Continue) {
                Write-Record @{ event='sampleUnavailable'; reason='commandTimeout'; sqlNumber=$failure.Number; queryMs=($clock.ElapsedMilliseconds - $started) }
                Start-Sleep -Milliseconds 250
                continue
            }
            throw
        } finally { if ($null -ne $reader) { $reader.Dispose() } }
        Write-Record @{ event='sample'; queryMs=($clock.ElapsedMilliseconds - $started); truncated=$overflow; rows=$rows.ToArray() }
        Start-Sleep -Milliseconds 250
    }
    Write-Record @{ event='stop'; reason=$reason }
    $exitCode = 0
} catch {
    if ($null -ne $writer) {
        $failure = Get-App03SqlReadFailure $_.Exception Closed
        $status = @{event='unavailable'; phase=$phase}
        if ($null -ne $failure.Number) { $status.sqlNumber=$failure.Number }
        try { Write-Record $status } catch {}
    }
} finally {
    if ($null -ne $command) { $command.Dispose() }
    if ($null -ne $db) { $db.Dispose() }
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $inputPipe) { $inputPipe.Dispose() }
    if ($null -ne $parent) { $parent.Dispose() }
}
exit $exitCode

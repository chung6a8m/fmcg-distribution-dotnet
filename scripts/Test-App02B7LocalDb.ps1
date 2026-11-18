# Disposable Windows runner ONLY. Never accepts a caller-selected SQL instance.
$ErrorActionPreference = 'Stop'
$server = '(localdb)\MSSQLLocalDB'
if ($env:APP02B7_SQL_SERVER -cne $server -or
    [string]::IsNullOrWhiteSpace($env:APP02B7_SOURCE_HEAD_SHA)) {
    throw 'B7_LOCALDB_ONLY_SOURCE_BOUND_GATE'
}
$nonce = [guid]::NewGuid().ToString()
$tag = ([guid]::NewGuid().ToString('N')).Substring(0,12)
$a = 'APP02B7_CI_' + $tag + '_A'
$b = 'APP02B7_CI_' + $tag + '_B'
$restored = 'APP02B7_CI_' + $tag + '_R'
$folder = Join-Path $env:RUNNER_TEMP ('app02b7-'+$tag)
New-Item -Path $folder -ItemType Directory -Force | Out-Null
function Lit([string]$v) { return $v.Replace("'","''") }
function Sql([string]$q) {
    $output = & sqlcmd -S $server -E -b -V 16 -h -1 -W -Q ("SET NOCOUNT ON; " + $q) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "B7_SQL_FAILED: $($output | Out-String)" }
    return ($output | Out-String).Trim()
}
function OwnedDrop([string]$name) {
    if ($name -notmatch '^APP02B7_CI_[0-9a-f]{12}_[ABR]$') { throw 'B7_UNOWNED_DATABASE_DENIED' }
    $q = "IF DB_ID(N'$name') IS NOT NULL BEGIN
        IF OBJECT_ID(N'[$name].dbo.__B7CiOwned',N'U') IS NULL
            THROW 61730,'B7_UNOWNED_DATABASE_NO_DROP',1;
        IF (SELECT COUNT_BIG(*) FROM [$name].dbo.__B7CiOwned WHERE RunNonce='$nonce')<>1
            THROW 61731,'B7_NONCE_MISMATCH_NO_DROP',1;
        ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [$name]; END"
    Sql $q | Out-Null
}
$f = Lit (Join-Path $folder 'full.bak')
$d = Lit (Join-Path $folder 'diff.bak')
$l = Lit (Join-Path $folder 'log.trn')
$corrupt = Lit (Join-Path $folder 'corrupt.trn')
$made = @()
try {
    foreach ($db in @($a,$b)) {
        Sql "CREATE DATABASE [$db];" | Out-Null
        $made += $db
        Sql "USE [$db]; CREATE TABLE dbo.__B7CiOwned(RunNonce uniqueidentifier NOT NULL PRIMARY KEY); INSERT dbo.__B7CiOwned VALUES ('$nonce'); CREATE TABLE dbo.B7Witness (Seq int NOT NULL PRIMARY KEY);" | Out-Null
    }
    Sql "ALTER DATABASE [$a] SET RECOVERY FULL;" | Out-Null
    Sql "USE [$a]; INSERT dbo.B7Witness VALUES (1);" | Out-Null
    $peerBefore = Sql "SELECT CHECKSUM_AGG(BINARY_CHECKSUM(RunNonce)) FROM [$b].dbo.__B7CiOwned;"
    Sql "BACKUP DATABASE [$a] TO DISK=N'$f' WITH INIT,CHECKSUM;" | Out-Null
    Sql "USE [$a]; INSERT dbo.B7Witness VALUES (2);" | Out-Null
    Sql "BACKUP DATABASE [$a] TO DISK=N'$d' WITH DIFFERENTIAL,INIT,CHECKSUM;" | Out-Null
    Sql "USE [$a]; INSERT dbo.B7Witness VALUES (3);" | Out-Null
    Sql "BACKUP LOG [$a] TO DISK=N'$l' WITH INIT,CHECKSUM;" | Out-Null
    foreach ($path in @($f,$d,$l)) {
        Sql "RESTORE VERIFYONLY FROM DISK=N'$path' WITH CHECKSUM;" | Out-Null
    }
    $logicalData = (Sql "SELECT TOP 1 name FROM sys.master_files WHERE database_id=DB_ID(N'$a') AND type=0;").Trim()
    $logicalLog = (Sql "SELECT TOP 1 name FROM sys.master_files WHERE database_id=DB_ID(N'$a') AND type=1;").Trim()
    if (!$logicalData -or !$logicalLog) { throw 'B7_LOGICAL_FILE_DISCOVERY_FAILED' }
    $mdf = Lit (Join-Path $folder 'restored.mdf')
    $ldf = Lit (Join-Path $folder 'restored.ldf')
    $data = Lit $logicalData
    $log = Lit $logicalLog
    Sql "RESTORE DATABASE [$restored] FROM DISK=N'$f' WITH MOVE N'$data' TO N'$mdf',MOVE N'$log' TO N'$ldf',NORECOVERY;" | Out-Null
    $made += $restored
    Sql "RESTORE DATABASE [$restored] FROM DISK=N'$d' WITH NORECOVERY;" | Out-Null
    Sql "RESTORE LOG [$restored] FROM DISK=N'$l' WITH RECOVERY;" | Out-Null
    $count = Sql "SELECT COUNT_BIG(*) FROM [$restored].dbo.B7Witness;"
    if ($count -cne '3') { throw "B7_RESTORED_WITNESS_MISMATCH: $count" }
    $peerAfter = Sql "SELECT CHECKSUM_AGG(BINARY_CHECKSUM(RunNonce)) FROM [$b].dbo.__B7CiOwned;"
    if ($peerAfter -cne $peerBefore) { throw 'B7_PEER_MUTATED' }
    $setCount = Sql "SELECT COUNT_BIG(*) FROM msdb.dbo.backupset WHERE database_name=N'$a' AND type IN ('D','I','L') AND backup_start_date >= DATEADD(HOUR,-1,GETDATE());"
    if ([int]$setCount -lt 3) { throw 'B7_MSDB_CHAIN_INCOMPLETE' }
    Copy-Item (Join-Path $folder 'log.trn') (Join-Path $folder 'corrupt.trn')
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $folder 'corrupt.trn'))
    if ($bytes.Length -lt 1024) { throw 'B7_UNEXPECTED_BACKUP_LENGTH' }
    $pos = [int]($bytes.Length / 2)
    $bytes[$pos] = $bytes[$pos] -bxor 255
    [System.IO.File]::WriteAllBytes((Join-Path $folder 'corrupt.trn'), $bytes)
    & sqlcmd -S $server -E -b -V 16 -Q "RESTORE VERIFYONLY FROM DISK=N'$corrupt' WITH CHECKSUM;" *> $null
    if ($LASTEXITCODE -eq 0) { throw 'B7_CORRUPT_LOG_ACCEPTED' }
    New-Item -Path TestResults -ItemType Directory -Force | Out-Null
    $evidence = [ordered]@{
        TestRunId = $env:APP02B7_TEST_RUN_ID
        SourceHeadSha = $env:APP02B7_SOURCE_HEAD_SHA
        EvidenceClass = 'GITHUB_HOSTED_DISPOSABLE_LOCALDB_ONLY'
        TenantDatabases = 2
        FullDifferentialLogPhysicalRestore = $true
        MsdbBackupSets = [int]$setCount
        RestoredCommittedWitnesses = [int]$count
        PeerBefore = $peerBefore
        PeerAfter = $peerAfter
        ChecksumVerifyOnly = $true
        CorruptedLogRejected = $true
        SqlBackupsEncrypted = $false
        OffHostRestored = $false
        ActualPreprodRpoRto = 'NOT_MEASURED'
        G_B03 = 'NOT_RUN'
        PhysicalPreprod = 'NOT_VERIFIED'
        FullSha256 = (Get-FileHash (Join-Path $folder 'full.bak') -Algorithm SHA256).Hash
        DiffSha256 = (Get-FileHash (Join-Path $folder 'diff.bak') -Algorithm SHA256).Hash
        LogSha256 = (Get-FileHash (Join-Path $folder 'log.trn') -Algorithm SHA256).Hash
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content TestResults/APP-02B7-LOCALDB-WITNESS.json -Encoding utf8
}
finally {
    # Strict nonce+name marker before any DROP; on failure leave scratch isolated.
    foreach ($db in @($restored,$a,$b)) {
        if ($made -contains $db) { OwnedDrop $db }
    }
    Remove-Item -Path $folder -Force -Recurse -ErrorAction SilentlyContinue
}

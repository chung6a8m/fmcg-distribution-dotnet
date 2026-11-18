# APP-02B.7 PREPROD operator-only SQL encrypted staging backup.
# NOT a scheduler or an off-host receipt. No API/Worker integration.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][ValidateSet('Full','Differential','Log')][string]$Kind,
    [Parameter(Mandatory)][string]$SqlInstance,
    [Parameter(Mandatory)][string]$ApprovedSqlInstance,
    [Parameter(Mandatory)][string]$ApprovedSqlServerName,
    [Parameter(Mandatory)][string]$SqlServerStageFile,
    [Parameter(Mandatory)][string]$ApprovedStagingDirectory,
    [Parameter(Mandatory)][string]$EncryptionCertificate,
    [Parameter(Mandatory)][string]$ApprovedChangeRef,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if ($TenantId -eq [guid]::Empty) { throw 'B7_TENANT_REQUIRED' }
if ([string]::IsNullOrWhiteSpace($ApprovedChangeRef) -or $ApprovedChangeRef.Length -gt 160) {
    throw 'B7_CHANGE_APPROVAL_REQUIRED'
}
if ($SqlInstance -cne $ApprovedSqlInstance -or
    $SqlInstance -match '(?i)localdb|localhost|127\.0\.0\.1' -or
    $SqlInstance -notmatch '^[a-zA-Z0-9.\\,-]+$' -or
    $ApprovedSqlServerName -notmatch '^[a-zA-Z0-9.\\-]+$') {
    throw 'B7_APPROVED_PRIVATE_SQL_TARGET_REQUIRED'
}
if ($EncryptionCertificate -notmatch '^[A-Za-z][A-Za-z0-9_]{0,127}$') {
    throw 'B7_SQL_BACKUP_CERTIFICATE_ID_INVALID'
}
# The file is on the SQL Server's local encrypted staging volume. The backup
# remains NOT_VERIFIED until the separate off-host transfer + decrypt witness.
if (!$SqlServerStageFile.StartsWith('/') -and
    $SqlServerStageFile -notmatch '^[A-Za-z]:\\') {
    throw 'B7_ABSOLUTE_SQL_SERVER_STAGING_PATH_REQUIRED'
}
if ($SqlServerStageFile.Contains("'") -or $SqlServerStageFile.Contains('"') -or
    $SqlServerStageFile.Contains('..') -or $SqlServerStageFile.Contains(';') -or
    $SqlServerStageFile -notmatch '\.(bak|trn)$') {
    throw 'B7_STAGING_PATH_INVALID'
}
$db = 'FmcgPpT_' + $TenantId.ToString('N')
$root = $ApprovedStagingDirectory.TrimEnd('/','\')
if ($root.Length -lt 8 -or
    (!$SqlServerStageFile.StartsWith($root + '/', [StringComparison]::Ordinal) -and
     !$SqlServerStageFile.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase))) {
    throw 'B7_STAGE_NOT_IN_APPROVED_DIRECTORY'
}
$extension = if ($Kind -eq 'Log') { 'trn' } else { 'bak' }
$leaf = $SqlServerStageFile.Replace('\','/').Split('/')[-1]
$leafPattern = '^' + [regex]::Escape($db) + '_' + $Kind + '_[0-9]{8}T[0-9]{6}Z_[a-fA-F0-9]{32}\.' + $extension + '$'
if ($leaf -cnotmatch $leafPattern) {
    throw 'B7_UNIQUE_BACKUP_OBJECT_NAME_REQUIRED'
}
$escapedPath = $SqlServerStageFile.Replace("'", "''")
$cert = "[$EncryptionCertificate]"
$requiredName = $ApprovedSqlServerName.Replace("'", "''")
$tenantText = $TenantId.ToString()
$backupSql = switch ($Kind) {
    'Full' { "BACKUP DATABASE [$db] TO DISK = N'$escapedPath' WITH INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
    'Differential' { "BACKUP DATABASE [$db] TO DISK = N'$escapedPath' WITH DIFFERENTIAL, INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
    'Log' { "BACKUP LOG [$db] TO DISK = N'$escapedPath' WITH INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
}
$sql = @"
SET NOCOUNT ON;
IF CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) <> N'$requiredName'
    THROW 61701, 'B7_WRONG_SQL_SERVER', 1;
IF DB_ID(N'$db') IS NULL OR
    (SELECT recovery_model_desc FROM sys.databases WHERE name=N'$db') <> 'FULL'
    THROW 61702, 'B7_FULL_RECOVERY_OR_DATABASE_REQUIRED', 1;
IF NOT EXISTS (SELECT 1 FROM [$db].sys.tables
    WHERE name=N'__PreprodTenantBinding' AND schema_id=SCHEMA_ID(N'dbo'))
    THROW 61703, 'B7_TENANT_BINDING_MARKER_MISSING', 1;
IF (SELECT COUNT_BIG(*) FROM [$db].dbo.__PreprodTenantBinding
    WHERE TenantId='$tenantText' AND BindingId IS NOT NULL AND BindingVersion>=1)<>1
    OR (SELECT COUNT_BIG(*) FROM [$db].dbo.__PreprodTenantBinding)<>1
    THROW 61704, 'B7_TENANT_BINDING_MISMATCH', 1;
IF NOT EXISTS (SELECT 1 FROM master.sys.certificates WHERE name=N'$EncryptionCertificate')
    THROW 61705, 'B7_ENCRYPTION_CERTIFICATE_MISSING', 1;
$backupSql
RESTORE VERIFYONLY FROM DISK = N'$escapedPath' WITH CHECKSUM;
"@
if (!$Execute) {
    Write-Output "B7_PLAN_ONLY tenant=$db kind=$Kind change=$ApprovedChangeRef; NO_SQL_EXECUTION"
    return
}
# An approved, authenticated, independently authorized SQL operator runs this
# on a controlled host. No SQL password or Vault token is read from arguments.
$path = Join-Path ([System.IO.Path]::GetTempPath()) ('b7-'+[guid]::NewGuid().ToString('N')+'.sql')
try {
    [System.IO.File]::WriteAllText($path, $sql, [System.Text.UTF8Encoding]::new($false))
    & sqlcmd -S $SqlInstance -E -N -b -V 16 -i $path
    if ($LASTEXITCODE -ne 0) { throw 'B7_BACKUP_FAILED_DENY_OFFHOST_COMPLETION' }
    Write-Output "B7_SQL_ENCRYPTED_STAGING_ONLY tenant=$db kind=$Kind; OFFHOST_AND_RESTORE_PROOF_NOT_VERIFIED"
} finally {
    Remove-Item $path -Force -ErrorAction SilentlyContinue
}

if ($leaf -cnotmatch $leafPattern) {
    throw 'B7_UNIQUE_BACKUP_OBJECT_NAME_REQUIRED'
}
$escapedPath = $SqlServerStageFile.Replace("'", "''")
$cert = "[$EncryptionCertificate]"
$requiredName = $ApprovedSqlServerName.Replace("'", "''")
$tenantText = $TenantId.ToString()
$backupSql = switch ($Kind) {
    'Full' { "BACKUP DATABASE [$db] TO DISK = N'$escapedPath' WITH INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
    'Differential' { "BACKUP DATABASE [$db] TO DISK = N'$escapedPath' WITH DIFFERENTIAL, INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
    'Log' { "BACKUP LOG [$db] TO DISK = N'$escapedPath' WITH INIT, CHECKSUM, COMPRESSION, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $cert);" }
}
$sql = @"
SET NOCOUNT ON;
IF CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) <> N'$requiredName'
    THROW 61701, 'B7_WRONG_SQL_SERVER', 1;
IF DB_ID(N'$db') IS NULL OR
    (SELECT recovery_model_desc FROM sys.databases WHERE name=N'$db') <> 'FULL'
    THROW 61702, 'B7_FULL_RECOVERY_OR_DATABASE_REQUIRED', 1;
IF NOT EXISTS (SELECT 1 FROM [$db].sys.tables
    WHERE name=N'__PreprodTenantBinding' AND schema_id=SCHEMA_ID(N'dbo'))
    THROW 61703, 'B7_TENANT_BINDING_MARKER_MISSING', 1;
IF (SELECT COUNT_BIG(*) FROM [$db].dbo.__PreprodTenantBinding
    WHERE TenantId='$tenantText' AND BindingId IS NOT NULL AND BindingVersion>=1)<>1
    OR (SELECT COUNT_BIG(*) FROM [$db].dbo.__PreprodTenantBinding)<>1
    THROW 61704, 'B7_TENANT_BINDING_MISMATCH', 1;
IF NOT EXISTS (SELECT 1 FROM master.sys.certificates WHERE name=N'$EncryptionCertificate')
    THROW 61705, 'B7_ENCRYPTION_CERTIFICATE_MISSING', 1;
$backupSql
RESTORE VERIFYONLY FROM DISK = N'$escapedPath' WITH CHECKSUM;
"@
if (!$Execute) {
    Write-Output "B7_PLAN_ONLY tenant=$db kind=$Kind change=$ApprovedChangeRef; NO_SQL_EXECUTION"
    return
}
# An approved, authenticated, independently authorized SQL operator runs this
# on a controlled host. No SQL password or Vault token is read from arguments.
$path = Join-Path ([System.IO.Path]::GetTempPath()) ('b7-'+[guid]::NewGuid().ToString('N')+'.sql')
try {
    [System.IO.File]::WriteAllText($path, $sql, [System.Text.UTF8Encoding]::new($false))
    & sqlcmd -S $SqlInstance -E -b -V 16 -i $path
    if ($LASTEXITCODE -ne 0) { throw 'B7_BACKUP_FAILED_DENY_OFFHOST_COMPLETION' }
    Write-Output "B7_SQL_ENCRYPTED_STAGING_ONLY tenant=$db kind=$Kind; OFFHOST_AND_RESTORE_PROOF_NOT_VERIFIED"
} finally {
    Remove-Item $path -Force -ErrorAction SilentlyContinue
}

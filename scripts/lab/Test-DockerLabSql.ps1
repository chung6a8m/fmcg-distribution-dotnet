[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$VaultStageReceipt,
    [Parameter(Mandatory)][string]$VaultTrustReceipt,
    [Parameter(Mandatory)][string]$VaultScope,
    [Parameter(Mandatory)][string]$VaultCa,
    [Parameter(Mandatory)][string]$SqlCa,
    [Parameter(Mandatory)][string]$EnrollmentFile,
    [Parameter(Mandatory)][string]$EnrollmentSha256,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsLinux -or $PSVersionTable.PSVersion.Major -ne 7) {
    [Console]::Error.WriteLine('COLLECTION_FAILED D04_PRIVATE_LINUX_PEER_REQUIRED')
    exit 1
}
$python = Get-Command python3 -CommandType Application
& $python.Source -I -S -B (Join-Path $PSScriptRoot '../../tools/app02b_lab/docker_d04_peer.py') --plan $PlanFile --stage $VaultStageReceipt --trust $VaultTrustReceipt --vault-scope $VaultScope --vault-ca $VaultCa --sql-ca $SqlCa --enrollment $EnrollmentFile --enrollment-sha256 $EnrollmentSha256 --output $OutputFile
exit $LASTEXITCODE

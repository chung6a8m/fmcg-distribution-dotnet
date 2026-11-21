[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$ConfigFile,
    [Parameter(Mandatory)][string]$CaCertificate,
    [Parameter(Mandatory)][string]$StageReceipt,
    [Parameter(Mandatory)][string]$AuditFile,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsLinux -or $PSVersionTable.PSVersion.Major -ne 7) {
    [Console]::Error.WriteLine('COLLECTION_FAILED D03_PRIVATE_LINUX_PEER_REQUIRED')
    exit 1
}
$python = Get-Command python3 -CommandType Application
& $python.Source -I -S -B (Join-Path $PSScriptRoot '../../tools/app02b_lab/docker_d03_peer.py') --config $ConfigFile --ca $CaCertificate --stage-receipt $StageReceipt --audit $AuditFile --output $OutputFile
exit $LASTEXITCODE

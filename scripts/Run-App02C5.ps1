[CmdletBinding()]
param([switch] $Verify)

& "$PSScriptRoot/Run-App02C1.ps1" -Increment App02C5 -Verify:$Verify

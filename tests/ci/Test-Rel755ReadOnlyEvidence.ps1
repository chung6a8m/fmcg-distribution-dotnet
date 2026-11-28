[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../scripts/Invoke-Rel75LocalDbSample.ps1')
. (Join-Path $PSScriptRoot '../../scripts/Rel755-ReadOnlyEvidence.ps1')
$script:passed=0
function Check([bool]$Condition,[string]$Name) {
    if (!$Condition) { throw "REL755 regression failed: $Name" }
    $script:passed++
}
$nl=[Environment]::NewLine
$info=@'
LogicalBytesPerSector : 512
PhysicalBytesPerSectorForAtomicity : 8192
PhysicalBytesPerSectorForPerformance : 16384
FileSystemEffectivePhysicalBytesPerSectorForAtomicity : 4096
Device Alignment : Aligned (0x000)
'@
$v=ConvertFrom-Rel755SectorInfo $info
Check ($v.status -eq 'OK' -and $v.maxPhysicalBytes -eq 16384 -and $v.effectiveAtomicityBytes -eq 4096) 'physical vs effective'
Check ((ConvertFrom-Rel755SectorInfo ($info+$nl+'LogicalBytesPerSector : 512')).status -ne 'OK') 'duplicate'
Check ((ConvertFrom-Rel755SectorInfo ($info.Replace('8192','8193'))).status -ne 'OK') 'invalid sector'
Check ((ConvertFrom-Rel755SectorInfo ($info.Replace('FileSystemEffectivePhysicalBytesPerSectorForAtomicity','Other'))).status -ne 'OK') 'missing field'
$dir=Join-Path ([IO.Path]::GetTempPath()) ('rel755-test-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dir) | Out-Null
try {
    $target=Join-Path $dir 'master.mdf'
    $header='2026-11-28 00:00:00.10 spid14s Error: 5178, Severity: 16, State: 1.'
    $message="2026-11-28 00:00:00.11 spid14s Cannot use file '$target' because it was originally formatted with sector size 4096 and is now on a volume with sector size 8192. Move the file to a volume with a sector size that is the same as or smaller than the original sector size."
    $text=$header+$nl+$message+$nl
    $pairs=ConvertFrom-Rel755SectorErrors $text $dir
    Check ($pairs.pairs.Count -eq 1 -and $pairs.pairs[0].scope -eq 'OWNED_FILE' -and $pairs.pairs[0].originalBytes -eq 4096) 'exact 5178'
    $json=$pairs | ConvertTo-Json -Depth 8
    Check (!$json.Contains($dir) -and !$json.Contains('Cannot use file')) 'redaction'
    $foreign=ConvertFrom-Rel755SectorErrors ($text.Replace($target,(Join-Path ([IO.Path]::GetTempPath()) 'master.mdf'))) $dir
    Check ($foreign.pairs.Count -eq 1 -and $foreign.pairs[0].scope -eq 'OUTSIDE_OR_UNKNOWN' -and $null -eq $foreign.pairs[0].file) 'basename is not scope'
    Check ((ConvertFrom-Rel755SectorErrors ($text.Replace('Error: 5178','Error: 5173')) $dir).pairs.Count -eq 0) '5173 not sector evidence'
    Check ((ConvertFrom-Rel755SectorErrors ($header+$nl+$message.Replace('spid14s','spid15s')) $dir).pairs.Count -eq 0) 'wrong spid'
    Check ((ConvertFrom-Rel755SectorErrors ($header+$nl+'OTHER'+$nl+$message) $dir).pairs.Count -eq 0) 'not adjacent'
    Check ((ConvertFrom-Rel755SectorErrors ($header+$nl+$message.Replace('00:00:00.11','00:00:05.11')) $dir).pairs.Count -eq 0) 'stale adjacent message'
    $log=Join-Path $dir 'error.log'
    [IO.File]::WriteAllText($log,$text,[Text.UnicodeEncoding]::new($false,$true))
    $raw=Get-Rel755LogText $log
    Check ($raw.status -eq 'OK' -and (ConvertFrom-Rel755SectorErrors $raw.text $dir).pairs.Count -eq 1) 'UTF16 bounded read'
    [IO.File]::WriteAllText($log,('x'*100000)+$nl+$text,[Text.UTF8Encoding]::new($false))
    $raw=Get-Rel755LogText $log
    Check ($raw.status -eq 'OK' -and $raw.tailOnly -and $raw.bytesRead -le 65536 -and (ConvertFrom-Rel755SectorErrors $raw.text $dir).pairs.Count -eq 1) 'bounded tail'
    Check ((Get-Rel755LogText (Join-Path $dir 'absent.log')).status -eq 'MISSING') 'missing log'
} finally { [IO.Directory]::Delete($dir,$true) }
# Dot-sourcing the legacy parameterized script assigns $Arm and $Iteration
# in the caller scope. Check the wrapper's actual AST uses disjoint names,
# and calls both runner guard and sampler with those preserved parameters.
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot '../../scripts/Invoke-Rel755LocalDbSample.ps1'),[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'wrapper parses'
$names=@($ast.ParamBlock.Parameters | ForEach-Object {$_.Name.VariablePath.UserPath})
Check ($names.Count -eq 2 -and $names -contains 'RequestedArm' -and $names -contains 'RequestedIteration' -and
    $names -notcontains 'Arm' -and $names -notcontains 'Iteration') 'parameter isolation'
$commands=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst]},$true))
$guard=@($commands | Where-Object {$_.GetCommandName() -eq 'Assert-Rel75Runner'})
$sampler=@($commands | Where-Object {$_.GetCommandName() -eq 'Invoke-Rel75Sample'})
Check ($guard.Count -eq 1 -and $guard[0].CommandElements[1].Extent.Text -ceq '$RequestedArm' -and
    $sampler.Count -eq 1 -and $sampler[0].CommandElements[1].Extent.Text -ceq '$RequestedArm' -and
    $sampler[0].CommandElements[2].Extent.Text -ceq '$RequestedIteration') 'preserved request forwarded'
Write-Host "REL-75.5 read-only projection regressions: $script:passed passed."

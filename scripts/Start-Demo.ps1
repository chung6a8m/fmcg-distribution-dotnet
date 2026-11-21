[CmdletBinding(DefaultParameterSetName = 'New')]
param(
    [Parameter(Mandatory, ParameterSetName = 'New')]
    [ValidateSet('Fresh', 'AC03Start', 'AC03Complete')]
    [string] $Mode,
    [Parameter(Mandatory, ParameterSetName = 'Resume')]
    [ValidatePattern('\A[a-f0-9]{32}\z')]
    [string] $Resume,
    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [ValidatePattern('\A[a-f0-9]{32}\z')]
    [string] $Remove
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $source = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $changes = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source state.' }
    if ($changes) { $source += '+working-tree' }
    switch ($PSCmdlet.ParameterSetName) {
        'New'    { $operation = $Mode; $runId = [Guid]::NewGuid().ToString('N') }
        'Resume' { $operation = 'Resume'; $runId = $Resume }
        'Remove' { $operation = 'Remove'; $runId = $Remove }
    }
    dotnet run --project src/FmcgDms.Demo/FmcgDms.Demo.csproj --no-launch-profile -- $operation $runId $source
    if ($LASTEXITCODE -ne 0) { throw "Demo command failed ($LASTEXITCODE). RunId $runId" }
}
finally { Pop-Location }

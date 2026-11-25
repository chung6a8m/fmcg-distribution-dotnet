[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scripts = Join-Path $PSScriptRoot '../../scripts'
$helper = Join-Path $scripts 'Use-CiPrebuiltDemo.ps1'
if (!(Test-Path -LiteralPath $helper)) { throw 'RED: CI prebuilt Demo guard is not implemented.' }
. $helper

# Exercise real files and assembly ProductVersion metadata. Only the external
# Git, application, and LocalDB process boundaries are simulated.
$source = 'a' * 40
$otherSource = 'b' * 40
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('fmcg-ci-prebuilt-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $temporary 'repo'
$previousState = Get-Variable -Name FmcgCiPrebuiltTestState -Scope Global -ErrorAction SilentlyContinue
$global:FmcgCiPrebuiltTestState = @{ Calls = [Collections.Generic.List[object]]::new() }
$script:Passed = 0
$environmentNames = @('FMCG_CI_PREBUILT_SHA', 'GITHUB_ACTIONS', 'RUNNER_OS', 'GITHUB_SHA')
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_.Exception.Message }
    Assert-That ($null -ne $failure -and $failure -match $Reason) "Expected rejection '$Reason'; observed '$failure'."
}

function New-FixtureAssembly([string]$Path, [string]$Version) {
    $type = 'PrebuiltFixture' + [Guid]::NewGuid().ToString('N')
    $definition = @"
using System.Reflection;
[assembly: AssemblyInformationalVersion("$Version")]
public static class $type { }
"@
    # Add-Type's source compiler omits Win32 resources. Linux can read the
    # managed attribute alone; Windows FileVersionInfo requires VERSIONINFO.
    Add-Type -AssemblyName Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.CSharp
    $syntax = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($definition)
    $reference = [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($type,
        [Microsoft.CodeAnalysis.SyntaxTree[]]@($syntax), [Microsoft.CodeAnalysis.MetadataReference[]]@($reference), $options)
    $native = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
    $output = [IO.File]::Create($Path)
    try {
        $result = $compilation.Emit($output, $null, $null, $native, $null, $null, [Threading.CancellationToken]::None)
        if (!$result.Success) { throw ($result.Diagnostics -join "`n") }
    } finally { $output.Dispose(); $native.Dispose() }

    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $directory = $reader.PEHeaders.PEHeader.ResourceTableDirectory
            Assert-That ($directory.Size -gt 0) 'Fixture requires native VERSIONINFO resources for Windows ProductVersion.'
            $bytes = [byte[]]$reader.GetSectionData($directory.RelativeVirtualAddress).GetContent(0, $directory.Size)
            $resources = [Text.Encoding]::Unicode.GetString($bytes)
            Assert-That ($resources.Contains('VS_VERSION_INFO') -and $resources.Contains('ProductVersion') -and
                $resources.Contains($Version)) 'Fixture native version resource does not contain the requested ProductVersion.'
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-OutputPath([string]$Project, [string]$Extension = 'dll') {
    return Join-Path $repository "src/$Project/bin/Release/net10.0/$Project.$Extension"
}

function Reset-Fixture {
    $env:FMCG_CI_PREBUILT_SHA = $source
    $env:GITHUB_ACTIONS = 'true'
    $env:RUNNER_OS = 'Windows'
    $env:GITHUB_SHA = $otherSource
    $global:FmcgCiPrebuiltTestState.GitHead = $source
    $global:FmcgCiPrebuiltTestState.GitChanges = $null
    $global:FmcgCiPrebuiltTestState.BuildExitCode = 0
    $global:FmcgCiPrebuiltTestState.RuntimeExitCode = 0
    $global:FmcgCiPrebuiltTestState.Calls.Clear()
    foreach ($project in @('FmcgDms.Demo', 'FmcgDms.Api')) {
        $dll = Get-OutputPath $project
        $null = New-Item -ItemType Directory -Force (Split-Path $dll -Parent)
        Copy-Item -LiteralPath $goodAssembly -Destination $dll -Force
        foreach ($extension in @('deps.json', 'runtimeconfig.json')) {
            Set-Content -LiteralPath (Get-OutputPath $project $extension) -Value '{}'
        }
    }
    $demoApi = Join-Path (Split-Path (Get-OutputPath 'FmcgDms.Demo') -Parent) 'FmcgDms.Api.dll'
    Copy-Item -LiteralPath $goodAssembly -Destination $demoApi -Force
}

function Test-Case([string]$Name, [scriptblock]$Action) {
    Reset-Fixture
    & $Action
    $script:Passed++
    Write-Host "PASS: $Name"
}

function git {
    $global:LASTEXITCODE = 0
    switch ($args -join ' ') {
        'rev-parse HEAD' { return $global:FmcgCiPrebuiltTestState.GitHead }
        'status --porcelain' { return $global:FmcgCiPrebuiltTestState.GitChanges }
        default { throw "Unexpected Git command: $args" }
    }
}

function dotnet {
    $global:FmcgCiPrebuiltTestState.Calls.Add(@{ executable = 'dotnet'; arguments = [string[]]$args })
    $global:LASTEXITCODE = if ($args[0] -ceq 'build') { $global:FmcgCiPrebuiltTestState.BuildExitCode } else { $global:FmcgCiPrebuiltTestState.RuntimeExitCode }
}

function sqllocaldb {
    $global:FmcgCiPrebuiltTestState.Calls.Add(@{ executable = 'sqllocaldb'; arguments = [string[]]$args })
    $global:LASTEXITCODE = 0
}

function Assert-Commands([string[]]$Expected) {
    $actual = @($global:FmcgCiPrebuiltTestState.Calls | ForEach-Object { $_.executable + ' ' + ($_.arguments -join ' ') })
    Assert-That (($actual -join "`n") -ceq ($Expected -join "`n")) "Unexpected process commands: $($actual -join '; ')"
}

try {
    $null = New-Item -ItemType Directory -Force (Join-Path $repository 'scripts')
    foreach ($name in @('Use-CiPrebuiltDemo.ps1', 'Run-App02C1.ps1', 'Run-App03Dev.ps1')) {
        Copy-Item -LiteralPath (Join-Path $scripts $name) -Destination (Join-Path $repository 'scripts')
    }
    $c1Launcher = Join-Path $repository 'scripts/Run-App02C1.ps1'
    $app03Launcher = Join-Path $repository 'scripts/Run-App03Dev.ps1'
    $goodAssembly = Join-Path $temporary 'current.dll'
    $oldAssembly = Join-Path $temporary 'old.dll'
    $unversionedAssembly = Join-Path $temporary 'unversioned.dll'
    New-FixtureAssembly $goodAssembly "1.0.0+$source"
    New-FixtureAssembly $oldAssembly "1.0.0+$otherSource"
    New-FixtureAssembly $unversionedAssembly '1.0.0'

    Test-Case 'No opt-in returns false without requiring CI or build outputs' {
        Remove-Item Env:FMCG_CI_PREBUILT_SHA
        Remove-Item Env:GITHUB_ACTIONS
        Remove-Item Env:RUNNER_OS
        $actual = Test-CiPrebuiltDemo -RepositoryRoot (Join-Path $temporary 'absent') -SourceCommit ($source + '+working-tree')
        Assert-That ($actual -is [bool] -and !$actual) 'Default execution must request its normal build.'
    }
    Test-Case 'Empty opt-in remains disabled' {
        $env:FMCG_CI_PREBUILT_SHA = ''
        Assert-That (!(Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source)) 'Empty marker enabled reuse.'
    }
    Test-Case 'Matching merge checkout and both real assembly versions permit reuse and log guard elapsed' {
        $output = @(Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source 6>&1)
        $result = @($output | Where-Object { $_ -is [bool] })
        Assert-That ($result.Count -eq 1 -and $result[0]) 'The guard must return one true Boolean.'
        Assert-That (($output -join ' ') -match 'guardMilliseconds=\d+') 'Successful reuse must expose guard elapsed.'
    }
    foreach ($marker in @(' ', 'true', ($source + '+working-tree'), ('A' * 40))) {
        Test-Case "Malformed nonempty opt-in is rejected: '$marker'" {
            $env:FMCG_CI_PREBUILT_SHA = $marker
            Assert-Rejected { Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source } 'marker'
        }
    }
    foreach ($condition in @('NoActions', 'Linux')) {
        Test-Case "Reuse outside declared Windows GitHub Actions is rejected: $condition" {
            if ($condition -eq 'NoActions') { Remove-Item Env:GITHUB_ACTIONS }
            else { $env:RUNNER_OS = 'Linux' }
            Assert-Rejected { Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source } 'Windows GitHub Actions'
        }
    }
    foreach ($candidate in @($otherSource, ($source + '+working-tree'))) {
        Test-Case "Dirty or different checkout source is rejected: $candidate" {
            Assert-Rejected { Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $candidate } 'clean checkout'
        }
    }
    foreach ($project in @('FmcgDms.Demo', 'FmcgDms.Api')) {
        foreach ($extension in @('dll', 'deps.json', 'runtimeconfig.json')) {
            Test-Case "Missing $project.$extension is rejected" {
                Remove-Item -LiteralPath (Get-OutputPath $project $extension)
                Assert-Rejected { Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source } 'missing'
            }
        }
        foreach ($assembly in @($oldAssembly, $unversionedAssembly)) {
            Test-Case "Mismatched or absent $project source identity is rejected: $([IO.Path]::GetFileName($assembly))" {
                Copy-Item -LiteralPath $assembly -Destination (Get-OutputPath $project) -Force
                Assert-Rejected { Test-CiPrebuiltDemo -RepositoryRoot $repository -SourceCommit $source } 'source identity'
            }
        }
    }

    $copyFailures = [Collections.Generic.List[string]]::new()
    foreach ($launcher in @('C1', 'APP03')) {
        foreach ($copyState in @('Missing', 'Stale')) {
            try {
                Test-Case "$launcher $copyState API copy in Demo output is rejected before LocalDB or runtime" {
                    $demoApi = Join-Path (Split-Path (Get-OutputPath 'FmcgDms.Demo') -Parent) 'FmcgDms.Api.dll'
                    if ($copyState -eq 'Missing') { Remove-Item -LiteralPath $demoApi }
                    else { Copy-Item -LiteralPath $oldAssembly -Destination $demoApi -Force }
                    $reason = if ($copyState -eq 'Missing') { 'missing' } else { 'source identity' }
                    Assert-Rejected {
                        if ($launcher -eq 'C1') { & $c1Launcher -Verify } else { & $app03Launcher -Action Verify }
                    } $reason
                    Assert-Commands @()
                }
            } catch { $copyFailures.Add("$launcher ${copyState}: $($_.Exception.Message)") }
        }
    }
    if ($copyFailures.Count) { throw ($copyFailures -join "`n") }

    foreach ($launcher in @('C1', 'APP03')) {
        Test-Case "$launcher without opt-in builds before LocalDB and retains runtime arguments" {
            Remove-Item Env:FMCG_CI_PREBUILT_SHA
            if ($launcher -eq 'C1') {
                & $c1Launcher -Verify -Increment DevF03
                $runtime = "dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll DevF03 Verify $repository $source"
            } else {
                & $app03Launcher -Action Verify
                $runtime = "dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll App03Session Verify $repository $source -"
            }
            Assert-Commands @('dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release', 'sqllocaldb start MSSQLLocalDB', $runtime)
        }
        Test-Case "$launcher default build failure prevents LocalDB and runtime launch" {
            Remove-Item Env:FMCG_CI_PREBUILT_SHA
            $global:FmcgCiPrebuiltTestState.BuildExitCode = 9
            Assert-Rejected {
                if ($launcher -eq 'C1') { & $c1Launcher -Verify } else { & $app03Launcher -Action Verify }
            } 'build failed'
            Assert-Commands @('dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release')
        }
        Test-Case "$launcher valid prebuild skips only the build" {
            if ($launcher -eq 'C1') {
                & $c1Launcher -Verify
                $runtime = "dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll App02C1 Verify $repository $source"
            } else {
                & $app03Launcher -Action VerifyFinance
                $runtime = "dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll App03Session VerifyFinance $repository $source -"
            }
            Assert-Commands @('sqllocaldb start MSSQLLocalDB', $runtime)
        }
        Test-Case "$launcher dirty source prevents every runtime process when reuse is requested" {
            $global:FmcgCiPrebuiltTestState.GitChanges = ' M src/changed.cs'
            Assert-Rejected {
                if ($launcher -eq 'C1') { & $c1Launcher -Verify } else { & $app03Launcher -Action Verify }
            } 'clean checkout'
            Assert-Commands @()
        }
        Test-Case "$launcher still propagates failure from a reused runtime" {
            $global:FmcgCiPrebuiltTestState.RuntimeExitCode = 7
            Assert-Rejected {
                if ($launcher -eq 'C1') { & $c1Launcher -Verify } else { & $app03Launcher -Action Verify }
            } 'failed'
            Assert-That ($global:FmcgCiPrebuiltTestState.Calls.Count -eq 2 -and $global:FmcgCiPrebuiltTestState.Calls[1].arguments[0] -like '*.dll') 'Runtime failure was not reached through reuse.'
        }
    }
    Test-Case 'APP03 Remove without opt-in preserves its existing-output build exception' {
        Remove-Item Env:FMCG_CI_PREBUILT_SHA
        & $app03Launcher -Action Remove -RunId ('c' * 32)
        Assert-Commands @('sqllocaldb start MSSQLLocalDB', "dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll App03Session Remove $repository $source $('c' * 32)")
    }
    Test-Case 'APP03 Remove without output still builds when opt-in is absent' {
        Remove-Item Env:FMCG_CI_PREBUILT_SHA
        Remove-Item -LiteralPath (Get-OutputPath 'FmcgDms.Demo')
        & $app03Launcher -Action Remove -RunId ('c' * 32)
        Assert-That ($global:FmcgCiPrebuiltTestState.Calls[0].arguments[0] -ceq 'build' -and $global:FmcgCiPrebuiltTestState.Calls.Count -eq 3) 'Remove lost its missing-output build fallback.'
    }
    Test-Case 'Standalone copied Stop reaches its own manifest validation without helper, Git, or outputs' {
        $standalone = Join-Path $temporary 'standalone'
        $null = New-Item -ItemType Directory $standalone
        $stopScript = Join-Path $standalone 'Run-App03Dev.ps1'
        Copy-Item -LiteralPath $app03Launcher -Destination $stopScript
        & {
            # Shadow only the existing OS boundary so the independent Stop path
            # is exercised on Linux too; the random session has no manifest.
            $wasWindows = $IsWindows
            try {
                Set-Variable -Name IsWindows -Value $true -Scope Local -Force
                Assert-Rejected { & $stopScript -Action Stop -RunId ([Guid]::NewGuid().ToString('N')) } 'session manifest does not exist'
            } finally { Set-Variable -Name IsWindows -Value $wasWindows -Scope Local -Force }
        }
        Assert-Commands @()
    }
    Test-Case 'A new PowerShell child inherits prebuild reuse for nested APP03 NextDay' {
        $child = Join-Path $temporary 'nextday.ps1'
        Set-Content -LiteralPath $child -Value @'
param([string]$RepositoryRoot, [string]$SourceCommit)
$ErrorActionPreference = 'Stop'
function git {
    $global:LASTEXITCODE = 0
    switch ($args -join ' ') {
        'rev-parse HEAD' { return $SourceCommit }
        'status --porcelain' { return }
        default { throw "Unexpected Git command: $args" }
    }
}
function sqllocaldb {
    if (($args -join ' ') -cne 'start MSSQLLocalDB') { throw 'Unexpected LocalDB target.' }
    $global:LASTEXITCODE = 0
}
function dotnet {
    $expected = @('src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll', 'App03Session', 'NextDay', $RepositoryRoot, $SourceCommit, ('c' * 32), '2026-11-24')
    if (($args -join '|') -cne ($expected -join '|')) { throw "Nested NextDay rebuilt or changed arguments: $args" }
    $global:LASTEXITCODE = 0
    Write-Output 'NEXTDAY_PREBUILT_OK'
}
& (Join-Path $RepositoryRoot 'scripts/Run-App03Dev.ps1') -Action NextDay -RunId ('c' * 32) -BusinessDate '2026-11-24'
'@
        $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
        $output = & $pwsh -NoProfile -File $child -RepositoryRoot $repository -SourceCommit $source 2>&1
        Assert-That ($LASTEXITCODE -eq 0 -and ($output -join ' ') -match 'NEXTDAY_PREBUILT_OK') "Nested NextDay failed: $output"
    }
    Write-Host "CI prebuilt Demo regressions: $script:Passed passed."
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    if ($null -eq $previousState) { Remove-Variable -Name FmcgCiPrebuiltTestState -Scope Global }
    else { Set-Variable -Name FmcgCiPrebuiltTestState -Scope Global -Value $previousState.Value }
}

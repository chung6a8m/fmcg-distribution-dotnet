function Test-CiPrebuiltDemo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$SourceCommit
    )

    $marker = $env:FMCG_CI_PREBUILT_SHA
    if ([string]::IsNullOrEmpty($marker)) { return $false }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($marker -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'CI prebuilt Demo marker must be an exact checkout SHA.'
    }
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Windows') {
        throw 'CI prebuilt Demo reuse requires Windows GitHub Actions.'
    }
    # The launchers resolve HEAD and status immediately before this check. A
    # dirty checkout carries +working-tree and cannot reuse compiled outputs.
    if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or $SourceCommit -cne $marker) {
        throw 'CI prebuilt Demo marker does not match the exact clean checkout.'
    }

    foreach ($project in @('FmcgDms.Demo', 'FmcgDms.Api')) {
        $directory = Join-Path $RepositoryRoot "src/$project/bin/Release/net10.0"
        foreach ($extension in @('deps.json', 'runtimeconfig.json')) {
            $path = Join-Path $directory "$project.$extension"
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "CI prebuilt Demo output is missing: $project.$extension."
            }
        }
        $assemblies = @("$project.dll")
        # Demo loads its own copy of the API project reference in addition to
        # launching the standalone API executable from the API output directory.
        if ($project -ceq 'FmcgDms.Demo') { $assemblies += 'FmcgDms.Api.dll' }
        foreach ($assembly in $assemblies) {
            $dll = Join-Path $directory $assembly
            if (!(Test-Path -LiteralPath $dll -PathType Leaf)) {
                throw "CI prebuilt Demo output is missing: $project/$assembly."
            }
            $version = [string][Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
            if (!$version.EndsWith('+' + $SourceCommit, [StringComparison]::Ordinal)) {
                throw "CI prebuilt Demo source identity does not match checkout: $project/$assembly."
            }
        }
    }

    $watch.Stop()
    Write-Host "CI prebuilt Demo/API reuse: source=$SourceCommit; guardMilliseconds=$($watch.ElapsedMilliseconds)"
    return $true
}

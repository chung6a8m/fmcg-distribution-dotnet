# LAB-D01-only Windows bootstrap and Authenticode checks.
function Assert-D01WindowsEnvironment {
    if (-not [Environment]::Is64BitProcess) { throw 'WINDOWS_64_BIT_REQUIRED' }
    $windows = [Environment]::GetFolderPath('Windows')
    $roots = @{
        SystemRoot = $windows
        windir = $windows
        ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
        ProgramData = [Environment]::GetFolderPath('CommonApplicationData')
        USERPROFILE = [Environment]::GetFolderPath('UserProfile')
        APPDATA = [Environment]::GetFolderPath('ApplicationData')
        LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
    }
    foreach ($entry in $roots.GetEnumerator()) {
        if (-not $entry.Value) { throw 'WINDOWS_ROOT_UNAVAILABLE' }
        $supplied = [Environment]::GetEnvironmentVariable($entry.Key)
        if (-not $supplied -or $supplied.TrimEnd('\') -ine $entry.Value.TrimEnd('\')) {
            throw 'WINDOWS_ROOT_ENV_OVERRIDE'
        }
    }
}

function Test-D01Authenticode {
    param([string]$Path, [string]$Publisher)
    $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { return $false }
    $certificate = $signature.SignerCertificate
    $signer = $certificate.GetNameInfo('SimpleName', $false)
    $expected = switch ($Publisher) {
        'Microsoft Corporation' { 'Microsoft Corporation', 'Microsoft Windows', 'Microsoft Windows Publisher' }
        'Docker Inc' { 'Docker Inc' }
    }
    return ($signer -in $expected -and
        $certificate.Subject -match ('(?:^|,\s*)O=' + [regex]::Escape($Publisher) + '(?:,|$)'))
}

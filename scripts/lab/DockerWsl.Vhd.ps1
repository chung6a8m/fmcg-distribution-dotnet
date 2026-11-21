# Read-only fallback for a Docker-managed VHD attached directly to the WSL VM.
function Get-D01WslVmId {
    param([string]$Hcs)
    $raw = & $Hcs list -raw 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $running = @(($raw -join [Environment]::NewLine | ConvertFrom-Json -AsHashtable) |
        Where-Object { $_.SystemType -eq 'VirtualMachine' -and $_.Owner -eq 'WSL' -and $_.State -eq 'Running' })
    if ($running.Count -eq 1) {
        $id = $running[0].Id -as [guid]
        if ($id -and $id -ne [guid]::Empty) { return $id }
    }
}

function Get-D01DockerWslMount {
    param([string]$Wsl)
    $distro = 'docker-desktop'
    $running = @(& $Wsl --list --running --quiet 2>$null |
        ForEach-Object { $_.Replace([string][char]0, '').Trim() })
    if ($LASTEXITCODE -ne 0 -or $distro -cnotin $running) { return }

    $lines = @(& $Wsl --distribution $distro --user root --exec /bin/cat /proc/self/mountinfo 2>$null)
    if ($LASTEXITCODE -ne 0) { return }
    $devices = @(foreach ($line in $lines) {
        if ($line -cmatch '^\d+ \d+ (?<id>\d+:\d+) / /mnt/docker-desktop-disk (?<flags>\S+)(?: \S+)* - ext4 /dev/sd[a-z]+ ') {
            if ('rw' -notin ($Matches.flags -split ',')) { return }
            $Matches.id
        } elseif ($line -cmatch '^\d+ \d+ \d+:\d+ \S+ /mnt/docker-desktop-disk ') {
            return
        }
    })
    $devices = @($devices | Sort-Object -Unique)
    if ($devices.Count -ne 1) { return }

    $device = "/sys/dev/block/$($devices[0])"
    $link = (& $Wsl --distribution $distro --user root --exec /usr/bin/readlink -f "$device/device" 2>$null) -join ''
    if ($LASTEXITCODE -ne 0 -or $link -notmatch '/(?<slot>0:0:0:(?<lun>\d{1,3}))$') { return }
    $slot = $Matches.slot
    $lun = [int]$Matches.lun
    $controllers = @(& $Wsl --distribution $distro --user root --exec /bin/ls -1 /sys/class/scsi_host 2>$null)
    if ($LASTEXITCODE -ne 0 -or $controllers.Count -ne 1 -or $controllers[0] -cne 'host0') { return }
    $scsi = @(& $Wsl --distribution $distro --user root --exec /bin/ls -1 /sys/class/scsi_device 2>$null)
    if ($LASTEXITCODE -ne 0 -or @($scsi | Where-Object { $_ -notmatch '^\d+:\d+:\d+:\d+$' }).Count) { return }
    $slots = @($scsi | Where-Object { $_ -match ":$lun$" })
    if ($slots.Count -ne 1 -or $slots[0] -cne $slot) { return }
    $size = (& $Wsl --distribution $distro --user root --exec /bin/cat "$device/size" 2>$null) -join ''
    $sectors = 0L
    if ($LASTEXITCODE -ne 0 -or -not [long]::TryParse($size, [ref]$sectors) -or
            $sectors -le 0 -or $sectors -gt ([long]::MaxValue -shr 9)) { return }
    return @{ device = $devices[0]; lun = $lun; capacity = $sectors -shl 9 }
}

function Get-D01HcsScsiChanges {
    param([guid]$VmId, [string]$VhdPath)
    # The provider returns newest first. If the matching Add has rotated out
    # of this bounded window, the collector has no attachment witness.
    $filter = @{
        LogName = 'Microsoft-Windows-Hyper-V-Compute-Operational'
        ProviderName = 'Microsoft-Windows-Hyper-V-Compute'
        Id = 2007
    }
    try {
        $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 1024)
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound,*') { return }
        throw
    }
    $changes = [Collections.Generic.List[object]]::new()
    foreach ($record in $events) {
        $xml = [xml]$record.ToXml()
        $subject = $xml.SelectSingleNode("//*[local-name()='SystemId']").InnerText -as [guid]
        if ($subject -ne $VmId) { continue }
        $result = $xml.SelectSingleNode("//*[local-name()='Result']").InnerText
        if ($result -notmatch '^(0x0+|0+)$') { continue }
        $change = $xml.SelectSingleNode("//*[local-name()='Parameter0']").InnerText | ConvertFrom-Json -AsHashtable
        if ($change.ResourcePath -cnotmatch '^VirtualMachine/Devices/Scsi/(?<controller>\d{1,3})/Attachments/(?<lun>\d{1,3})$') {
            if ($change.ResourcePath -match '^VirtualMachine(?:/Devices(?:/Scsi(?:/.*)?)?)?/?$') { return }
            continue
        }
        $controller = [int]$Matches.controller
        $lun = [int]$Matches.lun
        $backing = [string]$change.Settings.Path
        if ($backing.StartsWith('\\?\', [StringComparison]::Ordinal)) { $backing = $backing.Substring(4) }
        $changes.Add(@{
            recordId = $record.RecordId
            controller = $controller
            lun = $lun
            request = $change.RequestType
            matchesVhd = ($change.Settings.Type -ceq 'VirtualDisk' -and $backing -ceq $VhdPath)
        })
    }
    return $changes
}

function Get-D01WslVhdAttachment {
    param([string]$SystemDirectory, [string]$VhdPath)
    $hcs = Join-Path $SystemDirectory 'hcsdiag.exe'
    $wsl = Join-Path $SystemDirectory 'wsl.exe'
    foreach ($tool in $hcs, $wsl) {
        if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { return }
        if (-not (Test-D01Authenticode -Path $tool -Publisher 'Microsoft Corporation')) {
            throw 'UNTRUSTED_NATIVE_EXECUTABLE'
        }
    }
    $vm = Get-D01WslVmId -Hcs $hcs
    if (-not $vm) { return }
    $mount = Get-D01DockerWslMount -Wsl $wsl
    if (-not $mount) { return }
    $changes = @(Get-D01HcsScsiChanges -VmId $vm -VhdPath $VhdPath)
    $after = Get-D01DockerWslMount -Wsl $wsl
    if (-not $after -or $mount.device -ne $after.device -or $mount.lun -ne $after.lun -or
            $mount.capacity -ne $after.capacity) { return }
    $finalChanges = @(Get-D01HcsScsiChanges -VmId $vm -VhdPath $VhdPath)
    if (($changes.recordId -join ',') -cne ($finalChanges.recordId -join ',') -or
            $vm -ne (Get-D01WslVmId -Hcs $hcs)) { return }
    return @{ stableVm = $true; guestLun = $mount.lun; guestCapacity = $mount.capacity; changes = $changes }
}

# REL-75.5: read-only projections, loaded only by the investigation wrapper.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-Rel755SectorInfo([string]$Text) {
    $row = [ordered]@{ status='UNAVAILABLE'; logicalBytes=$null; physicalAtomicityBytes=$null
        physicalPerformanceBytes=$null; effectiveAtomicityBytes=$null; maxPhysicalBytes=$null }
    $fields = [ordered]@{ LogicalBytesPerSector='logicalBytes'
        PhysicalBytesPerSectorForAtomicity='physicalAtomicityBytes'
        PhysicalBytesPerSectorForPerformance='physicalPerformanceBytes'
        FileSystemEffectivePhysicalBytesPerSectorForAtomicity='effectiveAtomicityBytes' }
    foreach ($label in $fields.Keys) {
        $matches = [regex]::Matches($Text, '(?m)^[ \t]*' + $label + '[ \t]*:[ \t]*(\d{1,10})[ \t]*\r?$')
        if ($matches.Count -ne 1) { $row.status='MALFORMED_OR_UNSUPPORTED'; return $row }
        $n=[long]$matches[0].Groups[1].Value
        if ($n -lt 512 -or $n -gt 1048576 -or ($n -band ($n-1)) -ne 0) {
            $row.status='MALFORMED_OR_UNSUPPORTED'; return $row
        }
        $row[$fields[$label]]=$n
    }
    if ($row.physicalAtomicityBytes -lt $row.logicalBytes -or $row.physicalPerformanceBytes -lt $row.logicalBytes -or
        $row.effectiveAtomicityBytes -lt $row.logicalBytes -or $row.effectiveAtomicityBytes -gt $row.physicalAtomicityBytes) {
        $row.status='MALFORMED_OR_UNSUPPORTED'; return $row
    }
    $row.maxPhysicalBytes=[Math]::Max($row.physicalAtomicityBytes,$row.physicalPerformanceBytes)
    $row.status='OK'
    return $row
}

function Get-Rel755Volume([string]$Path) {
    # Existing path/ancestor guard rejects junctions, mount points, symlinks
    # and access errors. No fallback to C:, workspace or system drive.
    $row=[ordered]@{ status='UNAVAILABLE'; rootDrive=$null; resolution=$null; sector=$null }
    try {
        $full=[IO.Path]::GetFullPath($Path)
        $root=[IO.Path]::GetPathRoot($full)
        if ($root -cnotmatch '\A[A-Za-z]:\\\z') { $row.status='UNSUPPORTED_PATH'; return $row }
        $state=Get-Rel75PathState $full
        if ($state -notin 'PRESENT','MISSING') { $row.status=$state; return $row }
        $ancestor=$full
        while ((Get-Rel75PathState $ancestor) -eq 'MISSING') {
            $parent=[IO.Directory]::GetParent($ancestor)
            if ($null -eq $parent) { return $row }
            $ancestor=$parent.FullName
        }
        if ((Get-Rel75PathState $ancestor) -ne 'PRESENT') { return $row }
        $drive=[IO.DriveInfo]::new($root)
        if (!$drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) {
            $row.status='UNSUPPORTED_VOLUME'; return $row
        }
        $row.rootDrive=$root.Substring(0,2).ToUpperInvariant()
        $row.resolution=if ($state -eq 'PRESENT') { 'EXISTING_PATH' } else { 'NEAREST_EXISTING_ANCESTOR' }
        $exe=Join-Path ([Environment]::GetFolderPath('System')) 'fsutil.exe'
        $native=Invoke-LocalDbProbe -Executable $exe -Arguments @('fsinfo','sectorinfo',$row.rootDrive) -TimeoutMilliseconds 3000
        if ($native.status -ne 'OK' -or $native.exitCode -ne 0 -or $native.truncated -or $native.stderr.Trim()) {
            $row.status='SECTOR_QUERY_FAILED'; return $row
        }
        $row.sector=ConvertFrom-Rel755SectorInfo $native.stdout
        $row.status=$row.sector.status
        return $row
    } catch { return $row }
}

function Get-Rel755LogText([string]$Path) {
    # Read only a bounded tail of the known error.log, never publish contents.
    $row=[ordered]@{ status='UNAVAILABLE'; bytesRead=0; tailOnly=$false; text=$null }
    $state=Get-Rel75PathState $Path
    if ($state -ne 'PRESENT') { $row.status=$state; return $row }
    $stream=$null
    try {
        if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::Directory) { return $row }
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $length=$stream.Length
        $header=[byte[]]::new([Math]::Min(3,$length))
        $stream.ReadExactly($header)
        $row.bytesRead=$header.Length
        $utf16=$header.Length -ge 2 -and $header[0] -eq 255 -and $header[1] -eq 254
        if (($utf16 -and ($length%2 -ne 0)) -or ($header.Length -ge 2 -and $header[0] -eq 254 -and $header[1] -eq 255)) {
            $row.status='MALFORMED_CONTENT'; return $row
        }
        $offset=if ($length -gt 65536-$header.Length) { $length-(65536-$header.Length) } else { 0 }
        if ($utf16 -and ($offset%2 -ne 0)) { $offset++ }
        $row.tailOnly=$offset -gt 0
        $stream.Position=$offset
        $buffer=[byte[]]::new([int]($length-$offset))
        $stream.ReadExactly($buffer)
        $row.bytesRead+=$buffer.Length
        $from=0
        if ($row.tailOnly) {
            $from=$buffer.Length
            $step=if ($utf16) { 2 } else { 1 }
            for ($i=0;$i -lt $buffer.Length;$i+=$step) {
                if ($buffer[$i] -eq 10 -and (!$utf16 -or $buffer[$i+1] -eq 0)) { $from=$i+$step; break }
            }
        } elseif ($utf16) { $from=2 }
        elseif ($header.Length -eq 3 -and $header[0] -eq 239 -and $header[1] -eq 187 -and $header[2] -eq 191) { $from=3 }
        $encoding=if ($utf16) { [Text.UnicodeEncoding]::new($false,$false,$true) } else { [Text.UTF8Encoding]::new($false,$true) }
        $row.text=$encoding.GetString($buffer,$from,$buffer.Length-$from)
        $row.status='OK'
        return $row
    } catch { $row.status='READ_FAILED'; return $row }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function ConvertFrom-Rel755SectorErrors([string]$Text,[string]$Directory) {
    # A full 5178 header and its immediately adjacent same-SPID message are
    # required. Never infer a pair from a 5173, a filename suffix, or another line.
    $lines=$Text -split '\r?\n'
    $rows=[Collections.Generic.List[object]]::new()
    $header='^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{2,3}\s+(?<spid>spid\d+[a-z]?)\s+Error: 5178, Severity: (?<severity>\d+), State: (?<state>\d+)\.\s*$'
    $message="^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{2,3}\s+(?<spid>spid\d+[a-z]?)\s+Cannot use file '(?<path>[^'\r\n]{1,1024})' because it was originally formatted with sector size (?<original>\d{1,7}) and is now on a volume with sector size (?<actual>\d{1,7})\. Move the file to a volume with a sector size that is the same as or smaller than the original sector size\.\s*$"
    for ($i=0;$i+1 -lt $lines.Length;$i++) {
        $h=[regex]::Match($lines[$i],$header,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$h.Success) { continue }
        $m=[regex]::Match($lines[$i+1],$message,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$m.Success -or $m.Groups['spid'].Value -ine $h.Groups['spid'].Value) { continue }
        if ($rows.Count -ge 16) { return @{ status='TRUNCATED'; pairs=@($rows.ToArray()) } }
        $a=[long]$m.Groups['original'].Value; $b=[long]$m.Groups['actual'].Value
        if ($a -lt 512 -or $b -lt 512 -or $a -gt 1048576 -or $b -gt 1048576 -or
            ($a -band ($a-1)) -ne 0 -or ($b -band ($b-1)) -ne 0) { continue }
        $file=$null
        try {
            $path=$m.Groups['path'].Value
            if ([IO.Path]::IsPathFullyQualified($path)) {
                foreach ($name in 'master.mdf','mastlog.ldf','model.mdf','modellog.ldf','msdbdata.mdf','msdblog.ldf',
                    'model_msdbdata.mdf','model_msdblog.ldf','model_replicatedmaster.mdf','model_replicatedmaster.ldf') {
                    if ([IO.Path]::GetFullPath($path) -ieq [IO.Path]::GetFullPath((Join-Path $Directory $name))) { $file=$name; break }
                }
            }
        } catch { }
        # Outside-owned-path messages are counted but cannot support remediation.
        $rows.Add([ordered]@{ error=5178; severity=[int]$h.Groups['severity'].Value; state=[int]$h.Groups['state'].Value
            scope=if ($null -ne $file) {'OWNED_FILE'} else {'OUTSIDE_OR_UNKNOWN'}
            file=$file; originalBytes=$a; actualBytes=$b })
    }
    return @{ status='OK'; pairs=@($rows.ToArray()) }
}

function Get-Rel755SectorErrors([string]$Directory) {
    return @(foreach ($name in 'error.log','error1.log','error2.log') {
        $log=Get-Rel755LogText (Join-Path $Directory $name)
        $parsed=if ($log.status -eq 'OK') { ConvertFrom-Rel755SectorErrors $log.text $Directory } else { @{ status=$log.status; pairs=@() } }
        $pairs=@(foreach ($pair in $parsed.pairs) {
            $pair['volume']=if ($pair.scope -eq 'OWNED_FILE') { Get-Rel755Volume (Join-Path $Directory $pair.file) } else { $null }
            $pair
        })
        @{ file=$name; status=$parsed.status; bytesRead=$log.bytesRead; tailOnly=$log.tailOnly; pairs=$pairs }
    })
}

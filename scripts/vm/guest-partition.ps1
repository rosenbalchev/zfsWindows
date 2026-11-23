# Runs inside the driver test VM: one stage of the partition test (issue
# #48) per invocation, printing one JSON object.  Driven by
# scripts\vm\Test-PartitionInVm.ps1.
param(
    [Parameter(Mandatory)]
    [ValidateSet("layout", "resolve", "create", "verify", "export", "restart", "neighbors", "msi")]
    [string]$Stage,
    # The orchestrator's arguments as Base64 JSON.
    [string]$ArgumentsBase64 = ""
)

$ErrorActionPreference = "Continue"
$a = if ($ArgumentsBase64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64)) | ConvertFrom-Json } else { [pscustomobject]@{} }
$work = "C:\zfswin\partition"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$edition = if ($a.Edition) { $a.Edition } else { "kernel" }
$product = if ($edition -eq "usermode") { "OpenZFS for Windows (user mode)" } else { "OpenZFS for Windows" }
$bin = Join-Path ${env:ProgramFiles} "$product\bin"
$zpool = Join-Path $bin "zpool.exe"
$zfswin = Join-Path $bin "zfswin.exe"
$r = [ordered]@{ edition = $edition }

function Zfswin([string[]]$arguments) {
    $out = & $zfswin --json @arguments 2>$null
    $code = $LASTEXITCODE
    $json = $null
    try { $json = ($out -join "`n") | ConvertFrom-Json } catch { }
    [pscustomobject]@{ args = ($arguments -join ' '); exit = $code; error = $(if ($json -and -not $json.ok) { "$($json.error.code): $($json.error.message)" }); result = $(if ($json) { $json.result }) }
}

function Zpool([string[]]$arguments) {
    $out = & $zpool @arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ args = ($arguments -join ' '); exit = $LASTEXITCODE; text = ($out -join "`n") }
}

function Check([string]$name, $value, $expected) {
    $r[$name] = [ordered]@{ value = "$value"; expected = "$expected"; pass = ("$value" -eq "$expected") }
}

function MountPath([string]$dataset) {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        $m = (Zfswin @('mount', 'list')).result.mounts | Where-Object dataset -eq $dataset | Select-Object -First 1
        if ($m) {
            $p = $m.mountpoint -replace '/', '\'
            if ($p -match '^[A-Za-z]:$') { $p += '\' }
            if (Test-Path -LiteralPath $p) { return $p }
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $null
}

function Pools { @((Zfswin @('pool', 'list')).result.pools | ForEach-Object name) }

function Layout([int]$disk) {
    (Get-Partition -DiskNumber $disk | Sort-Object PartitionNumber | ForEach-Object { "$($_.PartitionNumber):$($_.Offset):$($_.Size):$($_.GptType)" }) -join ' '
}

function Finish {
    $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
    $r | ConvertTo-Json -Depth 6 -Compress
}

switch ($Stage) {
    # The test disk (the VM's only RAW disk): GPT with an NTFS partition on
    # each side of every partition a pool goes on.
    #   P: NTFS | T1 | Q: NTFS | T2 | T3 | R: NTFS
    "layout" {
        $disk = Get-Disk | Where-Object PartitionStyle -eq 'RAW' | Sort-Object Number | Select-Object -Last 1
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $parts = [ordered]@{}
        foreach ($p in @(@('P', 1GB, 'P'), @('T1', 2GB, $null), @('Q', 1GB, 'Q'), @('T2', 2GB, $null), @('T3', 2GB, $null), @('R', 1GB, 'R'))) {
            $part = if ($p[2]) { New-Partition -DiskNumber $disk.Number -Size $p[1] -DriveLetter $p[2] } else { New-Partition -DiskNumber $disk.Number -Size $p[1] }
            if ($p[2]) { Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel "neighbor$($p[0])" -Confirm:$false | Out-Null }
            $parts[$p[0]] = [ordered]@{ number = $part.PartitionNumber; offset = $part.Offset; size = $part.Size }
        }
        $hashes = [ordered]@{}
        foreach ($letter in 'P', 'Q', 'R') {
            $bytes = [byte[]]::new(256MB); [Random]::new([int][char]$letter).NextBytes($bytes)
            [IO.File]::WriteAllBytes("${letter}:\neighbor.bin", $bytes)
            $hashes[$letter] = (Get-FileHash -LiteralPath "${letter}:\neighbor.bin" -Algorithm SHA256).Hash
        }
        $r.disk = $disk.Number
        $r.partitions = $parts
        $r.hashes = $hashes
        $r.layout = Layout $disk.Number
        Finish
    }

    # zpool create -n with every spelling of one partition: all resolve to
    # the same extent.  Partition 0, a missing partition, a partition
    # holding NTFS and the whole disk are refused.
    "resolve" {
        $n = $a.Disk; $m = $a.Partition; $ntfs = $a.NtfsPartition
        $want = "#$($a.Offset)#$($a.Length)#"
        $spellings = @("\\?\Harddisk${n}Partition$m", "\\.\Harddisk${n}Partition$m", "\\?\Harddisk$n\Partition$m", "Harddisk${n}Partition$m",
            "PhysicalDrive$n/Partition$m", "PhysicalDrive$n\Partition$m", "\\?\PhysicalDrive$n\Partition$m", "PhysicalDisk$n/Partition$m",
            "//?/Harddisk${n}Partition$m", "/dev/Harddisk${n}Partition$m", "#$($a.Offset)#$($a.Length)#\\?\PhysicalDrive$n")
        $r.spellings = [ordered]@{}
        foreach ($s in $spellings) {
            $z = Zpool @('create', '-n', 'zres', $s)
            $r.spellings[$s] = $z.text
            Check "dryrun $s" $z.exit 0
        }
        # The dry run prints the vdev as zpool names it; every spelling the same.
        $texts = @($r.spellings.Values | ForEach-Object { ($_ -split "`n" | Where-Object { $_ -match '^\s+\S' -and $_ -notmatch 'would create' }) -join '|' } | Sort-Object -Unique)
        Check "one-extent" $texts.Count 1
        $z = Zpool @('create', '-n', 'zres', "\\?\Harddisk${n}Partition0")
        Check "partition0-refused" ($z.exit -ne 0 -and $z.text -match 'partition 0 is the whole disk') $true
        $z = Zpool @('create', '-n', 'zres', "\\?\Harddisk${n}Partition99")
        Check "missing-refused" ($z.exit -ne 0 -and $z.text -match 'has no partition 99') $true
        $z = Zpool @('create', '-n', 'zres', "\\?\Harddisk${n}Partition$ntfs")
        $r.ntfsText = $z.text
        Check "ntfs-refused" ($z.exit -ne 0 -and $z.text -match 'overlaps volume') $true
        $z = Zpool @('create', '-n', 'zres', "\\?\PhysicalDrive$n")
        $r.wholeText = $z.text
        Check "wholedisk-refused" ($z.exit -ne 0) $true
        $w = Zfswin @('pool', 'create', 'zres', "PhysicalDrive$n/Partition0", '--yes')
        Check "broker-partition0-refused" ($w.exit -ne 0 -and "$($w.error)" -match 'partition 0') $true
        $w = Zfswin @('pool', 'create', 'zres', "\\?\Harddisk${n}Partition$ntfs", '--yes')
        Check "broker-ntfs-refused" ($w.exit -ne 0) $true
        Check "nothing-created" ((Pools) -contains 'zres') $false
        $r.want = $want
        Finish
    }

    # A pool on one partition, by zpool.exe or zfswin, then a file on it.
    "create" {
        $want = "#$($a.Offset)#$($a.Length)#"
        if ($a.Via -eq 'zpool') {
            $z = Zpool (@('create', '-o', 'ashift=12', '-O', 'compression=lz4', '-O', 'normalization=formD', '-O', 'casesensitivity=insensitive', $a.Pool, $a.Device))
            $r.create = $z.text
            Check "create" $z.exit 0
        } else {
            $w = Zfswin @('pool', 'create', $a.Pool, $a.Device, '-O', 'compression=lz4', '--yes')
            $r.create = $w.error
            Check "create" $w.exit 0
        }
        $status = Zpool @('status', '-j', '-p', '-P', $a.Pool)
        $r.status = $status.text
        Check "recorded-as-extent" ($status.text -match [regex]::Escape($want)) $true
        $size = [long]((Zpool @('get', '-H', '-p', '-o', 'value', 'size', $a.Pool)).text.Trim())
        $r.size = $size
        Check "within-partition" ($size -gt 0 -and $size -le [long]$a.Length) $true
        $root = MountPath $a.Pool
        $r.root = $root
        $bytes = [byte[]]::new(192MB); [Random]::new([int]$a.Seed).NextBytes($bytes)
        if ($root) { [IO.File]::WriteAllBytes((Join-Path $root "data.bin"), $bytes) }
        $r.hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', ''
        Check "written" ([bool]$root -and (Get-FileHash -LiteralPath (Join-Path $root "data.bin") -Algorithm SHA256).Hash -eq $r.hash) $true
        Finish
    }

    # Every pool imported (by zfswin if it is not), its file intact, a
    # scrub clean, and no device errors.
    "verify" {
        foreach ($p in $a.Pools) {
            if ((Pools) -notcontains $p.Name) {
                $i = Zfswin @('pool', 'import', $p.Name)
                Check "$($p.Name) import" $i.exit 0
                if ($i.exit -ne 0) { $r["$($p.Name) importError"] = $i.error }
            }
            Check "$($p.Name) imported" ((Pools) -contains $p.Name) $true
            $root = MountPath $p.Name
            $hash = if ($root) { (Get-FileHash -LiteralPath (Join-Path $root "data.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }
            Check "$($p.Name) data" $hash $p.Hash
            $s = Zpool @('scrub', '-w', $p.Name)
            Check "$($p.Name) scrub" $s.exit 0
            $x = Zpool @('status', '-x', $p.Name)
            $r["$($p.Name) status"] = (Zpool @('status', '-P', $p.Name)).text
            Check "$($p.Name) healthy" ($x.text -match 'is healthy') $true
        }
        Finish
    }

    "export" {
        foreach ($p in $a.Pools) {
            if ((Pools) -contains $p) { Check "$p export" (Zfswin @('pool', 'export', $p)).exit 0 }
        }
        Finish
    }

    # The service that keeps the pools: the broker (kernel) or the engine
    # (user mode), restarted; the pools are back without an import.
    "restart" {
        $service = if ($edition -eq 'usermode') { 'ZfsWinUserMode' } else { 'ZfsWinBroker' }
        Restart-Service $service -Force
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep -Seconds 3; $now = Pools } while (@($a.Pools | Where-Object { $now -notcontains $_ }).Count -gt 0 -and (Get-Date) -lt $deadline)
        foreach ($p in $a.Pools) { Check "$p after restart" ($now -contains $p) $true }
        Finish
    }

    # The NTFS partitions around the pools: their files, their file
    # systems and the partition table, as they were.
    "neighbors" {
        foreach ($letter in 'P', 'Q', 'R') {
            Check "$letter file" (Get-FileHash -LiteralPath "${letter}:\neighbor.bin" -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash $a.Hashes.$letter
            Check "$letter ntfs" (Repair-Volume -DriveLetter $letter -Scan -ErrorAction SilentlyContinue) 'NoErrorsFound'
        }
        Check "layout" (Layout $a.Disk) $a.Layout
        Finish
    }

    "msi" {
        $verb = if ($a.Action -eq 'uninstall') { '/x' } else { '/i' }
        $p = Start-Process msiexec.exe -ArgumentList $verb, $a.Msi, '/qn', '/norestart', '/l*v', "$work\$(Split-Path -Leaf $a.Msi).$($a.Action).log" -Wait -PassThru
        Check "msi $($a.Action)" ($p.ExitCode -in 0, 3010) $true
        $r.exit = $p.ExitCode
        Finish
    }
}

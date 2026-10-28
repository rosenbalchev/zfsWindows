<#
Runs inside the driver test VM (Test-DriverInVm.ps1 -Phase cache, plan
step 13).  One stage per call:

  arc             ARC hits on re-read, primarycache=metadata, zfs_arc_max
  l2arc           cache vdev (as scripts\vm\l2arc-reference.py on Linux): L2ARC
                  hits on random rereads, persistent rebuild, corrupted
                  cache blocks detected, removal
  coherency       Cache Manager / mapping / non-cached coherency, remount
  cut1-start      pools for the first power cut; start the writers
  cut1-check      after the power cut: durability per sync mode and SLOG
  cut2-start      SLOG pool again, one sync=always writer
  cut2-check      import with the log device missing (zpool import -m)

Pools are file-backed under C:\zfswin\images\cache with cachefile=none, so
nothing is imported automatically after the power cut.  Prints JSON.
#>
param([Parameter(Mandatory)][string]$Stage)
$ErrorActionPreference = "Continue"	# native tools write progress to stderr
$z = "C:\zfswin\driver"
$zpool = "$z\zpool.exe"; $zfs = "$z\zfs.exe"; $kstat = "$z\kstat.exe"
$verifier = "C:\zfswin\tools\Zfs.MountVerifier.exe"
$images = "C:\zfswin\images\cache"
$work = "C:\zfswin\cache"
$tunables = "HKLM:\SYSTEM\CurrentControlSet\Services\OpenZFS"
$results = [ordered]@{ stage = $Stage }
$failures = [System.Collections.Generic.List[string]]::new()

# zpool.exe can crash with 0xC0000005 at process exit after a successful
# import (CRT teardown racing libspl taskq threads; fixed in plan step 14).
# Only that case, with the pool really imported, is recorded separately.
$ExitCrash = -1073741819
function Run($name, [scriptblock]$block, $importedPool) {
    $text = (& $block 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $rc = $LASTEXITCODE
    $results[$name] = [ordered]@{ rc = $rc; out = $text.Trim() }
    if ($rc -eq $ExitCrash -and $importedPool -and
        @(& $zpool list -H -o name 2>$null) -contains $importedPool) {
        $results[$name].knownExitCrash = $true
    } elseif ($rc -ne 0) {
        $failures.Add("$name exited $rc")
    }
    $text
}

function Verify($name, [string[]]$arguments) {
    $json = (& $verifier @arguments) -join "`n"
    $rc = $LASTEXITCODE
    Set-Content -Path "$work\$name.json" -Value $json -Encoding utf8
    $parsed = $json | ConvertFrom-Json
    $results[$name] = [ordered]@{ rc = $rc; failures = @($parsed.failures); result = $parsed.result }
    if ($rc -ne 0) { $failures.Add("$name failed: $(@($parsed.failures)[0..4] -join '; ')") }
    $parsed.result
}

function MountOf($dataset) {
    $line = & $zfs mount | Where-Object { $_ -match "^\s*$([regex]::Escape($dataset))\s+(.+)$" } |
        Select-Object -First 1
    if (-not $line) { $failures.Add("$dataset is not mounted"); return $null }
    $path = ($line -replace "^\s*$([regex]::Escape($dataset))\s+", '').Trim()
    if ($path -match '^[A-Za-z]:/?$') { $path = $path.Substring(0, 2) + '\' }
    $path
}

function Export($pool) {
    for ($try = 1; $try -le 10; $try++) {
        & $zpool export $pool 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 15
    }
    $failures.Add("export $pool failed")
}

function ArcStats {
    $stats = @{}
    foreach ($line in & $kstat -p zfs:0:arcstats 2>$null) {
        # Numeric statistics only (kstat also prints class, crtime, ...).
        $parts = "$line" -split "\s+"
        $value = 0L
        if ($parts.Count -ge 2 -and [int64]::TryParse($parts[1], [ref]$value)) {
            $stats[$parts[0].Split(':')[-1]] = $value
        }
    }
    $stats
}

function Delta($before, $after, [string[]]$names) {
    $d = [ordered]@{}
    foreach ($n in $names) { $d[$n] = $after[$n] - $before[$n] }
    $d
}

# GUID of the first vdev listed under a "cache" or "logs" heading.
function GuidUnder($pool, $heading) {
    $lines = @(& $zpool status -g $pool 2>$null | ForEach-Object { "$_" })
    $at = [array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l -match "^\s+$heading\s*$" })
    if ($at -lt 0) { return $null }
    for ($i = $at + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s+(\d{6,})\s') { return $Matches[1] }
    }
    $null
}

# The driver creates each tunable's value with its type (REG_DWORD for INT,
# REG_QWORD for U64) and copies the data raw, so keep the existing type.
function SetTunable($scope, $name, [int64]$value) {
    $key = Get-Item "$tunables\$scope"
    $kind = $key.GetValueKind($name)
    if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) {
        Set-ItemProperty "$tunables\$scope" -Name $name -Type DWord -Value ([int32]$value)
    } else {
        Set-ItemProperty "$tunables\$scope" -Name $name -Type QWord -Value $value
    }
    $results["tunable $scope\$name"] = "$kind=$value"
    Start-Sleep -Seconds 5	# the driver re-reads its registry key on change
}

function NewPool($pool, [string[]]$vdevs, [string[]]$extra = @()) {
    # $vdevs: "name:sizeMiB" files, or the literal words cache/log.
    $spec = @()
    foreach ($v in $vdevs) {
        if ($v -in "cache", "log") { $spec += $v; continue }
        $file, $mib = $v -split ':'
        $path = "$images\$file"
        Remove-Item -Force $path -ErrorAction SilentlyContinue
        & fsutil file createnew $path ([int64]$mib * 1MB) | Out-Null
        $spec += "\\?\$path"
    }
    Run "create-$pool" { & $zpool create -f -o ashift=12 -o cachefile=none -O compression=off @extra $pool @spec } |
        Out-Null
}

New-Item -ItemType Directory -Force -Path $images, $work | Out-Null
$arcNames = "hits", "misses", "demand_data_hits", "demand_data_misses", "size", "c_max"
$l2Names = "l2_hits", "l2_misses", "l2_size", "l2_cksum_bad", "l2_io_error", "l2_writes_done"

switch ($Stage) {
"arc" {
    SetTunable "zfs_arc" "zfs_arc_max" 268435456
    $results.c_max = (ArcStats).c_max
    if ($results.c_max -ne 268435456) { $failures.Add("zfs_arc_max not applied: c_max $($results.c_max)") }
    NewPool "zw_arc" @("arc.raw:2048")
    Run "create-meta" { & $zfs create -o primarycache=metadata zw_arc/meta } | Out-Null
    Run "create-big" { & $zfs create zw_arc/big } | Out-Null
    $root = MountOf "zw_arc"
    Verify "fill-all" @("fill", (Join-Path $root "all"), "random", "128") | Out-Null
    Verify "fill-meta" @("fill", (MountOf "zw_arc/meta"), "random", "128") | Out-Null
    Verify "fill-big" @("fill", (MountOf "zw_arc/big"), "random", "768") | Out-Null
    Export "zw_arc"
    Run "import" { & $zpool import -d $images zw_arc } "zw_arc" | Out-Null
    $root = MountOf "zw_arc"
    foreach ($case in @(@{ name = "all"; file = (Join-Path $root "all\random-0.bin") },
                        @{ name = "meta"; file = (Join-Path (MountOf "zw_arc/meta") "random-0.bin") })) {
        $s0 = ArcStats
        Verify "read1-$($case.name)" @("read-unbuffered", $case.file) | Out-Null
        $s1 = ArcStats
        Verify "read2-$($case.name)" @("read-unbuffered", $case.file) | Out-Null
        $s2 = ArcStats
        $results["first-$($case.name)"] = Delta $s0 $s1 $arcNames
        $results["second-$($case.name)"] = Delta $s1 $s2 $arcNames
    }
    # 128 MiB at the default 128 KiB recordsize is 1024 data blocks.  Count
    # all ARC misses (blocks read from disk): sequential reads prefetch, so
    # demand reads "hit" prefetched buffers even when nothing stays cached.
    $all = $results["second-all"]; $meta = $results["second-meta"]
    if ($all.misses -gt 50) { $failures.Add("primarycache=all: re-read missed the ARC $($all.misses) times") }
    if ($meta.misses -lt 900) { $failures.Add("primarycache=metadata: re-read missed only $($meta.misses) times") }
    Verify "read-big" @("read-unbuffered", (Join-Path (MountOf "zw_arc/big") "random-0.bin")) | Out-Null
    $after = ArcStats
    $results.sizeAfterBig = $after.size
    if ($after.size -gt 1.1 * 268435456) { $failures.Add("ARC size $($after.size) exceeds zfs_arc_max") }
    Export "zw_arc"
    SetTunable "zfs_arc" "zfs_arc_max" 0
    Remove-Item -Force "$images\arc.raw"
}
"l2arc" {
    # Same scenario as scripts/vm/l2arc-reference.py on Linux OpenZFS.
    $baseline = @{}
    foreach ($t in @(@("zfs_arc", "zfs_arc_max"), @("zfs_l2arc", "l2arc_noprefetch"),
                     @("zfs_l2arc", "l2arc_write_max"), @("zfs_l2arc", "l2arc_write_boost"),
                     @("zfs_l2arc", "l2arc_rebuild_blocks_min_l2size"))) {
        $baseline["$($t[0])\$($t[1])"] = (Get-ItemProperty "$tunables\$($t[0])").($t[1])
    }
    SetTunable "zfs_arc" "zfs_arc_max" 201326592
    SetTunable "zfs_l2arc" "l2arc_noprefetch" 0
    SetTunable "zfs_l2arc" "l2arc_write_max" 67108864
    SetTunable "zfs_l2arc" "l2arc_write_boost" 67108864
    # The default (1 GiB) skips persistent L2ARC on this 512 MiB device.
    SetTunable "zfs_l2arc" "l2arc_rebuild_blocks_min_l2size" 0
    $results.c_max = (ArcStats).c_max
    $results.c_min = (ArcStats).c_min
    NewPool "zw_l2" @("l2data.raw:1536", "cache", "l2cache.raw:512")
    $root = MountOf "zw_l2"
    Verify "fill" @("fill", (Join-Path $root "data"), "random", "512") | Out-Null
    Export "zw_l2"
    Run "import" { & $zpool import -d $images zw_l2 } "zw_l2" | Out-Null
    $file = Join-Path (MountOf "zw_l2") "data\random-0.bin"
    # 4 passes of 4,096 random 128 KiB reads (fixed seed): a working set
    # larger than the ARC, so evicted blocks are served from the L2ARC.
    $want = (Verify "random-0" @("read-random", $file, "4096", "1307")).sha256
    $s0 = ArcStats
    for ($i = 1; $i -le 4; $i++) {
        Start-Sleep -Seconds 10	# let the L2ARC feed thread write
        $r = Verify "random-$i" @("read-random", $file, "4096", "1307")
        if ($r.sha256 -ne $want) { $failures.Add("random pass $i returned different bytes") }
    }
    $s1 = ArcStats
    $results.l2Feed = Delta $s0 $s1 $l2Names
    $results.l2SizeAfterFeed = $s1.l2_size
    if ($s1.l2_size -le 0) { $failures.Add("nothing was written to the L2ARC") }
    if ($results.l2Feed.l2_hits -le 0) { $failures.Add("no L2ARC hits") }
    # The driver opens the cache device exclusively, so corrupt it while the
    # pool is exported (past the front labels); persistent L2ARC is rebuilt
    # on import, and every L2ARC read is checksummed.
    Export "zw_l2"
    $corrupt = (& $verifier corrupt "$images\l2cache.raw" 33554432 268435456) -join "`n" | ConvertFrom-Json
    if (@($corrupt.failures).Count -ne 0) { $failures.Add("could not corrupt the cache device") }
    $rebuildNames = $l2Names + "l2_rebuild_success", "l2_rebuild_cksum_lb_errors", "l2_rebuild_io_errors",
        "l2_rebuild_bufs"
    $s2 = ArcStats
    Run "import-after-corrupt" { & $zpool import -d $images zw_l2 } "zw_l2" | Out-Null
    Start-Sleep -Seconds 5
    $s3 = ArcStats
    $results.rebuildOnImport = Delta $s2 $s3 $rebuildNames
    $file = Join-Path (MountOf "zw_l2") "data\random-0.bin"
    for ($i = 1; $i -le 2; $i++) {
        $r = Verify "random-after-corrupt-$i" @("read-random", $file, "4096", "1307")
        if ($r.sha256 -ne $want) { $failures.Add("read after L2ARC corruption returned different bytes") }
    }
    $s4 = ArcStats
    $results.afterCorrupt = Delta $s3 $s4 $rebuildNames
    if ($results.rebuildOnImport.l2_rebuild_bufs -le 0) { $failures.Add("persistent L2ARC was not rebuilt") }
    # Whether the rebuilt buffers fell in the corrupted range is chance
    # (Linux saw 6 of 1,385); record it.  Round 2 makes it deterministic.
    $results.round1Detected = ($results.afterCorrupt.l2_cksum_bad + $results.afterCorrupt.l2_io_error) -gt 0
    # Round 2: overwrite the whole data area past the device header (5 MiB)
    # up to the back labels; rebuilt log blocks and data must be rejected.
    Export "zw_l2"
    $corrupt = (& $verifier corrupt "$images\l2cache.raw" 5242880 530579456) -join "`n" | ConvertFrom-Json
    if (@($corrupt.failures).Count -ne 0) { $failures.Add("could not corrupt the cache device (round 2)") }
    $s5 = ArcStats
    Run "import-after-corrupt-2" { & $zpool import -d $images zw_l2 } "zw_l2" | Out-Null
    Start-Sleep -Seconds 5
    $file = Join-Path (MountOf "zw_l2") "data\random-0.bin"
    for ($i = 1; $i -le 2; $i++) {
        $r = Verify "random-after-corrupt2-$i" @("read-random", $file, "4096", "1307")
        if ($r.sha256 -ne $want) { $failures.Add("read after full L2ARC corruption returned different bytes") }
    }
    $s6 = ArcStats
    $results.round2 = Delta $s5 $s6 $rebuildNames
    $detected = $results.round2.l2_cksum_bad + $results.round2.l2_io_error +
        $results.round2.l2_rebuild_cksum_lb_errors + $results.round2.l2_rebuild_io_errors
    if ($results.round2.l2_hits -gt 0 -and $detected -le 0) {
        $failures.Add("L2ARC served hits from a fully corrupted device without errors")
    }
    $results.round2Detected = $detected -gt 0
    Run "status" { & $zpool status -v zw_l2 } | Out-Null
    $guid = GuidUnder "zw_l2" "cache"
    if (-not $guid) { $failures.Add("cache vdev GUID not found") }
    Run "remove-cache" { & $zpool remove zw_l2 $guid } | Out-Null
    Run "status-after-remove" { & $zpool status zw_l2 } | Out-Null
    if ($results["status-after-remove"].out -match '(?m)^\s+cache\s*$') { $failures.Add("cache vdev still present after remove") }
    Export "zw_l2"
    foreach ($key in $baseline.Keys) {
        $scope, $name = $key -split '\'
        SetTunable $scope $name $baseline[$key]
    }
    Remove-Item -Force "$images\l2cache.raw"
}
"coherency" {
    NewPool "zw_coh" @("coh.raw:512")
    $root = MountOf "zw_coh"
    $r = Verify "coherency" @("coherency", (Join-Path $root "coherency"))
    Export "zw_coh"
    Run "reimport" { & $zpool import -d $images zw_coh } "zw_coh" | Out-Null
    $root = MountOf "zw_coh"
    $sha = (Get-FileHash (Join-Path $root "coherency\coherent.bin") -Algorithm SHA256).Hash
    $results.afterRemount = $sha
    if ($sha -ne $r.finalSha256) { $failures.Add("coherent.bin differs after remount") }
    Export "zw_coh"
}
"cut1-start" {
    Remove-Item -Force "$work\ack-*.log" -ErrorAction SilentlyContinue
    NewPool "zw_main" @("main.raw:2048")
    foreach ($d in @(@("buffered", "standard"), @("flush", "standard"), @("writethrough", "standard"),
                     @("syncalways", "always"), @("syncdisabled", "disabled"))) {
        Run "create-$($d[0])" { & $zfs create -o sync=$($d[1]) "zw_main/$($d[0])" } | Out-Null
    }
    NewPool "zw_slog" @("slogdata.raw:768", "log", "slog.raw:256")
    Run "create-slog" { & $zfs create -o sync=always zw_slog/slog } | Out-Null
    $writers = @(@("zw_main/buffered", "buffered"), @("zw_main/flush", "flush"),
                 @("zw_main/writethrough", "writethrough"), @("zw_main/syncalways", "buffered"),
                 @("zw_main/syncdisabled", "flush"), @("zw_slog/slog", "buffered"),
                 @("ntfs", "flush"))
    # Control: the same flushed writer on the guest's NTFS volume, which
    # also holds the pool files and the ack logs.
    Remove-Item -Recurse -Force "$work\ntfs" -ErrorAction SilentlyContinue
    $results.writers = @()
    foreach ($w in $writers) {
        $name = $w[0].Split('/')[-1]
        $dir = if ($name -eq "ntfs") { "$work\ntfs\w" } else { Join-Path (MountOf $w[0]) "w" }
        # Win32_Process.Create: the writer outlives this remoting session.
        $cmd = "`"$verifier`" cache-writer `"$dir`" $($w[1]) `"$work\ack-$name.log`""
        $p = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        $results.writers += "$name pid=$($p.ProcessId) rc=$($p.ReturnValue)"
    }
}
"cut1-check" {
    Run "import-main" { & $zpool import -d $images zw_main } "zw_main" | Out-Null
    $checks = @(@("zw_main/buffered", "any"), @("zw_main/flush", "durable"),
                @("zw_main/writethrough", "durable"), @("zw_main/syncalways", "durable"),
                @("zw_main/syncdisabled", "any"))
    foreach ($c in $checks) {
        $name = $c[0].Split('/')[-1]
        Verify "check-$name" @("cache-check", (Join-Path (MountOf $c[0]) "w"), "$work\ack-$name.log", $c[1]) |
            Out-Null
    }
    Verify "check-ntfs" @("cache-check", "$work\ntfs\w", "$work\ack-ntfs.log", "durable") | Out-Null
    Run "import-slog" { & $zpool import -d $images zw_slog } "zw_slog" | Out-Null
    Verify "check-slog" @("cache-check", (Join-Path (MountOf "zw_slog/slog") "w"), "$work\ack-slog.log", "durable") |
        Out-Null
    foreach ($p in "zw_main", "zw_slog") {
        Run "scrub-$p" { & $zpool scrub -w $p } | Out-Null
        $status = Run "status-$p" { & $zpool status -v $p }
        if ($status -notmatch 'with 0 errors') { $failures.Add("$p scrub found errors") }
    }
    Export "zw_main"
    Export "zw_slog"
}
"cut2-start" {
    Remove-Item -Force "$work\ack-slog2.log" -ErrorAction SilentlyContinue
    Run "import-slog" { & $zpool import -d $images zw_slog } "zw_slog" | Out-Null
    Run "create-slog2" { & $zfs create -o sync=always zw_slog/slog2 } | Out-Null
    $dir = Join-Path (MountOf "zw_slog/slog2") "w"
    $cmd = "`"$verifier`" cache-writer `"$dir`" buffered `"$work\ack-slog2.log`""
    $p = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
    $results.writer = "slog2 pid=$($p.ProcessId) rc=$($p.ReturnValue)"
}
"cut2-check" {
    # The log device is gone: only the data vdev is offered to import.
    New-Item -ItemType Directory -Force "$images\nolog" | Out-Null
    Move-Item -Force "$images\slogdata.raw" "$images\nolog\slogdata.raw"
    Move-Item -Force "$images\slog.raw" "$work\slog-removed.raw"
    # Without -m, import must refuse a pool whose log device is missing.
    $out = (& $zpool import -d "$images\nolog" zw_slog 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $results.importWithoutLog = [ordered]@{ rc = $LASTEXITCODE; out = $out.Trim() }
    if ($LASTEXITCODE -eq 0) {
        $failures.Add("import succeeded although the log device is missing")
    } else {
        Run "import-m" { & $zpool import -m -d "$images\nolog" zw_slog } "zw_slog" | Out-Null
    }
    Verify "check-slog2" @("cache-check", (Join-Path (MountOf "zw_slog/slog2") "w"), "$work\ack-slog2.log", "any") |
        Out-Null
    Run "status-missing-log" { & $zpool status zw_slog } | Out-Null
    $guid = GuidUnder "zw_slog" "logs"
    if (-not $guid) { $failures.Add("missing log vdev GUID not found") }
    Run "remove-missing-log" { & $zpool remove zw_slog $guid } | Out-Null
    Run "scrub" { & $zpool scrub -w zw_slog } | Out-Null
    $status = Run "status-final" { & $zpool status -v zw_slog }
    if ($status -notmatch 'with 0 errors') { $failures.Add("zw_slog scrub found errors") }
    Export "zw_slog"
}
default { $failures.Add("unknown stage $Stage") }
}

$results.bugchecks = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1001 } `
    -ErrorAction SilentlyContinue).Count
$results.failures = @($failures)
$results | ConvertTo-Json -Depth 8

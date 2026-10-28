<#
Runs inside the driver test VM (Test-DriverInVm.ps1 -Phase extended).
Exercises, through the OpenZFS kernel driver, everything the basic read and
write phases do not:

  zc_lz4 (Linux-created fixture, fresh copy):
    - extended-write: ADS, DACL edits, a 20,000-entry directory, 2,000
      subdirectories, concurrent writers, memory-mapped writes;
    - zfs create of dedup=on, compression=zstd and compression=gzip-6
      children, filled with duplicate or compressible data;
    - snapshot @win1, mutate, read .zfs\snapshot\win1, snapshot @win2,
      mutate, rollback to @win2;
    - zpool scrub, then export/import and re-verify everything.
  zw_mirror (created here by the driver from two file vdevs):
    - fill, export, overwrite 160 MiB of member b, import, scrub must repair.

Every step's output is kept; the images are left in C:\zfswin\images\ext for
the host to copy back for Linux validation.  Prints the results as JSON.
#>
$ErrorActionPreference = "Continue"	# native tools write progress to stderr
$z = "C:\zfswin\driver"
$zpool = "$z\zpool.exe"; $zfs = "$z\zfs.exe"
$verifier = "C:\zfswin\tools\Zfs.MountVerifier.exe"
$work = "C:\zfswin\ext"
$images = "C:\zfswin\images\ext"
$results = [ordered]@{}
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

# Verifier verbs print {result, failures}; keep the JSON as a file.
function Verify($name, [string[]]$arguments) {
    $json = (& $verifier @arguments) -join "`n"
    $rc = $LASTEXITCODE
    Set-Content -Path "$work\$name.json" -Value $json -Encoding utf8
    $parsed = $json | ConvertFrom-Json
    $r = $parsed.result
    $summary = if ($r.files -is [System.Management.Automation.PSCustomObject]) {
        [ordered]@{ files = @($r.files.PSObject.Properties).Count; dirs = @($r.dirs).Count
                    streams = @($r.streams.PSObject.Properties).Count; acls = @($r.acls).Count }
    } else { $r }
    $results[$name] = [ordered]@{ rc = $rc; failures = @($parsed.failures); summary = $summary }
    if ($rc -ne 0) { $failures.Add("$name failed: $(@($parsed.failures)[0..4] -join '; ')") }
}

function MountOf($dataset) {
    $line = & $zfs mount | Where-Object { $_ -match "^\s*$([regex]::Escape($dataset))\s+(.+)$" } |
        Select-Object -First 1
    if (-not $line) { $failures.Add("$dataset is not mounted"); return $null }
    $path = ($line -replace "^\s*$([regex]::Escape($dataset))\s+", '').Trim()
    if ($path -match '^[A-Za-z]:$') { $path += '\' }
    $path
}

function Export($pool) {
    $attempts = @()
    for ($try = 1; $try -le 10; $try++) {
        $msg = (& $zpool export $pool 2>&1 | ForEach-Object { "$_" }) -join ' '
        $attempts += "attempt ${try}: rc=$LASTEXITCODE $msg"
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 15
    }
    $results["export-$pool-$($results.Count)"] = $attempts
    if ($LASTEXITCODE -ne 0) { $failures.Add("export $pool failed") }
}

# Pools left imported by an interrupted run hold the image files open.
foreach ($leftover in @(& $zpool list -H -o name 2>$null)) {
    if ($leftover -in "zc_lz4", "zw_mirror") { Export $leftover }
}
Remove-Item -Recurse -Force $work, $images -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work, "$images\lz4", "$images\mirror" | Out-Null
Copy-Item C:\zfswin\images\lz4-pristine\lz4.raw "$images\lz4\lz4.raw"

# --- zc_lz4 -----------------------------------------------------------------
Run "import" { & $zpool import -d "$images\lz4" zc_lz4 } "zc_lz4" | Out-Null
$data = MountOf "zc_lz4/data"
$ext = Join-Path $data "windows-extended"
Verify "extended-write" @("extended-write", $ext)

foreach ($child in @(
        @{ name = "dedup"; props = @("-o", "dedup=on", "-o", "compression=off"); kind = "dup"; mib = 16 },
        @{ name = "zstd"; props = @("-o", "compression=zstd"); kind = "compressible"; mib = 32 },
        @{ name = "gzip"; props = @("-o", "compression=gzip-6"); kind = "compressible"; mib = 32 })) {
    Run "create-$($child.name)" { & $zfs create @($child.props) "zc_lz4/data/$($child.name)" } | Out-Null
    $mount = MountOf "zc_lz4/data/$($child.name)"
    if ($mount) { Verify "fill-$($child.name)" @("fill", $mount, $child.kind, "$($child.mib)") }
}
Run "sync-1" { & $zpool sync zc_lz4 } | Out-Null

# Snapshots: @win1 must keep the pre-mutation state, visible under .zfs.
Run "snapshot-win1" { & $zfs snapshot zc_lz4/data@win1 } | Out-Null
Verify "snapshot-mutate" @("snapshot-mutate", $ext, "$work\extended-write.json")
Verify "check-snapshot-win1" @("check", (Join-Path $data ".zfs\snapshot\win1\windows-extended"),
    "$work\extended-write.json")
Run "snapshot-win2" { & $zfs snapshot zc_lz4/data@win2 } | Out-Null
Set-Content -Path (Join-Path $ext "discarded-by-rollback.txt") -Value "gone after rollback"
Remove-Item (Join-Path $ext "acl.txt")
Run "rollback-win2" { & $zfs rollback zc_lz4/data@win2 } | Out-Null
$data = MountOf "zc_lz4/data"
$ext = Join-Path $data "windows-extended"
Verify "check-after-rollback" @("check", $ext, "$work\snapshot-mutate.json")
Run "list-snapshots" { & $zfs list -t snapshot -o name,used,refer -r zc_lz4 } | Out-Null

Run "scrub" { & $zpool scrub -w zc_lz4 } | Out-Null
Run "status" { & $zpool status -v zc_lz4 } | Out-Null
Run "properties" { & $zfs get -H -r -o name,property,value compression,compressratio,dedup zc_lz4/data } | Out-Null
Run "dedupratio" { & $zpool get -H -o value dedupratio zc_lz4 } | Out-Null
Export "zc_lz4"

# Persistence: everything must survive export and re-import.
Run "reimport" { & $zpool import -d "$images\lz4" zc_lz4 } "zc_lz4" | Out-Null
$data = MountOf "zc_lz4/data"
Verify "check-after-reimport" @("check", (Join-Path $data "windows-extended"), "$work\snapshot-mutate.json")
foreach ($child in "dedup", "zstd", "gzip") {
    $mount = MountOf "zc_lz4/data/$child"
    if ($mount) { Verify "check-$child" @("check", $mount, "$work\fill-$child.json") }
}
Export "zc_lz4"

# --- zw_mirror: created by the driver, repaired by scrub ---------------------
foreach ($member in "a", "b") {
    & fsutil file createnew "$images\mirror\$member.raw" 268435456 | Out-Null
}
Run "mirror-create" { & $zpool create -o ashift=12 -O compression=lz4 zw_mirror mirror `
        "\\?\$images\mirror\a.raw" "\\?\$images\mirror\b.raw" } | Out-Null
$mirror = MountOf "zw_mirror"
if ($mirror) { Verify "mirror-fill" @("fill", (Join-Path $mirror "data"), "random", "120") }
Export "zw_mirror"
# Past the front labels and boot area, short of the back labels.
Verify "mirror-corrupt" @("corrupt", "$images\mirror\b.raw", "16777216", "167772160")
Run "mirror-import" { & $zpool import -d "$images\mirror" zw_mirror } "zw_mirror" | Out-Null
Run "mirror-scrub" { & $zpool scrub -w zw_mirror } | Out-Null
$status = Run "mirror-status" { & $zpool status -v zw_mirror }
if ($status -notmatch 'scrub repaired [1-9]') { $failures.Add("mirror scrub repaired nothing") }
$mirror = MountOf "zw_mirror"
if ($mirror) { Verify "mirror-check" @("check", (Join-Path $mirror "data"), "$work\mirror-fill.json") }
Run "mirror-clear" { & $zpool clear zw_mirror } | Out-Null
Run "mirror-scrub-2" { & $zpool scrub -w zw_mirror } | Out-Null
$status = Run "mirror-status-2" { & $zpool status -v zw_mirror }
if ($status -notmatch 'scrub repaired 0B.*with 0 errors') { $failures.Add("second mirror scrub not clean") }
Export "zw_mirror"

$results.bugchecks = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1001 } `
    -ErrorAction SilentlyContinue).Count
$results.failures = @($failures)
$results | ConvertTo-Json -Depth 8

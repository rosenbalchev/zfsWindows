# Repeats zpool import/status/export of a disposable copy of the
# single-disk fixture and counts the tools' exit codes (plan step 14: the
# tools must return reliable exit codes).  Prints one JSON object.
# -CpuLoad starts that many busy-loop processes for the duration, which
# widens the exit-time race windows (the original crashes happened under
# load in the extended and cache phases).
param([int]$Iterations = 100, [string]$Driver = "C:\zfswin\driver", [int]$CpuLoad = 0)

$ErrorActionPreference = "Continue"
$zpool = Join-Path $Driver "zpool.exe"
$zfs = Join-Path $Driver "zfs.exe"
$dir = "C:\zfswin\images\exitcodes"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item -Force C:\zfswin\images\single\disk01.raw "$dir\disk01.raw"
$dumpsBefore = @(Get-ChildItem C:\zfswin\dumps -Filter *.dmp -ErrorAction SilentlyContinue).Count

$counts = [ordered]@{}
function Count($command, $code) {
    $key = "$command=$code"
    $counts[$key] = 1 + [int]$counts[$key]
}
foreach ($pool in @(& $zpool list -H -o name 2>$null)) { & $zpool export $pool 2>&1 | Out-Null }

$burners = @(for ($b = 0; $b -lt $CpuLoad; $b++) {
    Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command', 'while ($true) { }'
})
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Iterations; $i++) {
    & $zpool import -d $dir zfswin_single 2>&1 | Out-Null;  Count "import" $LASTEXITCODE
    & $zpool status zfswin_single 2>&1 | Out-Null;             Count "status" $LASTEXITCODE
    & $zfs list -H -r zfswin_single 2>&1 | Out-Null;           Count "zfs-list" $LASTEXITCODE
    & $zpool export zfswin_single 2>&1 | Out-Null;             Count "export" $LASTEXITCODE
    # An import whose exit code was lost still imported the pool.
    if (@(& $zpool list -H -o name 2>$null) -contains "zfswin_single") { & $zpool export zfswin_single 2>&1 | Out-Null }
}
$burners | Stop-Process -Force -ErrorAction SilentlyContinue
[ordered]@{
    iterations = $Iterations
    cpuLoad = $CpuLoad
    seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds)
    zpoolSha256 = (Get-FileHash $zpool).Hash
    exitCodes = $counts
    newDumps = @(Get-ChildItem C:\zfswin\dumps -Filter *.dmp -ErrorAction SilentlyContinue).Count - $dumpsBefore
} | ConvertTo-Json -Compress

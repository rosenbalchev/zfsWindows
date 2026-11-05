# Runs inside the test VM: the same file-system workload against a mounted
# dataset, for comparing the kernel driver with the user-mode (WinFsp)
# variant (plan step 15).  Prints one JSON object of timings.
#
#   guest-bench.ps1 -Root Z:\ [-SequentialMiB 256] [-SmallFiles 2000]
#
# The data is random, so lz4 cannot shrink it, and every sequential write
# ends with FlushFileBuffers, so it is measured to stable storage.
param(
    [Parameter(Mandatory)][string]$Root,
    [int]$SequentialMiB = 256,
    [int]$SmallFiles = 2000
)

$ErrorActionPreference = "Stop"
$dir = Join-Path $Root "bench"
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Path $dir | Out-Null
$result = [ordered]@{ root = $Root; sequentialMiB = $SequentialMiB; smallFiles = $SmallFiles }

function Time([scriptblock]$block) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $block | Out-Null
    [math]::Round($watch.Elapsed.TotalSeconds, 3)
}

# Sequential write, 1 MiB at a time, then flushed.
$chunk = New-Object byte[] (1MB)
(New-Object Random 15).NextBytes($chunk)
$big = Join-Path $dir "sequential.bin"
$seconds = Time {
    $fs = [IO.FileStream]::new($big, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 1MB)
    try {
        for ($i = 0; $i -lt $SequentialMiB; $i++) { $chunk[0] = [byte]$i; $fs.Write($chunk, 0, $chunk.Length) }
        $fs.Flush($true)
    } finally { $fs.Dispose() }
}
$result.seqWriteSeconds = $seconds
$result.seqWriteMBps = [math]::Round($SequentialMiB / [math]::Max($seconds, 0.001), 1)

# Sequential read through the cache (first read after the write).
$seconds = Time {
    $fs = [IO.FileStream]::new($big, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
    try { while ($fs.Read($chunk, 0, $chunk.Length) -gt 0) { } } finally { $fs.Dispose() }
}
$result.seqReadSeconds = $seconds
$result.seqReadMBps = [math]::Round($SequentialMiB / [math]::Max($seconds, 0.001), 1)

# Non-cached sequential read (FILE_FLAG_NO_BUFFERING): every request reaches
# the file system, so this shows the per-request path, not the Cache Manager.
$unbuffered = & C:\zfswin\tools\Zfs.MountVerifier.exe read-unbuffered $big | ConvertFrom-Json
$result.seqReadUnbufferedSeconds = $unbuffered.result.seconds
$result.seqReadUnbufferedMBps = [math]::Round($SequentialMiB / [math]::Max($unbuffered.result.seconds, 0.001), 1)

# Small files: create (4 KiB each), enumerate, read, rename, delete.
$small = Join-Path $dir "small"
New-Item -ItemType Directory -Path $small | Out-Null
$payload = New-Object byte[] 4096
(New-Object Random 16).NextBytes($payload)
$result.smallCreateSeconds = Time {
    for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::WriteAllBytes((Join-Path $small "f$i.bin"), $payload) }
}
$result.smallEnumerateSeconds = Time { [IO.Directory]::GetFiles($small).Count }
$result.smallReadSeconds = Time {
    for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::ReadAllBytes((Join-Path $small "f$i.bin")) }
}
$result.smallRenameSeconds = Time {
    for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::Move((Join-Path $small "f$i.bin"), (Join-Path $small "r$i.bin")) }
}
$result.smallDeleteSeconds = Time {
    for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::Delete((Join-Path $small "r$i.bin")) }
}
$result.smallCreatePerSecond = [math]::Round($SmallFiles / [math]::Max($result.smallCreateSeconds, 0.001), 0)

# Tidy up so the pool can be validated without the bench data.
Remove-Item -Recurse -Force $dir
$result.remaining = @(Get-ChildItem -LiteralPath $Root -Filter bench -ErrorAction SilentlyContinue).Count
$result | ConvertTo-Json -Compress

# Runs inside the test VM: the same file-system workload against a mounted
# dataset, for comparing the kernel driver with the user-mode (WinFsp)
# variant (plan step 15).  Prints one JSON object of timings.
#
#   guest-bench.ps1 -Root Z:\ [-SequentialMiB 256] [-SmallFiles 2000]
#
# Compare random and compressible data, with configurable flush cadence.
param(
    [Parameter(Mandatory)][string]$Root,
    [int]$SequentialMiB = 256,
    [int]$SmallFiles = 2000,
    [ValidateSet(4, 16, 64, 256, 1024)][int]$ChunkKiB = 1024,
    [ValidateSet("Random", "Compressible")][string]$DataPattern = "Random",
    [ValidateRange(0, 256)][int]$FlushEveryMiB = 0,
    [ValidateRange(0, 86400)][int]$FlushEverySeconds = 0,
    [ValidateSet(0, 8, 16, 32, 64, 256, 1024)][int]$CoalesceToKiB = 0,
    [int]$MeasureProcessId = 0,
    [switch]$WriteThrough,
    [switch]$SkipUnbufferedRead,
    [switch]$KeepBenchData
)

$ErrorActionPreference = "Stop"
if ($SequentialMiB -le 0) { throw "-SequentialMiB must be positive." }
if ($SmallFiles -lt 0) { throw "-SmallFiles must not be negative." }
if ($CoalesceToKiB -gt 0 -and $WriteThrough) {
    throw "-CoalesceToKiB cannot be used with -WriteThrough because it delays write-through requests."
}
if ($CoalesceToKiB -gt 0 -and ($CoalesceToKiB -lt $ChunkKiB -or $CoalesceToKiB % $ChunkKiB -ne 0)) {
    throw "-CoalesceToKiB must be at least ChunkKiB and a multiple of ChunkKiB."
}
$writeCount = [int](($SequentialMiB * 1024) / $ChunkKiB)
$streamWriteEstimate = if ($CoalesceToKiB -gt 0) {
    [int][math]::Ceiling(($SequentialMiB * 1024) / $CoalesceToKiB)
} else {
    $writeCount
}
$dir = Join-Path $Root "bench"
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Path $dir | Out-Null
$result = [ordered]@{
    root = $Root
    sequentialMiB = $SequentialMiB
    chunkKiB = $ChunkKiB
    writeCalls = $writeCount
    estimatedFileStreamWriteCalls = $streamWriteEstimate
    dataPattern = $DataPattern
    smallFiles = $SmallFiles
    flushEveryMiB = $FlushEveryMiB
    flushEverySeconds = $FlushEverySeconds
    coalesceToKiB = $CoalesceToKiB
    writeThrough = [bool]$WriteThrough
}

function Time([scriptblock]$block) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $block | Out-Null
    [math]::Round($watch.Elapsed.TotalSeconds, 3)
}

# Sequential write at the selected request size and durability cadence.
$chunk = New-Object byte[] ($ChunkKiB * 1KB)
if ($DataPattern -eq "Random") {
    (New-Object Random 15).NextBytes($chunk)
} else {
    $pattern = [Text.Encoding]::ASCII.GetBytes("The quick brown fox jumps over the lazy dog 0123456789`n")
    for ($i = 0; $i -lt $chunk.Length; $i++) { $chunk[$i] = $pattern[$i % $pattern.Length] }
}
$big = Join-Path $dir "sequential.bin"
$options = if ($WriteThrough) { [IO.FileOptions]::WriteThrough } else { [IO.FileOptions]::None }
$result.durableFlushCalls = 0
$cpuBefore = if ($MeasureProcessId -gt 0) { (Get-Process -Id $MeasureProcessId).TotalProcessorTime.TotalSeconds } else { 0 }
$seconds = Time {
    $fs = [IO.FileStream]::new($big, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, $chunk.Length, $options)
    $writer = if ($CoalesceToKiB -gt 0) {
        [IO.BufferedStream]::new($fs, $CoalesceToKiB * 1KB)
    } else {
        $fs
    }
    $durableFlushTimer = [Diagnostics.Stopwatch]::StartNew()
    $bytesSinceDurableFlush = 0L
    try {
        for ($i = 0; $i -lt $writeCount; $i++) {
            $chunk[0] = [byte]($i % 256)
            $writer.Write($chunk, 0, $chunk.Length)
            $bytesSinceDurableFlush += $chunk.Length
            $flushByBytes = $FlushEveryMiB -gt 0 -and $bytesSinceDurableFlush -ge ($FlushEveryMiB * 1MB)
            $flushByTime = $FlushEverySeconds -gt 0 -and $durableFlushTimer.Elapsed.TotalSeconds -ge $FlushEverySeconds
            if ($flushByBytes -or $flushByTime) {
                $writer.Flush()
                $fs.Flush($true)
                $result.durableFlushCalls++
                $bytesSinceDurableFlush = 0
                $durableFlushTimer.Restart()
            }
        }
        $writer.Flush()
        $fs.Flush($true)
        $result.durableFlushCalls++
    } finally {
        if ([object]::ReferenceEquals($writer, $fs)) { $fs.Dispose() } else { $writer.Dispose() }
    }
}
$cpuAfter = if ($MeasureProcessId -gt 0) { (Get-Process -Id $MeasureProcessId).TotalProcessorTime.TotalSeconds } else { 0 }
$result.seqWriteSeconds = $seconds
$result.seqWriteMBps = [math]::Round($SequentialMiB / [math]::Max($seconds, 0.001), 1)
if ($MeasureProcessId -gt 0) {
    $result.seqWriteCpuSeconds = [math]::Round($cpuAfter - $cpuBefore, 3)
    $result.seqWriteCpuPercentOfOneCore = [math]::Round(100 * ($cpuAfter - $cpuBefore) / [math]::Max($seconds, 0.001), 1)
}

# Sequential read through the cache (first read after the write).
$seconds = Time {
    $fs = [IO.FileStream]::new($big, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
    try { while ($fs.Read($chunk, 0, $chunk.Length) -gt 0) { } } finally { $fs.Dispose() }
}
$result.seqReadSeconds = $seconds
$result.seqReadMBps = [math]::Round($SequentialMiB / [math]::Max($seconds, 0.001), 1)

# Non-cached sequential read (FILE_FLAG_NO_BUFFERING): every request reaches
# the file system, so this shows the per-request path, not the Cache Manager.
if (-not $SkipUnbufferedRead) {
    $unbuffered = & C:\zfswin\tools\Zfs.MountVerifier.exe read-unbuffered $big | ConvertFrom-Json
    $result.seqReadUnbufferedSeconds = $unbuffered.result.seconds
    $result.seqReadUnbufferedMBps = [math]::Round($SequentialMiB / [math]::Max($unbuffered.result.seconds, 0.001), 1)
}

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
if (-not $KeepBenchData) { Remove-Item -Recurse -Force $dir }
$result.remaining = if (Test-Path -LiteralPath $dir) { 1 } else { 0 }
$result | ConvertTo-Json -Compress

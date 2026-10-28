<#
Runs inside the driver test VM.  Measures the cost of opening files through
the driver as a function of directory size, on the zc_lz4 pool left in
C:\zfswin\images\ext by the extended phase (windows-extended\large holds
~15,000 entries; subdirs\dNNNN are empty).  Prints JSON.
#>
param([int]$Opens = 300)
$ErrorActionPreference = "Continue"
$zpool = "C:\zfswin\driver\zpool.exe"; $zfs = "C:\zfswin\driver\zfs.exe"
if (@(& $zpool list -H -o name 2>$null) -notcontains "zc_lz4") {
    & $zpool import -d C:\zfswin\images\ext\lz4 zc_lz4 2>&1 | Out-Null
}
$mount = ((& $zfs mount | Select-String '^\s*zc_lz4/data\s+(.+)$').Matches[0].Groups[1].Value.Trim())
$root = Join-Path $mount "windows-extended"

# A small directory with its own files, next to the big one.
$small = Join-Path $root "bench-small"
New-Item -ItemType Directory -Force $small | Out-Null
for ($i = 0; $i -lt 50; $i++) { [IO.File]::WriteAllText((Join-Path $small "s$i.txt"), "small $i") }

function Time($label, [string[]]$files) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $Opens; $i++) { [IO.File]::ReadAllBytes($files[$i % $files.Count]) | Out-Null }
    [ordered]@{ case = $label; opens = $Opens; msPerOpen = [math]::Round($sw.Elapsed.TotalMilliseconds / $Opens, 2) }
}
$large = [IO.Directory]::GetFiles((Join-Path $root "large"))
$results = @(
    (Time "large dir ($($large.Count) entries), distinct files" $large),
    (Time "large dir, one file repeatedly" @($large[0])),
    (Time "small dir (50 entries), distinct files" ([IO.Directory]::GetFiles($small))),
    (Time "small dir, one file repeatedly" @([IO.Directory]::GetFiles($small)[0]))
)
@{ mount = $mount; results = $results } | ConvertTo-Json -Depth 4

# Runs on the ARM host (through Invoke-ArmHost.ps1): the admin phase of
# Test-UserModeInVm.ps1 in the ARM64 VM, using scripts\vm\guest-installer.ps1
# stages, then exports the pool and stages its images on the host for the
# Linux check.  A kernel-written x64 pool in C:\zfswin-arm\stage\cross
# (a.img, d.img, fs-expected.json), if present, is cross-checked.
function Guest($stage, [hashtable]$arguments = @{}) {
    $arguments.Edition = "usermode"
    $s = Open-ArmGuest
    try {
        $json = Invoke-Command $s {
            param($stage, $arguments)
            $ErrorActionPreference = "Continue"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($arguments | ConvertTo-Json -Compress)))
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\installer\guest-installer.ps1 -Stage $stage -ArgumentsBase64 $encoded 2>$null
        } -ArgumentList $stage, $arguments
    } finally { Remove-PSSession $s }
    $text = ($json | Where-Object { "$_".StartsWith('{') } | Select-Object -Last 1)
    if (-not $text) { return [pscustomobject]@{ failed = @("no JSON: $(($json | Out-String).Trim())") } }
    $text | ConvertFrom-Json
}
function Summary($name, $r, $seconds) {
    $checks = @($r.PSObject.Properties | Where-Object { $_.Value -and $_.Value.PSObject.Properties['pass'] })
    [ordered]@{ stage = $name; seconds = $seconds; checks = $checks.Count; passed = @($checks | Where-Object { $_.Value.pass }).Count; failed = @($r.failed) }
}
$results = @()
$s = Open-ArmGuest
Invoke-Command $s {
    Remove-Item -Recurse -Force "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Mounts" -ErrorAction SilentlyContinue
    & sc.exe start ZfsWinUserMode | Out-Null
}
Copy-Item -ToSession $s C:\zfswin-arm\stage\guest-installer.ps1 C:\zfswin\installer\guest-installer.ps1 -Force
Remove-PSSession $s

$all = [ordered]@{}
foreach ($stage in "cli", "gui", "powershell", "standard-user", "filesystem", "datafeatures") {
    $t = [Diagnostics.Stopwatch]::StartNew(); $r = Guest $stage; $sec = [math]::Round($t.Elapsed.TotalSeconds, 1)
    $all[$stage] = $r
    $results += Summary $stage $r $sec
}

# Persistence across a service restart.
$s = Open-ArmGuest
$persist = Invoke-Command $s {
    $ErrorActionPreference = "Continue"
    $z = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin\zfswin.exe"
    $restartError = try { Restart-Service ZfsWinUserMode -Force -ErrorAction Stop; $null } catch { "$_" }
    $deadline = (Get-Date).AddMinutes(3)
    do { Start-Sleep -Seconds 2; $mounts = (& $z --json mount list 2>$null | ConvertFrom-Json).result.mounts } while (-not ($mounts | Where-Object dataset -eq 'zwin/fs') -and (Get-Date) -lt $deadline)
    $fsPath = ($mounts | Where-Object dataset -eq 'zwin/fs').mountpoint
    if ($fsPath -match '^[A-Za-z]:$') { $fsPath += '\' }
    [ordered]@{
        restartError = $restartError
        pools = @((& $z --json pool list 2>$null | ConvertFrom-Json).result.pools | ForEach-Object name)
        mounts = @($mounts | ForEach-Object { "$($_.dataset)=$($_.mountpoint)" })
        fileAfterRestart = $(if ($fsPath) { Get-Content -LiteralPath (Join-Path $fsPath "streams.txt") -Stream meta -ErrorAction SilentlyContinue })
    }
}
Remove-PSSession $s
$all["restart"] = $persist
$results += [ordered]@{ stage = "restart"; ok = (-not $persist.restartError -and @($persist.pools) -contains 'zwin' -and $persist.fileAfterRestart -eq 'metadata stream'); detail = ($persist | ConvertTo-Json -Compress) }

# The x64 kernel-written pool, read on ARM64 in user mode.
if (Test-Path C:\zfswin-arm\stage\cross\fs-expected.json) {
    $s = Open-ArmGuest
    Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\cross | Out-Null; Remove-Item C:\zfswin\cross\* -Force -ErrorAction SilentlyContinue }
    foreach ($f in "a.img", "d.img", "fs-expected.json") { Copy-Item -ToSession $s "C:\zfswin-arm\stage\cross\$f" "C:\zfswin\cross\$f" -Force }
    Remove-PSSession $s
    $t = [Diagnostics.Stopwatch]::StartNew(); $cc = Guest "crosscheck" @{ Dir = "C:\zfswin\cross" }
    $all["crosscheck"] = $cc
    $results += Summary "crosscheck" $cc ([math]::Round($t.Elapsed.TotalSeconds, 1))
}

$t = [Diagnostics.Stopwatch]::StartNew(); $exp = Guest "export"
$results += [ordered]@{ stage = "export"; ok = ($exp.exit -eq 0); seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); detail = ($exp | ConvertTo-Json -Compress -Depth 4) }

# Images for Linux, staged on the host.
New-Item -ItemType Directory -Force C:\zfswin-arm\stage\out | Out-Null
Remove-Item C:\zfswin-arm\stage\out\* -Force -ErrorAction SilentlyContinue
$s = Open-ArmGuest
foreach ($f in "images\a.img", "images\d.img", "fs-expected.json", "manifest.json", "datafeatures.json") { Copy-Item -FromSession $s "C:\zfswin\installer\$f" "C:\zfswin-arm\stage\out\$(Split-Path -Leaf $f)" -Force -ErrorAction Continue }
$dumps = Invoke-Command $s { @(Get-ChildItem C:\zfswin\dumps -ErrorAction SilentlyContinue | ForEach-Object Name) }
Remove-PSSession $s
$results += [ordered]@{ stage = "dumps"; ok = (@($dumps).Count -eq 0); detail = ($dumps -join ', ') }

$all | ConvertTo-Json -Depth 6 -Compress | Set-Content C:\zfswin-arm\stage\usermode-admin-full.json -Encoding UTF8
$results | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 }

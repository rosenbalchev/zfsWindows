<#
.SYNOPSIS
Runs the ARM64 kernel or user-mode suite (host-<edition>-suite.ps1) on the
ARM PC for one build, with the tree pools of the other platforms.

.DESCRIPTION
Uploads the ARM64 package of -Edition/-Version/-Configuration, the current
guest-installer.ps1, the test certificate and every TestVolumes\vm\tree-*
pool not written on ARM64 to C:\zfswin-arm\stage (trees under
stage\trees), runs the suite from -Checkpoint, and brings back:
  TestVolumes\vm\arm64-<edition>-<config>.json   every stage's raw result
  TestVolumes\vm\arm64-<edition>-<config>.jsonl  one line per check
  TestVolumes\vm\tree-arm64-<edition>-<config>\  this run's tree pool
The suite keeps running on the ARM PC if the SSH connection drops; the
results are read from files, not from the session.  The ARM64 .NET Desktop
Runtime and WinFsp installers are expected in the stage folder already.

  Test-ArmSuite.ps1 -Edition kernel -Configuration Release
  Test-ArmSuite.ps1 -Edition usermode -Configuration Debug
  Test-ArmSuite.ps1 -Edition usermode -Only memory   (setup and that stage)
#>
[CmdletBinding()]
param(
    [ValidateSet("kernel", "usermode")]
    [string]$Edition = "kernel",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$Checkpoint = "testsigning-ready-updated",
    # User mode: only these admin stages (comma-separated), no tree pools.
    [string]$Only = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not $Version) { $Version = & (Join-Path $repo "scripts\Get-ZfsWinVersion.ps1") }
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$label = "arm64-$Edition-$($Configuration.ToLower())"
$variant = if ($Edition -eq "kernel") { "testsigned" } else { "usermode" }
$msi = Join-Path $repo "out\release\$Version\OpenZFS-for-Windows-$Version-arm64-$variant$tag.msi"
if (-not (Test-Path $msi)) { throw "Missing $msi" }
$suite = Join-Path $PSScriptRoot "host-$Edition-suite.ps1"
$full = "$Edition-full.json"; $lines = "$Edition-results.jsonl"
$state = Get-Content (Join-Path $repo "TestVolumes\vm\ZFSTestOnArm.json") -Raw | ConvertFrom-Json
$key = $state.hostKey -replace '^~', $env:USERPROFILE
$known = $state.hostKnownHosts -replace '^~', $env:USERPROFILE
$target = "$($state.hostUser)@$($state.hostAddress)"
$sshOptions = @("-i", $key, "-o", "UserKnownHostsFile=$known", "-o", "HostKeyAlias=$($state.hostKeyAlias)",
    "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20")
$invoke = Join-Path $PSScriptRoot "Invoke-ArmHost.ps1"

# No results of an earlier run; the other platforms' tree pools, fresh.
& powershell -NoProfile -ExecutionPolicy Bypass -File $invoke -Script "Remove-Item C:\zfswin-arm\stage\trees, C:\zfswin-arm\stage\tree-$label, C:\zfswin-arm\stage\$full, C:\zfswin-arm\stage\$lines -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force C:\zfswin-arm\stage\trees | Out-Null" | Out-Null
foreach ($tree in @(Get-ChildItem (Join-Path $repo "TestVolumes\vm") -Directory -Filter "tree-*" | Where-Object { $_.Name -notlike "tree-arm64-*" -and (Test-Path (Join-Path $_.FullName "tree.img")) })) {
    & ssh.exe @sshOptions $target "mkdir C:\zfswin-arm\stage\trees\$($tree.Name)" | Out-Null
    foreach ($f in "tree.img", "tree-manifest.tsv") {
        & scp -q @sshOptions (Join-Path $tree.FullName $f) "${target}:C:/zfswin-arm/stage/trees/$($tree.Name)/$f"
        if ($LASTEXITCODE -ne 0) { throw "scp of $($tree.Name)\$f failed" }
    }
    Write-Host "uploaded $($tree.Name)"
}

# The suite, with this run's settings in front of it.
$job = Join-Path $env:TEMP "arm-$label-$PID.ps1"
$prefix = "`$Version = '$Version'`r`n`$MsiTag = '$tag'`r`n`$Checkpoint = '$Checkpoint'`r`n`$TreeLabel = '$label'`r`n`$Only = '$Only'`r`n"
[IO.File]::WriteAllText($job, $prefix + [IO.File]::ReadAllText($suite), (New-Object Text.UTF8Encoding $true))
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $invoke -Upload "$msi;$(Join-Path $repo 'scripts\vm\guest-installer.ps1');$(Join-Path $repo 'out\driver-package\zfswindows-test.cer')" -ScriptFile $job | Out-Null
} finally { Remove-Item $job -ErrorAction SilentlyContinue }

# Results from files (the session may have dropped).
$out = Join-Path $repo "TestVolumes\vm"
# An earlier run's results must not stand in for a run that produced none.
Remove-Item (Join-Path $out "$label.json"), (Join-Path $out "$label.jsonl") -Force -ErrorAction SilentlyContinue
& scp -q @sshOptions "${target}:C:/zfswin-arm/stage/$full" (Join-Path $out "$label.json")
& scp -q @sshOptions "${target}:C:/zfswin-arm/stage/$lines" (Join-Path $out "$label.jsonl")
$own = Join-Path $out "tree-$label"
if (-not $Only) {
    New-Item -ItemType Directory -Force $own | Out-Null
    foreach ($f in "tree.img", "tree-manifest.tsv") {
        Remove-Item (Join-Path $own $f) -Force -ErrorAction SilentlyContinue
        & scp -q @sshOptions "${target}:C:/zfswin-arm/stage/tree-$label/$f" (Join-Path $own $f)
    }
}
$checks = @(Get-Content (Join-Path $out "$label.jsonl") -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
foreach ($c in $checks) { $d = "$($c.detail)" -replace '\s+', ' '; "{0} {1}: {2}" -f $(if ($c.ok) { 'OK  ' } else { 'FAIL' }), $c.stage, $d.Substring(0, [Math]::Min(220, $d.Length)) }
"ARM64 $Edition $Configuration $Version : $(@($checks | Where-Object { -not $_.ok }).Count) of $($checks.Count) checks failed; tree pool $(if (Test-Path (Join-Path $own 'tree.img')) { 'kept' } else { 'missing' })"

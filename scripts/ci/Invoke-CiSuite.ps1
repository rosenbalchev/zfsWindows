<#
.SYNOPSIS
CI gate (.github/workflows/ci.yml): runs one VM suite on a self-hosted
runner and fails only on checks not listed in scripts\ci\known-failures.txt.

.DESCRIPTION
x64, on the x64 Hyper-V host (VM zfsTest): Test-InstallerInVm.ps1 (kernel)
or Test-UserModeInVm.ps1 (usermode) against out\release\<version> of this
checkout.  TestVolumes\ (VM credentials, fixtures), out\dotnet-runtime\,
out\driver-tools\ and the upgrade package out\release\1.0.65000\ come from
the runner's data folder, $env:ZFSWIN_CI_DATA, which mirrors those paths.

arm64, on the ARM PC (the Hyper-V host of ZFSTestOnArm): the host suites
Test-ArmSuite.ps1 runs there over SSH (scripts\vm\arm\host-*-suite.ps1),
run locally.  The package, guest-installer.ps1 and the test certificate are
staged in C:\zfswin-arm\stage, where the WinFsp and .NET installers already
are; the VM's name and credentials come from
$env:ZFSWIN_CI_DATA\ZFSTestOnArm.json (vmName, user, password).

Every failure is printed; a step summary is written for GitHub.

  Invoke-CiSuite.ps1 -Platform x64 -Edition usermode -Configuration Release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("x64", "arm64")]
    [string]$Platform,
    [Parameter(Mandatory)]
    [ValidateSet("kernel", "usermode")]
    [string]$Edition,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Version) { $Version = & (Join-Path $repo "scripts\Get-ZfsWinVersion.ps1") }
$data = $env:ZFSWIN_CI_DATA
if (-not $data -or -not (Test-Path -LiteralPath $data)) { throw "ZFSWIN_CI_DATA (the runner's test data folder) is not set or does not exist." }
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$failures = @()

if ($Platform -eq "x64") {
    foreach ($item in "TestVolumes", "out\dotnet-runtime", "out\driver-tools", "out\release\1.0.65000") {
        $from = Join-Path $data $item
        if (-not (Test-Path -LiteralPath $from)) { throw "Missing $from" }
        & robocopy.exe $from (Join-Path $repo $item) /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy of $item failed ($LASTEXITCODE)" }
    }
    $global:LASTEXITCODE = 0
    $suite = if ($Edition -eq "kernel") { "Test-InstallerInVm.ps1" } else { "Test-UserModeInVm.ps1" }
    try {
        & (Join-Path $repo "scripts\vm\$suite") -Configuration $Configuration
    } catch {
        # Both suites throw one message: a first line, then a failure per line.
        $lines = @("$($_.Exception.Message)" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $failures = if ($lines.Count -gt 1) { @($lines | Select-Object -Skip 1) } else { $lines }
    }
} else {
    $stage = "C:\zfswin-arm\stage"
    $variant = if ($Edition -eq "kernel") { "testsigned" } else { "usermode" }
    $msi = Join-Path $repo "out\release\$Version\OpenZFS-for-Windows-$Version-arm64-$variant$tag.msi"
    foreach ($f in $msi, (Join-Path $repo "scripts\vm\guest-installer.ps1"), (Join-Path $repo "out\driver-package\zfswindows-test.cer")) {
        if (-not (Test-Path -LiteralPath $f)) { throw "Missing $f" }
        Copy-Item -LiteralPath $f $stage -Force
    }
    $label = "arm64-$Edition-$($Configuration.ToLower())"
    $resultFile = Join-Path $stage "$Edition-results.jsonl"
    Remove-Item -LiteralPath $resultFile, (Join-Path $stage "trees") -Recurse -Force -ErrorAction SilentlyContinue
    $vm = Get-Content -LiteralPath (Join-Path $data "ZFSTestOnArm.json") -Raw | ConvertFrom-Json
    $checkpoint = if ($vm.PSObject.Properties['checkpoint'] -and $vm.checkpoint) { $vm.checkpoint } else { "testsigning-ready-updated" }
    # The prelude Invoke-ArmHost.ps1 puts in front of a host suite.
    $Guest = $vm.vmName
    $GuestCredential = New-Object PSCredential ($vm.user, (ConvertTo-SecureString $vm.password -AsPlainText -Force))
    function Open-ArmGuest([int]$Minutes = 6) {
        $deadline = (Get-Date).AddMinutes($Minutes)
        while ($true) {
            try { return New-PSSession -VMName $Guest -Credential $GuestCredential -ErrorAction Stop }
            catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 10 }
        }
    }
    $MsiTag = $tag; $Checkpoint = $checkpoint; $TreeLabel = $label; $Only = ""
    $ErrorActionPreference = "Continue"
    . (Join-Path $repo "scripts\vm\arm\host-$Edition-suite.ps1") | Out-Null
    $ErrorActionPreference = "Stop"
    $checks = @(Get-Content -LiteralPath $resultFile -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($checks.Count -eq 0) { $failures = @("no results in $resultFile") }
    # "tree (154 s)" / "49/52 passed; failed: a, b" -> "tree a, b"
    $failures += @($checks | Where-Object { -not $_.ok } | ForEach-Object {
        $name = "$($_.stage)" -replace '\s*\(\d+ s\)$', ''
        $what = if ("$($_.detail)" -match 'failed: (.*)$') { $Matches[1] } else { "$($_.detail)" }
        "$name $what".Trim()
    })
}

# Known failures: one regular expression per line, matched against a whole
# failure line, optionally scoped "platform/edition: regex".
$known = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot "known-failures.txt") | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
function IsKnown([string]$failure) {
    foreach ($k in $known) {
        $scope = "*/*"; $pattern = $k
        if ($k -match '^(\S+/\S+):\s+(.*)$') { $scope = $Matches[1]; $pattern = $Matches[2] }
        if ("$Platform/$Edition" -like $scope -and $failure -match "^(?:$pattern)$") { return $true }
    }
    $false
}
$new = @($failures | Where-Object { -not (IsKnown $_) })
$old = @($failures | Where-Object { IsKnown $_ })

$title = "$Platform $Edition $Configuration $Version"
$summary = @("### $title", "", $(if ($new.Count) { "**$($new.Count) new failure(s)**" } else { "No new failures" }), "")
$summary += @($new | ForEach-Object { "- :x: $_" })
$summary += @($old | ForEach-Object { "- known: $_" })
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
$summary | ForEach-Object { Write-Host $_ }
if ($new.Count) { exit 1 }
exit 0

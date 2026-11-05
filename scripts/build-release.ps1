[CmdletBinding()]
<#
.SYNOPSIS
Builds a numbered release of both variants and publishes it to
downloads\<major.minor>\ (plan step 16).

.DESCRIPTION
1. Increments build\BUILD_NUMBER: every release build gets a new version,
   <prefix>.<build number> (1.0.N), which is what Windows Installer compares
   for upgrades.
2. Builds, with that version stamped in: the administration suite (tests
   first), zfs-write-core and zfswin-um.exe (ZFSWIN_VERSION), the
   kernel-driver MSI (test-signed driver package from out\driver-package;
   -BuildDriver rebuilds it first) and the user-mode MSI.
3. Copies the two MSIs, the test certificate and the SBOM to
   downloads\<major.minor>\, writes SHA256SUMS and VERSION.json there, and
   prints the result.

Nothing is installed on this machine.  -NoIncrement rebuilds the current
number (for a rebuild that must keep its version).
#>
param(
    [switch]$BuildDriver,
    [switch]$NoIncrement,
    [ValidateSet("x64")]
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = if ($NoIncrement) { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") } else { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") -Next }
$series = ($version -split '\.')[0..1] -join '.'
Write-Host "== OpenZFS for Windows $version"
$env:ZFSWIN_VERSION = $version

function Step($name, [scriptblock]$block) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "-- $name"
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
    Write-Host ("   {0} done in {1:n0} s" -f $name, $watch.Elapsed.TotalSeconds)
}

# Each build script runs in a child process: native tools write progress to
# stderr, which a parent with $ErrorActionPreference=Stop would treat as fatal.
function Run($script, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments
}

if ($BuildDriver) { Step "driver" { Run "build-driver.ps1" @() } }
Step "administration suite" { Run "build-admin.ps1" @("-Architecture", $Architecture) }
Step "user-mode file system" { Run "build-write-core.ps1" @("-Targets", "zfs-write-core,zfswin-um") }
Step "kernel-driver MSI" { Run "build-installer.ps1" @("-Architecture", $Architecture, "-Variant", "kernel") }
Step "user-mode MSI" { Run "build-installer.ps1" @("-Architecture", $Architecture, "-Variant", "usermode") }

# --- publish ---------------------------------------------------------------
$release = Join-Path $root "out\release\$version"
$downloads = Join-Path $root "downloads\$series"
New-Item -ItemType Directory -Force $downloads | Out-Null
Get-ChildItem $downloads -File | Remove-Item
$artifacts = @(
    "OpenZFS-for-Windows-$version-$Architecture-testsigned.msi",
    "OpenZFS-for-Windows-$version-$Architecture-usermode.msi",
    "OpenZFS-for-Windows-$version-$Architecture.cdx.json",
    "zfswindows-test.cer")
foreach ($a in $artifacts) {
    $source = Join-Path $release $a
    if (-not (Test-Path $source)) { throw "Missing release artifact: $source" }
    Copy-Item $source $downloads
}
[ordered]@{
    product = "OpenZFS for Windows"; version = $version; architecture = $Architecture
    built = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    repositoryCommit = (& git -C $root rev-parse HEAD).Trim()
    openzfs = "zfs-2.4.4 (OpenZFS submodule); openzfs-windows zfswin-2.4.1rc17 + patches/openzfs-windows"
    variants = [ordered]@{
        kernel = "OpenZFS-for-Windows-$version-$Architecture-testsigned.msi: kernel driver, test-signed (needs test signing and Secure Boot off)"
        usermode = "OpenZFS-for-Windows-$version-$Architecture-usermode.msi: WinFsp user-mode file system (needs WinFsp 2.1+; no driver signature of ours)"
    }
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $downloads "VERSION.json")
Get-ChildItem $downloads -File | Where-Object Name -ne "SHA256SUMS" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii (Join-Path $downloads "SHA256SUMS")
Get-ChildItem $downloads -File | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
Write-Output $downloads

[CmdletBinding()]
<#
.SYNOPSIS
Builds a numbered release of both variants and publishes it to
downloads\<major.minor>\ (plan step 17).

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
    # Both by default.  ARM64 is cross-compiled and statically checked
    # (machine type, signatures) but cannot be run on an x64 build host.
    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64", "arm64"),
    # Both by default, published to downloads\<series>\release and \debug.
    # Release: optimized native code (plain file names); Debug: unoptimized
    # with full checks (file names end in -debug).  The .NET tools are
    # Release builds in both.
    [ValidateSet("Debug", "Release")]
    [string[]]$Configuration = @("Release", "Debug")
)

$ErrorActionPreference = "Stop"
$Architecture = @($Architecture | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Configuration = @($Configuration | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$version = if ($NoIncrement) { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") } else { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") -Next }
$series = ($version -split '\.')[0..1] -join '.'
Write-Host "== OpenZFS for Windows $version ($($Architecture -join ', '))"
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

Step "administration suite" { Run "build-admin.ps1" @("-Architecture", ($Architecture -join ',')) }
foreach ($config in $Configuration) {
    foreach ($arch in $Architecture) {
        if ($BuildDriver) { Step "driver ($arch, $config)" { Run "build-driver.ps1" @("-Architecture", $arch, "-Configuration", $config) } }
        Step "user-mode file system ($arch, $config)" { Run "build-write-core.ps1" @("-Targets", "zfs-write-core,zfswin-um", "-Architecture", $arch, "-Configuration", $config) }
        Step "kernel-driver MSI ($arch, $config)" { Run "build-installer.ps1" @("-Architecture", $arch, "-Variant", "kernel", "-Configuration", $config) }
        Step "user-mode MSI ($arch, $config)" { Run "build-installer.ps1" @("-Architecture", $arch, "-Variant", "usermode", "-Configuration", $config) }
    }
}

# --- publish: downloads\<series>\release and \debug, each self-contained ----
$release = Join-Path $root "out\release\$version"
$series = Join-Path $root "downloads\$series"
New-Item -ItemType Directory -Force $series | Out-Null
# The flat layout of releases before 1.0.8 is replaced by the two folders.
Get-ChildItem $series -File | Remove-Item
$dirty = [bool](& git -C $root status --porcelain --untracked-files=no -- . ':!third_party' ':!OpenZFS')
foreach ($config in $Configuration) {
    $tag = if ($config -eq "Debug") { "-debug" } else { "" }
    $downloads = Join-Path $series $config.ToLowerInvariant()
    New-Item -ItemType Directory -Force $downloads | Out-Null
    Get-ChildItem $downloads -File | Remove-Item
    $artifacts = @("zfswindows-test.cer")
    foreach ($arch in $Architecture) {
        $artifacts += "OpenZFS-for-Windows-$version-$arch-testsigned$tag.msi", "OpenZFS-for-Windows-$version-$arch-usermode$tag.msi", "OpenZFS-for-Windows-$version-$arch$tag.cdx.json"
    }
    foreach ($a in $artifacts) {
        $source = Join-Path $release $a
        if (-not (Test-Path $source)) { throw "Missing release artifact: $source" }
        Copy-Item $source $downloads
    }
    [ordered]@{
        product = "OpenZFS for Windows"; version = $version; configuration = $config; architectures = $Architecture
        built = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        builtFromCommit = (& git -C $root rev-parse HEAD).Trim(); workingTreeModified = $dirty
        openzfs = "zfs-2.4.4 (OpenZFS submodule); openzfs-windows zfswin-2.4.1rc17 + patches/openzfs-windows"
        variants = [ordered]@{
            kernel = "OpenZFS-for-Windows-$version-<arch>-testsigned$tag.msi: kernel driver, test-signed (needs test signing and Secure Boot off)"
            usermode = "OpenZFS-for-Windows-$version-<arch>-usermode$tag.msi: WinFsp user-mode file system (needs WinFsp 2.1+; no driver signature of ours)"
        }
        build = $(if ($config -eq "Release") { "optimized native code (/O2); OpenZFS keeps its own consistency checks (ZFS_DEBUG) in the user-mode engine, as upstream libzpool does" } else { "unoptimized native code with full debug checks; larger and slower, for diagnosing problems" })
        tested = "x64 Debug and Release: Windows 11 24H2 VM suites (scripts/vm) and upstream OpenZFS validation on Linux, as recorded in the README for each release. arm64: see the README."
    } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $downloads "VERSION.json")
    Get-ChildItem $downloads -File | Where-Object Name -ne "SHA256SUMS" | Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -Encoding ascii (Join-Path $downloads "SHA256SUMS")
    Get-ChildItem $downloads -File | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
    Write-Output $downloads
}
[CmdletBinding()]
<#
.SYNOPSIS
Builds the administration suite (plan step 14): zfswin.exe and
ZfsWinBroker.exe as NativeAOT executables, plus the ZfsWin PowerShell
module, into out\admin-<arch>.

.DESCRIPTION
NativeAOT needs no .NET runtime on the target, which keeps the MSI
self-contained.  The ILCompiler locates the MSVC linker with vswhere.exe, so
the Visual Studio Installer directory is put on PATH first.  Runs the unit
and mocked-protocol tests unless -SkipTests.
#>
param(
    # x64, arm64, or both ("x64,arm64" also works through -File).
    [string[]]$Architecture = @("x64"),
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$Architecture = @($Architecture | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($a in $Architecture) { if ($a -notin "x64", "arm64") { throw "Unknown architecture '$a'." } }
$root = Split-Path -Parent $PSScriptRoot
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (-not (Test-Path (Join-Path $vsInstaller "vswhere.exe"))) { throw "vswhere.exe not found in $vsInstaller." }
$env:PATH = "$vsInstaller;$env:PATH"

if (-not $SkipTests) {
    # The schema tests read real zpool/zfs output that the installer VM run
    # captures (guest-installer.ps1 -Stage samples), which needs this build.
    $testArgs = @()
    if (-not (Test-Path (Join-Path $root "tests\Zfs.Admin.Tests\samples\zpool-list.json"))) {
        Write-Warning "No captured samples yet: skipping SchemaCompatibilityTests (run scripts\vm\Test-InstallerInVm.ps1)."
        $testArgs = @("--filter", "FullyQualifiedName!~SchemaCompatibilityTests")
    }
    & dotnet test (Join-Path $root "tests\Zfs.Admin.Tests\Zfs.Admin.Tests.csproj") -c Release @testArgs
    if ($LASTEXITCODE -ne 0) { throw "Administration tests failed." }
}

foreach ($arch in $Architecture) {
    $out = Join-Path $root "out\admin-$arch"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    foreach ($project in "Zfs.Admin.Cli", "Zfs.Admin.Broker") {
        & dotnet publish (Join-Path $root "src\managed\$project\$project.csproj") -c Release -r "win-$arch" -o $out
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project for $arch failed." }
    }
    # ZfsWinGui.exe: one framework-dependent file (needs the .NET 10 Desktop
    # Runtime; WinForms cannot be NativeAOT-compiled).
    & dotnet publish (Join-Path $root "src\managed\Zfs.Admin.Gui\Zfs.Admin.Gui.csproj") -c Release -r "win-$arch" --self-contained false -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publishing Zfs.Admin.Gui for $arch failed." }
    # Only the executables ship; symbols are kept beside them for the symbols policy.
    New-Item -ItemType Directory -Force (Join-Path $out "symbols") | Out-Null
    Get-ChildItem $out -Filter *.pdb | Move-Item -Destination (Join-Path $out "symbols") -Force
    Copy-Item -Recurse -Force (Join-Path $root "src\powershell\ZfsWin") (Join-Path $out "ZfsWin")
    Get-ChildItem $out -File | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
}

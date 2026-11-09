[CmdletBinding()]
<#
.SYNOPSIS
Runs the user-mode engine unit tests (tests\Zfs.UserMode.Tests): OpenZFS in
the test process, on image files, without WinFsp, a driver or a VM.

.DESCRIPTION
Needs ZfsUserModeTest.dll from the write-core tree of the same
configuration and architecture (scripts\build-write-core.ps1 -Targets
zfs-um-test; build-release.ps1 builds it).  The images go to
%TEMP%\zfswin-um-tests.  The Linux reference-image tests run only where
the 5 GiB images are (ZFSWIN_LINUX_IMAGES or TestVolumes\); elsewhere they
are skipped.  ARM64 runs on an ARM64 machine only.  Results (.trx) go to
out\test-results\usermode-<arch>-<configuration>.
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # A dotnet test --filter expression, e.g. "FullyQualifiedName~MemoryTests".
    [string]$Filter = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tree = Join-Path $root ("out\openzfs-write-$($Configuration.ToLowerInvariant())" + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }))
$dll = Join-Path $tree "zfs-um-test\ZfsUserModeTest.dll"
if (-not (Test-Path $dll)) { throw "$dll is missing; run build-write-core.ps1 -Targets zfs-um-test -Architecture $Architecture -Configuration $Configuration first." }
$results = Join-Path $root "out\test-results\usermode-$Architecture-$($Configuration.ToLowerInvariant())"
if (Test-Path $results) { Remove-Item -Recurse -Force $results }

$arguments = @(
    "test", (Join-Path $root "tests\Zfs.UserMode.Tests\Zfs.UserMode.Tests.csproj"), "-c", "Release",
    "-p:ZfsUmConfiguration=$Configuration", "-p:ZfsUmArchitecture=$Architecture",
    "--logger", "trx", "--logger", "console;verbosity=normal", "--results-directory", $results)
if ($Filter) { $arguments += @("--filter", $Filter) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "User-mode unit tests failed ($Architecture, $Configuration); results in $results." }

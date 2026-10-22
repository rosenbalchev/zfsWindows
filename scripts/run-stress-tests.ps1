param(
    [ValidateSet('Small','Medium','Large','Extreme')][string]$Profile = 'Small',
    [string]$Image,
    [string]$Manifest,
    [string]$Summary
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$profileName = $Profile.ToLowerInvariant()
if (-not $Image) { $Image = Join-Path $root "TestVolumes\scale\$profileName.raw" }
if (-not $Manifest) { $Manifest = Join-Path $root "tests\fixtures\scale\$profileName.jsonl" }
if (-not $Summary) { $Summary = Join-Path $root "tests\fixtures\scale\$profileName.summary.json" }
$env:ZFS_SCALE_IMAGE = (Resolve-Path $Image).Path
$env:ZFS_SCALE_MANIFEST = (Resolve-Path $Manifest).Path
$env:ZFS_SCALE_SUMMARY = (Resolve-Path $Summary).Path
dotnet test (Join-Path $root 'tests\Zfs.Core.Tests\Zfs.Core.Tests.csproj') `
    --filter 'Category=Stress' --logger 'console;verbosity=detailed'

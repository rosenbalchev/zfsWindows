param(
    [Parameter(Mandatory=$true)][string]$Image,
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$BenchmarkArguments
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:ZFS_SCALE_IMAGE = (Resolve-Path $Image).Path
$env:ZFS_SCALE_MANIFEST = (Resolve-Path $Manifest).Path
dotnet run --project (Join-Path $root 'benchmarks\Zfs.Benchmarks\Zfs.Benchmarks.csproj') `
    --configuration Release -- @BenchmarkArguments

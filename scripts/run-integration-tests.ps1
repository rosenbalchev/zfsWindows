param([Parameter(Mandatory=$true)][string]$Image, [string]$Manifest)
$ErrorActionPreference = 'Stop'
$env:ZFS_SINGLE_IMAGE = (Resolve-Path $Image).Path
if ($Manifest) { $env:ZFS_SINGLE_MANIFEST = (Resolve-Path $Manifest).Path }
dotnet test (Join-Path $PSScriptRoot '..\ZfsWindows.sln') --filter 'Category=Integration'

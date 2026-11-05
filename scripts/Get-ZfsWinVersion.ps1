<#
.SYNOPSIS
Prints the current product version, <prefix>.<build number>, from
build\ZfsWinVersion.props and build\BUILD_NUMBER (the same inputs MSBuild
uses).  -Next increments build\BUILD_NUMBER first (scripts\build-release.ps1
does this once per release build).
#>
param([switch]$Next)

$ErrorActionPreference = "Stop"
$build = Join-Path (Split-Path -Parent $PSScriptRoot) "build"
$numberFile = Join-Path $build "BUILD_NUMBER"
$number = [int](Get-Content $numberFile -Raw).Trim()
if ($Next) {
    $number++
    if ($number -gt 65535) { throw "The build number exceeds the MSI limit of 65535; raise the version prefix." }
    [IO.File]::WriteAllText($numberFile, "$number`n")
}
[xml]$props = Get-Content (Join-Path $build "ZfsWinVersion.props")
"$($props.Project.PropertyGroup.ZfsWinVersionPrefix).$number"

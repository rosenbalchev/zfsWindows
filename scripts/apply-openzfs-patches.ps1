# Applies patches/openzfs-windows/*.patch to the pinned submodule
# (idempotent: a patch that already reverse-applies is skipped).  Each patch
# is documented in patches/openzfs-windows/README.md.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
$patches = Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch -ErrorAction SilentlyContinue | Sort-Object Name
foreach ($patch in $patches) {
    & git -C $openZfs apply --reverse --check --whitespace=nowarn $patch.FullName 2>$null
    if ($LASTEXITCODE -eq 0) { continue }
    & git -C $openZfs apply --whitespace=nowarn $patch.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name) to $openZfs." }
    Write-Host "Applied $($patch.Name)"
}

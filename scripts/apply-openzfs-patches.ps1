# Applies patches/openzfs-windows/*.patch to the pinned submodule
# (idempotent: a patch that already reverse-applies is skipped).  Each patch
# is documented in patches/openzfs-windows/README.md.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
$patches = Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch -ErrorAction SilentlyContinue | Sort-Object Name
foreach ($patch in $patches) {
    # The probe writes to stderr for a patch not yet applied (a fresh
    # checkout); under Stop, Windows PowerShell 5.1 turns that into a
    # terminating error even with 2>$null.
    $ErrorActionPreference = "Continue"
    & git -C $openZfs apply --reverse --check --whitespace=nowarn $patch.FullName 2>$null
    $applied = $LASTEXITCODE -eq 0
    $ErrorActionPreference = "Stop"
    if ($applied) { continue }
    & git -C $openZfs apply --whitespace=nowarn $patch.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name) to $openZfs." }
    Write-Host "Applied $($patch.Name)"
}

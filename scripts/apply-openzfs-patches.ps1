# Applies patches/openzfs-windows/*.patch, in name order, to the pinned
# submodule.  Each patch is documented in patches/openzfs-windows/README.md.
#
# Idempotent.  The check is on the whole stack, not per patch: later patches
# may change lines an earlier one added, so an applied patch does not always
# reverse-apply on its own.  In a scratch index holding the working tree, the
# script undoes the last k patches in reverse order for k = n .. 1.  The
# first k that leads back to the pinned commit tells how many are applied;
# the rest are then applied.  A tree that matches no prefix (for example
# patched with an older patch set) is refused, with the command to reset it.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
$patches = @(Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch -ErrorAction SilentlyContinue | Sort-Object Name)

function Invoke-Git([string[]]$GitArgs) {
    # git writes to stderr for a probe that fails; under Stop, Windows
    # PowerShell 5.1 turns that into a terminating error even with 2>$null.
    $ErrorActionPreference = "Continue"
    $out = & git -C $openZfs @GitArgs 2>$null
    $script:gitExit = $LASTEXITCODE
    return $out
}

$headTree = Invoke-Git @("rev-parse", "HEAD^{tree}")
if ($gitExit -ne 0) { throw "$openZfs is not a checkout; run git submodule update --init." }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("zfswin-patch-index-" + [Guid]::NewGuid())
$savedIndex = $env:GIT_INDEX_FILE
$applied = -1
try {
    $env:GIT_INDEX_FILE = $scratch
    Invoke-Git @("read-tree", "HEAD") | Out-Null
    Invoke-Git @("add", "-A") | Out-Null
    $workTree = Invoke-Git @("write-tree")
    if ($gitExit -ne 0) { throw "Could not snapshot $openZfs." }

    if ($workTree -eq $headTree) {
        $applied = 0
    } else {
        for ($k = $patches.Count; $k -ge 1 -and $applied -lt 0; $k--) {
            Invoke-Git @("read-tree", $workTree) | Out-Null
            $ok = $true
            for ($i = $k - 1; $i -ge 0; $i--) {
                Invoke-Git @("apply", "--cached", "--reverse", "--whitespace=nowarn", $patches[$i].FullName) | Out-Null
                if ($gitExit -ne 0) { $ok = $false; break }
            }
            if ($ok -and (Invoke-Git @("write-tree")) -eq $headTree) { $applied = $k }
        }
    }
} finally {
    $env:GIT_INDEX_FILE = $savedIndex
    Remove-Item -Force -ErrorAction SilentlyContinue $scratch, "$scratch.lock"
}

if ($applied -lt 0) {
    throw ("$openZfs has changes that are not the patch set (an older patch set, or local edits). " +
        "Reset it with: git -C `"$openZfs`" checkout -- . ; git -C `"$openZfs`" clean -fd")
}

for ($i = $applied; $i -lt $patches.Count; $i++) {
    $patch = $patches[$i]
    & git -C $openZfs apply --whitespace=nowarn $patch.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name) to $openZfs." }
    Write-Host "Applied $($patch.Name)"
}

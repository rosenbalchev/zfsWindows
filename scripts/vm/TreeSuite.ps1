# Dot-sourced by Test-InstallerInVm.ps1 and Test-UserModeInVm.ps1: the
# "tree" stage of guest-installer.ps1 (many small files in deep trees) and
# "treeverify" of every tree pool another platform left in TestVolumes\vm.
# Uses the caller's Guest, Open-Guest, Record, Expect and $repo.
#
# Each pool is kept as TestVolumes\vm\tree-<label>\{tree.img,
# tree-manifest.tsv}; scripts\vm\validate-tree.py checks them on Linux and
# scripts\vm\arm\host-kernel-suite.ps1 on Windows ARM64.

function Invoke-TreeStages([string]$label, [string]$phasePrefix) {
    $vmDir = Join-Path $repo "TestVolumes\vm"
    $tree = Guest "tree"
    Record "$phasePrefix-tree" $tree
    Expect "tree: $($tree.files) files in deep directories, all CPUs" (@($tree.failed).Count -eq 0) ($tree.failed -join ', ')

    $own = Join-Path $vmDir "tree-$label"
    New-Item -ItemType Directory -Force $own | Out-Null
    foreach ($f in "tree.img", "tree-manifest.tsv") { Remove-Item (Join-Path $own $f) -Force -ErrorAction SilentlyContinue }
    # Kept when every file verified and the pool exported cleanly, so the
    # other platforms can read it even if an unrelated tree check failed.
    $intact = @($tree.PSObject.Properties | Where-Object { $_.Name -like 'intact-*' -and -not $_.Value.pass }).Count -eq 0
    if ($intact -and $tree.'export-final'.pass) {
        $s = Open-Guest
        foreach ($f in "tree.img", "tree-manifest.tsv") { Copy-Item -FromSession $s "C:\zfswin\installer\tree\$f" (Join-Path $own $f) -Force }
        Remove-PSSession $s
    }

    foreach ($other in @(Get-ChildItem $vmDir -Directory -Filter "tree-*" | Where-Object Name -ne "tree-$label")) {
        if (-not (Test-Path (Join-Path $other.FullName "tree.img")) -or -not (Test-Path (Join-Path $other.FullName "tree-manifest.tsv"))) { continue }
        $s = Open-Guest
        Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\treecheck | Out-Null; Remove-Item C:\zfswin\treecheck\* -Force -ErrorAction SilentlyContinue }
        foreach ($f in "tree.img", "tree-manifest.tsv") { Copy-Item -ToSession $s (Join-Path $other.FullName $f) "C:\zfswin\treecheck\$f" -Force }
        Remove-PSSession $s
        $v = Guest "treeverify" @{ Dir = "C:\zfswin\treecheck" }
        Record "$phasePrefix-treeverify-$($other.Name)" $v
        Expect "tree pool from $($other.Name) read here" (@($v.failed).Count -eq 0) ($v.failed -join ', ')
    }
}

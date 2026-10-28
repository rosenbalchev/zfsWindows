$ErrorActionPreference = "Continue"
$zpool = "C:\zfswin\driver\zpool.exe"
$v = "C:\zfswin\tools\Zfs.MountVerifier.exe"
$out = [ordered]@{}
for ($round = 1; $round -le 4; $round++) {
    & $zpool import -d C:\zfswin\images\ext\lz4 zc_lz4 2>&1 | Out-Null
    $t = Measure-Command {
        $j = (& $v check "C:\var\tmp\zfs-windows-step06\mnt-lz4\windows-extended" C:\zfswin\ext\snapshot-mutate.json) -join "`n" | ConvertFrom-Json
    }
    $out["round$round"] = [ordered]@{ seconds = [int]$t.TotalSeconds; failures = @($j.failures) }
    $out["export$round"] = (& $zpool export zc_lz4 2>&1 | % { "$_" }) -join ' '
}
$out | ConvertTo-Json -Depth 5


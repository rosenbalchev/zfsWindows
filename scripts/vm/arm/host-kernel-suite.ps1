# Runs on the ARM host (through Invoke-ArmHost.ps1): the kernel-edition
# checks of Test-InstallerInVm.ps1 (scenarios 1-4: untrusted refusal, forced
# rollback, install, workflows) in the ARM64 VM from "testsigning-ready",
# plus a cross-check of the ARM64 user-mode pool (C:\zfswin-arm\stage\out),
# an import/export loop (the x64 Release export hang) and the uninstall.
# $Version may be set by a line prepended to this script; the MSI, the test
# certificate and guest-installer.ps1 are taken from C:\zfswin-arm\stage.
if (-not $Version) { $Version = '1.0.11' }
# The starting point; "testsigning-ready-updated" is the same state with
# Windows Update applied and paused (no servicing restarts mid-test).
if (-not $Checkpoint) { $Checkpoint = 'testsigning-ready' }
# Where this run's tree pool is kept (C:\zfswin-arm\stage\tree-<label>);
# the other platforms' pools to verify are uploaded to stage\trees\<name>.
if (-not $TreeLabel) { $TreeLabel = 'arm64-kernel-release' }
# $MsiTag "-debug" selects the Debug package.
$Msi = "C:\zfswin\installer\OpenZFS-for-Windows-$Version-arm64-testsigned$MsiTag.msi"
$results = [Collections.Generic.List[object]]::new()
$full = [ordered]@{}
function Note($stage, $ok, $detail) { $results.Add([ordered]@{ stage = $stage; ok = [bool]$ok; detail = $detail }) }
function Guest($stage, [hashtable]$arguments = @{}) {
    $s = Open-ArmGuest 15
    try {
        $json = Invoke-Command $s {
            param($stage, $arguments)
            $ErrorActionPreference = "Continue"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($arguments | ConvertTo-Json -Compress)))
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\installer\guest-installer.ps1 -Stage $stage -ArgumentsBase64 $encoded 2>$null
        } -ArgumentList $stage, $arguments
    } finally { Remove-PSSession $s }
    $text = ($json | Where-Object { "$_".StartsWith('{') } | Select-Object -Last 1)
    if (-not $text) { return [pscustomobject]@{ failed = @("no JSON: $(($json | Out-String).Trim())") } }
    $text | ConvertFrom-Json
}
function Restart-Guest {
    $s = Open-ArmGuest
    Invoke-Command $s { Restart-Computer -Force }
    Remove-PSSession $s
    Start-Sleep -Seconds 45
}
function Bugchecks {
    $s = Open-ArmGuest 15
    $b = Invoke-Command $s { @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1001 } -MaxEvents 3 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'bugcheck' } | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $(($_.Message -replace '\s+', ' ').Substring(0, 160))" }) }
    Remove-PSSession $s
    $b
}
function Checks($name, $r) {
    $checks = @($r.PSObject.Properties | Where-Object { $_.Value -and $_.Value.PSObject.Properties['pass'] })
    Note $name (@($r.failed | Where-Object { $_ }).Count -eq 0) ("{0}/{1} passed; failed: {2}" -f @($checks | Where-Object { $_.Value.pass }).Count, $checks.Count, (@($r.failed) -join ', '))
}

Restore-VMCheckpoint -VMName $Guest -Name $Checkpoint -Confirm:$false
$deadline = (Get-Date).AddMinutes(2)
while ((Get-VM $Guest).State -notin 'Saved', 'Off' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
Start-VM -Name $Guest
Start-Sleep -Seconds 30
$s = Open-ArmGuest 10
foreach ($f in (Split-Path -Leaf $Msi), 'zfswindows-test.cer', 'guest-installer.ps1') { Copy-Item -ToSession $s "C:\zfswin-arm\stage\$f" "C:\zfswin\installer\$f" -Force }
Remove-PSSession $s

$clean = Guest "state"; $full.clean = $clean
Note "clean guest" ($null -eq $clean.services.OpenZFS) "OpenZFS service: $($clean.services.OpenZFS)"

# 1. untrusted test certificate: refused, nothing left behind.
$r = Guest "msi" @{ Msi = $Msi; Action = "install"; Properties = @() }; $full.untrusted = $r
$after = Guest "state"
Note "untrusted install refused" ($r.exit -eq 1603 -and (($r.refused -join ' ') -match 'certificate') -and $null -eq $after.services.OpenZFS -and @($after.driverPackages).Count -eq 0 -and -not $after.installDirExists) "exit $($r.exit); $($r.refused -join ' ')"
$s = Open-ArmGuest
Invoke-Command $s { foreach ($store in "Root", "TrustedPublisher") { & certutil.exe -f -addstore $store C:\zfswin\installer\zfswindows-test.cer | Out-Null } }
Remove-PSSession $s

# 2. forced rollback of a fresh install.
$r = Guest "msi" @{ Msi = $Msi; Action = "install"; Properties = @("WIXFAILWHENDEFERRED=1") }
$after = Guest "state"; $full.rollback = $after
Note "forced rollback" ($r.exit -eq 1603 -and @($after.driverPackages).Count -eq 0 -and $null -eq $after.services.ZfsWinBroker -and -not $after.installDirExists) "exit $($r.exit); packages $($after.driverPackages -join ' | ')"
Note "no bugcheck after rollback (driver unload)" (@(Bugchecks).Count -eq 0) ((Bugchecks) -join ' | ')

# 3. install, reboot.
$r = Guest "msi" @{ Msi = $Msi; Action = "install"; Properties = @() }
Note "install" ($r.exit -in 0, 3010) "exit $($r.exit)"
Restart-Guest
$inst = Guest "state"; $full.installed = $inst
Note "driver running after reboot" ($inst.services.OpenZFS.state -eq 'Running') ($inst.services.OpenZFS | ConvertTo-Json -Compress)
Note "broker running as LocalSystem" ($inst.services.ZfsWinBroker.state -eq 'Running' -and $inst.services.ZfsWinBroker.account -eq 'LocalSystem') ($inst.services.ZfsWinBroker | ConvertTo-Json -Compress)
Note "driver packages staged" (@($inst.driverPackages).Count -eq 2) ($inst.driverPackages -join ' | ')
Note "signatures valid" (@($inst.badSignatures).Count -eq 0) ($inst.badSignatures -join ',')
Note "zfswin talks to the broker" ($inst.zfswinVersion.protocol -eq 1) ($inst.zfswinVersion | ConvertTo-Json -Compress)
$s = Open-ArmGuest
$full.driverInfo = Invoke-Command $s {
    $sys = Get-CimInstance Win32_SystemDriver -Filter "Name='OpenZFS'"
    $p = $sys.PathName -replace '^\\\?\?\\', ''
    $b = [IO.File]::ReadAllBytes($p); $pe = [BitConverter]::ToInt32($b, 0x3c)
    [ordered]@{ path = $p; machine = '0x{0:X4}' -f [BitConverter]::ToUInt16($b, $pe + 4); version = (Get-Item $p).VersionInfo.FileVersion; state = $sys.State }
}
Remove-PSSession $s
Note "driver binary" ($full.driverInfo.machine -eq '0xAA64') ($full.driverInfo | ConvertTo-Json -Compress)

# 4. workflows.
foreach ($stage in "cli", "powershell", "filesystem") {
    $t = [Diagnostics.Stopwatch]::StartNew(); $r = Guest $stage; $full[$stage] = $r
    Checks "$stage ($([int]$t.Elapsed.TotalSeconds) s)" $r
}
$gui = Guest "gui"; $full.gui = $gui
Note "gui" ($gui.exit -eq 0 -and @($gui.failures).Count -eq 0 -and -not $gui.brokerError -and @($gui.pools | Where-Object name -eq 'zwin').Count -eq 1 -and @($gui.datasets | Where-Object { $_.name -eq 'zwin/docs' -and $_.dedup -eq 'on' }).Count -eq 1) "exit $($gui.exit) $($gui.brokerError) shortcuts $(@($gui.shortcuts).Count)"
$user = Guest "standard-user"; $full.standardUser = $user
Note "standard user" ($user.admin -eq $false -and $user.list.exit -eq 0 -and $user.status.exit -eq 0 -and $user.datasets.exit -eq 0 -and $user.export.exit -eq 4 -and $user.snapshot.exit -eq 4 -and $user.cmdletExport -eq 'PermissionDenied') "list $($user.list.exit) export $($user.export.exit) snapshot $($user.snapshot.exit) cmdlet $($user.cmdletExport)"

# The ARM64 user-mode pool, read by the ARM64 kernel driver.
if (Test-Path C:\zfswin-arm\stage\out\fs-expected.json) {
    $s = Open-ArmGuest
    Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\cross | Out-Null; Remove-Item C:\zfswin\cross\* -Force -ErrorAction SilentlyContinue }
    foreach ($f in "a.img", "d.img", "fs-expected.json") { Copy-Item -ToSession $s "C:\zfswin-arm\stage\out\$f" "C:\zfswin\cross\$f" -Force }
    Remove-PSSession $s
    $cc = Guest "crosscheck" @{ Dir = "C:\zfswin\cross" }; $full.crosscheck = $cc
    Checks "crosscheck: ARM64 user-mode pool read by the kernel driver" $cc
}
# Many small files in deep trees (guest-installer.ps1 stage tree), and the
# tree pools of the other platforms read here.
$t = [Diagnostics.Stopwatch]::StartNew(); $tree = Guest "tree"; $full.tree = $tree
Checks "tree ($([int]$t.Elapsed.TotalSeconds) s)" $tree
$own = "C:\zfswin-arm\stage\tree-$TreeLabel"
New-Item -ItemType Directory -Force $own | Out-Null
Get-ChildItem $own -File -ErrorAction SilentlyContinue | Remove-Item -Force
if (@($tree.PSObject.Properties | Where-Object { $_.Name -like 'intact-*' -and -not $_.Value.pass }).Count -eq 0 -and $tree.'export-final'.pass) {
    $s = Open-ArmGuest
    foreach ($f in "tree.img", "tree-manifest.tsv") { Copy-Item -FromSession $s "C:\zfswin\installer\tree\$f" "$own\$f" -Force }
    Remove-PSSession $s
}
foreach ($other in @(Get-ChildItem C:\zfswin-arm\stage\trees -Directory -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path "$($other.FullName)\tree.img")) { continue }
    $s = Open-ArmGuest
    Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\treecheck | Out-Null; Get-ChildItem C:\zfswin\treecheck -File | Remove-Item -Force }
    foreach ($f in "tree.img", "tree-manifest.tsv") { Copy-Item -ToSession $s "$($other.FullName)\$f" "C:\zfswin\treecheck\$f" -Force }
    Remove-PSSession $s
    $v = Guest "treeverify" @{ Dir = "C:\zfswin\treecheck" }; $full["treeverify-$($other.Name)"] = $v
    Checks "tree pool from $($other.Name) read here" $v
}
Note "no bugcheck after workflows" (@(Bugchecks).Count -eq 0) ((Bugchecks) -join ' | ')

# Import/export loop (x64 Release: export hung intermittently in unmount).
$s = Open-ArmGuest
$loop = Invoke-Command $s {
    function T([string[]]$a, [int]$to) {
        $w = [Diagnostics.Stopwatch]::StartNew()
        $j = Start-Job -ArgumentList (, $a) { param($a) & 'C:\Program Files\OpenZFS for Windows\bin\zfswin.exe' @a 2>&1 | ForEach-Object { "$_" }; "exit $LASTEXITCODE" }
        if (Wait-Job $j -Timeout $to) { $o = (Receive-Job $j) -join ' '; [pscustomobject]@{ s = [int]$w.Elapsed.TotalSeconds; hung = $false; out = $o.Substring(0, [Math]::Min(120, $o.Length)) } }
        else { [pscustomobject]@{ s = $to; hung = $true; out = '' } }
    }
    $images = 'C:\zfswin\installer\images'
    $lines = @()
    $first = T @('pool', 'export', 'zwin') 300
    $lines += "initial export $($first.s) s$(if ($first.hung) { ' HUNG' }) $($first.out)"
    $hung = $first.hung
    for ($i = 1; $i -le 6 -and -not $hung; $i++) {
        $im = T @('pool', 'import', 'zwin', '--dir', $images) 180
        $ex = T @('pool', 'export', 'zwin') 300
        $lines += "run ${i}: import $($im.s) s, export $($ex.s) s$(if ($ex.hung -or $im.hung) { ' HUNG' })"
        $hung = $ex.hung -or $im.hung
    }
    if ($hung) {
        New-Item -ItemType Directory -Force C:\zfswin\dumps\live | Out-Null
        $ss = Get-StorageSubSystem | Select-Object -First 1
        Get-StorageDiagnosticInfo -StorageSubSystemUniqueId $ss.UniqueId -IncludeLiveDump -DestinationPath C:\zfswin\dumps\live -ErrorAction Continue 2>&1 | Out-Null
        $lines += "live dump: " + (@(Get-ChildItem C:\zfswin\dumps\live -Recurse -Filter *.dmp | ForEach-Object { "$($_.FullName) $([int]($_.Length / 1MB)) MB" }) -join ', ')
    }
    [ordered]@{ hung = $hung; lines = $lines }
}
Remove-PSSession $s
$full.loop = $loop
Note "import/export loop" (-not $loop.hung) ($loop.lines -join ' | ')

# Uninstall (only when nothing hangs: a hung export would block it).
if (-not $loop.hung) {
    $r = Guest "msi" @{ Msi = $Msi; Action = "uninstall"; Properties = @() }
    $after = Guest "state"; $full.uninstalled = $after
    Note "uninstall" ($r.exit -in 0, 3010 -and $null -eq $after.services.ZfsWinBroker -and -not $after.installDirExists) "exit $($r.exit); packages $(@($after.driverPackages).Count); installDir $($after.installDirExists)"
    Note "no bugcheck after uninstall" (@(Bugchecks).Count -eq 0) ((Bugchecks) -join ' | ')
}
$s = Open-ArmGuest
$dumps = Invoke-Command $s { @(Get-ChildItem C:\zfswin\dumps, C:\Windows\Minidump -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer } | ForEach-Object Name) }
Remove-PSSession $s
Note "crash dumps" (@($dumps).Count -eq 0) ($dumps -join ', ')

$full | ConvertTo-Json -Depth 8 -Compress | Set-Content C:\zfswin-arm\stage\kernel-full.json -Encoding UTF8
# Also on disk: the SSH session (and with it this script's output) may be gone.
$results | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content C:\zfswin-arm\stage\kernel-results.jsonl -Encoding UTF8
$results | ForEach-Object { $_ | ConvertTo-Json -Compress }

# Runs on the ARM host (through Invoke-ArmHost.ps1, driven by
# Test-ArmSuite.ps1 -Edition usermode): the user-mode edition's suite in
# the ARM64 VM, as Test-UserModeInVm.ps1 -Phase setup,admin,uninstall does on
# x64: refusal without WinFsp, install, every admin stage of
# guest-installer.ps1, the tree stage and the other platforms' tree pools
# (stage\trees), a service restart, export and uninstall.
# Settings prepended by the caller: $Version, $MsiTag, $Checkpoint, $TreeLabel,
# and $Only (comma-separated admin stages: setup and those stages only).
if (-not $Version) { throw "Version not set" }
if (-not $Checkpoint) { $Checkpoint = 'testsigning-ready-updated' }
if (-not $TreeLabel) { $TreeLabel = 'arm64-usermode-release' }
$MsiName = "OpenZFS-for-Windows-$Version-arm64-usermode$MsiTag.msi"
$results = [Collections.Generic.List[object]]::new()
$full = [ordered]@{}
function Note($stage, $ok, $detail) { $results.Add([ordered]@{ stage = $stage; ok = [bool]$ok; detail = $detail }) }
function Guest($stage, [hashtable]$arguments = @{}) {
    $arguments.Edition = "usermode"
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
function Checks($name, $r) {
    $checks = @($r.PSObject.Properties | Where-Object { $_.Value -and $_.Value.PSObject.Properties['pass'] })
    Note $name (@($r.failed | Where-Object { $_ }).Count -eq 0) ("{0}/{1} passed; failed: {2}" -f @($checks | Where-Object { $_.Value.pass }).Count, $checks.Count, (@($r.failed) -join ', '))
}
function Save {
    $full | ConvertTo-Json -Depth 8 -Compress | Set-Content C:\zfswin-arm\stage\usermode-full.json -Encoding UTF8
    $results | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content C:\zfswin-arm\stage\usermode-results.jsonl -Encoding UTF8
}

Restore-VMCheckpoint -VMName $Guest -Name $Checkpoint -Confirm:$false
$deadline = (Get-Date).AddMinutes(2)
while ((Get-VM $Guest).State -notin 'Saved', 'Off' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
Start-VM -Name $Guest
Start-Sleep -Seconds 30

# --- setup ---------------------------------------------------------------
$s = Open-ArmGuest 10
Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\um, C:\zfswin\installer, C:\zfswin\dumps | Out-Null }
Copy-Item -ToSession $s "C:\zfswin-arm\stage\$MsiName" C:\zfswin\um\zfswin-usermode.msi -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\winfsp-2.1.25156.msi C:\zfswin\um\winfsp.msi -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\windowsdesktop-runtime-10.0.14-win-arm64.exe C:\zfswin\um\dotnet-desktop-runtime.exe -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\guest-installer.ps1 C:\zfswin\installer\guest-installer.ps1 -Force
$setup = Invoke-Command $s {
    $ErrorActionPreference = "Continue"
    function Msi($arguments, $log) {
        $p = Start-Process msiexec.exe -ArgumentList ($arguments + @('/qn', '/norestart', '/l*v', $log)) -Wait -PassThru
        [ordered]@{ exit = $p.ExitCode; refused = @(Get-Content $log -ErrorAction SilentlyContinue | Select-String 'REFUSED:' | ForEach-Object { ($_.Line -replace '^.*REFUSED:', 'REFUSED:').Trim() } | Select-Object -Unique) }
    }
    $withoutWinFsp = Msi @('/i', 'C:\zfswin\um\zfswin-usermode.msi') 'C:\zfswin\um\usermode-nowinfsp.log'
    $serviceAfterRefusal = [bool](Get-Service ZfsWinUserMode -ErrorAction SilentlyContinue)
    $winfsp = Msi @('/i', 'C:\zfswin\um\winfsp.msi') 'C:\zfswin\um\winfsp-install.log'
    $install = Msi @('/i', 'C:\zfswin\um\zfswin-usermode.msi') 'C:\zfswin\um\usermode-install.log'
    $rt = Start-Process C:\zfswin\um\dotnet-desktop-runtime.exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    foreach ($tool in "zfswin-um.exe", "zpool.exe", "zfs.exe", "zfswin.exe", "ZfsWinBroker.exe") {
        $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$tool"
        New-Item -Force $key | Out-Null
        New-ItemProperty -Force $key -Name DumpFolder -PropertyType ExpandString -Value C:\zfswin\dumps | Out-Null
        New-ItemProperty -Force $key -Name DumpType -PropertyType DWord -Value 2 | Out-Null
    }
    $bin = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin"
    $um = "$bin\zfswin-um.exe"
    $machine = if (Test-Path $um) { $b = [IO.File]::ReadAllBytes($um); $pe = [BitConverter]::ToInt32($b, 0x3c); '0x{0:X4}' -f [BitConverter]::ToUInt16($b, $pe + 4) }
    $svc = Get-CimInstance Win32_Service -Filter "Name='ZfsWinUserMode'"
    [ordered]@{
        secureBoot = $(try { Confirm-SecureBootUEFI } catch { "n/a" })
        withoutWinFsp = $withoutWinFsp; serviceAfterRefusal = $serviceAfterRefusal
        winfspExit = $winfsp.exit; installExit = $install.exit; installRefused = $install.refused; runtimeExit = $rt.ExitCode
        engineMachine = $machine; engineVersion = $(if (Test-Path $um) { (Get-Item $um).VersionInfo.FileVersion })
        engineSignature = $(if (Test-Path $um) { (Get-AuthenticodeSignature $um).Status.ToString() })
        service = $(if ($svc) { "$($svc.StartMode) $($svc.StartName) $($svc.State)" })
    }
}
Remove-PSSession $s
$full.setup = $setup
Note "refused without WinFsp, nothing left" ($setup.withoutWinFsp.exit -eq 1603 -and -not $setup.serviceAfterRefusal) "exit $($setup.withoutWinFsp.exit); $($setup.withoutWinFsp.refused -join ' ')"
Note "install with WinFsp" ($setup.winfspExit -eq 0 -and $setup.installExit -eq 0 -and $setup.runtimeExit -in 0, 3010) "winfsp $($setup.winfspExit) msi $($setup.installExit) runtime $($setup.runtimeExit)"
# Test-signed: without the test certificate trusted (user mode does not need
# it) the signature is intact but its chain unknown (UnknownError).
Note "engine is ARM64 and signed" ($setup.engineMachine -eq '0xAA64' -and $setup.engineSignature -in 'Valid', 'UnknownError') "$($setup.engineMachine) $($setup.engineVersion) $($setup.engineSignature)"
Note "service automatic, LocalSystem, running" ($setup.service -match '^Auto LocalSystem Running') $setup.service
Save

# --- admin stages ----------------------------------------------------------
$s = Open-ArmGuest
Invoke-Command $s { Remove-Item -Recurse -Force "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Mounts" -ErrorAction SilentlyContinue; & sc.exe start ZfsWinUserMode | Out-Null }
Remove-PSSession $s
$adminStages = if ($Only) { @($Only -split ',') } else { @("cli", "gui", "powershell", "standard-user", "filesystem", "datafeatures", "memory", "wholedisk") }
foreach ($stage in $adminStages) {
    $t = [Diagnostics.Stopwatch]::StartNew(); $r = Guest $stage; $full[$stage] = $r
    if ($stage -eq "gui") { Note "gui ($([int]$t.Elapsed.TotalSeconds) s)" ($r.exit -eq 0 -and -not $r.brokerError -and @($r.pools | Where-Object name -eq 'zwin').Count -eq 1) "exit $($r.exit) $($r.brokerError)" }
    elseif ($stage -eq "standard-user") { Note "standard user" ($r.admin -eq $false -and $r.list.exit -eq 0 -and $r.export.exit -eq 4 -and $r.snapshot.exit -eq 4 -and $r.cmdletExport -eq 'PermissionDenied') "list $($r.list.exit) export $($r.export.exit) snapshot $($r.snapshot.exit) cmdlet $($r.cmdletExport)" }
    else { Checks "$stage ($([int]$t.Elapsed.TotalSeconds) s)" $r }
    Save
}
if ($Only) { $results | ForEach-Object { $_ | ConvertTo-Json -Compress }; return }

# --- tree, and the other platforms' tree pools -------------------------------
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
Save

# --- pool and mounts back after a service restart ------------------------------
$s = Open-ArmGuest
$persist = Invoke-Command $s {
    $ErrorActionPreference = "Continue"
    $z = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin\zfswin.exe"
    $restartError = try { Restart-Service ZfsWinUserMode -Force -ErrorAction Stop; $null } catch { "$_" }
    $deadline = (Get-Date).AddMinutes(3)
    do { Start-Sleep -Seconds 2; $mounts = (& $z --json mount list 2>$null | ConvertFrom-Json).result.mounts } while (-not ($mounts | Where-Object dataset -eq 'zwin/fs') -and (Get-Date) -lt $deadline)
    $fsPath = ($mounts | Where-Object dataset -eq 'zwin/fs').mountpoint
    if ($fsPath -match '^[A-Za-z]:$') { $fsPath += '\' }
    [ordered]@{
        restartError = $restartError
        pools = @((& $z --json pool list 2>$null | ConvertFrom-Json).result.pools | ForEach-Object name)
        fileAfterRestart = $(if ($fsPath) { Get-Content -LiteralPath (Join-Path $fsPath "streams.txt") -Stream meta -ErrorAction SilentlyContinue })
    }
}
Remove-PSSession $s
$full.restart = $persist
Note "service restart: pool and mounts back" (-not $persist.restartError -and @($persist.pools) -contains 'zwin' -and $persist.fileAfterRestart -eq 'metadata stream') ($persist | ConvertTo-Json -Compress)

# --- export, uninstall ---------------------------------------------------------
$exp = Guest "export"; $full.export = $exp
Note "export" ($exp.exit -eq 0) ($exp | ConvertTo-Json -Compress -Depth 4)
$s = Open-ArmGuest
$un = Invoke-Command $s {
    $p = Start-Process msiexec.exe -ArgumentList '/x', 'C:\zfswin\um\zfswin-usermode.msi', '/qn', '/norestart', '/l*v', 'C:\zfswin\um\usermode-uninstall.log' -Wait -PassThru
    [ordered]@{
        exit = $p.ExitCode
        service = [bool](Get-Service ZfsWinUserMode -ErrorAction SilentlyContinue)
        installDir = Test-Path "$env:ProgramFiles\OpenZFS for Windows (user mode)"
        imageKept = Test-Path C:\zfswin\installer\images\a.img
        winfspKept = [bool](Get-Service WinFsp.Launcher -ErrorAction SilentlyContinue)
        dumps = @(Get-ChildItem C:\zfswin\dumps -ErrorAction SilentlyContinue | ForEach-Object Name)
    }
}
Remove-PSSession $s
$full.uninstall = $un
Note "uninstall: service and files gone, pool image and WinFsp kept" ($un.exit -in 0, 3010 -and -not $un.service -and -not $un.installDir -and $un.imageKept -and $un.winfspKept) ($un | ConvertTo-Json -Compress)
Note "crash dumps" (@($un.dumps).Count -eq 0) ($un.dumps -join ', ')
Save
$results | ForEach-Object { $_ | ConvertTo-Json -Compress }

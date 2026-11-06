<#
.SYNOPSIS
Tests the user-mode (WinFsp) variant in the isolated VM (plan step 15).

.DESCRIPTION
Runs unelevated on the host; everything happens in the guest over
PowerShell Direct.  The guest has Secure Boot ON and test signing OFF, so
nothing that needs a Microsoft or test signature of ours can load: the only
kernel driver is WinFsp's own, Microsoft-signed one.

Phases (default: all, in order):
  setup      revert to "clean", turn Secure Boot on, prove test signing is
             off, install the official WinFsp MSI, copy zfswin-um.exe,
             Zfs.MountVerifier and the fixtures, register the
             ZfsWinUserMode service, and take the checkpoint "usermode".
  read       mount the genuine 5 GiB single-disk fixture read-only, verify
             it against its manifest, unmount, and prove the image is
             byte-identical.
  write      mount a fresh copy of the Linux-created lz4 fixture read-write,
             verify it, run the step-12 write workload, unmount (export) and
             copy the image to TestVolumes\vm\written-usermode\ for
             scripts\vm\validate-written.py.
  bench      scripts\vm\guest-bench.ps1 on a fresh lz4 copy.
  powercut   writers acknowledging after FlushFileBuffers and after
             write-through, a Hyper-V TurnOff, then remount: every
             acknowledged file must be intact.
  admin      plan step 18: the kernel suite's guest stages (guest-installer.ps1)
             against this edition: the zfswin CLI workflow on image files
             (create, datasets, properties, snapshots, scrub, mirror
             attach/detach/replace, cache/log, dedup, block cloning,
             export/import, .zfs\snapshot), the GUI self-check, the ZfsWin
             module, standard-user access, and ACLs/streams/.zfs; pools and
             mounts back after a service restart; the kernel-written pool
             (TestVolumes\vm\installer) read here; this pool saved to
             TestVolumes\vm\crossedition-usermode for the kernel suite.
  uninstall  uninstall with a dataset mounted: the volume goes, the pool
             image and WinFsp stay.
Each phase records its wall-clock duration.  Results go to
TestVolumes\vm\results.jsonl as "usermode-*".
#>
[CmdletBinding()]
param(
    [string[]]$Phase = @("all"),
    [string]$Name = "zfsTest"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ($state.user, (ConvertTo-SecureString $state.password -AsPlainText -Force))
$results = Join-Path $repo "TestVolumes\vm\results.jsonl"
$Phase = @($Phase | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Phase -contains "all") { $Phase = @("setup", "read", "write", "bench", "powercut", "admin", "uninstall") }
$winfsp = Join-Path $repo "third_party\winfsp\download\winfsp-2.1.25156.msi"
$version = & (Join-Path $repo "scripts\Get-ZfsWinVersion.ps1")
$msi = Join-Path $repo "out\release\$version\OpenZFS-for-Windows-$version-x64-usermode.msi"
$failures = [Collections.Generic.List[string]]::new()

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try { return New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop }
        catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 30	# gentle: one attempt per 30 s
        }
    }
}

function Record($phase, $data) {
    $line = [ordered]@{ time = (Get-Date).ToString("o"); phase = "usermode-$phase"; data = $data } | ConvertTo-Json -Depth 10 -Compress
    for ($try = 0; ; $try++) {
        try { Add-Content -Path $results -Value $line -ErrorAction Stop; break }
        catch [System.IO.IOException] { if ($try -ge 20) { throw }; Start-Sleep -Milliseconds 500 }
    }
    Write-Host "[usermode-$phase] $(($data | ConvertTo-Json -Depth 10 -Compress))"
}

function Expect($label, [bool]$condition, $detail = "") {
    if (-not $condition) { $failures.Add("$label $detail"); Write-Warning "FAILED: $label $detail" }
}

# Mount one dataset through the service (registry configuration, as the
# installer sets it up) and wait until the volume answers.
$mountBlock = {
    param($source, $pool, $dataset, $mountpoint, $readonly)
    $ErrorActionPreference = "Continue"
    $key = "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Mounts"
    Remove-Item -Recurse -Force $key -ErrorAction SilentlyContinue
    New-Item -Force "$key\test" | Out-Null
    foreach ($p in @{ Source = $source; Pool = $pool; Dataset = $dataset; MountPoint = $mountpoint }.GetEnumerator()) {
        New-ItemProperty -Force "$key\test" -Name $p.Key -Value $p.Value | Out-Null
    }
    New-ItemProperty -Force "$key\test" -Name ReadOnly -PropertyType DWord -Value ([int]$readonly) | Out-Null
    # The service starts at boot (automatic); restart it so it reads this
    # configuration.
    & sc.exe stop ZfsWinUserMode | Out-Null
    $stop = [Diagnostics.Stopwatch]::StartNew()
    while ((& sc.exe query ZfsWinUserMode | Out-String) -notmatch 'STOPPED' -and $stop.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 250 }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & sc.exe start ZfsWinUserMode | Out-Null
    $root = "$mountpoint\"
    while (-not (Test-Path -LiteralPath $root) -and $watch.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Milliseconds 250 }
    [ordered]@{
        mounted = [bool](Test-Path -LiteralPath $root); mountSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
        service = ((& sc.exe query ZfsWinUserMode) -match 'STATE' | Out-String).Trim()
        volume = (Get-Volume -FilePath $root -ErrorAction SilentlyContinue | Select-Object FileSystemType, Size, SizeRemaining | Out-String).Trim()
    }
}
$unmountBlock = {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & sc.exe stop ZfsWinUserMode | Out-Null
    while ((& sc.exe query ZfsWinUserMode | Out-String) -notmatch 'STOPPED' -and $watch.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Milliseconds 250 }
    [ordered]@{
        stopped = (& sc.exe query ZfsWinUserMode | Out-String) -match 'STOPPED'
        unmountSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
        # (A ProviderName filter throws "The parameter is incorrect" for an
        # unregistered event source, whatever -ErrorAction says.)
        # FspServiceLog() reports under the event source "WinFsp".
        events = @(Get-WinEvent -LogName Application -MaxEvents 300 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -eq 'WinFsp' -and ($_.Properties.Value -join ' ') -match 'zfswin-um' } | Select-Object -First 4 |
            ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($_.Properties.Value -join ' ')" })
    }
}

# One stage of scripts\vm\guest-installer.ps1, for this edition.
function Guest($stage, [hashtable]$arguments = @{}) {
    $arguments.Edition = "usermode"
    $s = Open-Guest
    try {
        $json = Invoke-Command $s {
            param($stage, $arguments)
            $ErrorActionPreference = "Continue"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($arguments | ConvertTo-Json -Compress)))
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\installer\guest-installer.ps1 -Stage $stage -ArgumentsBase64 $encoded 2>$null
        } -ArgumentList $stage, $arguments
    } finally { Remove-PSSession $s }
    $text = ($json | Where-Object { "$_".StartsWith('{') } | Select-Object -Last 1)
    if (-not $text) { throw "guest stage $stage printed no JSON: $($json | Out-String)" }
    $text | ConvertFrom-Json
}

function Fresh-Lz4($s) {
    Invoke-Command $s {
        New-Item -ItemType Directory -Force C:\zfswin\images\lz4 | Out-Null
        Copy-Item C:\zfswin\images\lz4-pristine\lz4.raw C:\zfswin\images\lz4\lz4.raw -Force
    }
}

if ($Phase -contains "setup") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Restore-VMCheckpoint -VMName $Name -Name "clean" -Confirm:$false
    if ((Get-VM $Name).State -eq 'Saved') { Remove-VMSavedState -VMName $Name }
    if ((Get-VM $Name).State -ne 'Off') { Stop-VM -Name $Name -TurnOff -Force }
    Set-VMFirmware -VMName $Name -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    Start-VM -Name $Name
    Start-Sleep -Seconds 30
    $s = Open-Guest
    Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\um, C:\zfswin\tools, C:\zfswin\fixtures, C:\zfswin\images\single, C:\zfswin\images\lz4-pristine, C:\zfswin\dumps | Out-Null }
    Copy-Item -ToSession $s $winfsp C:\zfswin\um\winfsp.msi -Force
    Copy-Item -ToSession $s $msi C:\zfswin\um\zfswin-usermode.msi -Force
    # The .NET 10 Desktop Runtime for ZfsWinGui.exe: the official, signed
    # installer, verified on the host.
    $runtime = Get-ChildItem (Join-Path $repo "out\dotnet-runtime") -Filter "windowsdesktop-runtime-*-win-x64.exe" | Select-Object -First 1
    if (-not $runtime -or (Get-AuthenticodeSignature $runtime.FullName).Status -ne "Valid") { throw "out\dotnet-runtime\windowsdesktop-runtime-*-win-x64.exe is missing or not validly signed." }
    Copy-Item -ToSession $s $runtime.FullName C:\zfswin\um\dotnet-desktop-runtime.exe -Force
    Copy-Item -ToSession $s (Join-Path $repo "out\driver-tools\Zfs.MountVerifier.exe") C:\zfswin\tools\ -Force
    Copy-Item -ToSession $s (Join-Path $PSScriptRoot "guest-bench.ps1") C:\zfswin\guest-bench.ps1 -Force
    Copy-Item -ToSession $s (Join-Path $repo "tests\fixtures\single-disk.manifest.json") C:\zfswin\fixtures\ -Force
    Copy-Item -ToSession $s (Join-Path $repo "tests\fixtures\compression\lz4.json") C:\zfswin\fixtures\ -Force
    Copy-Item -ToSession $s (Join-Path $repo "TestVolumes\single\disk01.raw") C:\zfswin\images\single\disk01.raw -Force
    Copy-Item -ToSession $s (Join-Path $repo "TestVolumes\compression\lz4.raw") C:\zfswin\images\lz4-pristine\lz4.raw -Force
    $out = Invoke-Command $s {
        $ErrorActionPreference = "Continue"
        function Msi($package, $log, $ui = '/qn') {
            $p = Start-Process msiexec.exe -ArgumentList '/i', $package, $ui, '/norestart', '/l*v', $log -Wait -PassThru
            [ordered]@{ exit = $p.ExitCode; refused = @(Get-Content $log -ErrorAction SilentlyContinue | Select-String 'REFUSED:' | ForEach-Object { ($_.Line -replace '^.*REFUSED:', 'REFUSED:').Trim() } | Select-Object -Unique) }
        }
        # Our package first: without WinFsp it must refuse and change nothing.
        $withoutWinFsp = Msi 'C:\zfswin\um\zfswin-usermode.msi' 'C:\zfswin\um\usermode-nowinfsp.log'
        $serviceAfterRefusal = [bool](Get-Service ZfsWinUserMode -ErrorAction SilentlyContinue)
        $winfspInstall = Msi 'C:\zfswin\um\winfsp.msi' 'C:\zfswin\um\winfsp-install.log'
        # Reduced UI (/qr), as a person installing: the client-side UI
        # sequence runs and opens the getting-started page (OpenReadMe);
        # silent installs never run it.
        $install = Msi 'C:\zfswin\um\zfswin-usermode.msi' 'C:\zfswin\um\usermode-install.log' '/qr'
        $readme = @(Get-Content C:\zfswin\um\usermode-install.log | Select-String 'Action ended .*: OpenReadMe\. Return value (\d)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        $rt = Start-Process C:\zfswin\um\dotnet-desktop-runtime.exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        $gui = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin\ZfsWinGui.exe"
        $gp = Start-Process $gui -ArgumentList '--self-check', 'C:\zfswin\um\gui-selfcheck.json' -Wait -PassThru
        $guiReport = if (Test-Path C:\zfswin\um\gui-selfcheck.json) { Get-Content C:\zfswin\um\gui-selfcheck.json -Raw | ConvertFrom-Json } else { $null }
        $svc = Get-CimInstance Win32_Service -Filter "Name='ZfsWinUserMode'"
        foreach ($tool in "zfswin-um.exe") {
            $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$tool"
            New-Item -Force $key | Out-Null
            New-ItemProperty -Force $key -Name DumpFolder -PropertyType ExpandString -Value C:\zfswin\dumps | Out-Null
            New-ItemProperty -Force $key -Name DumpType -PropertyType DWord -Value 2 | Out-Null
        }
        [ordered]@{
            secureBoot = (Confirm-SecureBootUEFI)
            testSigning = [bool]((& bcdedit /enum '{current}' | Out-String) -match 'testsigning\s+Yes')
            withoutWinFsp = $withoutWinFsp; serviceAfterRefusal = $serviceAfterRefusal
            winfspExit = $winfspInstall.exit
            winfspDriver = (Get-AuthenticodeSignature "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-x64.sys" | ForEach-Object { "$($_.Status) $($_.SignerCertificate.Subject)" })
            winfspService = ((& sc.exe query WinFsp.Launcher) -match 'STATE' | Out-String).Trim()
            install = $install; readmeOpened = $readme; runtimeExit = $rt.ExitCode
            gui = [ordered]@{ exit = $gp.ExitCode; report = $guiReport }
            shortcuts = @(Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\OpenZFS for Windows (user mode)" -ErrorAction SilentlyContinue | ForEach-Object Name)
            service =$(if ($svc) { [ordered]@{ start = $svc.StartMode; account = $svc.StartName; path = $svc.PathName; state = $svc.State } })
            ourBinarySignature = (Get-AuthenticodeSignature "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin\zfswin-um.exe").Status.ToString()
            os = (Get-CimInstance Win32_OperatingSystem).Caption + " " + [Environment]::OSVersion.Version
        }
    }
    Remove-PSSession $s
    Expect "Secure Boot is on" ($out.secureBoot -eq $true)
    Expect "test signing is off" (-not $out.testSigning)
    Expect "install without WinFsp refused" ($out.withoutWinFsp.exit -eq 1603 -and (($out.withoutWinFsp.refused -join ' ') -match 'WinFsp')) "exit $($out.withoutWinFsp.exit) $($out.withoutWinFsp.refused)"
    Expect "refused install left no service" (-not $out.serviceAfterRefusal)
    Expect "WinFsp installed" ($out.winfspExit -eq 0) "exit $($out.winfspExit)"
    Expect "user-mode MSI installed" ($out.install.exit -eq 0) "exit $($out.install.exit)"
    # PowerShell Direct has no desktop, so Windows Installer downgrades /qr to
    # basic UI and never runs the UI sequence that holds OpenReadMe.  The MSI
    # tables are checked on the host instead (docs/building.md); the page
    # opening is confirmed by a person once per release.
    if (@($out.readmeOpened) -notcontains "1") { Write-Host "note: OpenReadMe not run (no desktop in this session; expected)" }
    # Step 18: the broker is part of this edition too, and reaches the engine.
    Expect "GUI self-check on the user-mode edition" ($out.gui.exit -eq 0 -and $out.gui.report.broker -and -not $out.gui.report.brokerError -and $out.gui.report.userModeService -eq 'Running') "exit $($out.gui.exit) $($out.gui.report.brokerError)"
    Expect "Start menu shortcuts" (@($out.shortcuts).Count -eq 2) ($out.shortcuts -join ', ')
    Expect "service is automatic, LocalSystem" ($out.service.start -eq 'Auto' -and $out.service.account -eq 'LocalSystem') ($out.service | ConvertTo-Json -Compress)
    Stop-VM -Name $Name
    Get-VMSnapshot -VMName $Name -Name "usermode" -ErrorAction SilentlyContinue | Remove-VMSnapshot
    Checkpoint-VM -Name $Name -SnapshotName "usermode"
    Start-VM -Name $Name
    Start-Sleep -Seconds 30
    $out.seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    Record "setup" $out
}

if ($Phase -contains "read") {
    $manifest = Get-Content (Join-Path $repo "tests\fixtures\single-disk.manifest.json") -Raw | ConvertFrom-Json
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    $mount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\single", "zfswin_single", "zfswin_single/data", "Z:", $true
    $verify = Invoke-Command $s { $ErrorActionPreference = "Continue"; & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\single-disk.manifest.json Z:\ ; "exit=$LASTEXITCODE" }
    $unmount = Invoke-Command $s $unmountBlock
    $hash = Invoke-Command $s { (Get-FileHash C:\zfswin\images\single\disk01.raw -Algorithm SHA256).Hash }
    Remove-PSSession $s
    $v = ($verify | Where-Object { $_ -notmatch '^exit=' }) -join "`n" | ConvertFrom-Json
    $out = [ordered]@{ mount = $mount; verifier = $v; verifierExit = ($verify | Where-Object { $_ -match '^exit=' }); unmount = $unmount
                       imageUnchanged = ($hash -eq $manifest.image.sha256); seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Record "read" $out
    Expect "read: mounted" $mount.mounted
    Expect "read: verifier passed" ($out.verifierExit -eq 'exit=0') (@($v.failures) -join '; ')
    Expect "read: image unchanged" $out.imageUnchanged
}

if ($Phase -contains "write") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    Fresh-Lz4 $s
    $mount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\lz4", "zc_lz4", "zc_lz4/data", "Z:", $false
    $verify = Invoke-Command $s { $ErrorActionPreference = "Continue"; & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\lz4.json Z:\ --write Z:\windows-written; "exit=$LASTEXITCODE" }
    $unmount = Invoke-Command $s $unmountBlock
    $v = ($verify | Where-Object { $_ -notmatch '^exit=' }) -join "`n" | ConvertFrom-Json
    $local = Join-Path $repo "TestVolumes\vm\written-usermode"
    New-Item -ItemType Directory -Force $local | Out-Null
    Copy-Item -FromSession $s C:\zfswin\images\lz4\lz4.raw $local -Force
    ($v.written | ConvertTo-Json) | Set-Content (Join-Path $local "written.json")
    Remove-PSSession $s
    $out = [ordered]@{ mount = $mount; verifier = $v; verifierExit = ($verify | Where-Object { $_ -match '^exit=' }); unmount = $unmount
                       seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Record "write" $out
    Expect "write: mounted" $mount.mounted
    Expect "write: verifier passed" ($out.verifierExit -eq 'exit=0') (@($v.failures) -join '; ')
    Expect "write: service stopped (pool exported)" $unmount.stopped
}

if ($Phase -contains "bench") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    Fresh-Lz4 $s
    $mount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\lz4", "zc_lz4", "zc_lz4/data", "Z:", $false
    $bench = Invoke-Command $s { $ErrorActionPreference = "Continue"; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\guest-bench.ps1 -Root Z:\ 2>&1 | ForEach-Object { "$_" } }
    $unmount = Invoke-Command $s $unmountBlock
    Remove-PSSession $s
    $b = $bench | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1
    $out = [ordered]@{ variant = "usermode"; mount = $mount; bench = $(if ($b) { $b | ConvertFrom-Json } else { $bench -join "`n" }); unmount = $unmount
                       seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Record "bench" $out
    Expect "bench: completed" ([bool]$b) ($bench -join ' ')
}

if ($Phase -contains "powercut") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    Fresh-Lz4 $s
    $mount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\lz4", "zc_lz4", "zc_lz4/data", "Z:", $false
    # Acknowledgement logs live on NTFS (C:), written through.
    Invoke-Command $s {
        New-Item -ItemType Directory -Force C:\zfswin\pc, Z:\flush, Z:\wt | Out-Null
        Remove-Item C:\zfswin\pc\*.log -ErrorAction SilentlyContinue
        foreach ($mode in "flush", "writethrough") {
            $dir = if ($mode -eq "flush") { "Z:\flush" } else { "Z:\wt" }
            Start-Process C:\zfswin\tools\Zfs.MountVerifier.exe -ArgumentList "cache-writer", $dir, $mode, "C:\zfswin\pc\$mode.log" -WindowStyle Hidden
        }
    }
    Remove-PSSession $s
    Start-Sleep -Seconds 45
    $cut = Get-Date
    Stop-VM -Name $Name -TurnOff -Force
    Start-VM -Name $Name
    Start-Sleep -Seconds 30
    $s = Open-Guest
    $remount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\lz4", "zc_lz4", "zc_lz4/data", "Z:", $false
    $checks = Invoke-Command $s {
        $ErrorActionPreference = "Continue"
        foreach ($mode in "flush", "writethrough") {
            $dir = if ($mode -eq "flush") { "Z:\flush" } else { "Z:\wt" }
            $acks = @(Get-Content "C:\zfswin\pc\$mode.log" -ErrorAction SilentlyContinue).Count
            $r = (& C:\zfswin\tools\Zfs.MountVerifier.exe cache-check $dir "C:\zfswin\pc\$mode.log" durable) -join "`n" | ConvertFrom-Json
            [ordered]@{ mode = $mode; acknowledged = $acks; result = $r.result; failures = $r.failures }
        }
    }
    $unmount = Invoke-Command $s $unmountBlock
    Remove-PSSession $s
    $out = [ordered]@{ mount = $mount; cut = $cut.ToString("o"); remount = $remount; checks = $checks; unmount = $unmount
                       seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Record "powercut" $out
    Expect "powercut: remounted after the cut" $remount.mounted
    foreach ($c in $checks) {
        Expect "powercut $($c.mode): writers acknowledged files" ($c.acknowledged -gt 0)
        Expect "powercut $($c.mode): every acknowledged file intact" (@($c.failures).Count -eq 0) (@($c.failures) -join '; ')
    }
}

if ($Phase -contains "admin") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    Invoke-Command $s {
        # No leftovers from the mount-configuration phases.
        Remove-Item -Recurse -Force "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Mounts" -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force C:\zfswin\installer | Out-Null
        & sc.exe start ZfsWinUserMode | Out-Null
        foreach ($tool in "zpool.exe", "zfs.exe", "zfswin.exe", "ZfsWinBroker.exe") {
            $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$tool"
            New-Item -Force $key | Out-Null
            New-ItemProperty -Force $key -Name DumpFolder -PropertyType ExpandString -Value C:\zfswin\dumps | Out-Null
            New-ItemProperty -Force $key -Name DumpType -PropertyType DWord -Value 2 | Out-Null
        }
    }
    Copy-Item -ToSession $s (Join-Path $PSScriptRoot "guest-installer.ps1") C:\zfswin\installer\guest-installer.ps1 -Force
    Remove-PSSession $s
    $timings = [ordered]@{}
    $t = [Diagnostics.Stopwatch]::StartNew(); $cli = Guest "cli"; $timings.cli = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-cli" $cli
    Expect "admin: CLI workflow" (@($cli.failed).Count -eq 0) ($cli.failed -join ', ')
    $t.Restart(); $gui = Guest "gui"; $timings.gui = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-gui" $gui
    Expect "admin: GUI sees the pool and its datasets" ($gui.exit -eq 0 -and -not $gui.brokerError -and @($gui.pools | Where-Object name -eq "zwin").Count -eq 1 -and @($gui.datasets | Where-Object { $_.name -eq "zwin/docs" -and $_.dedup -eq "on" }).Count -eq 1) "exit $($gui.exit) $($gui.brokerError)"
    $t.Restart(); $ps = Guest "powershell"; $timings.powershell = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-powershell" $ps
    Expect "admin: PowerShell workflow" ($ps.PSObject.Properties['failed'] -and @($ps.failed).Count -eq 0) ($ps | ConvertTo-Json -Compress -Depth 4)
    $t.Restart(); $user = Guest "standard-user"; $timings.standardUser = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-standard-user" $user
    Expect "admin: standard user can read" ($user.admin -eq $false -and $user.list.exit -eq 0 -and $user.status.exit -eq 0 -and $user.datasets.exit -eq 0)
    Expect "admin: standard user cannot mutate" ($user.export.exit -eq 4 -and $user.snapshot.exit -eq 4 -and $user.cmdletExport -eq 'PermissionDenied')
    $t.Restart(); $fs = Guest "filesystem"; $timings.filesystem = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-filesystem" $fs
    Expect "admin: ACLs, streams and .zfs" (@($fs.failed).Count -eq 0) ($fs.failed -join ', ')
    $t.Restart(); $df = Guest "datafeatures"; $timings.datafeatures = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-datafeatures" $df
    Expect "admin: compression algorithms and deduplication" (@($df.failed).Count -eq 0) ($df.failed -join ', ')

    # Pools and mounts come back when the service restarts (as at boot).
    $t.Restart()
    $s = Open-Guest
    $persist = Invoke-Command $s {
        $ErrorActionPreference = "Continue"
        $z = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin\zfswin.exe"
        $restartError = try { Restart-Service ZfsWinUserMode -Force -ErrorAction Stop; $null } catch { "$_" }
        $deadline = (Get-Date).AddMinutes(3)
        do {
            Start-Sleep -Seconds 2
            $mounts = (& $z --json mount list 2>$null | ConvertFrom-Json).result.mounts
        } while (-not ($mounts | Where-Object dataset -eq 'zwin/fs') -and (Get-Date) -lt $deadline)
        $fsPath = ($mounts | Where-Object dataset -eq 'zwin/fs').mountpoint
        if ($fsPath -match '^[A-Za-z]:$') { $fsPath += '\' }
        [ordered]@{
            pools = @((& $z --json pool list 2>$null | ConvertFrom-Json).result.pools | ForEach-Object name)
            mounts = @($mounts | ForEach-Object { "$($_.dataset)=$($_.mountpoint)" })
            fileAfterRestart = $(if ($fsPath) { Get-Content -LiteralPath (Join-Path $fsPath "streams.txt") -Stream meta -ErrorAction SilentlyContinue })
            records = @(Get-ChildItem "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Pools" -ErrorAction SilentlyContinue | ForEach-Object PSChildName)
            restartError = $restartError
            stopEvent = @(Get-WinEvent -LogName Application -MaxEvents 50 -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -eq 'WinFsp' -and ($_.Properties.Value -join ' ') -match 'stop took' } | Select-Object -First 1 | ForEach-Object { $_.Properties.Value -join ' ' })
        }
    }
    Remove-PSSession $s
    $timings.restart = [math]::Round($t.Elapsed.TotalSeconds, 1)
    Record "admin-restart" $persist
    Expect "admin: service restarts cleanly" (-not $persist.restartError) $persist.restartError
    Expect "admin: pool imported again after a service restart" (@($persist.pools) -contains 'zwin') ($persist | ConvertTo-Json -Compress)
    Expect "admin: datasets mounted again after a service restart" ($persist.fileAfterRestart -eq 'metadata stream') ($persist.mounts -join ', ')

    # The kernel-written pool, if the kernel suite left one.
    $kernelPool = Join-Path $repo "TestVolumes\vm\installer"
    if (Test-Path (Join-Path $kernelPool "fs-expected.json")) {
        $s = Open-Guest
        Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\cross | Out-Null; Remove-Item C:\zfswin\cross\* -Force -ErrorAction SilentlyContinue }
        foreach ($f in "a.img", "d.img", "fs-expected.json") { Copy-Item -ToSession $s (Join-Path $kernelPool $f) "C:\zfswin\cross\$f" -Force }
        Remove-PSSession $s
        $t.Restart(); $cc = Guest "crosscheck" @{ Dir = "C:\zfswin\cross" }; $timings.crosscheck = [math]::Round($t.Elapsed.TotalSeconds, 1)
        Record "admin-crosscheck" $cc
        Expect "admin: kernel-written pool read in user mode" (@($cc.failed).Count -eq 0) ($cc.failed -join ', ')
    } else {
        Write-Warning "No kernel-written pool in $kernelPool to cross-check (run Test-InstallerInVm.ps1 first)."
    }

    # This pool, exported, for the kernel suite and for Linux.
    $exp = Guest "export"
    Expect "admin: export" ($exp.exit -eq 0) ($exp | ConvertTo-Json -Compress)
    $local = Join-Path $repo "TestVolumes\vm\crossedition-usermode"
    New-Item -ItemType Directory -Force $local | Out-Null
    $s = Open-Guest
    foreach ($f in "images\a.img", "images\d.img", "fs-expected.json", "manifest.json", "datafeatures.json") { Copy-Item -FromSession $s "C:\zfswin\installer\$f" (Join-Path $local (Split-Path -Leaf $f)) -Force }
    $dumps = Invoke-Command $s { @(Get-ChildItem C:\zfswin\dumps -ErrorAction SilentlyContinue | ForEach-Object Name) }
    Remove-PSSession $s
    Record "admin" @{ timings = $timings; dumps = $dumps; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Expect "admin: no crash dumps" (@($dumps).Count -eq 0) ($dumps -join ', ')
}

if ($Phase -contains "uninstall") {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $s = Open-Guest
    Fresh-Lz4 $s
    $mount = Invoke-Command $s $mountBlock -ArgumentList "C:\zfswin\images\lz4", "zc_lz4", "zc_lz4/data", "Z:", $false
    $out = Invoke-Command $s {
        $ErrorActionPreference = "Continue"
        Set-Content Z:\before-uninstall.txt "written before uninstall"
        $p = Start-Process msiexec.exe -ArgumentList '/x', 'C:\zfswin\um\zfswin-usermode.msi', '/qn', '/norestart', '/l*v', 'C:\zfswin\um\usermode-uninstall.log' -Wait -PassThru
        [ordered]@{
            exit = $p.ExitCode
            service = [bool](Get-Service ZfsWinUserMode -ErrorAction SilentlyContinue)
            installDir = Test-Path "$env:ProgramFiles\OpenZFS for Windows (user mode)"
            volumeGone = -not (Test-Path Z:\)
            imageKept = Test-Path C:\zfswin\images\lz4\lz4.raw
            winfspKept = Test-Path "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-x64.sys"
        }
    }
    Remove-PSSession $s
    $local = Join-Path $repo "TestVolumes\vm\uninstalled-usermode"
    New-Item -ItemType Directory -Force $local | Out-Null
    $s = Open-Guest
    Copy-Item -FromSession $s C:\zfswin\images\lz4\lz4.raw $local -Force
    Remove-PSSession $s
    $out.mount = $mount
    $out.seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    Record "uninstall" $out
    Expect "uninstall succeeded" ($out.exit -eq 0) "exit $($out.exit)"
    Expect "uninstall removed the service and files" (-not $out.service -and -not $out.installDir)
    Expect "uninstall unmounted the volume" $out.volumeGone
    Expect "uninstall kept the pool image and WinFsp" ($out.imageKept -and $out.winfspKept)
}

if ($failures.Count -gt 0) { throw "user-mode VM test failed:`n  " + ($failures -join "`n  ") }
Write-Host "User-mode VM test passed: $($Phase -join ', ')"

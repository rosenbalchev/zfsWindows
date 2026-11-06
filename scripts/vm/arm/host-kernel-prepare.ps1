# Runs on the ARM host (through Invoke-ArmHost.ps1): prepares the VM for the
# kernel edition, from the user's "fresh-install" checkpoint.  The user-mode
# state is kept as checkpoint "usermode-tested"; Secure Boot is switched off
# in this VM's firmware only (allowed by the user), test signing turned on,
# and the result kept as checkpoint "testsigning-ready".
$ErrorActionPreference = 'Stop'
$log = @()
if ((Get-VM $Guest).State -ne 'Off') { Stop-VM -Name $Guest -Force }
if (-not (Get-VMSnapshot -VMName $Guest -Name 'usermode-tested' -ErrorAction SilentlyContinue)) { Checkpoint-VM -Name $Guest -SnapshotName 'usermode-tested'; $log += 'checkpoint usermode-tested' }
Restore-VMCheckpoint -VMName $Guest -Name 'fresh-install' -Confirm:$false
$deadline = (Get-Date).AddMinutes(2)
while ((Get-VM $Guest).State -notin 'Saved', 'Off' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if ((Get-VM $Guest).State -eq 'Saved') { Remove-VMSavedState -VMName $Guest; $log += 'discarded fresh-install saved state (cold boot)' }
Set-VMFirmware -VMName $Guest -EnableSecureBoot Off
$log += 'secure boot off: ' + ((Get-VMFirmware -VMName $Guest).SecureBoot)
Start-VM -Name $Guest
Start-Sleep -Seconds 30
$s = Open-ArmGuest 10
Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\installer, C:\zfswin\dumps | Out-Null }
foreach ($f in 'OpenZFS-for-Windows-1.0.11-arm64-testsigned.msi', 'zfswindows-test.cer', 'guest-installer.ps1') { Copy-Item -ToSession $s "C:\zfswin-arm\stage\$f" "C:\zfswin\installer\$f" -Force }
Copy-Item -ToSession $s C:\zfswin-arm\stage\windowsdesktop-runtime-10.0.14-win-arm64.exe C:\zfswin\installer\dotnet-desktop-runtime.exe -Force
$prep = Invoke-Command $s {
    $ErrorActionPreference = 'Continue'
    $ts = (& bcdedit /set testsigning on 2>&1 | Out-String).Trim()
    & bcdedit /debug off | Out-Null
    Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl -Name NMICrashDump -Value 1 -Type DWord
    Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl -Name CrashDumpEnabled -Value 2 -Type DWord
    foreach ($tool in "zpool.exe", "zfs.exe", "zfswin.exe", "ZfsWinBroker.exe") {
        $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$tool"
        New-Item -Force $key | Out-Null
        New-ItemProperty -Force $key -Name DumpFolder -PropertyType ExpandString -Value C:\zfswin\dumps | Out-Null
        New-ItemProperty -Force $key -Name DumpType -PropertyType DWord -Value 2 | Out-Null
    }
    $rt = Start-Process C:\zfswin\installer\dotnet-desktop-runtime.exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    [ordered]@{ testsigning = $ts; runtimeExit = $rt.ExitCode }
}
$log += "bcdedit: $($prep.testsigning); runtime exit $($prep.runtimeExit)"
Invoke-Command $s { Restart-Computer -Force }
Remove-PSSession $s
Start-Sleep -Seconds 45
$s = Open-ArmGuest 10
$after = Invoke-Command $s {
    [ordered]@{
        secureBoot = $(try { Confirm-SecureBootUEFI } catch { "n/a: $($_.Exception.Message)" })
        testSigning = [bool]((& bcdedit /enum '{current}' | Out-String) -match 'testsigning\s+Yes')
        files = @(Get-ChildItem C:\zfswin\installer | ForEach-Object Name)
        dotnet = @(& dotnet --list-runtimes 2>$null)
    }
}
Remove-PSSession $s
$log += "after reboot: secureBoot $($after.secureBoot), testSigning $($after.testSigning), files $($after.files -join ','), runtimes $($after.dotnet -join '; ')"
if ($after.testSigning) {
    Stop-VM -Name $Guest
    Checkpoint-VM -Name $Guest -SnapshotName 'testsigning-ready'
    $log += 'checkpoint testsigning-ready'
    Start-VM -Name $Guest
    Start-Sleep -Seconds 30
}
$log += 'checkpoints: ' + ((Get-VMSnapshot -VMName $Guest | ForEach-Object Name) -join ', ')
$log

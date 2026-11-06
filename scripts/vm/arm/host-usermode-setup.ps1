# Runs on the ARM host (through Invoke-ArmHost.ps1): installs the ARM64
# user-mode edition in the VM the way Test-UserModeInVm.ps1 -Phase setup
# does on x64 (refusal without WinFsp first), with Secure Boot on.
# Expects the staged files in C:\zfswin-arm\stage.
$s = Open-ArmGuest
Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\um, C:\zfswin\installer, C:\zfswin\dumps | Out-Null }
Copy-Item -ToSession $s C:\zfswin-arm\stage\OpenZFS-for-Windows-1.0.11-arm64-usermode.msi C:\zfswin\um\zfswin-usermode.msi -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\winfsp-2.1.25156.msi C:\zfswin\um\winfsp.msi -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\windowsdesktop-runtime-10.0.14-win-arm64.exe C:\zfswin\um\dotnet-desktop-runtime.exe -Force
Copy-Item -ToSession $s C:\zfswin-arm\stage\guest-installer.ps1 C:\zfswin\installer\guest-installer.ps1 -Force
$out = Invoke-Command $s {
    $ErrorActionPreference = "Continue"
    function Msi($package, $log, $ui = '/qn') {
        $p = Start-Process msiexec.exe -ArgumentList '/i', $package, $ui, '/norestart', '/l*v', $log -Wait -PassThru
        [ordered]@{ exit = $p.ExitCode; refused = @(Get-Content $log -ErrorAction SilentlyContinue | Select-String 'REFUSED:' | ForEach-Object { ($_.Line -replace '^.*REFUSED:', 'REFUSED:').Trim() } | Select-Object -Unique) }
    }
    $withoutWinFsp = Msi 'C:\zfswin\um\zfswin-usermode.msi' 'C:\zfswin\um\usermode-nowinfsp.log'
    $serviceAfterRefusal = [bool](Get-Service ZfsWinUserMode -ErrorAction SilentlyContinue)
    $winfspInstall = Msi 'C:\zfswin\um\winfsp.msi' 'C:\zfswin\um\winfsp-install.log'
    $install = Msi 'C:\zfswin\um\zfswin-usermode.msi' 'C:\zfswin\um\usermode-install.log'
    $rt = Start-Process C:\zfswin\um\dotnet-desktop-runtime.exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    $bin = "$env:ProgramFiles\OpenZFS for Windows (user mode)\bin"
    $gp = Start-Process "$bin\ZfsWinGui.exe" -ArgumentList '--self-check', 'C:\zfswin\um\gui-selfcheck.json' -Wait -PassThru
    $guiReport = if (Test-Path C:\zfswin\um\gui-selfcheck.json) { Get-Content C:\zfswin\um\gui-selfcheck.json -Raw | ConvertFrom-Json } else { $null }
    foreach ($tool in "zfswin-um.exe", "zpool.exe", "zfs.exe", "zfswin.exe", "ZfsWinBroker.exe") {
        $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$tool"
        New-Item -Force $key | Out-Null
        New-ItemProperty -Force $key -Name DumpFolder -PropertyType ExpandString -Value C:\zfswin\dumps | Out-Null
        New-ItemProperty -Force $key -Name DumpType -PropertyType DWord -Value 2 | Out-Null
    }
    $svc = Get-CimInstance Win32_Service -Filter "Name='ZfsWinUserMode'"
    $um = "$bin\zfswin-um.exe"
    $machine = if (Test-Path $um) { $b = [IO.File]::ReadAllBytes($um); $pe = [BitConverter]::ToInt32($b, 0x3c); '0x{0:X4}' -f [BitConverter]::ToUInt16($b, $pe + 4) }
    [ordered]@{
        os = (Get-CimInstance Win32_OperatingSystem).Caption + " " + [Environment]::OSVersion.Version + " " + $env:PROCESSOR_ARCHITECTURE
        secureBoot = (Confirm-SecureBootUEFI)
        testSigning = [bool]((& bcdedit /enum '{current}' | Out-String) -match 'testsigning\s+Yes')
        withoutWinFsp = $withoutWinFsp; serviceAfterRefusal = $serviceAfterRefusal
        winfspExit = $winfspInstall.exit
        winfspDrivers = @(Get-ChildItem "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-*.sys" -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) $((Get-AuthenticodeSignature $_.FullName).Status)" })
        winfspService = ((& sc.exe query WinFsp.Launcher) -match 'STATE' | Out-String).Trim()
        installExit = $install.exit; installRefused = $install.refused; runtimeExit = $rt.ExitCode
        engineMachine = $machine
        engineVersion = $(if (Test-Path $um) { (Get-Item $um).VersionInfo.FileVersion })
        engineSignature = $(if (Test-Path $um) { (Get-AuthenticodeSignature $um).Status.ToString() })
        service = $(if ($svc) { "$($svc.StartMode) $($svc.StartName) $($svc.State)" })
        gui = [ordered]@{ exit = $gp.ExitCode; version = $guiReport.version; broker = [bool]$guiReport.broker; brokerError = $guiReport.brokerError; userModeService = $guiReport.userModeService }
        zfsVersion = (& "$bin\zfs.exe" version 2>&1 | ForEach-Object { "$_" }) -join ' | '
        dumps = @(Get-ChildItem C:\zfswin\dumps -ErrorAction SilentlyContinue | ForEach-Object Name)
    }
}
Remove-PSSession $s
$out | ConvertTo-Json -Depth 5

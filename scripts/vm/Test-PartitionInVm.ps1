[CmdletBinding()]
<#
.SYNOPSIS
Issue #48: pools on single partitions of a disk, in both editions, inside
the isolated driver VM.  Never targets this host.

.DESCRIPTION
From the "fixtures" checkpoint, with a 12 GB disk added to the VM:

  P: NTFS | T1 | Q: NTFS | T2 | T3 | T4 | R: NTFS   (GPT, 256 MiB file on
  each NTFS; T2 has the ZFS partition type, T4 is larger)

 1. kernel edition: every spelling of T1 (\\?\HarddiskNPartitionM,
    PhysicalDriveN/PartitionM, ...) resolves to the same extent
    (zpool create -n); partition 0, a missing partition, an NTFS partition
    and the whole disk are refused, by zpool and by zfswin;
 2. kernel: pool kp1 on T1 by zpool.exe (\\?\HarddiskNPartitionM, the
    issue's command), kp2 on T2 by zfswin (PhysicalDiskN/PartitionM);
    each recorded as its extent, no larger than its partition;
 3. kernel: export and import, broker restart, Windows restart; kp2
    replaced onto T4 (zpool replace <guid> \\?\HarddiskNPartitionM);
 4. switch to the user-mode edition: the kernel's pools imported, scrubbed
    clean; the same resolution checks; pool up3 on T3 by New-ZfsPool; engine
    restart and Windows restart;
 5. switch back to the kernel edition: all three pools imported and clean.

After every step the NTFS partitions' files, file systems and the partition
table are checked.  Each step is recorded in TestVolumes\vm\results.jsonl;
the script throws at the end if any check failed.
#>
param(
    [string]$Name = "zfsTest",
    # out\release\<version>; default: the current build number.
    [string]$Version = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ($state.user, (ConvertTo-SecureString $state.password -AsPlainText -Force))
$results = Join-Path $repo "TestVolumes\vm\results.jsonl"
$version = if ($Version) { $Version } else { & (Join-Path $repo "scripts\Get-ZfsWinVersion.ps1") }
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$release = Join-Path $repo "out\release\$version"
$kernelMsi = Join-Path $release "OpenZFS-for-Windows-$version-x64-testsigned$tag.msi"
$userMsi = Join-Path $release "OpenZFS-for-Windows-$version-x64-usermode$tag.msi"
$winfsp = Join-Path $repo "third_party\winfsp\download\winfsp-2.1.25156.msi"
foreach ($f in $kernelMsi, $userMsi, $winfsp) { if (-not (Test-Path $f)) { throw "Missing $f." } }
$guestDir = "C:\zfswin\partition"
$guestKernelMsi = "$guestDir\$(Split-Path -Leaf $kernelMsi)"
$guestUserMsi = "$guestDir\$(Split-Path -Leaf $userMsi)"
$vhd = Join-Path $repo "TestVolumes\vm\partition-disk.vhdx"
$failures = [Collections.Generic.List[string]]::new()

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try { return New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop }
        catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 30
        }
    }
}

function Record($phase, $data) {
    $line = [ordered]@{ time = (Get-Date).ToString("o"); phase = "partition-$phase"; data = $data } | ConvertTo-Json -Depth 10 -Compress
    Add-Content -Path $results -Value $line
    $failed = @($data.failed | Where-Object { $_ })
    Write-Host "[$phase] $(if ($failed.Count) { 'FAILED: ' + ($failed -join ', ') } else { 'ok' })"
    foreach ($f in $failed) { $failures.Add("${phase}: $f = $($data.$f.value) (expected $($data.$f.expected))") }
}

function Guest($stage, [hashtable]$arguments = @{}) {
    $s = Open-Guest
    try {
        $json = Invoke-Command $s {
            param($stage, $arguments)
            $ErrorActionPreference = "Continue"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($arguments | ConvertTo-Json -Compress -Depth 6)))
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\partition\guest-partition.ps1 -Stage $stage -ArgumentsBase64 $encoded 2>$null
        } -ArgumentList $stage, $arguments
    } finally { Remove-PSSession $s }
    $text = ($json | Where-Object { "$_".StartsWith('{') } | Select-Object -Last 1)
    if (-not $text) { throw "guest stage $stage printed no JSON: $($json | Out-String)" }
    $text | ConvertFrom-Json
}

function Step($phase, $stage, [hashtable]$arguments = @{}) {
    $r = Guest $stage $arguments
    Record $phase $r
    $r
}

function Restart-Guest {
    $s = Open-Guest
    $before = Invoke-Command $s { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
    Invoke-Command $s { Restart-Computer -Force }
    Remove-PSSession $s -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddMinutes(10)
    do {
        Start-Sleep -Seconds 15
        $boot = $null
        try { $s = New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop; $boot = Invoke-Command $s { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }; Remove-PSSession $s } catch {}
    } while ((-not $boot -or $boot -eq $before) -and (Get-Date) -lt $deadline)
    if (-not $boot -or $boot -eq $before) { throw "The guest did not restart." }
    # Services and the boot-time import settle.
    Start-Sleep -Seconds 45
}

# --- 0. fresh guest, test disk, kernel edition --------------------------------------------
Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
Restore-VMCheckpoint -VMName $Name -Name "fixtures" -Confirm:$false
Get-VMHardDiskDrive -VMName $Name | Where-Object Path -eq $vhd | Remove-VMHardDiskDrive
Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue
New-VHD -Path $vhd -SizeBytes 12GB -Dynamic | Out-Null
# A disk of the VM, not a guest-attached VHDX: it must come back after a restart.
Add-VMHardDiskDrive -VMName $Name -ControllerType SCSI -Path $vhd
Start-VM -Name $Name
$s = Open-Guest
Invoke-Command $s {
    New-Item -ItemType Directory -Force C:\zfswin\partition | Out-Null
    & bcdedit /debug off | Out-Null
    $wu = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
    New-Item -Force $wu | Out-Null
    $until = (Get-Date).AddDays(7).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($n in 'PauseUpdatesExpiryTime', 'PauseFeatureUpdatesEndTime', 'PauseQualityUpdatesEndTime') { Set-ItemProperty $wu -Name $n -Value $until }
}
foreach ($f in $kernelMsi, $userMsi, $winfsp, (Join-Path $release "zfswindows-test.cer"), (Join-Path $PSScriptRoot "guest-partition.ps1")) {
    Copy-Item -ToSession $s -Path $f -Destination "$guestDir\$(Split-Path -Leaf $f)" -Force
}
Invoke-Command $s { foreach ($store in "Root", "TrustedPublisher") { & certutil.exe -f -addstore $store C:\zfswin\partition\zfswindows-test.cer | Out-Null } }
Remove-PSSession $s

$layout = Step "layout" "layout"
$disk = $layout.disk
$parts = $layout.partitions
$hashes = $layout.hashes
function Neighbors($phase) { Step "$phase-neighbors" "neighbors" @{ Disk = $disk; Layout = $layout.layout; Hashes = $hashes } | Out-Null }
function Resolve($phase, $edition) {
    Step "$phase-resolve" "resolve" @{ Edition = $edition; Disk = $disk; Partition = $parts.T1.number; Offset = $parts.T1.offset; Length = $parts.T1.size; NtfsPartition = $parts.P.number } | Out-Null
}

Step "kernel-install" "msi" @{ Msi = $guestKernelMsi; Action = "install" } | Out-Null

# --- 1-2. kernel: resolution, two pools ------------------------------------------------------------
Resolve "kernel" "kernel"
$kp1 = Step "kernel-create-kp1" "create" @{ Edition = "kernel"; Via = "zpool"; Pool = "kp1"; Device = "\\?\Harddisk${disk}Partition$($parts.T1.number)"; Disk = $disk; Number = $parts.T1.number; Offset = $parts.T1.offset; Length = $parts.T1.size; Seed = 1 }
$kp2 = Step "kernel-create-kp2" "create" @{ Edition = "kernel"; Via = "zfswin"; Pool = "kp2"; Device = "PhysicalDisk$disk/Partition$($parts.T2.number)"; Disk = $disk; Number = $parts.T2.number; Offset = $parts.T2.offset; Length = $parts.T2.size; Seed = 2 }
$kernelPools = @(@{ Name = "kp1"; Hash = $kp1.hash }, @{ Name = "kp2"; Hash = $kp2.hash })
Neighbors "kernel-created"

# --- 3. kernel: export/import, broker restart, Windows restart ------------------------------------
Step "kernel-export" "export" @{ Edition = "kernel"; Pools = @("kp1", "kp2") } | Out-Null
Step "kernel-import" "verify" @{ Edition = "kernel"; Pools = $kernelPools } | Out-Null
Step "kernel-restart" "restart" @{ Edition = "kernel"; Pools = @("kp1", "kp2") } | Out-Null
Restart-Guest
Step "kernel-rebooted" "restart" @{ Edition = "kernel"; Pools = @("kp1", "kp2") } | Out-Null
Step "kernel-after-reboot" "verify" @{ Edition = "kernel"; Pools = $kernelPools } | Out-Null
Step "kernel-replace" "replace" @{ Edition = "kernel"; Pool = "kp2"; Device = "\\?\Harddisk${disk}Partition$($parts.T4.number)"; Disk = $disk; Number = $parts.T4.number; Offset = $parts.T4.offset; Length = $parts.T4.size } | Out-Null
Step "kernel-after-replace" "verify" @{ Edition = "kernel"; Pools = $kernelPools } | Out-Null
Neighbors "kernel-done"

# --- 4. user mode -------------------------------------------------------------------------------------
Step "kernel-export-for-switch" "export" @{ Edition = "kernel"; Pools = @("kp1", "kp2") } | Out-Null
Step "kernel-uninstall" "msi" @{ Msi = $guestKernelMsi; Action = "uninstall" } | Out-Null
Restart-Guest
Step "winfsp-install" "msi" @{ Msi = "$guestDir\$(Split-Path -Leaf $winfsp)"; Action = "install" } | Out-Null
Step "usermode-install" "msi" @{ Msi = $guestUserMsi; Action = "install" } | Out-Null
Step "usermode-import-kernel-pools" "verify" @{ Edition = "usermode"; Pools = $kernelPools } | Out-Null
Step "usermode-export-kp" "export" @{ Edition = "usermode"; Pools = @("kp1", "kp2") } | Out-Null
Resolve "usermode" "usermode"
$up3 = Step "usermode-create-up3" "create" @{ Edition = "usermode"; Via = "powershell"; Pool = "up3"; Device = "PhysicalDrive$disk/Partition$($parts.T3.number)"; Disk = $disk; Number = $parts.T3.number; Offset = $parts.T3.offset; Length = $parts.T3.size; Seed = 3 }
$allPools = $kernelPools + @(@{ Name = "up3"; Hash = $up3.hash })
Step "usermode-import-all" "verify" @{ Edition = "usermode"; Pools = $allPools } | Out-Null
Step "usermode-restart" "restart" @{ Edition = "usermode"; Pools = @("kp1", "kp2", "up3") } | Out-Null
Restart-Guest
Step "usermode-rebooted" "restart" @{ Edition = "usermode"; Pools = @("kp1", "kp2", "up3") } | Out-Null
Step "usermode-after-reboot" "verify" @{ Edition = "usermode"; Pools = $allPools } | Out-Null
Neighbors "usermode-done"

# --- 5. back to the kernel edition ---------------------------------------------------------------
Step "usermode-export-for-switch" "export" @{ Edition = "usermode"; Pools = @("kp1", "kp2", "up3") } | Out-Null
Step "usermode-uninstall" "msi" @{ Msi = $guestUserMsi; Action = "uninstall" } | Out-Null
Restart-Guest
Step "kernel-reinstall" "msi" @{ Msi = $guestKernelMsi; Action = "install" } | Out-Null
Step "kernel-import-all" "verify" @{ Edition = "kernel"; Pools = $allPools } | Out-Null
Neighbors "final"

Stop-VM -Name $Name -TurnOff -Force
Get-VMHardDiskDrive -VMName $Name | Where-Object Path -eq $vhd | Remove-VMHardDiskDrive
Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) { throw "partition VM test failed:`n  " + ($failures -join "`n  ") }
Write-Host "Partition VM test passed ($version $Configuration)."

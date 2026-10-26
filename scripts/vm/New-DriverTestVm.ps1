#Requires -RunAsAdministrator
<#
.SYNOPSIS
Creates the isolated Hyper-V VM in which the experimental OpenZFS kernel
driver is tested (plan step 12). The driver is never loaded on the host.

.DESCRIPTION
One elevated run:
 * creates an Internal switch (host 10.77.0.1, VM 10.77.0.2) with no route
   to the LAN or the internet;
 * applies Windows 11 Pro from the ISO directly into a new VHDX (no Setup
   UI), with an unattend file that creates a local administrator, assigns
   the static address and enables PowerShell remoting;
 * enables test signing and KDNET kernel debugging to the host in the VM's
   BCD store, and disables Secure Boot (required for test signing);
 * configures the host side: WinRM TrustedHosts for the VM address and a
   firewall rule for the kernel debugger port on the internal switch only;
 * boots the VM, waits until remoting answers, and takes the checkpoint
   "clean" so a crashed or corrupted guest can be reverted.

Credentials and the KDNET key are written to TestVolumes\vm (git-ignored).
With -GrantHyperVAdmin the current user is added to "Hyper-V
Administrators" (effective after the next sign-in), so later test runs can
revert checkpoints without elevation.

Nothing is overwritten: the script stops if the VM or its disk exists.
#>
[CmdletBinding()]
param(
    [string]$Name = "zfswin-driver-test",
    [string]$IsoPath = "D:\Install\en-us_windows_11_business_editions_version_24h2_updated_nov_2024_x64_dvd_81b35a62.iso",
    [string]$Edition = "Windows 11 Pro",
    [string]$VmRoot = "D:\VMs",
    [string]$SwitchName = "zfswin-test",
    [string]$HostIp = "10.77.0.1",
    [string]$VmIp = "10.77.0.2",
    [int]$MemoryGB = 8,
    [int]$ProcessorCount = 4,
    [int]$DiskGB = 80,
    [int]$KdPort = 50000,
    [switch]$GrantHyperVAdmin
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stateDir = Join-Path $repo "TestVolumes\vm"
$vmDir = Join-Path $VmRoot $Name
$vhd = Join-Path $vmDir "$Name.vhdx"

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "VM '$Name' already exists." }
if (Test-Path $vhd) { throw "$vhd already exists." }
if (-not (Test-Path $IsoPath)) { throw "ISO not found: $IsoPath" }
New-Item -ItemType Directory -Force -Path $vmDir, $stateDir | Out-Null

function New-Secret([int]$length) {
    $chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $bytes = New-Object byte[] $length
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# --- network: an internal switch only the host and the VM share ----------
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
    New-VMSwitch -Name $SwitchName -SwitchType Internal | Out-Null
}
$hostAdapter = Get-NetAdapter -Name "vEthernet ($SwitchName)"
if (-not (Get-NetIPAddress -InterfaceIndex $hostAdapter.ifIndex -IPAddress $HostIp -ErrorAction SilentlyContinue)) {
    New-NetIPAddress -InterfaceIndex $hostAdapter.ifIndex -IPAddress $HostIp -PrefixLength 24 | Out-Null
}

# --- secrets ----------------------------------------------------------------
$user = "zfstest"
$password = New-Secret 20
$kdKey = (1..4 | ForEach-Object { New-Secret 8 }) -join '.'
@{ vm = $Name; address = $VmIp; user = $user; password = $password
   kd_host = $HostIp; kd_port = $KdPort; kd_key = $kdKey } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $stateDir "$Name.json")

# --- disk: apply Windows directly ------------------------------------------
$iso = Mount-DiskImage -ImagePath $IsoPath -PassThru
try {
    $isoRoot = ($iso | Get-Volume).DriveLetter + ":\sources"
    $wim = @("install.wim", "install.esd") | ForEach-Object { Join-Path $isoRoot $_ } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    $index = (Get-WindowsImage -ImagePath $wim | Where-Object ImageName -eq $Edition).ImageIndex
    if (-not $index) { throw "Edition '$Edition' not found in $wim." }

    New-VHD -Path $vhd -SizeBytes ($DiskGB * 1GB) -Dynamic | Out-Null
    $disk = Mount-VHD -Path $vhd -Passthru | Get-Disk
    try {
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $efi = New-Partition -DiskNumber $disk.Number -Size 260MB `
            -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter
        Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel System | Out-Null
        New-Partition -DiskNumber $disk.Number -Size 16MB `
            -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
        $os = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        Format-Volume -Partition $os -FileSystem NTFS -NewFileSystemLabel Windows | Out-Null
        $S = "$($efi.DriveLetter):"
        $W = "$($os.DriveLetter):"

        Write-Host "Applying $Edition (index $index) ..."
        Expand-WindowsImage -ImagePath $wim -Index $index -ApplyPath "$W\" | Out-Null
        & bcdboot "$W\Windows" /s $S /f UEFI | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "bcdboot failed" }
        $bcd = "$S\EFI\Microsoft\Boot\BCD"
        foreach ($bcdArgs in @(
                @('/set', '{default}', 'testsigning', 'on'),
                @('/set', '{default}', 'debug', 'on'),
                @('/dbgsettings', 'net', "hostip:$HostIp", "port:$KdPort", "key:$kdKey"))) {
            & bcdedit /store $bcd @bcdArgs | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "bcdedit $bcdArgs failed" }
        }

        $unattend = Get-Content (Join-Path $PSScriptRoot "unattend.xml.template") -Raw
        $unattend = $unattend.Replace('@USER@', $user).Replace('@PASSWORD@', $password)
        $unattend = $unattend.Replace('@VMIP@', $VmIp).Replace('@HOSTIP@', $HostIp)
        New-Item -ItemType Directory -Force -Path "$W\Windows\Panther" | Out-Null
        Set-Content -Encoding UTF8 -Path "$W\Windows\Panther\unattend.xml" -Value $unattend
    } finally {
        Dismount-VHD -Path $vhd
    }
} finally {
    Dismount-DiskImage -ImagePath $IsoPath | Out-Null
}

# --- VM ---------------------------------------------------------------------
New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
    -VHDPath $vhd -SwitchName $SwitchName -Path $VmRoot | Out-Null
Set-VM -Name $Name -ProcessorCount $ProcessorCount -StaticMemory `
    -AutomaticCheckpointsEnabled $false -CheckpointType Standard
Set-VMFirmware -VMName $Name -EnableSecureBoot Off

# --- host side, first boot and the clean checkpoint -------------------------
& (Join-Path $PSScriptRoot "Complete-DriverTestVm.ps1") -Name $Name -SwitchName $SwitchName `
    -GrantHyperVAdmin:$GrantHyperVAdmin

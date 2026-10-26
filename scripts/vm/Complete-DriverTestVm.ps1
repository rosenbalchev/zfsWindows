#Requires -RunAsAdministrator
<#
.SYNOPSIS
Host-side setup and first-boot completion for the driver test VM. Called by
New-DriverTestVm.ps1; safe to run again on its own (every step is
idempotent), e.g. after a failure in that script.

.DESCRIPTION
 * WinRM TrustedHosts for the VM address. The WSMan provider needs the
   WinRM service running; if it is stopped, it is started only for this
   change and then returned to its previous state (the client reads the
   setting from the registry, so remoting works with the service stopped).
 * A firewall rule for the KDNET port on the internal switch only.
 * Optionally (-GrantHyperVAdmin) membership of "Hyper-V Administrators".
 * Starts the VM if needed, waits until the guest's unattended setup has
   finished, then takes the checkpoint "clean" (once) and restarts it.
#>
[CmdletBinding()]
param(
    [string]$Name = "zfswin-driver-test",
    [string]$SwitchName = "zfswin-test",
    [switch]$GrantHyperVAdmin
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$VmIp = $state.address

# --- WinRM client: trust the VM's address -----------------------------------
$winrm = Get-Service WinRM
$wasRunning = $winrm.Status -eq 'Running'
if (-not $wasRunning) { Start-Service WinRM }
try {
    $trusted = (Get-Item WSMan:\localhost\Client\TrustedHosts).Value
    if ($trusted -notmatch [regex]::Escape($VmIp)) {
        $value = (@($trusted, $VmIp) | Where-Object { $_ }) -join ','
        Set-Item WSMan:\localhost\Client\TrustedHosts -Value $value -Force
    }
} finally {
    if (-not $wasRunning) { Stop-Service WinRM }
}

# --- kernel debugger port, on the internal switch only ------------------------
if (-not (Get-NetFirewallRule -DisplayName "zfswin KDNET" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "zfswin KDNET" -Direction Inbound -Protocol UDP `
        -LocalPort $state.kd_port -InterfaceAlias "vEthernet ($SwitchName)" -Action Allow | Out-Null
}

if ($GrantHyperVAdmin) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $group = (Get-LocalGroup -SID 'S-1-5-32-578').Name	# Hyper-V Administrators
    if (-not (Get-LocalGroupMember -Group $group | Where-Object Name -eq $me)) {
        Add-LocalGroupMember -Group $group -Member $me
        Write-Host "Added $me to '$group' (sign out and back in to use it)."
    }
}

# --- first boot and the clean checkpoint ---------------------------------------
if (Get-VMSnapshot -VMName $Name -Name "clean" -ErrorAction SilentlyContinue) {
    Write-Host "Checkpoint 'clean' already exists."
    exit 0
}
if ((Get-VM -Name $Name).State -ne 'Running') { Start-VM -Name $Name }
Write-Host "Waiting for the guest's unattended setup to finish ..."
$credential = New-Object PSCredential ("$VmIp\$($state.user)",
    (ConvertTo-SecureString $state.password -AsPlainText -Force))
$deadline = (Get-Date).AddMinutes(45)
do {
    $ready = $false
    try {
        $ready = Invoke-Command -ComputerName $VmIp -Credential $credential `
            -ScriptBlock { Test-Path C:\zfswin-ready.txt } -ErrorAction Stop
    } catch { }
    if (-not $ready) { Start-Sleep -Seconds 20 }
} until ($ready -or (Get-Date) -gt $deadline)
if (-not $ready) { throw "The guest did not become reachable at $VmIp." }

Stop-VM -Name $Name
Checkpoint-VM -Name $Name -SnapshotName "clean"
Start-VM -Name $Name
Write-Host "VM '$Name' is ready at $VmIp with checkpoint 'clean'."

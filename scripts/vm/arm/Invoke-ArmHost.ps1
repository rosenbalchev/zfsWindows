<#
.SYNOPSIS
Runs PowerShell on the ARM test PC (the Hyper-V host of ZFSTestOnArm) over SSH.

.DESCRIPTION
The ARM PC is reached only over SSH with its own key and pinned host key
(TestVolumes\vm\ZFSTestOnArm.json).  Its default SSH shell is cmd, whose
command line is too short for real scripts, so the script text is copied to
C:\zfswin-arm\run on the host and run with -File.  Files listed in -Upload
are copied to C:\zfswin-arm\stage first (the only place anything is put on
the host).  On the host, $Guest holds the VM name and Open-ArmGuest opens a
PowerShell Direct session into the VM; nothing is installed on the host.

  Invoke-ArmHost.ps1 -Script 'Get-VM | Format-Table'
  Invoke-ArmHost.ps1 -Upload "out\release\1.0.11\x.msi;scripts\vm\y.ps1" -Script '...'
#>
[CmdletBinding()]
param(
    [string]$Script = "",
    # A script file to run instead of -Script (text with quotes does not
    # survive powershell -File).
    [string]$ScriptFile = "",
    # Files to copy to C:\zfswin-arm\stage; ';'-separated (powershell -File
    # cannot pass arrays).
    [string]$Upload = ""
)
$ErrorActionPreference = "Stop"
if ($ScriptFile) { $Script = Get-Content -LiteralPath $ScriptFile -Raw }
if (-not $Script) { throw "Give -Script or -ScriptFile." }
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$state = Get-Content (Join-Path $repo "TestVolumes\vm\ZFSTestOnArm.json") -Raw | ConvertFrom-Json
$key = $state.hostKey -replace '^~', $env:USERPROFILE
$known = $state.hostKnownHosts -replace '^~', $env:USERPROFILE
$target = "$($state.hostUser)@$($state.hostAddress)"
$sshOptions = @("-i", $key, "-o", "UserKnownHostsFile=$known", "-o", "HostKeyAlias=$($state.hostKeyAlias)",
    "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20")

function Invoke-HostCommand([string]$command) { & ssh.exe @sshOptions $target $command }

Invoke-HostCommand "if not exist C:\zfswin-arm\run mkdir C:\zfswin-arm\run & if not exist C:\zfswin-arm\stage mkdir C:\zfswin-arm\stage" | Out-Null
foreach ($file in @($Upload -split ';' | Where-Object { $_ })) {
    & scp -q @sshOptions $file "${target}:C:/zfswin-arm/stage/$(Split-Path -Leaf $file)"
    if ($LASTEXITCODE -ne 0) { throw "scp of $file failed" }
}

# Prelude: the VM name and a PowerShell Direct session opener.
$prelude = @"
`$ErrorActionPreference = 'Continue'
`$Guest = '$($state.vmName)'
`$GuestCredential = New-Object PSCredential ('$($state.user)', (ConvertTo-SecureString '$($state.password -replace "'", "''")' -AsPlainText -Force))
function Open-ArmGuest([int]`$Minutes = 6) {
    `$deadline = (Get-Date).AddMinutes(`$Minutes)
    while (`$true) {
        try { return New-PSSession -VMName `$Guest -Credential `$GuestCredential -ErrorAction Stop }
        catch { if ((Get-Date) -gt `$deadline) { throw }; Start-Sleep -Seconds 10 }
    }
}
"@
$local = Join-Path $env:TEMP "zfsarm-job-$PID.ps1"
[IO.File]::WriteAllText($local, $prelude + "`r`n" + $Script, (New-Object Text.UTF8Encoding $true))
# A job file of its own: a long job keeps running on the host when the SSH
# connection drops, and a later call must not overwrite its script.
$job = "job-$(Get-Date -Format yyyyMMdd-HHmmss)-$PID.ps1"
try {
    & scp -q @sshOptions $local "${target}:C:/zfswin-arm/run/$job"
    if ($LASTEXITCODE -ne 0) { throw "scp of the job script failed" }
    Invoke-HostCommand "powershell -NoProfile -ExecutionPolicy Bypass -File C:\zfswin-arm\run\$job"
} finally {
    Remove-Item $local -ErrorAction SilentlyContinue
}

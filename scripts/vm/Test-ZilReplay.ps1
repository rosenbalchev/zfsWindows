<#
.SYNOPSIS
Leaves an unreplayed ZFS intent log for Linux (plan step 13).

.DESCRIPTION
Runs guest-cache.ps1 -Stage cut1-start in the driver test VM (writers in
every sync mode, a SLOG pool), powers the VM off after 45 s, boots it and
copies the pool files to TestVolumes\vm\zil-unreplayed\ WITHOUT importing
them on Windows (the pools use cachefile=none).  The ack logs come along.
scripts\vm\validate-zil-replay.py then lets Linux OpenZFS replay the logs
the Windows driver wrote, which separates a log-writing bug from a
replay bug.
#>
[CmdletBinding()]
param([string]$Name = "zfsTest", [int]$Seconds = 45)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ($state.user,
    (ConvertTo-SecureString $state.password -AsPlainText -Force))

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try { return New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop }
        catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 30 }
    }
}

$s = Open-Guest
Copy-Item -ToSession $s (Join-Path $PSScriptRoot "guest-cache.ps1") -Destination C:\zfswin\guest-cache.ps1 -Force
Copy-Item -ToSession $s (Join-Path $repo "out\driver-tools\Zfs.MountVerifier.exe") `
    -Destination C:\zfswin\tools\Zfs.MountVerifier.exe -Force
$start = Invoke-Command $s {
    $ErrorActionPreference = "Continue"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\zfswin\guest-cache.ps1 -Stage cut1-start 2>$null
}
Remove-PSSession $s
Start-Sleep -Seconds $Seconds
Stop-VM -Name $Name -TurnOff -Force
Start-VM -Name $Name
Start-Sleep -Seconds 30
$s = Open-Guest
$local = Join-Path $repo "TestVolumes\vm\zil-unreplayed"
Remove-Item -Recurse -Force $local -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $local | Out-Null
$pools = Invoke-Command $s { (& C:\zfswin\driver\zpool.exe list -H -o name 2>$null) -join ',' }
if ($pools) { throw "pools were imported after the power cut ($pools); the logs may be replayed" }
foreach ($f in "main.raw", "slogdata.raw", "slog.raw") {
    Copy-Item -FromSession $s -Path "C:\zfswin\images\cache\$f" -Destination $local -Force
}
Copy-Item -FromSession $s -Path "C:\zfswin\cache\ack-*.log" -Destination $local -Force
Remove-PSSession $s
($start -join "`n") | Set-Content (Join-Path $local "cut-start.json")
Write-Host "Unreplayed pools: $local (validate with scripts\vm\validate-zil-replay.py)"

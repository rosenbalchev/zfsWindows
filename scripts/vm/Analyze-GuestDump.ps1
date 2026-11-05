<#
.SYNOPSIS
Analyzes a kernel dump inside the driver test VM (never on the host).

.DESCRIPTION
Copies the Windows debugger kit, the host's symbol cache and the driver
PDB into the guest once, then runs `kd -z` there with the given commands
and returns the output.  The guest has no Internet access, so symbols
come only from TestVolumes\vm\symcache and the driver build.

  Analyze-GuestDump.ps1 -Dump C:\Windows\MEMORY.DMP -Commands "!analyze -v; kn 40"
#>
[CmdletBinding()]
param(
    [string]$Dump = "C:\Windows\MEMORY.DMP",
    [string]$Commands = "!analyze -v; kn 40",
    [string]$Name = "zfsTest"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ($state.user,
    (ConvertTo-SecureString $state.password -AsPlainText -Force))
# PowerShell Direct: works whatever the state of the guest network.
$s = New-PSSession -VMName $Name -Credential $credential
try {
    $hasKit = Invoke-Command $s { Test-Path C:\zfswin\dbg\kd.exe }
    if (-not $hasKit) {
        Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\dbg, C:\zfswin\sym | Out-Null }
        Copy-Item -ToSession $s -Path "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\*" `
            -Destination C:\zfswin\dbg -Recurse -Force
        Copy-Item -ToSession $s -Path (Join-Path $repo "TestVolumes\vm\symcache\*") `
            -Destination C:\zfswin\sym -Recurse -Force
    }
    Copy-Item -ToSession $s -Path (Join-Path $repo "out\openzfs-driver-debug\module\os\windows\driver\OpenZFS.pdb") `
        -Destination C:\zfswin\sym\OpenZFS.pdb -Force
    Invoke-Command $s {
        param($dump, $commands)
        # -i: the installed driver image, so kd can find its PDB by name.
        & C:\zfswin\dbg\kd.exe -z $dump -y "C:\zfswin\sym" -i "C:\zfswin\driver" `
            -c ".symopt+0x40; .reload /f OpenZFS.sys; $commands; q" 2>&1 |
            Where-Object { "$_" -notmatch 'NatVis' } | ForEach-Object { "$_" }
    } -ArgumentList $Dump, $Commands
} finally {
    Remove-PSSession $s
}

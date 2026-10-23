<#
.SYNOPSIS
Runs the OpenZFS kernel driver tests inside the isolated VM created by
New-DriverTestVm.ps1 (plan step 12).  Runs unelevated on the host; all
driver activity happens in the guest over PowerShell remoting.

.DESCRIPTION
Phases (default: all, in order):
  install  copy out\driver-package, trust the test certificate in the guest,
           install the driver with zfsinstaller, enable Driver Verifier
           (standard checks) for OpenZFS.sys, and reboot the guest.
  read     copy the genuine 5 GiB single-disk fixture, import it read-only
           through the driver, verify it with Zfs.MountVerifier (standard
           .NET file APIs) against tests\fixtures\single-disk.manifest.json,
           export it, and prove the image SHA-256 is unchanged.
  write    copy the Linux-created lz4 compression fixture, import it
           read-write, verify it, write/append/rename/delete through the
           driver, export, and copy the image back to
           TestVolumes\vm\written\ for upstream validation on Linux.

Results are appended to TestVolumes\vm\results.jsonl.
#>
[CmdletBinding()]
param(
    [ValidateSet("all", "install", "read", "write")]
    [string[]]$Phase = @("all"),
    [string]$Name = "zfswin-driver-test"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ("$($state.address)\$($state.user)",
    (ConvertTo-SecureString $state.password -AsPlainText -Force))
$results = Join-Path $repo "TestVolumes\vm\results.jsonl"
if ($Phase -contains "all") { $Phase = @("install", "read", "write") }

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try {
            return New-PSSession -ComputerName $state.address -Credential $credential `
                -SessionOption (New-PSSessionOption -OperationTimeout 3600000) -ErrorAction Stop
        } catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 10
        }
    }
}

function Record($phase, $data) {
    $line = [ordered]@{ time = (Get-Date).ToString("o"); phase = $phase; data = $data } |
        ConvertTo-Json -Depth 8 -Compress
    Add-Content -Path $results -Value $line
    Write-Host "[$phase] $(($data | ConvertTo-Json -Depth 8))"
}

function Copy-ToGuest($session, $source, $destination) {
    Invoke-Command $session { param($d) New-Item -ItemType Directory -Force -Path $d | Out-Null } `
        -ArgumentList (Split-Path -Parent $destination)
    Copy-Item -ToSession $session -Path $source -Destination $destination -Recurse -Force
}

# Mount path of a dataset as the driver presents it.
$findMount = {
    param($dataset)
    $zfs = "C:\zfswin\driver\zfs.exe"
    $line = & $zfs mount | Where-Object { $_ -match "^\s*$([regex]::Escape($dataset))\s+(.+)$" } |
        Select-Object -First 1
    if (-not $line) { throw "dataset $dataset is not mounted: $(& $zfs mount | Out-String)" }
    $path = ($line -replace "^\s*$([regex]::Escape($dataset))\s+", '').Trim()
    if ($path -match '^[A-Za-z]:$') { $path += '\' }
    $path
}

if ($Phase -contains "install") {
    $s = Open-Guest
    Copy-ToGuest $s (Join-Path $repo "out\driver-package") "C:\zfswin\driver"
    Copy-ToGuest $s (Join-Path $repo "out\driver-tools") "C:\zfswin\tools"
    $out = Invoke-Command $s {
        $ErrorActionPreference = "Stop"
        Set-Location C:\zfswin\driver
        Import-Certificate .\zfswindows-test.cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate .\zfswindows-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
        $install = & .\zfsinstaller.exe install .\OpenZFS.inf .\OpenZVOL.inf 2>&1 | Out-String
        $verifier = & verifier.exe /standard /driver OpenZFS.sys 2>&1 | Out-String
        [ordered]@{
            testsigning = (& bcdedit /enum '{current}' | Select-String testsigning).ToString().Trim()
            install = $install.Trim(); verifier = $verifier.Trim()
        }
    }
    Record "install" $out
    Invoke-Command $s { Restart-Computer -Force }
    Remove-PSSession $s
    Start-Sleep -Seconds 30
    $s = Open-Guest
    $out = Invoke-Command $s {
        [ordered]@{
            service = (& sc.exe query OpenZFS | Out-String).Trim()
            verifier = (& verifier.exe /querysettings | Out-String).Trim()
            zpool = (& C:\zfswin\driver\zpool.exe status 2>&1 | Out-String).Trim()
        }
    }
    Record "install-after-reboot" $out
    if ($out.verifier -notmatch 'OpenZFS\.sys') { throw "Driver Verifier is not active for OpenZFS.sys" }
    Remove-PSSession $s
}

if ($Phase -contains "read") {
    $manifestPath = Join-Path $repo "tests\fixtures\single-disk.manifest.json"
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $image = Join-Path $repo "TestVolumes\single\disk01.raw"
    $hostHash = (Get-FileHash $image -Algorithm SHA256).Hash
    if ($hostHash -ne $manifest.image.sha256) { throw "host fixture image does not match its manifest" }
    $s = Open-Guest
    Copy-ToGuest $s $manifestPath "C:\zfswin\fixtures\single-disk.manifest.json"
    Copy-ToGuest $s $image "C:\zfswin\images\single\disk01.raw"
    $out = Invoke-Command $s {
        param($findMount)
        $ErrorActionPreference = "Stop"
        $zpool = "C:\zfswin\driver\zpool.exe"
        $import = & $zpool import -o readonly=on -d C:\zfswin\images\single zfswin_single 2>&1 | Out-String
        $mount = & ([scriptblock]::Create($findMount)) "zfswin_single/data"
        $verify = & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\single-disk.manifest.json $mount
        $verifyExit = $LASTEXITCODE
        $status = & $zpool status -v zfswin_single 2>&1 | Out-String
        $export = & $zpool export zfswin_single 2>&1 | Out-String
        [ordered]@{
            import = $import.Trim(); mount = $mount; verifierExit = $verifyExit
            verifier = ($verify -join "`n") | ConvertFrom-Json
            status = $status.Trim(); export = $export.Trim()
            imageSha256 = (Get-FileHash C:\zfswin\images\single\disk01.raw -Algorithm SHA256).Hash
            bugchecks = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1001 } -ErrorAction SilentlyContinue).Count
        }
    } -ArgumentList $findMount.ToString()
    $out.imageUnchanged = ($out.imageSha256 -eq $manifest.image.sha256)
    Record "read" $out
    if ($out.verifierExit -ne 0 -or -not $out.imageUnchanged) { throw "read phase failed" }
    Remove-PSSession $s
}

if ($Phase -contains "write") {
    $manifestPath = Join-Path $repo "tests\fixtures\compression\lz4.json"
    $s = Open-Guest
    Copy-ToGuest $s $manifestPath "C:\zfswin\fixtures\lz4.json"
    Copy-ToGuest $s (Join-Path $repo "TestVolumes\compression\lz4.raw") "C:\zfswin\images\lz4\lz4.raw"
    $out = Invoke-Command $s {
        param($findMount)
        $ErrorActionPreference = "Stop"
        $zpool = "C:\zfswin\driver\zpool.exe"
        $import = & $zpool import -d C:\zfswin\images\lz4 zc_lz4 2>&1 | Out-String
        $mount = & ([scriptblock]::Create($findMount)) "zc_lz4/data"
        $verify = & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\lz4.json $mount `
            --write (Join-Path $mount "windows-written")
        $verifyExit = $LASTEXITCODE
        $status = & $zpool status -v zc_lz4 2>&1 | Out-String
        $export = & $zpool export zc_lz4 2>&1 | Out-String
        [ordered]@{
            import = $import.Trim(); mount = $mount; verifierExit = $verifyExit
            verifier = ($verify -join "`n") | ConvertFrom-Json
            status = $status.Trim(); export = $export.Trim()
        }
    } -ArgumentList $findMount.ToString()
    Record "write" $out
    if ($out.verifierExit -ne 0) { throw "write phase failed" }
    $local = Join-Path $repo "TestVolumes\vm\written"
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    Copy-Item -FromSession $s -Path C:\zfswin\images\lz4\lz4.raw -Destination $local -Force
    ($out.verifier.written | ConvertTo-Json) | Set-Content (Join-Path $local "written.json")
    Remove-PSSession $s
    Write-Host "Written image: $local\lz4.raw (validate with scripts\vm\validate-written.py)"
}

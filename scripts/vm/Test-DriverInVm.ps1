<#
.SYNOPSIS
Runs the OpenZFS kernel driver tests inside the isolated VM created by
New-DriverTestVm.ps1 (plan step 12).  Runs unelevated on the host; all
driver activity happens in the guest over PowerShell remoting.

.DESCRIPTION
Phases (default: all, in order):
  prepare  revert to the "clean" checkpoint, copy the fixtures into the
           guest once, and take the checkpoint "fixtures" (needs Hyper-V
           Administrators membership).
  tools    refresh only the userland tools (zpool, zfs, ...) and the
           verifier in the guest, leaving the installed driver alone.
  install  revert to "fixtures" (or "clean") so the driver is never
           installed over a running one, copy out\driver-package, trust the test certificate in the guest,
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
    # all, install, tools, read, write (comma-separated with -File)
    [string[]]$Phase = @("all"),
    [string]$Name = "zfswin-driver-test"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ("$($state.address)\$($state.user)",
    (ConvertTo-SecureString $state.password -AsPlainText -Force))
$results = Join-Path $repo "TestVolumes\vm\results.jsonl"
$Phase = @($Phase | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($p in $Phase) {
    if ($p -notin "all", "prepare", "install", "tools", "read", "write") { throw "Unknown phase '$p'." }
}
if ($Phase -contains "all") { $Phase = @("install", "read", "write") }

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try {
            return New-PSSession -ComputerName $state.address -Credential $credential `
                -SessionOption (New-PSSessionOption -OperationTimeout 3600000) -ErrorAction Stop
        } catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 30	# gentle: one attempt per 30 s
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
    if (Test-Path $source -PathType Container) {
        # Copy the contents, so an existing destination is refreshed rather
        # than receiving a nested copy.
        Invoke-Command $session { param($d) New-Item -ItemType Directory -Force -Path $d | Out-Null } `
            -ArgumentList $destination
        Copy-Item -ToSession $session -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
    } else {
        Copy-Item -ToSession $session -Path $source -Destination $destination -Force
    }
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

function Restore-Guest($checkpoint) {
    Restore-VMCheckpoint -VMName $Name -Name $checkpoint -Confirm:$false
    Start-VM -Name $Name
    Start-Sleep -Seconds 20
}

$fixtures = @(
    @{ source = "tests\fixtures\single-disk.manifest.json"; target = "C:\zfswin\fixtures\single-disk.manifest.json" },
    @{ source = "tests\fixtures\compression\lz4.json"; target = "C:\zfswin\fixtures\lz4.json" },
    @{ source = "TestVolumes\single\disk01.raw"; target = "C:\zfswin\images\single\disk01.raw" },
    @{ source = "TestVolumes\compression\lz4.raw"; target = "C:\zfswin\images\lz4-pristine\lz4.raw" })

if ($Phase -contains "prepare") {
    Restore-Guest "clean"
    $s = Open-Guest
    foreach ($f in $fixtures) { Copy-ToGuest $s (Join-Path $repo $f.source) $f.target }
    Remove-PSSession $s
    Stop-VM -Name $Name
    Get-VMSnapshot -VMName $Name -Name "fixtures" -ErrorAction SilentlyContinue | Remove-VMSnapshot
    Checkpoint-VM -Name $Name -SnapshotName "fixtures"
    Start-VM -Name $Name
    Record "prepare" @{ checkpoint = "fixtures"; files = @($fixtures | ForEach-Object { $_.target }) }
}

if ($Phase -contains "install") {
    $base = if (Get-VMSnapshot -VMName $Name -Name "fixtures" -ErrorAction SilentlyContinue) { "fixtures" } else { "clean" }
    Restore-Guest $base
    $s = Open-Guest
    Copy-ToGuest $s (Join-Path $repo "out\driver-package") "C:\zfswin\driver"
    Copy-ToGuest $s (Join-Path $repo "out\driver-tools") "C:\zfswin\tools"
    $out = Invoke-Command $s {
        # Native tools report progress on stderr; check exit codes instead.
        $ErrorActionPreference = "Continue"
        Set-Location C:\zfswin\driver
        # certutil: Import-Certificate is refused in remoting (network) logons.
        foreach ($store in "Root", "TrustedPublisher") {
            & certutil.exe -f -addstore $store .\zfswindows-test.cer | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "certutil -addstore $store failed ($LASTEXITCODE)" }
        }
        $install = & .\zfsinstaller.exe install .\OpenZFS.inf .\OpenZVOL.inf 2>&1 | Out-String
        $installExit = $LASTEXITCODE
        $verifier = & verifier.exe /standard /driver OpenZFS.sys 2>&1 | Out-String
        [ordered]@{
            testsigning = (& bcdedit /enum '{current}' | Select-String testsigning).ToString().Trim()
            install = $install.Trim(); installExit = $installExit; verifier = $verifier.Trim()
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
    if ($out.service -notmatch 'STATE\s+:\s+4\s+RUNNING') { throw "The OpenZFS driver is not running after the reboot" }
    Remove-PSSession $s
}

if ($Phase -contains "tools") {
    $s = Open-Guest
    Invoke-Command $s {
        $z = "C:\zfswin\driver"
        foreach ($pool in @(& "$z\zpool.exe" list -H -o name 2>$null)) { & "$z\zpool.exe" export $pool 2>&1 | Out-Null }
    }
    foreach ($tool in Get-ChildItem (Join-Path $repo "out\driver-package") -Filter *.exe) {
        Copy-Item -ToSession $s -Path $tool.FullName -Destination "C:\zfswin\driver\$($tool.Name)" -Force
    }
    Copy-ToGuest $s (Join-Path $repo "out\driver-tools") "C:\zfswin\tools"
    Record "tools" @{ refreshed = @(Get-ChildItem (Join-Path $repo "out\driver-package") -Filter *.exe).Name }
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
    # Reuse the guest's copy while it is still byte-identical to the fixture.
    $guestHash = Invoke-Command $s {
        $f = "C:\zfswin\images\single\disk01.raw"
        if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash }
    }
    if ($guestHash -ne $manifest.image.sha256) {
        Copy-ToGuest $s $image "C:\zfswin\images\single\disk01.raw"
    }
    $out = Invoke-Command $s {
        param($findMount)
        # Native tools report progress on stderr; check exit codes instead.
        $ErrorActionPreference = "Continue"
        $zpool = "C:\zfswin\driver\zpool.exe"
        $import = & $zpool import -o readonly=on -d C:\zfswin\images\single zfswin_single 2>&1 | Out-String
        $mount = & ([scriptblock]::Create($findMount)) "zfswin_single/data"
        $verify = & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\single-disk.manifest.json $mount
        $verifyExit = $LASTEXITCODE
        $status = & $zpool status -v zfswin_single 2>&1 | Out-String
        # Windows may hold file objects briefly after heavy reads (lazy
        # close); retry, and keep every attempt in the evidence.
        $export = @()
        for ($try = 1; $try -le 10; $try++) {
            $msg = (& $zpool export zfswin_single 2>&1 | ForEach-Object { "$_" }) -join ' '
            $export += "attempt ${try}: rc=$LASTEXITCODE $msg"
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 15
        }
        [ordered]@{
            import = $import.Trim(); mount = $mount; verifierExit = $verifyExit
            verifier = ($verify -join "`n") | ConvertFrom-Json
            status = $status.Trim(); export = $export
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
    # A fresh writable copy of the pristine fixture (from the "fixtures"
    # checkpoint, or uploaded if the guest lacks it).
    $hasPristine = Invoke-Command $s { Test-Path C:\zfswin\images\lz4-pristine\lz4.raw }
    if (-not $hasPristine) {
        Copy-ToGuest $s (Join-Path $repo "TestVolumes\compression\lz4.raw") "C:\zfswin\images\lz4-pristine\lz4.raw"
    }
    Invoke-Command $s {
        New-Item -ItemType Directory -Force -Path C:\zfswin\images\lz4 | Out-Null
        Copy-Item C:\zfswin\images\lz4-pristine\lz4.raw C:\zfswin\images\lz4\lz4.raw -Force
    }
    $out = Invoke-Command $s {
        param($findMount)
        # Native tools report progress on stderr; check exit codes instead.
        $ErrorActionPreference = "Continue"
        $zpool = "C:\zfswin\driver\zpool.exe"
        $import = & $zpool import -d C:\zfswin\images\lz4 zc_lz4 2>&1 | Out-String
        $mount = & ([scriptblock]::Create($findMount)) "zc_lz4/data"
        $verify = & C:\zfswin\tools\Zfs.MountVerifier.exe C:\zfswin\fixtures\lz4.json $mount `
            --write (Join-Path $mount "windows-written")
        $verifyExit = $LASTEXITCODE
        $status = & $zpool status -v zc_lz4 2>&1 | Out-String
        # Windows may hold file objects briefly after heavy reads (lazy
        # close); retry, and keep every attempt in the evidence.
        $export = @()
        for ($try = 1; $try -le 10; $try++) {
            $msg = (& $zpool export zc_lz4 2>&1 | ForEach-Object { "$_" }) -join ' '
            $export += "attempt ${try}: rc=$LASTEXITCODE $msg"
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 15
        }
        [ordered]@{
            import = $import.Trim(); mount = $mount; verifierExit = $verifyExit
            verifier = ($verify -join "`n") | ConvertFrom-Json
            status = $status.Trim(); export = $export
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

<#
.SYNOPSIS
Repeats pool import / write / export in the driver test VM to catch the
intermittent `zpool export` hang (plan step 19).

.DESCRIPTION
Restores the "fixtures" checkpoint, installs the kernel-driver MSI of the
given configuration with the .NET Desktop Runtime, creates pool "zrep"
(mirror of two image files) with child datasets docs and fs, then runs
-Iterations rounds of: import, write files into every dataset, export.
An export that takes longer than -HangSeconds is a hang: a live kernel
dump is taken (Get-StorageDiagnosticInfo -IncludeLiveDump, no bugcheck)
and the loop stops.  Prints one line per round and a JSON summary.

  Repro-ExportHang.ps1 -Configuration Debug -Iterations 12
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [int]$Iterations = 10,
    [int]$HangSeconds = 300,
    [string]$Name = "zfsTest",
    # A package version other than the current build (e.g. a diagnostic
    # build made with build-installer.ps1 -Version 1.0.64999).
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$state = Get-Content (Join-Path $repo "TestVolumes\vm\$Name.json") -Raw | ConvertFrom-Json
$credential = New-Object PSCredential ($state.user,
    (ConvertTo-SecureString $state.password -AsPlainText -Force))
$version = if ($Version) { $Version } else { "1.0." + (Get-Content (Join-Path $repo "build\BUILD_NUMBER") -Raw).Trim() }
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$msi = Join-Path $repo "out\release\$version\OpenZFS-for-Windows-$version-x64-testsigned$tag.msi"
if (-not (Test-Path $msi)) { throw "Missing $msi" }
$runtime = Get-ChildItem (Join-Path $repo "out\dotnet-runtime") -Filter "windowsdesktop-runtime-*-win-x64.exe" | Select-Object -First 1

function Open-Guest {
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        try { return New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop }
        catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 20 }
    }
}

Restore-VMCheckpoint -VMName $Name -Name "fixtures" -Confirm:$false
Start-VM -Name $Name
$s = Open-Guest
Invoke-Command $s { New-Item -ItemType Directory -Force C:\zfswin\installer, C:\zfswin\dumps | Out-Null; & bcdedit /debug off | Out-Null }
Copy-Item -ToSession $s -Path $msi -Destination C:\zfswin\installer\zfswin.msi -Force
Copy-Item -ToSession $s -Path $runtime.FullName -Destination C:\zfswin\installer\dotnet-desktop-runtime.exe -Force
Copy-Item -ToSession $s -Path (Join-Path $repo "out\driver-package\zfswindows-test.cer") -Destination C:\zfswin\installer\zfswindows-test.cer -Force
$install = Invoke-Command $s {
    # The installer's preflight requires the test certificate in Root and
    # TrustedPublisher.  certutil: Import-Certificate is refused in remoting
    # (network) logons.
    foreach ($store in "Root", "TrustedPublisher") {
        & certutil.exe -f -addstore $store C:\zfswin\installer\zfswindows-test.cer | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "certutil -addstore $store failed ($LASTEXITCODE)" }
    }
    $p = Start-Process C:\zfswin\installer\dotnet-desktop-runtime.exe -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
    $m = Start-Process msiexec.exe -ArgumentList "/i", "C:\zfswin\installer\zfswin.msi", "/qn", "/norestart", "/l*v", "C:\zfswin\installer\repro-install.log" -Wait -PassThru
    [ordered]@{ runtime = $p.ExitCode; msi = $m.ExitCode }
}
Remove-PSSession $s
if ($install.msi -notin 0, 3010) { throw "MSI install failed: $($install.msi)" }
Restart-VM -Name $Name -Force -Wait -For Heartbeat
$s = Open-Guest
$result = Invoke-Command $s {
    param($iterations, $hangSeconds)
    $bin = "C:\Program Files\OpenZFS for Windows\bin"
    function Run([string]$exe, [string[]]$a, [int]$timeout) {
        $w = [Diagnostics.Stopwatch]::StartNew()
        $j = Start-Job -ArgumentList "$bin\$exe", (, $a) { param($exe, $a) & $exe @a 2>&1 | ForEach-Object { "$_" }; "exit $LASTEXITCODE" }
        $done = Wait-Job $j -Timeout $timeout
        $out = if ($done) { (Receive-Job $j | Where-Object { $_ -notmatch 'Assuming 512|RemoteException' }) -join ' | ' } else { "" }
        [ordered]@{ seconds = [math]::Round($w.Elapsed.TotalSeconds, 1); hung = -not $done; out = $out.Substring(0, [Math]::Min(200, $out.Length)) }
    }
    Start-Sleep -Seconds 30	# let the driver and broker settle after boot
    $dir = "C:\zfswin\repro"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($n in "a", "b") { if (-not (Test-Path "$dir\$n.img")) { fsutil file createnew "$dir\$n.img" 1073741824 | Out-Null } }
    # The kernel driver takes image files as \\?\ paths.
    $create = Run zpool.exe @("create", "-f", "zrep", "mirror", "\\?\$dir\a.img", "\\?\$dir\b.img") 180
    if ($create.out -notmatch 'exit 0') { return [ordered]@{ create = $create } }
    $docs = Run zfs.exe @("create", "zrep/docs") 60
    $fs = Run zfs.exe @("create", "zrep/fs") 60
    $first = Run zpool.exe @("export", "zrep") $hangSeconds
    $rounds = @()
    for ($i = 1; $i -le $iterations; $i++) {
        $imp = Run zpool.exe @("import", "-d", $dir, "zrep") 180
        $mounts = @(& "$bin\zfs.exe" list -H -o mountpoint -r zrep 2>$null)
        $written = 0
        foreach ($m in $mounts) {
            $root = $m.Trim()
            if ($root -match '^[A-Za-z]:/?$') { $root = $root.Substring(0, 2) + "\" }
            if (-not (Test-Path $root)) { continue }
            for ($k = 0; $k -lt 4; $k++) {
                $b = [byte[]]::new(4MB); [Random]::new($i * 10 + $k).NextBytes($b)
                [IO.File]::WriteAllBytes((Join-Path $root "r$i-$k.bin"), $b); $written++
            }
        }
        $exp = Run zpool.exe @("export", "zrep") $hangSeconds
        $rounds += [ordered]@{ round = $i; import = $imp.seconds; written = $written; export = $exp.seconds; hung = $exp.hung; out = $exp.out }
        if ($exp.hung -or $imp.hung) {
            $d = "C:\zfswin\dumps\live-$i"
            $ss = Get-StorageSubSystem | Select-Object -First 1
            Get-StorageDiagnosticInfo -StorageSubSystemUniqueId $ss.UniqueId -IncludeLiveDump -DestinationPath $d -ErrorAction Continue 2>&1 | Out-Null
            $rounds[-1].dump = @(Get-ChildItem $d -Recurse -Include *.dmp | ForEach-Object FullName)
            break
        }
    }
    [ordered]@{ create = $create; docs = $docs; fs = $fs; firstExport = $first; rounds = $rounds }
} -ArgumentList $Iterations, $HangSeconds
Remove-PSSession $s
foreach ($r in $result.rounds) {
    "round {0,2}: import {1,5} s, wrote {2,2} files, export {3,6} s{4}" -f $r.round, $r.import, $r.written, $r.export, $(if ($r.hung) { "  HUNG, dump: $($r.dump -join ', ')" })
}
[ordered]@{ configuration = $Configuration; version = $version; install = $install; result = $result } | ConvertTo-Json -Depth 6 -Compress

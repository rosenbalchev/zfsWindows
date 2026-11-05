# Runs inside the driver test VM (plan step 14): one stage of the setup
# package and administration-suite test per invocation, printing one JSON
# object.  Driven by scripts\vm\Test-InstallerInVm.ps1.
param(
    [Parameter(Mandatory)]
    [ValidateSet("state", "msi", "cli", "powershell", "standard-user", "samples", "import", "export", "hash")]
    [string]$Stage,
    [string]$Msi = "",
    [ValidateSet("install", "repair", "uninstall")]
    [string]$Action = "install",
    [string[]]$Properties = @(),
    [string]$Log = "",
    # The orchestrator's arguments as Base64 JSON ({ "Msi": ..., ... }).
    [string]$ArgumentsBase64 = ""
)

$ErrorActionPreference = "Continue"
if ($ArgumentsBase64) {
    $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64)) | ConvertFrom-Json
    if ($decoded.Msi) { $Msi = $decoded.Msi }
    if ($decoded.Action) { $Action = $decoded.Action }
    if ($decoded.Properties) { $Properties = @($decoded.Properties) }
    if ($decoded.Log) { $Log = $decoded.Log }
}
$work = "C:\zfswin\installer"
$images = "$work\images"
$product = "OpenZFS for Windows"
$installDir = Join-Path ${env:ProgramFiles} $product
$zfswin = Join-Path $installDir "bin\zfswin.exe"
$pool = "zwin"
New-Item -ItemType Directory -Force -Path $work, $images | Out-Null

function Zfswin([string[]]$arguments) {
    $out = & $zfswin --json @arguments 2>$null
    $code = $LASTEXITCODE
    $json = $null
    try { $json = ($out -join "`n") | ConvertFrom-Json } catch { }
    [pscustomobject]@{ args = ($arguments -join ' '); exit = $code; ok = [bool]($json -and $json.ok); error = $(if ($json -and -not $json.ok) { $json.error.code }); result = $(if ($json) { $json.result }) }
}

function Check($results, [string]$name, $value, $expected) {
    $results[$name] = [ordered]@{ value = $value; expected = $expected; pass = ("$value" -eq "$expected") }
}

function MountPath([string]$dataset) {
    $m = (Zfswin @('mount', 'list')).result.mounts | Where-Object dataset -eq $dataset | Select-Object -First 1
    if (-not $m) { return $null }
    $p = $m.mountpoint
    if ($p -match '^[A-Za-z]:$') { $p += '\' }
    $p
}

function WaitScan([string]$function, [int]$seconds = 600) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $s = (Zfswin @('pool', 'status', $pool)).result.pools | Select-Object -First 1
        if ($s.scan -and $s.scan.function -eq $function -and $s.scan.state -eq 'FINISHED') { return $s }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $s
}

function Manifest([string]$root) {
    Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length).TrimStart('\'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    }
}

switch ($Stage) {
    # Everything setup may own, so stages can be compared before and after.
    "state" {
        $services = [ordered]@{}
        foreach ($name in "OpenZFS", "OpenZVOL", "ZfsWinBroker") {
            $svc = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'" -ErrorAction SilentlyContinue
            if (-not $svc) { $svc = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue }
            $services[$name] = if ($svc) { [ordered]@{ state = $svc.State; start = $svc.StartMode; account = $svc.StartName; path = $svc.PathName } } else { $null }
        }
        $drivers = @((& pnputil.exe /enum-drivers) -join "`n" -split "`r?`n`r?`n" | Where-Object { $_ -match 'Original Name:\s+(openzfs|openzvol)\.inf' } |
            ForEach-Object { (($_ -split "`n" | Where-Object { $_ -match 'Published Name|Original Name|Driver Version|Signer Name' }) -replace '\s+', ' ').Trim() -join '; ' })
        $uninstall = Get-ChildItem HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall | Get-ItemProperty |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq $product } |
            Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation, EstimatedSize, NoModify, UninstallString
        $files = if (Test-Path $installDir) { @(Get-ChildItem $installDir -Recurse -File) } else { @() }
        $badSignatures = @($files | Where-Object Extension -in ".exe", ".sys", ".cat" | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' } | ForEach-Object Name)
        $machinePath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
        $module = & powershell.exe -NoProfile -Command "(Get-Module -ListAvailable ZfsWin | Select-Object -First 1 | ForEach-Object { `$_.Version.ToString() + ' ' + `$_.ModuleBase })" 2>$null
        [ordered]@{
            services = $services
            brokerPrivileges = $(if ($services.ZfsWinBroker) { ((& sc.exe qprivs ZfsWinBroker) -join ' ') -replace '\s+', ' ' })
            brokerSidType = $(if ($services.ZfsWinBroker) { ((& sc.exe qsidtype ZfsWinBroker) -join ' ') -replace '\s+', ' ' })
            brokerFailure = $(if ($services.ZfsWinBroker) { ((& sc.exe qfailure ZfsWinBroker) -join ' ') -replace '\s+', ' ' })
            driverPackages = $drivers
            arp = $uninstall
            installDirExists = Test-Path $installDir
            fileCount = $files.Count
            badSignatures = $badSignatures
            installDirAcl = $(if (Test-Path $installDir) { ((& icacls.exe $installDir) -join '; ') -replace '\s+', ' ' })
            pathEntries = @($machinePath -split ';' | Where-Object { $_ -like "*$product*" })
            module = "$module".Trim()
            eventSource = [Diagnostics.EventLog]::SourceExists('ZfsWinBroker')
            registry = $(if (Test-Path "HKLM:\SOFTWARE\$product") { Get-ItemProperty "HKLM:\SOFTWARE\$product" | Select-Object Version, InstallFolder, AddedToPath })
            pipe = Test-Path '\\.\pipe\zfswin-broker'
            zfswinVersion = $(if (Test-Path $zfswin) { (Zfswin @('version')).result })
            pendingReboot = [bool](Get-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending' -ErrorAction SilentlyContinue)
            pendingFileRenames = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations | Where-Object { $_ -match 'OpenZ|zfs' })
            importedPools = @(if (Test-Path $zfswin) { (Zfswin @('pool', 'list')).result.pools | ForEach-Object name })
            brokerEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ZfsWinBroker' } -MaxEvents 5 -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id): $($_.Message)" })
        } | ConvertTo-Json -Depth 6 -Compress
    }

    "msi" {
        # msiexec /i "" opens a usage dialog that nobody can dismiss.
        if (-not $Msi -or -not (Test-Path -LiteralPath $Msi)) { throw "msi stage: package '$Msi' not found" }
        if (-not $Log) { $Log = "$work\msi-$Action-$(Get-Date -Format yyyyMMdd-HHmmss).log" }
        $arguments = switch ($Action) {
            "install" { @('/i', "`"$Msi`"") }
            # /fa ignores property assignments (ZFSWIN_EXPORT_POOLS=1 never
            # reached the package); this is the same repair with properties.
            "repair" { @('/i', "`"$Msi`"", 'REINSTALL=ALL', 'REINSTALLMODE=amus') }
            "uninstall" { @('/x', "`"$Msi`"") }
        }
        $arguments += @('/qn', '/norestart', '/l*v', "`"$Log`"") + $Properties
        $p = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
        $text = Get-Content $Log -ErrorAction SilentlyContinue
        [ordered]@{
            action = $Action; msi = $Msi; properties = $Properties; exit = $p.ExitCode; log = $Log
            refused = @($text | Select-String 'REFUSED:' | ForEach-Object { ($_.Line -replace '^.*REFUSED:', 'REFUSED:').Trim() } | Select-Object -Unique)
            setup = @($text | Select-String 'WixQuietExec.*(Preflight|passed|Windows (client|Server)|Secure Boot|zfsinstaller exit|REBOOT|Imported pools|Exported|No pools|not running)' |
                ForEach-Object { ($_.Line -replace '^.*WixQuietExec[^:]*:\s*', '').Trim() } | Select-Object -Unique)
            rollback = [bool]($text | Select-String 'Rollback|rolling back' -SimpleMatch -Quiet)
            reboot = @($text | Select-String 'ReplacedInUseFiles|REBOOT|Restart' | Select-Object -Last 3 | ForEach-Object { $_.Line.Trim() })
            result = @($text | Select-String 'Product: .* -- |Installation success or error status' | ForEach-Object { $_.Line.Trim() })
        } | ConvertTo-Json -Depth 4 -Compress
    }

    # CLI workflow on file vdevs: create, datasets, properties, data,
    # snapshots, scrub, attach/detach/replace, error codes, export/import.
    "cli" {
        $r = [ordered]@{}
        foreach ($pl in @((Zfswin @('pool', 'list')).result.pools | ForEach-Object name)) { & $zfswin pool export $pl 2>&1 | Out-Null }
        Remove-Item -Force "$images\*.img" -ErrorAction SilentlyContinue
        foreach ($d in "a", "b", "c", "d") { & fsutil.exe file createnew "$images\$d.img" (256MB) | Out-Null }
        $a = "$images\a.img"; $b = "$images\b.img"; $c = "$images\c.img"; $d = "$images\d.img"

        Check $r "create-unconfirmed" (Zfswin @('pool', 'create', $pool, '--mirror', $a, $b)).exit 6
        Check $r "create-relative-device" (Zfswin @('pool', 'create', $pool, 'a.img', '--yes')).exit 2
        Check $r "create" (Zfswin @('pool', 'create', $pool, '--mirror', $a, $b, '-o', 'ashift=12', '-O', 'compression=lz4', '--yes')).exit 0
        $list = Zfswin @('pool', 'list')
        Check $r "list-health" (($list.result.pools | Where-Object name -eq $pool).health) "ONLINE"
        Check $r "dataset-create" (Zfswin @('dataset', 'create', "$pool/docs", '-o', 'compression=zstd')).exit 0
        Check $r "dataset-set" (Zfswin @('dataset', 'set', "$pool/docs", 'atime=off', 'com.zfswin:test=hello world')).exit 0
        $props = (Zfswin @('dataset', 'get', "$pool/docs", 'compression', 'atime', 'com.zfswin:test')).result.properties
        Check $r "get-compression" (($props | Where-Object name -eq compression).value) "zstd"
        Check $r "get-atime" (($props | Where-Object name -eq atime).value) "off"
        Check $r "get-user-property" (($props | Where-Object name -eq 'com.zfswin:test').value) "hello world"

        $docs = MountPath "$pool/docs"
        $r.mountpoint = $docs
        if ($docs -and (Test-Path -LiteralPath $docs)) {
            $rng = [Random]::new(1414)
            for ($i = 0; $i -lt 24; $i++) {
                $bytes = [byte[]]::new($rng.Next(1, 3MB)); $rng.NextBytes($bytes)
                if ($i % 3 -eq 0) { $bytes = [Text.Encoding]::UTF8.GetBytes(("compressible line $i`n" * ($bytes.Length / 20))) }
                [IO.File]::WriteAllBytes((Join-Path $docs "file$i.bin"), $bytes)
            }
            Check $r "snapshot-create-s1" (Zfswin @('snapshot', 'create', "$pool/docs@s1")).exit 0
            $s1 = @(Manifest $docs)
            [IO.File]::WriteAllText((Join-Path $docs "after-s1.txt"), "written after s1")
            Remove-Item -LiteralPath (Join-Path $docs "file0.bin")
            Check $r "snapshot-create-s2" (Zfswin @('snapshot', 'create', "$pool/docs@s2")).exit 0
            $snaps = (Zfswin @('snapshot', 'list', "$pool/docs")).result.snapshots
            Check $r "snapshot-count" @($snaps).Count 2
            Check $r "snapshot-destroy-unconfirmed" (Zfswin @('snapshot', 'destroy', "$pool/docs@s2")).exit 6
            Check $r "snapshot-destroy" (Zfswin @('snapshot', 'destroy', "$pool/docs@s2", '--yes')).exit 0
            Check $r "snapshot-count-after" @((Zfswin @('snapshot', 'list', "$pool/docs")).result.snapshots).Count 1
            Check $r "unmount" (Zfswin @('dataset', 'unmount', "$pool/docs")).exit 0
            Check $r "mount" (Zfswin @('dataset', 'mount', "$pool/docs")).exit 0
            $docs = MountPath "$pool/docs"
            $after = @(Manifest $docs)
            @{ s1 = $s1; current = $after } | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 "$work\manifest.json"
            Check $r "files-after-remount" $after.Count 24
        } else {
            $r["mounted"] = [ordered]@{ value = $docs; expected = "a mount path"; pass = $false }
        }

        Check $r "scrub-start" (Zfswin @('scrub', 'start', $pool)).exit 0
        $s = WaitScan 'SCRUB'
        Check $r "scrub-finished" "$($s.scan.function)/$($s.scan.state)/$($s.scan.errors)" "SCRUB/FINISHED/0"
        Check $r "scrub-status" (Zfswin @('scrub', 'status', $pool)).result.scan.state "FINISHED"

        Check $r "attach-unconfirmed" (Zfswin @('vdev', 'attach', $pool, $a, $c)).exit 6
        Check $r "attach" (Zfswin @('vdev', 'attach', $pool, $a, $c, '--yes')).exit 0
        $s = WaitScan 'RESILVER'
        Check $r "attach-resilvered" "$($s.scan.function)/$($s.scan.state)" "RESILVER/FINISHED"
        Check $r "three-way" @(($s.vdevs | Select-Object -First 1).children | Select-Object -First 1 | ForEach-Object { $_.children }).Count 3
        Check $r "detach" (Zfswin @('vdev', 'detach', $pool, $c, '--yes')).exit 0
        Check $r "replace" (Zfswin @('vdev', 'replace', $pool, $b, $d, '--yes')).exit 0
        $s = WaitScan 'RESILVER'
        $leaves = @(($s.vdevs | Select-Object -First 1).children | Select-Object -First 1 | ForEach-Object { $_.children } | ForEach-Object { $_.path })
        Check $r "replaced-members" (($leaves | ForEach-Object { Split-Path -Leaf $_ }) -join ',') "a.img,d.img"
        Check $r "pool-state" $s.state "ONLINE"

        Check $r "status-missing-pool" (Zfswin @('pool', 'status', 'nosuchpool')).exit 3
        Check $r "destroy-root-dataset" (Zfswin @('dataset', 'destroy', $pool, '--yes')).exit 2
        Check $r "option-injection" (Zfswin @('pool', 'export', '-f')).exit 2
        Check $r "export" (Zfswin @('pool', 'export', $pool)).exit 0
        $found = (Zfswin @('pool', 'discover', '--dir', $images)).result.pools
        Check $r "discover" (($found | Where-Object name -eq $pool).state) "ONLINE"
        Check $r "import" (Zfswin @('pool', 'import', $pool, '--dir', $images)).exit 0
        $docs = MountPath "$pool/docs"
        $reimported = if ($docs) { @(Manifest $docs) } else { @() }
        $expected = (Get-Content "$work\manifest.json" -Raw | ConvertFrom-Json).current
        Check $r "data-after-reimport" ((Compare-Object @($reimported | ForEach-Object { "$($_.path)|$($_.sha256)" }) @($expected | ForEach-Object { "$($_.path)|$($_.sha256)" })) -eq $null) $true
        $snapPath = if ($docs) { Join-Path $docs ".zfs\snapshot\s1" }
        $snapFiles = if ($snapPath -and (Test-Path -LiteralPath $snapPath)) { @(Manifest $snapPath) } else { @() }
        $expectedS1 = (Get-Content "$work\manifest.json" -Raw | ConvertFrom-Json).s1
        Check $r "snapshot-s1-content" ((Compare-Object @($snapFiles | ForEach-Object { "$($_.path)|$($_.sha256)" }) @($expectedS1 | ForEach-Object { "$($_.path)|$($_.sha256)" })) -eq $null) $true
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # The same kind of workflow through the ZfsWin module (pool zwin imported).
    "powershell" {
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command {
            $ErrorActionPreference = 'Stop'
            $r = [ordered]@{}
            function Check([string]$name, $value, $expected) { $r[$name] = [ordered]@{ value = "$value"; expected = "$expected"; pass = ("$value" -eq "$expected") } }
            Import-Module ZfsWin
            Check "module-path" ((Get-Module ZfsWin).ModuleBase -like "*WindowsPowerShell\Modules\ZfsWin\*") $true
            Check "signature" (Get-AuthenticodeSignature (Join-Path (Get-Module ZfsWin).ModuleBase 'ZfsWin.psm1')).Status 'Valid'
            Check "pool" (Get-ZfsPool zwin).health 'ONLINE'
            $ds = New-ZfsDataset zwin/ps -Property @{ compression = 'gzip' } -Confirm:$false
            Check "new-dataset" $ds.name 'zwin/ps'
            Set-ZfsProperty zwin/ps @{ 'com.zfswin:ps' = 'set by powershell'; recordsize = '64K' } -Confirm:$false
            Check "property" (Get-ZfsProperty zwin/ps 'com.zfswin:ps').value 'set by powershell'
            Check "recordsize" (Get-ZfsProperty zwin/ps recordsize).value 65536
            $path = (Get-ZfsMount | Where-Object dataset -eq 'zwin/ps').mountpoint
            if ($path -match '^[A-Za-z]:$') { $path += '\' }
            Set-Content -LiteralPath (Join-Path $path 'hello.txt') -Value 'hello from the ZfsWin module'
            $snap = New-ZfsSnapshot zwin/ps@p1 -Confirm:$false
            Check "new-snapshot" $snap.name 'zwin/ps@p1'
            Check "snapshots" @(Get-ZfsSnapshot zwin/ps).Count 1
            Remove-ZfsSnapshot zwin/ps@p1 -WhatIf
            Check "whatif-kept-snapshot" @(Get-ZfsSnapshot zwin/ps).Count 1
            Remove-ZfsSnapshot zwin/ps@p1 -Force
            Check "removed-snapshot" @(Get-ZfsSnapshot zwin/ps).Count 0
            $scrub = Get-ZfsScrub zwin
            Check "scrub-object" $scrub.pool 'zwin'
            try { Get-ZfsPoolStatus nosuchpool | Out-Null; Check "missing-pool-error" 'none' 'ObjectNotFound' }
            catch { Check "missing-pool-error" $_.CategoryInfo.Category 'ObjectNotFound'; Check "missing-pool-code" $_.Exception.Data['ZfsWinErrorCode'] 'not-found' }
            try { New-ZfsPool zwin2 -Device 'C:\does\not\exist.img' -Confirm:$false | Out-Null; Check "missing-device-error" 'none' 'ObjectNotFound' }
            catch { Check "missing-device-error" $_.CategoryInfo.Category 'ObjectNotFound' }
            Remove-ZfsDataset zwin/ps -Recurse -Force
            Check "removed-dataset" @(Get-ZfsDataset zwin -Recurse | Where-Object name -eq 'zwin/ps').Count 0
            Export-ZfsPool zwin -Confirm:$false
            Check "exported" @(Get-ZfsPool).Count 0
            $found = Find-ZfsPool -SearchDirectory C:\zfswin\installer\images
            Check "find" ($found | Where-Object name -eq zwin).state 'ONLINE'
            Import-ZfsPool zwin -SearchDirectory C:\zfswin\installer\images -Confirm:$false | Out-Null
            Check "reimported" (Get-ZfsPool zwin).health 'ONLINE'
            Check "version" ((Get-ZfsWinVersion).protocol) 1
            $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and -not $r[$_].pass })
            $r | ConvertTo-Json -Depth 4 -Compress
        } 2>&1
        $json = $out | Where-Object { $_ -is [string] -and $_.StartsWith('{') } | Select-Object -Last 1
        if ($json) { $json } else { [ordered]@{ error = ($out | Out-String) } | ConvertTo-Json -Compress }
    }

    # Read-only commands work for a standard user; mutations are refused (4).
    "standard-user" {
        $user = "zfsuser"
        $password = "Zfs-User-$(Get-Random -Minimum 100000 -Maximum 999999)!"
        if (-not (Get-LocalUser $user -ErrorAction SilentlyContinue)) {
            New-LocalUser $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires | Out-Null
        } else {
            Set-LocalUser $user -Password (ConvertTo-SecureString $password -AsPlainText -Force)
        }
        # A scheduled task needs "Log on as a batch job", which Users lack by
        # default (the task otherwise never starts: 0x41303).  Disposable VM.
        $sid = (Get-LocalUser $user).SID.Value
        & secedit.exe /export /cfg "$work\rights.inf" /areas USER_RIGHTS | Out-Null
        $rights = @(Get-Content "$work\rights.inf")
        if (-not ($rights -match "^SeBatchLogonRight.*\*$sid")) {
            $rights = if ($rights -match '^SeBatchLogonRight') {
                $rights | ForEach-Object { if ($_ -match '^SeBatchLogonRight') { "$_,*$sid" } else { $_ } }
            } else { $rights + "SeBatchLogonRight = *$sid" }
            $rights | Set-Content -Encoding Unicode "$work\rights.inf"
            & secedit.exe /configure /db "$work\rights.sdb" /cfg "$work\rights.inf" /areas USER_RIGHTS | Out-Null
        }
        $script = "$work\standard-user.ps1"
        $result = "$work\standard-user.json"
        Remove-Item $result -ErrorAction SilentlyContinue
        @"
`$z = '$zfswin'
function Run([string[]]`$a) { `$o = & `$z --json @a 2>`$null; `$j = (`$o -join "``n") | ConvertFrom-Json; [ordered]@{ args = (`$a -join ' '); exit = `$LASTEXITCODE; error = `$j.error.code } }
Import-Module ZfsWin
`$denied = try { Export-ZfsPool $pool -Confirm:`$false; 'none' } catch { "`$(`$_.CategoryInfo.Category)" }
[ordered]@{
    user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')
    list = Run @('pool', 'list'); status = Run @('pool', 'status', '$pool'); datasets = Run @('dataset', 'list')
    export = Run @('pool', 'export', '$pool'); discover = Run @('pool', 'discover'); snapshot = Run @('snapshot', 'create', '$pool@user')
    cmdletList = @(Get-ZfsPool).Count; cmdletExport = `$denied
} | ConvertTo-Json -Compress | Set-Content -Encoding utf8 '$result'
"@ | Set-Content -Encoding utf8 $script
        $task = "zfswin-standard-user"
        Unregister-ScheduledTask $task -Confirm:$false -ErrorAction SilentlyContinue
        $taskAction = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
        Register-ScheduledTask $task -Action $taskAction -User $user -Password $password -RunLevel Limited | Out-Null
        Start-ScheduledTask $task
        $deadline = (Get-Date).AddMinutes(3)
        while (-not (Test-Path $result) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
        Unregister-ScheduledTask $task -Confirm:$false
        if (Test-Path $result) { Get-Content $result -Raw } else { '{"error":"the standard-user task produced no result"}' }
    }

    # Raw OpenZFS JSON for the schema-compatibility tests' fixtures.
    "samples" {
        $tools = Join-Path $installDir "bin"
        $samples = [ordered]@{}
        foreach ($s in @(
                @{ name = "zpool-list"; exe = "zpool.exe"; args = @('list', '-j', '--json-int') },
                @{ name = "zpool-status"; exe = "zpool.exe"; args = @('status', '-j', '--json-int', '-p') },
                @{ name = "zpool-get"; exe = "zpool.exe"; args = @('get', '-j', '--json-int', 'all', $pool) },
                @{ name = "zfs-list"; exe = "zfs.exe"; args = @('list', '-j', '--json-int', '-t', 'filesystem,volume', '-o', 'name,type,used,available,referenced,mountpoint,mounted,driveletter,compression', '-r') },
                @{ name = "zfs-get"; exe = "zfs.exe"; args = @('get', '-j', '--json-int', 'all', "$pool/docs") },
                @{ name = "zfs-snapshots"; exe = "zfs.exe"; args = @('list', '-j', '--json-int', '-t', 'snapshot', '-o', 'name,used,referenced,creation', '-r') })) {
            $samples[$s.name] = ((& (Join-Path $tools $s.exe) @($s.args) 2>$null) -join "`n")
        }
        $samples["zfs-mount"] = ((& (Join-Path $tools "zfs.exe") mount 2>$null) -join "`n")
        & (Join-Path $tools "zpool.exe") export $pool 2>&1 | Out-Null
        $samples["zpool-import"] = ((& (Join-Path $tools "zpool.exe") import -d $images 2>$null) -join "`n")
        & (Join-Path $tools "zpool.exe") import -d $images $pool 2>&1 | Out-Null
        $samples | ConvertTo-Json -Compress
    }

    "import" { (Zfswin @('pool', 'import', $pool, '--dir', $images)) | ConvertTo-Json -Depth 4 -Compress }
    "export" { (Zfswin @('pool', 'export', $pool)) | ConvertTo-Json -Depth 4 -Compress }
    "hash" {
        [ordered]@{ images = @(Get-ChildItem $images -Filter *.img | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = (Get-FileHash $_.FullName).Hash } }) } | ConvertTo-Json -Depth 4 -Compress
    }
}

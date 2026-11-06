# Runs inside the driver test VM (plan step 14): one stage of the setup
# package and administration-suite test per invocation, printing one JSON
# object.  Driven by scripts\vm\Test-InstallerInVm.ps1.
param(
    [Parameter(Mandatory)]
    [ValidateSet("state", "msi", "cli", "gui", "powershell", "standard-user", "filesystem", "crosscheck", "samples", "import", "export", "hash")]
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
# The user-mode edition (plan step 18) runs the same stages with its own
# install folder; its zpool/zfs reach the engine through the ZFS bridge.
$edition = if ($decoded -and $decoded.Edition) { $decoded.Edition } else { "kernel" }
$product = if ($edition -eq "usermode") { "OpenZFS for Windows (user mode)" } else { "OpenZFS for Windows" }
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
    # Strings: Get-Content output carries provider properties that would
    # otherwise be serialized with it.
    $results[$name] = [ordered]@{ value = "$value"; expected = "$expected"; pass = ("$value" -eq "$expected") }
}

# Reads under .zfs\snapshot: the kernel driver mounts a snapshot on first
# access, so give it a moment instead of failing on the first try.
function Retry([scriptblock]$read, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $v = try { & $read } catch { $null }
        if ($null -ne $v -and "$v" -ne "") { return $v }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $v
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

# FSCTL_DUPLICATE_EXTENTS_TO_FILE, the block-cloning call ReFS copies use.
function Clone-Extents([string]$source, [string]$target, [long]$length) {
    if (-not ("ZfsWinClone" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ZfsWinClone {
    [StructLayout(LayoutKind.Sequential)]
    struct DuplicateExtentsData { public IntPtr FileHandle; public long SourceFileOffset; public long TargetFileOffset; public long ByteCount; }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref DuplicateExtentsData input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    public static int Clone(string source, string target, long length) {
        using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var dst = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
            dst.SetLength(length);
            var data = new DuplicateExtentsData { FileHandle = src.SafeFileHandle.DangerousGetHandle(), ByteCount = length };
            int returned;
            if (DeviceIoControl(dst.SafeFileHandle, 0x00098344, ref data, Marshal.SizeOf(data), IntPtr.Zero, 0, out returned, IntPtr.Zero))
                return 0;
            int error = Marshal.GetLastWin32Error();
            if (error != 1 && error != 50)
                return error;
            // User mode: WinFsp forwards only custom control codes, so
            // zfswin-um takes the same request as 0x8F5A2004.
            return DeviceIoControl(dst.SafeFileHandle, 0x8F5A2004, ref data, Marshal.SizeOf(data), IntPtr.Zero, 0, out returned, IntPtr.Zero)
                ? 0 : Marshal.GetLastWin32Error();
        }
    }
}
"@
    }
    [ZfsWinClone]::Clone($source, $target, $length)
}

# A local standard user (zfsuser) and a way to run a script as that user.
function Invoke-AsStandardUser([string]$scriptText, [string]$result) {
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
    $script = "$work\as-standard-user.ps1"
    Remove-Item $result -ErrorAction SilentlyContinue
    $scriptText | Set-Content -Encoding utf8 $script
    $task = "zfswin-standard-user"
    Unregister-ScheduledTask $task -Confirm:$false -ErrorAction SilentlyContinue
    $taskAction = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
    Register-ScheduledTask $task -Action $taskAction -User $user -Password $password -RunLevel Limited | Out-Null
    Start-ScheduledTask $task
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Test-Path $result) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    Unregister-ScheduledTask $task -Confirm:$false
    if (Test-Path $result) { Get-Content $result -Raw | ConvertFrom-Json } else { [pscustomobject]@{ error = "the standard-user task produced no result" } }
}

# What a file looks like to Windows: security descriptor, data and streams.
function Describe([string]$path) {
    $streams = [ordered]@{}
    foreach ($st in @(Get-Item -LiteralPath $path -Stream * -ErrorAction SilentlyContinue | Where-Object Stream -ne ':$DATA' | Sort-Object Stream)) {
        # (.NET Framework rejects "file:stream" paths; the provider does not.)
        $bytes = [byte[]](Get-Content -LiteralPath $path -Stream $st.Stream -Encoding Byte -Raw -ErrorAction SilentlyContinue)
        $sha = [Security.Cryptography.SHA256]::Create()
        $streams[$st.Stream] = [BitConverter]::ToString($sha.ComputeHash($(if ($bytes) { $bytes } else { [byte[]]@() }))) -replace '-', ''
    }
    [ordered]@{
        sddl = (Get-Acl -LiteralPath $path).Sddl
        sha256 = $(if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path).Hash })
        streams = $streams
    }
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
            # Block cloning: only synced blocks can be cloned, so flush and
            # let the transaction group commit (5 s) first.
            $cloneSrc = Join-Path $docs "clone-source.bin"
            $cloneBytes = [byte[]]::new(8MB); [Random]::new(77).NextBytes($cloneBytes)
            $fs = [IO.FileStream]::new($cloneSrc, 'Create'); $fs.Write($cloneBytes, 0, $cloneBytes.Length); $fs.Flush($true); $fs.Dispose()
            Start-Sleep -Seconds 12
            $before = [long](((Zfswin @('pool', 'get', $pool, 'bcloneused')).result.properties | Where-Object name -eq bcloneused).value)
            $cloneResult = Clone-Extents $cloneSrc (Join-Path $docs "clone-target.bin") 8MB
            Start-Sleep -Seconds 12
            $afterUsed = [long](((Zfswin @('pool', 'get', $pool, 'bcloneused')).result.properties | Where-Object name -eq bcloneused).value)
            Check $r "clone-ioctl" $cloneResult 0
            Check $r "clone-identical" ((Get-FileHash $cloneSrc).Hash -eq (Get-FileHash (Join-Path $docs "clone-target.bin")).Hash) $true
            Check $r "clone-shares-blocks" ($afterUsed -gt $before) $true
            $r.cloneBytes = [ordered]@{ bcloneusedBefore = $before; bcloneusedAfter = $afterUsed }
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
            Check $r "files-after-remount" $after.Count 26	# 24 + the clone source and target
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

        # Write log (SLOG) and read cache (L2ARC): add, see, remove.
        foreach ($n in "e", "f") { & fsutil.exe file createnew "$images\$n.img" (256MB) | Out-Null }
        $e = "$images\e.img"; $f = "$images\f.img"
        Check $r "add-log-unconfirmed" (Zfswin @('vdev', 'add-log', $pool, $e)).exit 6
        Check $r "add-log" (Zfswin @('vdev', 'add-log', $pool, $e, '--yes')).exit 0
        Check $r "add-cache" (Zfswin @('vdev', 'add-cache', $pool, $f, '--yes')).exit 0
        $st = (Zfswin @('pool', 'status', $pool)).result.pools | Select-Object -First 1
        Check $r "log-and-cache-listed" "$(@($st.logs).Count)/$(@($st.caches).Count)" "1/1"
        Check $r "remove-cache" (Zfswin @('vdev', 'remove', $pool, $f, '--yes')).exit 0
        Check $r "remove-log" (Zfswin @('vdev', 'remove', $pool, $e, '--yes')).exit 0
        $st = (Zfswin @('pool', 'status', $pool)).result.pools | Select-Object -First 1
        Check $r "log-and-cache-removed" "$(@($st.logs).Count)/$(@($st.caches).Count)" "0/0"
        Check $r "dedup-on" (Zfswin @('dataset', 'set', "$pool/docs", 'dedup=on')).exit 0
        Check $r "dedup-value" (((Zfswin @('dataset', 'get', "$pool/docs", 'dedup')).result.properties | Where-Object name -eq dedup).value) "on"

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
            $sig = Get-AuthenticodeSignature (Join-Path (Get-Module ZfsWin).ModuleBase 'ZfsWin.psm1')
            # The user-mode VM (Secure Boot on) does not trust the test root: there
            # the signer is what is checked.
            Check "signature" ($sig.Status -eq 'Valid' -or ($sig.Status -eq 'UnknownError' -and $sig.SignerCertificate.Subject -eq 'CN=zfsWindows Test Signing')) $true
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

    # The administration GUI, headless: the queries its windows make.
    "gui" {
        $gui = Join-Path $installDir "bin\ZfsWinGui.exe"
        $out = "$work\gui-selfcheck.json"
        Remove-Item $out -ErrorAction SilentlyContinue
        $p = Start-Process $gui -ArgumentList '--self-check', $out -Wait -PassThru
        if (Test-Path $out) {
            $j = Get-Content $out -Raw | ConvertFrom-Json
            $j | Add-Member exit $p.ExitCode
            $j | Add-Member shortcuts @(Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\$product" -ErrorAction SilentlyContinue | ForEach-Object Name)
            $j | ConvertTo-Json -Depth 6 -Compress
        } else {
            [ordered]@{ exit = $p.ExitCode; error = "no self-check report (is the .NET 10 Desktop Runtime installed?)" } | ConvertTo-Json -Compress
        }
    }

    # Windows ACLs, alternate data streams and .zfs\snapshot on a dataset of
    # the imported pool (plan step 18; both editions).  Leaves zwin/fs and
    # fs-expected.json for the other edition's "crosscheck".
    "filesystem" {
        $r = [ordered]@{}
        $ds = "$pool/fs"
        if (-not (Get-LocalUser zfsuser -ErrorAction SilentlyContinue)) {
            New-LocalUser zfsuser -Password (ConvertTo-SecureString "Zfs-User-$(Get-Random)!" -AsPlainText -Force) -PasswordNeverExpires | Out-Null
        }
        Check $r "fs-create" (Zfswin @('dataset', 'create', $ds)).exit 0
        $root = MountPath $ds
        $r.mountpoint = $root
        if (-not $root -or -not (Test-Path -LiteralPath $root)) {
            $r.failed = @("fs-mounted")
            $r | ConvertTo-Json -Depth 6 -Compress
            break
        }
        # ACLs: inherited from the root; explicit and protected; a deny.
        $plain = Join-Path $root "plain.txt"; Set-Content -LiteralPath $plain "inherits the root's ACL"
        $custom = Join-Path $root "custom-acl.txt"; Set-Content -LiteralPath $custom "zfsuser may read this"
        & icacls.exe $custom /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" "zfsuser:(R)" | Out-Null
        $denied = Join-Path $root "denied.txt"; Set-Content -LiteralPath $denied "secret"
        & icacls.exe $denied /deny "zfsuser:(R)" | Out-Null
        $dir = Join-Path $root "dir"; New-Item -ItemType Directory $dir | Out-Null
        & icacls.exe $dir /grant "zfsuser:(OI)(CI)(M)" | Out-Null
        $inner = Join-Path $dir "inner.txt"; Set-Content -LiteralPath $inner "inside"
        Check $r "acl-explicit" ([bool]((Get-Acl -LiteralPath $custom).Access | Where-Object { $_.IdentityReference -like '*zfsuser' -and -not $_.IsInherited })) $true
        Check $r "acl-protected" ((Get-Acl -LiteralPath $custom).AreAccessRulesProtected) $true
        Check $r "acl-inherited-from-dir" ([bool]((Get-Acl -LiteralPath $inner).Access | Where-Object { $_.IdentityReference -like '*zfsuser' -and $_.IsInherited })) $true

        # Alternate data streams.
        $streamed = Join-Path $root "streams.txt"; Set-Content -LiteralPath $streamed "main data"
        Set-Content -LiteralPath $streamed -Stream meta -Value "metadata stream"
        Set-Content -LiteralPath $streamed -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
        $big = [byte[]]::new(200KB); [Random]::new(5).NextBytes($big)
        Set-Content -LiteralPath $streamed -Stream big -Value $big -Encoding Byte
        Check $r "streams-listed" ((@(Get-Item -LiteralPath $streamed -Stream * | Where-Object Stream -ne ':$DATA' | ForEach-Object Stream) | Sort-Object) -join ',') "big,meta,Zone.Identifier"
        Check $r "stream-read" (Get-Content -LiteralPath $streamed -Stream meta) "metadata stream"
        Check $r "stream-big" ((Get-Content -LiteralPath $streamed -Stream big -Encoding Byte -Raw).Length) $big.Length
        Check $r "main-data-unchanged" (Get-Content -LiteralPath $streamed) "main data"
        Remove-Item -LiteralPath $streamed -Stream big
        Check $r "stream-removed" @(Get-Item -LiteralPath $streamed -Stream * | Where-Object Stream -eq big).Count 0
        $doomed = Join-Path $root "doomed.txt"; Set-Content -LiteralPath $doomed "x"; Set-Content -LiteralPath $doomed -Stream s -Value "y"
        Remove-Item -LiteralPath $doomed
        Check $r "delete-file-with-stream" (Test-Path -LiteralPath $doomed) $false

        # Everything above survives an unmount and mount.
        $names = "plain.txt", "custom-acl.txt", "denied.txt", "streams.txt", "dir", "dir\inner.txt"
        $before = [ordered]@{}; foreach ($f in $names) { $before[$f] = Describe (Join-Path $root $f) }
        Check $r "fs-unmount" (Zfswin @('dataset', 'unmount', $ds)).exit 0
        Check $r "fs-mount" (Zfswin @('dataset', 'mount', $ds)).exit 0
        $root = MountPath $ds
        foreach ($f in $names) {
            Check $r "persist-$f" ((Describe (Join-Path $root $f) | ConvertTo-Json -Compress -Depth 4) -eq ($before[$f] | ConvertTo-Json -Compress -Depth 4)) $true
        }

        # Enforcement, as a standard user.
        $result = "$work\fs-user.json"
        $u = Invoke-AsStandardUser @"
`$r = [ordered]@{ user = [Security.Principal.WindowsIdentity]::GetCurrent().Name }
try { `$r.readCustom = "`$(Get-Content -LiteralPath '$(Join-Path $root "custom-acl.txt")' -Raw -ErrorAction Stop)".Trim() } catch { `$r.readCustom = 'denied' }
try { Add-Content -LiteralPath '$(Join-Path $root "custom-acl.txt")' 'x' -ErrorAction Stop; `$r.writeCustom = 'written' } catch { `$r.writeCustom = 'denied' }
try { `$r.readDenied = "`$(Get-Content -LiteralPath '$(Join-Path $root "denied.txt")' -Raw -ErrorAction Stop)".Trim() } catch { `$r.readDenied = 'denied' }
try { `$r.readPlain = "`$(Get-Content -LiteralPath '$(Join-Path $root "plain.txt")' -Raw -ErrorAction Stop)".Trim() } catch { `$r.readPlain = 'denied' }
try { Set-Content -LiteralPath '$(Join-Path $root "dir\by-user.txt")' 'mine' -ErrorAction Stop; `$r.writeDir = 'written' } catch { `$r.writeDir = 'denied' }
`$r | ConvertTo-Json -Compress | Set-Content -Encoding utf8 '$result'
"@ $result
        $r.standardUser = $u
        Check $r "user-reads-granted-file" $u.readCustom "zfsuser may read this"
        Check $r "user-cannot-write-read-only-grant" $u.writeCustom "denied"
        Check $r "user-denied-by-deny-ace" $u.readDenied "denied"
        Check $r "user-reads-inherited" $u.readPlain "inherits the root's ACL"
        Check $r "user-writes-granted-dir" $u.writeDir "written"

        # .zfs\snapshot: contents, ACLs and streams as they were; read-only.
        Check $r "snapshot-f1" (Zfswin @('snapshot', 'create', "$ds@f1")).exit 0
        Set-Content -LiteralPath (Join-Path $root "plain.txt") "changed after f1"
        Check $r "snapdir-visible" (Zfswin @('dataset', 'set', $ds, 'snapdir=visible')).exit 0
        Check $r "snapdir-listed" ([bool](Get-ChildItem -LiteralPath $root -Force | Where-Object Name -eq '.zfs')) $true
        $snapRoot = Join-Path $root ".zfs\snapshot\f1"
        Check $r "snapshot-listed" ((Get-ChildItem -LiteralPath (Join-Path $root ".zfs\snapshot") | ForEach-Object Name) -join ',') "f1"
        Check $r "snapshot-old-content" (Retry { Get-Content -LiteralPath (Join-Path $snapRoot "plain.txt") -ErrorAction Stop }) "inherits the root's ACL"
        Check $r "snapshot-acl" ((Retry { (Get-Acl -LiteralPath (Join-Path $snapRoot "custom-acl.txt") -ErrorAction Stop).Sddl }) -eq $before["custom-acl.txt"].sddl) $true
        Check $r "snapshot-stream" (Retry { Get-Content -LiteralPath (Join-Path $snapRoot "streams.txt") -Stream meta -ErrorAction Stop }) "metadata stream"
        $readOnly = try { Set-Content -LiteralPath (Join-Path $snapRoot "new.txt") "x" -ErrorAction Stop; "written" } catch { "refused" }
        Check $r "snapshot-read-only" $readOnly "refused"
        Check $r "snapdir-hidden" (Zfswin @('dataset', 'set', $ds, 'snapdir=hidden')).exit 0
        Check $r "snapdir-not-listed" ([bool](Get-ChildItem -LiteralPath $root -Force | Where-Object Name -eq '.zfs')) $false

        $expected = [ordered]@{ edition = $edition; userSid = (Get-LocalUser zfsuser).SID.Value; files = [ordered]@{} }
        foreach ($f in $names) { $expected.files[$f] = Describe (Join-Path $root $f) }
        $expected | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 "$work\fs-expected.json"
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # The other edition's pool (its images and fs-expected.json in Dir):
    # imported as zcross, the same ACLs, data and streams, then exported.
    "crosscheck" {
        $r = [ordered]@{}
        $dir = $decoded.Dir
        $expected = Get-Content (Join-Path $dir "fs-expected.json") -Raw | ConvertFrom-Json
        $r.writtenBy = $expected.edition
        $localSid = (Get-LocalUser zfsuser -ErrorAction SilentlyContinue).SID.Value
        function Normalize([string]$sddl) {
            foreach ($sid in @($expected.userSid, $localSid) | Where-Object { $_ }) { $sddl = $sddl.Replace($sid, '<zfsuser>') }
            $sddl
        }
        Check $r "import" (Zfswin @('pool', 'import', $pool, '--dir', $dir, '--new-name', 'zcross')).exit 0
        $root = MountPath "zcross/fs"
        $r.mountpoint = $root
        foreach ($p in $expected.files.PSObject.Properties) {
            $actual = if ($root) { Describe (Join-Path $root $p.Name) } else { $null }
            Check $r "sddl-$($p.Name)" ($actual -and (Normalize $actual.sddl) -eq (Normalize $p.Value.sddl)) $true
            Check $r "data-$($p.Name)" ("$($actual.sha256)" -eq "$($p.Value.sha256)") $true
            Check $r "streams-$($p.Name)" (($actual.streams | ConvertTo-Json -Compress) -eq ($p.Value.streams | ConvertTo-Json -Compress)) $true
            if ($actual -and (Normalize $actual.sddl) -ne (Normalize $p.Value.sddl)) { $r["sddl-$($p.Name)"].detail = "$($actual.sddl) vs $($p.Value.sddl)" }
        }
        $old = if ($root) { Retry { Get-Content -LiteralPath (Join-Path $root ".zfs\snapshot\f1\plain.txt") -ErrorAction Stop } }
        Check $r "snapshot-f1" $old "inherits the root's ACL"
        Check $r "export" (Zfswin @('pool', 'export', 'zcross')).exit 0
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
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

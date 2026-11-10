# Runs inside the driver test VM (plan step 14): one stage of the setup
# package and administration-suite test per invocation, printing one JSON
# object.  Driven by scripts\vm\Test-InstallerInVm.ps1.
param(
    [Parameter(Mandatory)]
    [ValidateSet("state", "msi", "cli", "gui", "powershell", "standard-user", "filesystem", "datafeatures", "memory", "wholedisk", "rebootprep", "rebootcheck", "crosscheck", "tree", "treeverify", "samples", "import", "export", "hash")]
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
    # A mount restored from the registry is listed as "Y:/".
    $p = $m.mountpoint -replace '/', '\'
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

# The "tree" corpus (stages tree and treeverify): ~24,500 small files in
# chains of 10-30 nested directories plus one wide directory per dataset,
# sizes around block boundaries, zero/text/random/mixed content, DOS
# attributes and fixed times.  Written and verified by all CPUs at once;
# deterministic, so another machine can check it from the manifest.
function Initialize-Tree {
    if ("ZfsTree" -as [type]) { return }
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public static class ZfsTree {
    public class Entry { public int R; public string Rel; public long Size; public int Kind; public int Seed; public FileAttributes Attr; public DateTime Time; public string Sha; public bool Gone; }
    public class Result { public int Checked, Missing, Size, Data, Attr, Time, Errors; public string[] Samples; }
    public static List<Entry> Files = new List<Entry>();
    public static List<HashSet<string>> DirSets = new List<HashSet<string>>();
    public static int[] ExpectedDirs;
    public static string[] Roots;
    static readonly string[] DirNames = { "lvl", "\u00dcn\u00efc\u00f8d\u00e9", "\u65e5\u672c", "with space", "dots.in.name", "a_rather_long_directory_name_that_makes_paths_long", "x" };
    static readonly string[] Stems = { "f", "\u00d1ame ", "UPPER", "with.many.dots.", "file-with-a-much-longer-name-that-keeps-going-" };
    static readonly string[] Exts = { ".bin", ".txt", ".DAT", "", ".tar.gz" };
    static readonly long[] Sizes = { 0, 1, 2, 100, 511, 512, 513, 4095, 4096, 4097, 8191, 8192, 8193, 16384, 65535, 65536, 131073 };
    static readonly FileAttributes[] Attrs = { FileAttributes.Normal, FileAttributes.ReadOnly, FileAttributes.Hidden, FileAttributes.System, FileAttributes.Archive,
        FileAttributes.Hidden | FileAttributes.System, FileAttributes.ReadOnly | FileAttributes.Hidden, FileAttributes.Archive | FileAttributes.ReadOnly };
    public const FileAttributes Mask = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static string Full(int r, string rel) { return @"\\?\" + Roots[r] + rel; }
    static ParallelOptions Opt(int threads) { return new ParallelOptions { MaxDegreeOfParallelism = threads }; }
    static void Note(ConcurrentQueue<string> q, string what) { if (q.Count < 20) q.Enqueue(what); }

    public static int Plan(string[] roots, int perRoot, int branches, int wide) {
        Roots = roots; Files.Clear(); DirSets.Clear();
        int idx = 0;
        for (int r = 0; r < roots.Length; r++) {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int perBranch = (perRoot - wide) / branches;
            for (int b = 0; b < branches; b++) {
                int depth = 10 + (b * 7 + r * 3) % 21;
                string cur = "b" + b.ToString("D2");
                dirs.Add(cur);
                var levels = new List<string>();
                for (int d = 0; d < depth; d++) {
                    cur = cur + "\\" + DirNames[(b + d + r) % DirNames.Length] + d.ToString("D2");
                    dirs.Add(cur); levels.Add(cur);
                }
                for (int j = 0; j < perBranch; j++)
                    Add(r, levels[j % depth] + "\\" + Stems[j % Stems.Length] + j + Exts[(j / Stems.Length) % Exts.Length], idx++);
            }
            dirs.Add("wide");
            for (int i = 0; i < wide; i++) Add(r, "wide\\w" + i.ToString("D5") + ".dat", idx++);
            DirSets.Add(dirs);
        }
        ExpectedDirs = DirSets.Select(s => s.Count).ToArray();
        return Files.Count;
    }

    static void Add(int r, string rel, int idx) {
        var e = new Entry { R = r, Rel = rel, Kind = idx % 4, Seed = (int)((idx * 1103515245L + 12345) & 0x7fffffff) };
        e.Size = (idx % 5 == 4) ? new Random(e.Seed).Next(1, 32768) : Sizes[idx % Sizes.Length];
        e.Attr = Attrs[idx % Attrs.Length];
        e.Time = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(idx * 37L);
        Files.Add(e);
    }

    static byte[] Content(Entry e) {
        var buf = new byte[e.Size];
        if (e.Kind == 1 || e.Kind == 3) {
            var line = Encoding.ASCII.GetBytes("line " + e.Seed + " of the zfswin tree corpus, the same words again and again\n");
            for (long i = 0; i < buf.LongLength; i++) buf[i] = line[i % line.Length];
        }
        if (e.Kind == 2) new Random(e.Seed).NextBytes(buf);
        if (e.Kind == 3) { var half = new byte[buf.Length / 2]; new Random(e.Seed).NextBytes(half); Buffer.BlockCopy(half, 0, buf, 0, half.Length); }
        return buf;
    }

    static string Hex(byte[] data) { using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(data)).Replace("-", ""); }

    static void WriteOne(Entry e) {
        string p = Full(e.R, e.Rel);
        var data = Content(e);
        File.WriteAllBytes(p, data);
        e.Sha = Hex(data);
        File.SetCreationTimeUtc(p, e.Time);
        File.SetLastWriteTimeUtc(p, e.Time);
        File.SetAttributes(p, e.Attr);
    }

    public static string[] Write(int threads) {
        var errors = new ConcurrentQueue<string>();
        // The deepest directory of each chain (CreateDirectory makes the rest).
        var leaves = new List<KeyValuePair<int, string>>();
        for (int r = 0; r < DirSets.Count; r++)
            foreach (var d in DirSets[r])
                if (!DirSets[r].Any(o => o.Length > d.Length && o.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)))
                    leaves.Add(new KeyValuePair<int, string>(r, d));
        Parallel.ForEach(leaves, Opt(threads), d => {
            try { Directory.CreateDirectory(Full(d.Key, d.Value)); } catch (Exception x) { Note(errors, "mkdir " + d.Value + ": " + x.Message); }
        });
        Parallel.ForEach(Files, Opt(threads), e => {
            try { WriteOne(e); } catch (Exception x) { Note(errors, "write " + e.Rel + ": " + x.Message); }
        });
        return errors.ToArray();
    }

    public static Result Verify(int threads) {
        var res = new Result();
        var samples = new ConcurrentQueue<string>();
        Parallel.ForEach(Files.Where(f => !f.Gone), Opt(threads), e => {
            string p = Full(e.R, e.Rel);
            try {
                Interlocked.Increment(ref res.Checked);
                if (!File.Exists(p)) { Interlocked.Increment(ref res.Missing); Note(samples, "missing " + e.Rel); return; }
                var data = File.ReadAllBytes(p);
                if (data.LongLength != e.Size) { Interlocked.Increment(ref res.Size); Note(samples, "size " + e.Rel + " " + data.LongLength + " != " + e.Size); }
                else if (Hex(data) != e.Sha) { Interlocked.Increment(ref res.Data); Note(samples, "data " + e.Rel); }
                var a = File.GetAttributes(p) & Mask;
                if (a != (e.Attr & Mask)) { Interlocked.Increment(ref res.Attr); Note(samples, "attr " + e.Rel + " " + a + " != " + (e.Attr & Mask)); }
                var t = File.GetLastWriteTimeUtc(p);
                if (t != e.Time) { Interlocked.Increment(ref res.Time); Note(samples, "time " + e.Rel + " " + t.ToString("o") + " != " + e.Time.ToString("o")); }
            } catch (Exception x) { Interlocked.Increment(ref res.Errors); Note(samples, "error " + e.Rel + ": " + x.Message); }
        });
        res.Samples = samples.ToArray();
        return res;
    }

    // Windows' own folders: not part of the tree, and System Volume Information
    // is SYSTEM-only, so descending into it fails where ACLs are enforced.
    static bool Ours(string path) {
        string name = Path.GetFileName(path);
        return !name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) && !name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase);
    }

    // Files and directories each dataset holds, as Windows enumerates them.
    public static long[] Count() {
        var counts = new long[Roots.Length * 2];
        Parallel.For(0, Roots.Length, r => {
            long files = 0, dirs = 0;
            var pending = new Stack<string>();
            pending.Push(@"\\?\" + Roots[r]);
            while (pending.Count > 0) {
                string dir = pending.Pop();
                files += Directory.EnumerateFiles(dir).LongCount();
                foreach (string sub in Directory.EnumerateDirectories(dir).Where(Ours)) { dirs++; pending.Push(sub); }
            }
            counts[r * 2] = files;
            counts[r * 2 + 1] = dirs;
        });
        return counts;
    }

    // Every 20th file deleted, renamed and rewritten (5% each).
    public static string[] Mutate(int threads) {
        var errors = new ConcurrentQueue<string>();
        Parallel.For(0, Files.Count, Opt(threads), i => {
            var e = Files[i];
            string p = Full(e.R, e.Rel);
            try {
                switch (i % 20) {
                    case 3: File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); e.Gone = true; break;
                    // Windows sets Archive on renaming a System file (NTFS too):
                    // state the attributes again rather than test that rule.
                    case 7: File.Move(p, p + ".renamed"); e.Rel += ".renamed"; File.SetAttributes(p + ".renamed", e.Attr); break;
                    case 11:
                        File.SetAttributes(p, FileAttributes.Normal);
                        e.Seed ^= 0x5a5a5a; e.Size = (e.Size * 3 + 100) % 70000; e.Kind = (e.Kind + 1) % 4;
                        WriteOne(e); break;
                }
            } catch (Exception x) { Note(errors, "mutate " + e.Rel + ": " + x.Message); }
        });
        return errors.ToArray();
    }

    // Tab-separated: dataset index, path (/), size, SHA-256, DOS attributes, mtime (Unix s).
    public static void SaveManifest(string path, string[] datasets) {
        var sb = new StringBuilder();
        for (int r = 0; r < datasets.Length; r++)
            sb.Append("#dataset\t" + r + "\t" + datasets[r] + "\t" + Files.Count(f => f.R == r && !f.Gone) + "\t" + ExpectedDirs[r] + "\n");
        foreach (var e in Files.Where(f => !f.Gone).OrderBy(f => f.R).ThenBy(f => f.Rel, StringComparer.Ordinal))
            sb.Append(e.R + "\t" + e.Rel.Replace('\\', '/') + "\t" + e.Size + "\t" + e.Sha + "\t" + (int)(e.Attr & Mask) + "\t" + (long)(e.Time - Epoch).TotalSeconds + "\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static string[] LoadManifest(string path, string[] roots) {
        Roots = roots; Files.Clear(); DirSets.Clear();
        var names = new List<string>(); var dirs = new List<int>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8)) {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            if (f[0] == "#dataset") { names.Add(f[2]); dirs.Add(int.Parse(f[4])); continue; }
            Files.Add(new Entry { R = int.Parse(f[0]), Rel = f[1].Replace('/', '\\'), Size = long.Parse(f[2]), Sha = f[3],
                Attr = (FileAttributes)int.Parse(f[4]), Time = Epoch.AddSeconds(long.Parse(f[5])) });
        }
        ExpectedDirs = dirs.ToArray();
        return names.ToArray();
    }

    public static int[] ManifestCounts() { return Enumerable.Range(0, Roots.Length).Select(r => Files.Count(f => f.R == r && !f.Gone)).ToArray(); }
}
"@
}

# Mount path of a dataset as "X:\..\" with backslashes (the tree helper's form).
function TreeRoot([string]$dataset) {
    $p = MountPath $dataset
    if (-not $p) { return $null }
    $p = $p -replace '/', '\'
    if (-not $p.EndsWith('\')) { $p += '\' }
    $p
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
        # The snapshot is mounted on first access (see Retry).
        $snapFiles = if ($snapPath -and (Retry { if (Test-Path -LiteralPath $snapPath) { $true } })) { @(Manifest $snapPath) } else { @() }
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

    # Compression algorithms and deduplication with real data (both
    # editions).  Leaves zwin/c-* and zwin/dd* plus datafeatures.json for
    # scripts\vm\validate-datafeatures.py.
    "datafeatures" {
        $r = [ordered]@{}
        $zpool = Join-Path $installDir "bin\zpool.exe"
        function Sync { & $zpool sync $pool 2>&1 | Out-Null; Start-Sleep -Seconds 2 }
        function Prop([string]$ds, [string]$name) { ((Zfswin @('dataset', 'get', $ds, $name)).result.properties | Where-Object name -eq $name).value }
        function PoolProp([string]$name) { ((Zfswin @('pool', 'get', $pool, $name)).result.properties | Where-Object name -eq $name).value }
        function Ratio($value) { [double]("$value" -replace '[^0-9.]', '') }
        # The corpus: compressible text, incompressible random data, zeros.
        $text = [Text.Encoding]::UTF8.GetBytes((1..60000 | ForEach-Object { "line $_ of a compressible ZFS test corpus, the same words again and again`n" }) -join '')
        $random = [byte[]]::new(2MB); [Random]::new(2026).NextBytes($random)
        $zeros = [byte[]]::new(2MB)
        $expected = [ordered]@{ datasets = [ordered]@{} }

        $algorithms = "off", "lz4", "gzip-9", "zstd", "zstd-19", "zle", "lzjb"
        foreach ($alg in $algorithms) {
            $ds = "$pool/c-$alg"
            Check $r "create-$alg" (Zfswin @('dataset', 'create', $ds, '-o', "compression=$alg")).exit 0
            $root = MountPath $ds
            if (-not $root) { $r["mounted-$alg"] = [ordered]@{ value = ""; expected = "a mount path"; pass = $false }; continue }
            [IO.File]::WriteAllBytes((Join-Path $root "text.txt"), $text)
            [IO.File]::WriteAllBytes((Join-Path $root "random.bin"), $random)
            [IO.File]::WriteAllBytes((Join-Path $root "zeros.bin"), $zeros)
            $expected.datasets[$ds] = [ordered]@{ compression = $alg; files = @(Manifest $root) }
        }
        Sync
        foreach ($alg in $algorithms) {
            $ds = "$pool/c-$alg"
            Check $r "property-$alg" (Prop $ds compression) $alg
            $ratio = Ratio (Prop $ds compressratio)
            $r["ratio-$alg"] = $ratio
            # off stores everything; zle only squeezes runs of zeros (the
            # zero file); every other algorithm also shrinks the text.
            $ok = switch ($alg) { "off" { $ratio -le 1.01 } "zle" { $ratio -ge 1.0 } default { $ratio -ge 1.5 } }
            Check $r "compresses-$alg" $ok $true
        }

        # Deduplication: the same 16 MiB of random data four times (written,
        # not copied, so no block cloning can stand in for dedup).
        $dd = "$pool/dd"
        Check $r "create-dedup" (Zfswin @('dataset', 'create', $dd, '-o', 'dedup=on', '-o', 'compression=off')).exit 0
        $ddRoot = MountPath $dd
        $block = [byte[]]::new(16MB); [Random]::new(77).NextBytes($block)
        $before = Ratio (PoolProp dedupratio)
        foreach ($n in 1..4) { [IO.File]::WriteAllBytes((Join-Path $ddRoot "copy$n.bin"), $block) }
        Sync
        $after = Ratio (PoolProp dedupratio)
        $r.dedupratio = [ordered]@{ before = $before; afterFourCopies = $after }
        Check $r "dedup-ratio" ($after -ge 3.5) $true
        # Dedup and compression together.
        $ddz = "$pool/ddz"
        Check $r "create-dedup-zstd" (Zfswin @('dataset', 'create', $ddz, '-o', 'dedup=on', '-o', 'compression=zstd')).exit 0
        $ddzRoot = MountPath $ddz
        foreach ($n in 1..3) { [IO.File]::WriteAllBytes((Join-Path $ddzRoot "text$n.txt"), $text) }
        Sync
        Check $r "dedup-zstd-compresses" ((Ratio (Prop $ddz compressratio)) -ge 1.5) $true
        $expected.datasets[$dd] = [ordered]@{ compression = "off"; dedup = "on"; files = @(Manifest $ddRoot) }
        $expected.datasets[$ddz] = [ordered]@{ compression = "zstd"; dedup = "on"; files = @(Manifest $ddzRoot) }

        # Everything reads back the same after an unmount and mount.
        foreach ($ds in @($expected.datasets.Keys)) {
            Check $r "unmount-$ds" (Zfswin @('dataset', 'unmount', $ds)).exit 0
            Check $r "mount-$ds" (Zfswin @('dataset', 'mount', $ds)).exit 0
            $root = MountPath $ds
            $now = if ($root) { @(Manifest $root) } else { @() }
            Check $r "data-$ds" ((Compare-Object @($now | ForEach-Object { "$($_.path)|$($_.sha256)" }) @($expected.datasets[$ds].files | ForEach-Object { "$($_.path)|$($_.sha256)" })) -eq $null) $true
        }

        # Freeing deduplicated copies lowers the ratio again; one copy stays.
        $ddRoot = MountPath $dd
        foreach ($n in 2..4) { Remove-Item -LiteralPath (Join-Path $ddRoot "copy$n.bin") }
        Sync; Start-Sleep -Seconds 6; Sync
        $freed = Ratio (PoolProp dedupratio)
        $r.dedupratio.afterDelete = $freed
        Check $r "dedup-ratio-after-delete" ($freed -lt $after) $true
        Check $r "dedup-survivor-intact" ((Get-FileHash (Join-Path $ddRoot "copy1.bin")).Hash -eq ($expected.datasets[$dd].files | Where-Object path -eq "copy1.bin").sha256) $true
        $expected.datasets[$dd].files = @(Manifest $ddRoot)
        $expected.dedupratio = "$freed"
        $expected | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 "$work\datafeatures.json"
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # The user-mode engine's memory (issue #3): unique data, twice the
    # guest's RAM, written the way a sync client writes (a temp file in
    # 128 KiB chunks, renamed; older files deleted) to a pool with dedup on,
    # on a 16 GiB sparse image: a deduplicated block's free goes through the
    # DDT log and lands many txgs later, and a 4 GiB pool filled (98%, then
    # suspended on ENOSPC) with 1.5 GiB live.  zfswin-um's private bytes must
    # level off within RAM/3 (an ARC of RAM/4 and all around it), and fall
    # when another process takes the memory.  User mode only: the kernel
    # edition's ARC is not in a process.
    "memory" {
        $r = [ordered]@{}
        $zpool = Join-Path $installDir "bin\zpool.exe"
        $memPool = "zmem"
        $ram = [long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        function Engine { [long](@(Get-Process zfswin-um -ErrorAction SilentlyContinue | Select-Object -First 1).PrivateMemorySize64) }
        function Available { [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB }
        function MB([long]$bytes) { [long]($bytes / 1MB) }
        $r.ramMB = MB $ram
        $r.engineStartMB = MB (Engine)
        $img = "$images\mem.img"
        Remove-Item -LiteralPath $img -Force -ErrorAction SilentlyContinue
        [IO.File]::Create($img).Dispose()
        & fsutil.exe sparse setflag $img | Out-Null
        & fsutil.exe file seteof $img (16GB) | Out-Null
        Check $r "create" (Zfswin @('pool', 'create', $memPool, $img, '-O', 'dedup=on', '-O', 'compression=off', '--yes')).exit 0
        $root = MountPath $memPool
        if (-not $root) { $r.mounted = [ordered]@{ value = ""; expected = "a mount path"; pass = $false } }
        else {
            $fileSize = 256MB; $live = 6
            $count = [int][Math]::Ceiling(2 * $ram / $fileSize)
            $buffer = [byte[]]::new($fileSize)
            $hashes = @{}; $samples = New-Object Collections.Generic.List[long]
            $sha = [Security.Cryptography.SHA256]::Create()
            # Watchdog: writes stalled for 2 minutes -> a full dump of the
            # engine in C:\zfswin\dumps (the host copies it off the disk).
            $beat = "$work\mem-heartbeat"; [IO.File]::WriteAllText($beat, "start")
            $watch = @"
`$deadline = (Get-Date).AddHours(3)
while ((Get-Date) -lt `$deadline -and (Get-Process -Id $PID -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 10
    if (((Get-Date) - (Get-Item -LiteralPath '$beat').LastWriteTime).TotalSeconds -gt 120) {
        `$e = Get-Process zfswin-um | Select-Object -First 1
        New-Item -ItemType Directory -Force C:\zfswin\dumps | Out-Null
        "stalled at `$(Get-Date -f o): engine `$([long](`$e.PrivateMemorySize64 / 1MB)) MB, `$(`$e.Threads.Count) threads, cpu `$(`$e.CPU) s; `$(Get-Content -LiteralPath '$beat')" | Set-Content C:\zfswin\dumps\hang.txt
        & rundll32.exe C:\Windows\System32\comsvcs.dll, MiniDump `$e.Id C:\zfswin\dumps\zfswin-um-hang.dmp full
        Start-Sleep -Seconds 60
        "dump: `$((Get-Item C:\zfswin\dumps\zfswin-um-hang.dmp -ErrorAction SilentlyContinue).Length) bytes" | Add-Content C:\zfswin\dumps\hang.txt
        & cmd.exe /c "fsutil volume flush C: >nul 2>&1"
        break
    }
}
"@
            $watchdog = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($watch))) -WindowStyle Hidden -PassThru
            $t = [Diagnostics.Stopwatch]::StartNew()
            foreach ($n in 1..$count) {
                [Random]::new($n).NextBytes($buffer)
                $tmp = Join-Path $root ".f$n.tmp"
                [IO.File]::WriteAllText($beat, "creating .f$n.tmp")
                $fs = [IO.FileStream]::new($tmp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096)
                for ($o = 0; $o -lt $fileSize; $o += 128KB) {
                    $fs.Write($buffer, $o, 128KB)
                    if (($o -band (16MB - 1)) -eq 0) { [IO.File]::WriteAllText($beat, "writing .f$n.tmp at $($o / 1MB) MB") }
                }
                [IO.File]::WriteAllText($beat, "closing and renaming .f$n.tmp")
                $fs.Dispose()
                Move-Item -LiteralPath $tmp -Destination (Join-Path $root "f$n.bin")
                $hashes[$n] = [BitConverter]::ToString($sha.ComputeHash($buffer)) -replace '-', ''
                [IO.File]::WriteAllText($beat, "deleting f$($n - $live).bin")
                if ($n -gt $live) { Remove-Item -LiteralPath (Join-Path $root "f$($n - $live).bin") -Force }
                $samples.Add((Engine))
                # Streamed to the host, which keeps them if the stage times out.
                [Console]::Out.WriteLine("progress: file $n/$count at $([int]$t.Elapsed.TotalSeconds) s, engine $(MB $samples[-1]) MB, available $(MB (Available)) MB")
                [Console]::Out.Flush()
            }
            [IO.File]::WriteAllText($beat, "zpool sync")
            & $zpool sync $memPool 2>&1 | Out-Null
            Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 10
            $settled = Engine
            $peak = ($samples | Measure-Object -Maximum).Maximum
            $half = [int]($samples.Count / 2)
            $firstHalf = ($samples | Select-Object -First $half | Measure-Object -Maximum).Maximum
            $lastQuarter = ($samples | Select-Object -Last ([Math]::Max(1, [int]($samples.Count / 4))) | Measure-Object -Maximum).Maximum
            $r.writtenMB = MB ($count * [long]$fileSize)
            $r.writeSeconds = [int]$t.Elapsed.TotalSeconds
            $r.engineMB = @($samples | ForEach-Object { MB $_ })
            $r.settledMB = MB $settled
            # The whole process: ARC (RAM/4), dbufs, dedup table and heap,
            # plus a fixed 256 MiB for the engine and WinFsp themselves.
            $limit = [long]($ram / 3 + 256MB)
            $r.peak = [ordered]@{ value = MB $peak; expected = "<= $(MB $limit)"; pass = ($peak -le $limit) }
            # A leak keeps growing; a cache levels off.
            $r.levelsOff = [ordered]@{ value = "$(MB $firstHalf) then $(MB $lastQuarter)"; expected = "last quarter <= first half + 15% + 256"; pass = ($lastQuarter -le $firstHalf * 1.15 + 256MB) }

            # Pressure: another process takes memory until the guest has
            # RAM/32 available (below the ARC's reserve of RAM/16), for 90 s.
            $target = [long]($ram / 32); $cap = [long]($ram * 3 / 4)
            $helper = @"
`$chunks = New-Object Collections.ArrayList; `$total = [long]0; `$rnd = [Random]::new(1)
`$end = (Get-Date).AddSeconds(100)
while ((Get-Date) -lt `$end) {
    `$free = [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
    if (`$free -gt $target -and `$total -lt $cap) {
        try { `$b = [byte[]]::new(64MB); `$rnd.NextBytes(`$b); [void]`$chunks.Add(`$b); `$total += 64MB } catch { Start-Sleep -Milliseconds 500 }
    } else { Start-Sleep -Milliseconds 500 }
}
Start-Sleep -Seconds 600
"@
            $before = Engine
            $p = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helper))) -WindowStyle Hidden -PassThru
            $low = $before; $pressure = @()
            foreach ($i in 1..18) {
                Start-Sleep -Seconds 5
                $e = Engine; $low = [Math]::Min($low, $e)
                $pressure += "$(MB $e)/$(MB (Available))"
            }
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $r.pressureEngineMBAvailableMB = $pressure
            $r.releases = [ordered]@{ value = "$(MB $before) -> $(MB $low)"; expected = "at most half"; pass = ($low -le $before / 2) }

            # The data is all there after the pressure.
            $bad = @()
            foreach ($n in ($count - $live + 1)..$count) {
                $h = (Get-FileHash -LiteralPath (Join-Path $root "f$n.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
                if ($h -ne $hashes[$n]) { $bad += $n }
            }
            Check $r "data-intact" ($bad -join ',') ""
        }
        Check $r "export" (Zfswin @('pool', 'export', $memPool)).exit 0
        Remove-Item -LiteralPath $img -Force -ErrorAction SilentlyContinue
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # A pool on a whole disk (a VHDX attached in the guest) through a
    # service restart, as the GUI's mount editor does one (user mode): the
    # mount moved to another drive letter in the registry, the service
    # restarted, the pool and its file back on the new letter.  The pool
    # is recorded as "#offset#length#\\?\PhysicalDriveN", which the
    # restart could not import (the GUI "destroyed" the pool).  Then the
    # recorded disk number is made wrong, as after disks are added or
    # removed: the restart finds the pool on the disks by name.
    "wholedisk" {
        $r = [ordered]@{}
        $wdPool = "zwd"
        $vhd = "$images\whole.vhdx"
        $base = "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode"
        function DiskPart([string[]]$lines) { $f = "$work\diskpart.txt"; Set-Content -LiteralPath $f -Value $lines -Encoding ascii; & diskpart.exe /s $f 2>&1 | Out-String }
        function RestartAndWait([string]$letter) {
            Restart-Service ZfsWinUserMode -Force
            $deadline = (Get-Date).AddMinutes(3)
            do { Start-Sleep -Seconds 2; $m = MountPath $wdPool } while ($m -ne "$letter\" -and (Get-Date) -lt $deadline)
            $m
        }
        if (Test-Path -LiteralPath $vhd) { DiskPart @("select vdisk file=`"$vhd`"", "detach vdisk") | Out-Null; Remove-Item -LiteralPath $vhd -Force }
        $r.diskpart = DiskPart @("create vdisk file=`"$vhd`" maximum=2048 type=expandable", "select vdisk file=`"$vhd`"", "attach vdisk")
        $disk = Get-Disk | Where-Object Location -eq $vhd | Select-Object -First 1
        $dev = if ($disk) { "\\?\PhysicalDrive$($disk.Number)" } else { "" }
        $r.device = $dev
        Check $r "create" (Zfswin @('pool', 'create', $wdPool, $dev, '--yes')).exit 0
        $root = MountPath $wdPool
        $bytes = [byte[]]::new(8MB); [Random]::new(31).NextBytes($bytes)
        $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', ''
        if ($root) { [IO.File]::WriteAllBytes((Join-Path $root "kept.bin"), $bytes) }
        $r.recorded = (Get-ItemProperty -LiteralPath "$base\Pools\$wdPool" -ErrorAction SilentlyContinue).Sources
        Check $r "recorded-as-partition" ("$($r.recorded)" -like "#*#*#*PhysicalDrive*") $true

        # The GUI's edit: the same values, another drive letter, a restart.
        $free = @([char[]](77..89) | Where-Object { -not (Test-Path "$($_):\") -and "$($_):\" -ne $root }) | Select-Object -Last 1
        $letter = "$($free):"
        $key = "$base\Mounts\auto-$wdPool"
        Check $r "mount-recorded" (Test-Path -LiteralPath $key) $true
        Set-ItemProperty -LiteralPath $key -Name MountPoint -Value $letter
        $now = RestartAndWait $letter
        Check $r "pool-after-restart" (@((Zfswin @('pool', 'list')).result.pools | ForEach-Object name) -contains $wdPool) $true
        Check $r "new-letter" $now "$letter\"
        Check $r "file-after-restart" $(if ($now) { (Get-FileHash -LiteralPath (Join-Path $now "kept.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }) $hash

        # Another disk number recorded: found on the disks by name.
        if ($r.recorded) {
            Set-ItemProperty -LiteralPath "$base\Pools\$wdPool" -Name Sources -Value ("$($r.recorded)" -replace 'PhysicalDrive\d+', 'PhysicalDrive99')
            $now = RestartAndWait $letter
            Check $r "pool-after-renumber" (@((Zfswin @('pool', 'list')).result.pools | ForEach-Object name) -contains $wdPool) $true
            Check $r "file-after-renumber" $(if ($now) { (Get-FileHash -LiteralPath (Join-Path $now "kept.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }) $hash
            # Where it was found, recorded: the pool comes back from that.
            $r.rerecordedAs = (Get-ItemProperty -LiteralPath "$base\Pools\$wdPool" -ErrorAction SilentlyContinue).Sources
            Check $r "rerecorded" ([bool]$r.rerecordedAs -and "$($r.rerecordedAs)" -notlike "*PhysicalDrive99*") $true
            $now = RestartAndWait $letter
            Check $r "pool-after-rerecord" (@((Zfswin @('pool', 'list')).result.pools | ForEach-Object name) -contains $wdPool) $true
            Check $r "file-after-rerecord" $(if ($now) { (Get-FileHash -LiteralPath (Join-Path $now "kept.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }) $hash
        }
        Check $r "export" (Zfswin @('pool', 'export', $wdPool)).exit 0
        DiskPart @("select vdisk file=`"$vhd`"", "detach vdisk") | Out-Null
        Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # Before the guest restarts (Test-UserModeInVm.ps1 phase reboot): pool
    # zrb on the empty 2 GiB SCSI disk the host added, a file on it, and
    # its drive letter changed in the registry as the GUI does.
    "rebootprep" {
        $r = [ordered]@{}
        $rbPool = "zrb"
        $base = "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode"
        $disk = Get-Disk | Where-Object { $_.Size -eq 2GB -and $_.PartitionStyle -eq 'RAW' } | Select-Object -First 1
        if ($disk) {
            if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
        }
        $r.device = if ($disk) { "\\?\PhysicalDrive$($disk.Number)" } else { "" }
        Check $r "create" (Zfswin @('pool', 'create', $rbPool, $r.device, '--yes')).exit 0
        $root = MountPath $rbPool
        $bytes = [byte[]]::new(8MB); [Random]::new(37).NextBytes($bytes)
        $r.hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', ''
        if ($root) { [IO.File]::WriteAllBytes((Join-Path $root "kept.bin"), $bytes) }
        Check $r "written" ([bool]($root -and (Test-Path -LiteralPath (Join-Path $root "kept.bin")))) $true
        # Issue #15: an image-file pool imported with zpool and a cache file
        # at a drive-letter path (before zrb's new letter is chosen, which
        # must not be the one zrc takes).
        $zpool = Join-Path $installDir "bin\zpool.exe"
        $rcDir = "$images\zrc"; $rcCache = "$images\zrc\zrc.cache"
        if ((Zfswin @('pool', 'list')).result.pools | Where-Object name -eq "zrc") { Zfswin @('pool', 'export', 'zrc') | Out-Null }
        Remove-Item -Recurse -Force $rcDir -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $rcDir | Out-Null
        & fsutil.exe file createnew "$rcDir\zrc.img" (256MB) | Out-Null
        & $zpool create zrc "\\?\$rcDir\zrc.img" 2>$null | Out-Null
        & $zpool export zrc 2>$null | Out-Null
        & $zpool import -d $rcDir -o "cachefile=$rcCache" zrc 2>$null | Out-Null
        Check $r "cachefile-import" $LASTEXITCODE 0
        $rcRoot = MountPath "zrc"
        if ($rcRoot) { [IO.File]::WriteAllBytes((Join-Path $rcRoot "kept.bin"), $bytes) }
        Check $r "cachefile-written" ([bool]($rcRoot -and (Test-Path -LiteralPath (Join-Path $rcRoot "kept.bin")))) $true
        Check $r "cachefile-recorded" (Get-ItemProperty -LiteralPath "$base\Pools\zrc" -ErrorAction SilentlyContinue).CacheFile $rcCache
        $free = @([char[]](77..89) | Where-Object { -not (Test-Path "$($_):\") -and "$($_):\" -ne $root }) | Select-Object -Last 1
        $r.letter = "$($free):"
        $key = "$base\Mounts\auto-$rbPool"
        Check $r "mount-recorded" (Test-Path -LiteralPath $key) $true
        Set-ItemProperty -LiteralPath $key -Name MountPoint -Value $r.letter -ErrorAction SilentlyContinue
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # After the restart: zrb imported and mounted at the new letter by the
    # service alone (up to 4 minutes after boot), the file intact, the
    # partition path recorded; then exported so the host can remove the disk.
    "rebootcheck" {
        $r = [ordered]@{}
        $rbPool = "zrb"
        $base = "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode"
        $letter = "$($decoded.Letter)"
        $deadline = (Get-Date).AddMinutes(4)
        do { $m = MountPath $rbPool; if ($m -ne "$letter\") { Start-Sleep -Seconds 3 } } while ($m -ne "$letter\" -and (Get-Date) -lt $deadline)
        $r.bootToMount = [math]::Round(((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds)
        Check $r "pool-after-reboot" (@((Zfswin @('pool', 'list')).result.pools | ForEach-Object name) -contains $rbPool) $true
        Check $r "mounted-at-new-letter" $m "$letter\"
        Check $r "file-after-reboot" $(if ($m) { (Get-FileHash -LiteralPath (Join-Path $m "kept.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }) "$($decoded.Hash)"
        $r.recorded = (Get-ItemProperty -LiteralPath "$base\Pools\$rbPool" -ErrorAction SilentlyContinue).Sources
        Check $r "recorded-as-partition" ("$($r.recorded)" -like "#*#*#*PhysicalDrive*") $true
        $r.events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -like '*ZfsWin*' } | Select-Object -First 10 | ForEach-Object { "$($_.LevelDisplayName): $($_.Message)" })
        # Issue #15: the image-file pool comes back with its cache file.
        $zpool = Join-Path $installDir "bin\zpool.exe"
        $rcCache = "$images\zrc\zrc.cache"
        $rcRoot = MountPath "zrc"
        Check $r "cachefile-pool-mounted" ([bool]$rcRoot) $true
        Check $r "cachefile-pool-file" $(if ($rcRoot) { (Get-FileHash -LiteralPath (Join-Path $rcRoot "kept.bin") -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash }) "$($decoded.Hash)"
        Check $r "cachefile-property" ((& $zpool get -H -o value cachefile zrc 2>$null) -join '') $rcCache
        Check $r "cachefile-file" ((Get-Item -LiteralPath $rcCache -ErrorAction SilentlyContinue).Length -gt 0) $true
        Check $r "cachefile-export" (Zfswin @('pool', 'export', 'zrc')).exit 0
        Check $r "export" (Zfswin @('pool', 'export', $rbPool)).exit 0
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # Many small files in deep trees, all CPUs (both editions), on pool
    # ztree: a mirror of two 1 GiB images with a write log (SLOG) and a read
    # cache (L2ARC), a dataset per compression/recordsize (t-gzip-1 with
    # sync=always, so its writes go through the log device).  Write and
    # verify; one mirror disk offline while the tree changes, online and
    # resilvered; one disk's image corrupted while exported and repaired by
    # a scrub; that disk replaced by a new one; a clean scrub; export and
    # import; verify after each step.  Then the cache, log and second disk
    # are removed, and the pool is left exported on tree\tree.img with
    # tree\tree-manifest.tsv for the other platforms (stage treeverify,
    # scripts\vm\validate-tree.py).
    "tree" {
        $r = [ordered]@{}
        $treePool = "ztree"
        $treeDir = "$work\tree"
        $perDataset = if ($decoded -and $decoded.Files) { [int]$decoded.Files } else { 3500 }
        $threads = [Environment]::ProcessorCount
        $r.threads = $threads
        if ((Zfswin @('pool', 'list')).result.pools | Where-Object name -eq $treePool) { Zfswin @('pool', 'export', $treePool) | Out-Null }
        New-Item -ItemType Directory -Force $treeDir | Out-Null
        Remove-Item "$treeDir\*" -Force -ErrorAction SilentlyContinue
        $diskA = "$treeDir\tree.img"; $diskB = "$treeDir\tree-b.img"; $diskC = "$treeDir\tree-c.img"
        $logDisk = "$treeDir\tree-log.img"; $cacheDisk = "$treeDir\tree-cache.img"
        foreach ($d in $diskA, $diskB) { & fsutil.exe file createnew $d (1GB) | Out-Null }
        foreach ($d in $logDisk, $cacheDisk) { & fsutil.exe file createnew $d (256MB) | Out-Null }
        Check $r "create-mirror" (Zfswin @('pool', 'create', $treePool, '--mirror', $diskA, $diskB, '--yes')).exit 0
        Check $r "add-log" (Zfswin @('vdev', 'add-log', $treePool, $logDisk, '--yes')).exit 0
        Check $r "add-cache" (Zfswin @('vdev', 'add-cache', $treePool, $cacheDisk, '--yes')).exit 0
        $zpool = Join-Path $installDir "bin\zpool.exe"
        # zpool's own JSON: scan statistics with repaired bytes and per-disk errors.
        function TreeStatus { ((& $zpool status -j --json-int -p $treePool 2>$null) -join "`n" | ConvertFrom-Json).pools.$treePool }
        # Leaf disks under a vdev, or under a name->vdev map (logs, l2cache).
        function TreeLeaves($node) {
            if ($null -eq $node -or $node -isnot [pscustomobject]) { return }
            if ($node.PSObject.Properties['path']) { $node }
            elseif ($node.PSObject.Properties['vdevs']) { foreach ($p in $node.vdevs.PSObject.Properties) { TreeLeaves $p.Value } }
            elseif (-not $node.PSObject.Properties['vdev_type']) { foreach ($p in $node.PSObject.Properties) { TreeLeaves $p.Value } }
        }
        function TreeDisk([string]$file) { @(TreeLeaves (TreeStatus).vdevs.$treePool | Where-Object { $_.path -like "*$(Split-Path -Leaf $file)" }) | Select-Object -First 1 }
        function TreeWait([string]$function, [double]$since) {
            $deadline = (Get-Date).AddMinutes(15)
            do {
                Start-Sleep -Seconds 2
                $scan = (TreeStatus).scan_stats
            } while (-not ($scan -and $scan.function -eq $function -and $scan.state -eq 'FINISHED' -and [double]$scan.start_time -ge $since) -and (Get-Date) -lt $deadline)
            $scan
        }
        function UnixNow { [double][DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 2 }
        # One line of "zpool iostat -v -H -p" for a device: alloc, free, read/write ops, read/write bytes.
        function TreeIostat([string]$file) {
            $line = @(& $zpool iostat -v -H -p $treePool 2>$null | Where-Object { $_ -match [regex]::Escape((Split-Path -Leaf $file)) }) | Select-Object -First 1
            if (-not $line) { return $null }
            $f = @($line.Trim() -split "`t" | Where-Object { $_ -ne '' } | ForEach-Object { $n = 0L; if ([long]::TryParse($_, [ref]$n)) { $n } else { 0L } })
            [ordered]@{ alloc = $f[1]; readOps = $f[3]; writeOps = $f[4]; readBytes = $f[5]; writeBytes = $f[6] }
        }
        $specs = [ordered]@{
            "t-off" = @('compression=off'); "t-lz4" = @('compression=lz4', 'recordsize=1M'); "t-gzip-1" = @('compression=gzip-1', 'recordsize=8K', 'sync=always')
            "t-gzip-9" = @('compression=gzip-9'); "t-zstd-1" = @('compression=zstd-1'); "t-zstd-19" = @('compression=zstd-19', 'recordsize=16K'); "t-zle" = @('compression=zle')
        }
        $datasets = @($specs.Keys | ForEach-Object { "$treePool/$_" })
        foreach ($name in $specs.Keys) {
            $props = @(); foreach ($p in $specs[$name]) { $props += @('-o', $p) }
            Check $r "create-$name" (Zfswin (@('dataset', 'create', "$treePool/$name") + $props)).exit 0
        }
        $roots = @($datasets | ForEach-Object { TreeRoot $_ })
        Check $r "mounted" (@($roots | Where-Object { $_ }).Count) $datasets.Count
        if (@($roots | Where-Object { $_ }).Count -ne $datasets.Count) {
            $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
            $r | ConvertTo-Json -Depth 6 -Compress
            break
        }
        Initialize-Tree
        $total = [ZfsTree]::Plan([string[]]$roots, $perDataset, 20, 1000)
        $r.files = $total
        $r.directories = ([ZfsTree]::ExpectedDirs | Measure-Object -Sum).Sum
        function TreeSync { & $zpool sync $treePool 2>&1 | Out-Null }
        function TreeRemount([string]$phase) {
            Check $r "export-$phase" (Zfswin @('pool', 'export', $treePool)).exit 0
            $w = [Diagnostics.Stopwatch]::StartNew()
            Check $r "import-$phase" (Zfswin @('pool', 'import', $treePool, '--dir', $treeDir)).exit 0
            $r["importSeconds-$phase"] = [math]::Round($w.Elapsed.TotalSeconds, 1)
            $now = @($datasets | ForEach-Object { TreeRoot $_ })
            Check $r "mounted-$phase" (@($now | Where-Object { $_ }).Count) $datasets.Count
            if (@($now | Where-Object { $_ }).Count -eq $datasets.Count) { [ZfsTree]::Roots = [string[]]$now; return $true }
            $false
        }
        function TreeVerify([string]$phase) {
            $t = [Diagnostics.Stopwatch]::StartNew()
            $v = [ZfsTree]::Verify($threads)
            $r["verify-$phase"] = [ordered]@{ seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); checked = $v.Checked; filesPerSecond = [int]($v.Checked / [math]::Max($t.Elapsed.TotalSeconds, 0.001)); samples = $v.Samples }
            Check $r "intact-$phase" "$($v.Missing)/$($v.Size)/$($v.Data)/$($v.Attr)/$($v.Time)/$($v.Errors)" "0/0/0/0/0/0"
        }
        function TreeCount([string]$phase) {
            $c = [ZfsTree]::Count()
            $files = [ZfsTree]::ManifestCounts()
            $got = @(); $want = @()
            for ($i = 0; $i -lt $datasets.Count; $i++) { $got += "$($c[$i * 2])/$($c[$i * 2 + 1])"; $want += "$($files[$i])/$([ZfsTree]::ExpectedDirs[$i])" }
            Check $r "count-$phase" ($got -join ' ') ($want -join ' ')
        }

        # zpool iostat without an interval prints average rates since import,
        # so the log device is sampled every 2 s while the tree is written.
        $iostat = Start-Job -ArgumentList $zpool, $treePool { param($zp, $p) & $zp iostat -v -H -p $p 2 2>&1 | ForEach-Object { "$_" } }
        # A job takes seconds to start: write only once it is sampling.
        $wait = [Diagnostics.Stopwatch]::StartNew()
        while (-not $iostat.HasMoreData -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 250 }
        $t = [Diagnostics.Stopwatch]::StartNew()
        $errors = [ZfsTree]::Write($threads)
        TreeSync
        Start-Sleep -Seconds 5
        Stop-Job $iostat
        $logName = [regex]::Escape((Split-Path -Leaf $logDisk))
        $samples = @(Receive-Job $iostat | Where-Object { $_ -match $logName } | Select-Object -Skip 1 | ForEach-Object {
            $n = 0L; $f = @($_.Trim() -split "`t" | Where-Object { $_ -ne '' }); if ($f.Count -ge 5 -and [long]::TryParse($f[4], [ref]$n)) { $n } else { 0L } })
        Remove-Job $iostat -Force
        $r.write = [ordered]@{ seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); filesPerSecond = [int]($total / [math]::Max($t.Elapsed.TotalSeconds, 0.001)); errors = @($errors) }
        Check $r "write-errors" @($errors).Count 0
        TreeVerify "written"
        TreeCount "written"
        # Write log: sync=always on t-gzip-1 sends its writes through the SLOG.
        $r.slog = [ordered]@{ intervals = $samples.Count; maxWriteOpsPerSecond = ($samples | Measure-Object -Maximum).Maximum; sumWriteOpsPerSecond = ($samples | Measure-Object -Sum).Sum }
        Check $r "slog-written" (@($samples | Where-Object { $_ -gt 0 }).Count -gt 0) $true

        # A broken drive: one mirror disk offline while the tree changes.
        $b = TreeDisk $diskB
        & $zpool offline $treePool $b.guid 2>&1 | Out-Null
        Check $r "offline-degraded" (TreeStatus).state "DEGRADED"
        $t.Restart()
        $errors = [ZfsTree]::Mutate($threads)
        TreeSync
        $r.mutate = [ordered]@{ seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); errors = @($errors) }
        Check $r "mutate-errors" @($errors).Count 0
        TreeVerify "mutated-degraded"
        TreeCount "mutated-degraded"
        $since = UnixNow
        & $zpool online $treePool $b.guid 2>&1 | Out-Null
        $scan = TreeWait 'RESILVER' $since
        Check $r "online-resilvered" "$($scan.function)/$($scan.state)/$($scan.errors)" "RESILVER/FINISHED/0"
        Check $r "online-state" (TreeStatus).state "ONLINE"
        TreeVerify "resilvered"

        # Silent corruption: random blocks of disk B overwritten while the pool
        # is exported (not the labels at either end); reads and a scrub must
        # repair them from disk A.
        Check $r "export-corrupt" (Zfswin @('pool', 'export', $treePool)).exit 0
        $rng = [Random]::new(1907)
        $junk = [byte[]]::new(64KB)
        $fs = [IO.FileStream]::new($diskB, 'Open', 'ReadWrite', 'None')
        for ($i = 0; $i -lt 256; $i++) {
            $rng.NextBytes($junk)
            $fs.Position = 8MB + [long]$i * 2MB + $rng.Next(0, 1MB)
            $fs.Write($junk, 0, $junk.Length)
        }
        $fs.Flush($true); $fs.Dispose()
        $r.corrupted = "256 x 64 KiB of $(Split-Path -Leaf $diskB) between 8 MiB and 520 MiB"
        Check $r "import-corrupt" (Zfswin @('pool', 'import', $treePool, '--dir', $treeDir)).exit 0
        $roots = @($datasets | ForEach-Object { TreeRoot $_ })
        if (@($roots | Where-Object { $_ }).Count -eq $datasets.Count) { [ZfsTree]::Roots = [string[]]$roots }
        TreeVerify "corrupt-disk"
        $since = UnixNow
        & $zpool scrub $treePool 2>&1 | Out-Null
        $scan = TreeWait 'SCRUB' $since
        $bAfter = TreeDisk $diskB
        $r.scrubRepair = [ordered]@{ repairedBytes = $scan.processed; errors = $scan.errors; diskBChecksumErrors = $bAfter.checksum_errors }
        Check $r "scrub-repairs" "$($scan.function)/$($scan.state)/$($scan.errors)" "SCRUB/FINISHED/0"
        Check $r "corruption-detected" ([long]$scan.processed -gt 0 -or [long]$bAfter.checksum_errors -gt 0) $true
        & $zpool clear $treePool 2>&1 | Out-Null
        TreeVerify "repaired"

        # A failed drive replaced: disk B out, a new disk C in, resilvered.
        & fsutil.exe file createnew $diskC (1GB) | Out-Null
        $since = UnixNow
        Check $r "replace" (Zfswin @('vdev', 'replace', $treePool, $diskB, $diskC, '--yes')).exit 0
        $scan = TreeWait 'RESILVER' $since
        Check $r "replace-resilvered" "$($scan.function)/$($scan.state)/$($scan.errors)" "RESILVER/FINISHED/0"
        $members = @(TreeLeaves (TreeStatus).vdevs.$treePool | ForEach-Object { Split-Path -Leaf ($_.path -replace '/', '\') }) | Sort-Object
        Check $r "replace-members" ($members -join ',') ((@('tree.img', 'tree-c.img') | Sort-Object) -join ',')
        TreeVerify "replaced"

        # A clean scrub of the whole pool.
        $since = UnixNow
        & $zpool scrub $treePool 2>&1 | Out-Null
        $scan = TreeWait 'SCRUB' $since
        $cksum = (@(TreeLeaves (TreeStatus).vdevs.$treePool | ForEach-Object { [long]$_.checksum_errors }) | Measure-Object -Sum).Sum
        Check $r "scrub-clean" "$($scan.function)/$($scan.state)/$($scan.errors)/$($scan.processed)/$cksum" "SCRUB/FINISHED/0/0/0"

        # Export and import: the datasets mount again (all of them at once);
        # the read cache refills from the reads.
        if (TreeRemount "reimport") {
            TreeVerify "reimported"
            TreeCount "reimported"
        }
        $cache = TreeIostat $cacheDisk
        $r.l2arc = $cache
        Check $r "l2arc-in-use" ($cache -and $cache.alloc -gt 0) $true

        # Back to one disk for the other platforms: no cache, no log, no mirror.
        Check $r "remove-cache" (Zfswin @('vdev', 'remove', $treePool, $cacheDisk, '--yes')).exit 0
        Check $r "remove-log" (Zfswin @('vdev', 'remove', $treePool, $logDisk, '--yes')).exit 0
        Check $r "detach" (Zfswin @('vdev', 'detach', $treePool, $diskC, '--yes')).exit 0
        $st = TreeStatus
        $members = @(TreeLeaves $st.vdevs.$treePool | ForEach-Object { Split-Path -Leaf ($_.path -replace '/', '\') })
        Check $r "single-disk" "$($members -join ',')/$(@(TreeLeaves $st.logs).Count)/$(@(TreeLeaves $st.l2cache).Count)" "tree.img/0/0"
        TreeVerify "single-disk"
        $ratios = [ordered]@{}
        foreach ($ds in $datasets) {
            $ratio = [double](("$(((Zfswin @('dataset', 'get', $ds, 'compressratio')).result.properties | Where-Object name -eq compressratio).value)") -replace '[^0-9.]', '')
            $ratios[$ds] = $ratio
            $ok = if ($ds -like '*/t-off') { $ratio -le 1.01 } else { $ratio -gt 1.0 }
            Check $r "compresses-$ds" $ok $true
        }
        $r.ratios = $ratios
        [ZfsTree]::SaveManifest("$treeDir\tree-manifest.tsv", [string[]]$datasets)
        Check $r "export-final" (Zfswin @('pool', 'export', $treePool)).exit 0
        # Only tree.img holds the pool now.
        foreach ($d in $diskB, $diskC, $logDisk, $cacheDisk) { Remove-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue }
        $r.failed = @($r.Keys | Where-Object { $r[$_] -is [Collections.IDictionary] -and $r[$_].Contains('pass') -and -not $r[$_].pass })
        $r | ConvertTo-Json -Depth 6 -Compress
    }

    # Another machine's tree pool (tree.img and tree-manifest.tsv in Dir, a
    # copy): every file, its size, data, attributes and time.  Imported
    # read-write: a read-only import left the child datasets unreachable in
    # the kernel edition and the pool unexportable in user mode (plan 19).
    "treeverify" {
        $r = [ordered]@{}
        $dir = $decoded.Dir
        $treePool = "ztree"
        $threads = [Environment]::ProcessorCount
        if ((Zfswin @('pool', 'list')).result.pools | Where-Object name -eq $treePool) { Zfswin @('pool', 'export', $treePool) | Out-Null }
        $t = [Diagnostics.Stopwatch]::StartNew()
        Check $r "import" (Zfswin @('pool', 'import', $treePool, '--dir', $dir)).exit 0
        $r.importSeconds = [math]::Round($t.Elapsed.TotalSeconds, 1)
        Initialize-Tree
        $names = [ZfsTree]::LoadManifest("$dir\tree-manifest.tsv", [string[]]@())
        $roots = @($names | ForEach-Object { TreeRoot $_ })
        Check $r "mounted" (@($roots | Where-Object { $_ }).Count) $names.Count
        if (@($roots | Where-Object { $_ }).Count -eq $names.Count) {
            [ZfsTree]::Roots = [string[]]$roots
            $t.Restart()
            $v = [ZfsTree]::Verify($threads)
            $r.verify = [ordered]@{ seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); checked = $v.Checked; samples = $v.Samples }
            Check $r "intact" "$($v.Missing)/$($v.Size)/$($v.Data)/$($v.Attr)/$($v.Time)/$($v.Errors)" "0/0/0/0/0/0"
            $c = [ZfsTree]::Count()
            $files = [ZfsTree]::ManifestCounts()
            $got = @(); $want = @()
            for ($i = 0; $i -lt $names.Count; $i++) { $got += "$($c[$i * 2])/$($c[$i * 2 + 1])"; $want += "$($files[$i])/$([ZfsTree]::ExpectedDirs[$i])" }
            Check $r "count" ($got -join ' ') ($want -join ' ')
        }
        $r.files = [ZfsTree]::Files.Count
        # Right after reading every file, a scanner may still hold one open
        # ("busy", exit 9); try again a few times and say how often.
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $exportExit = (Zfswin @('pool', 'export', $treePool)).exit
            if ($exportExit -ne 9) { break }
            Start-Sleep -Seconds 10
        }
        $r.exportAttempts = $attempt
        Check $r "export" $exportExit 0
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

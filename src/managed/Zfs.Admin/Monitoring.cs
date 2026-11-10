using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zfs.Admin;

/// <summary>
/// Step 20 (issue #17): damaged files, per-device I/O counters and ARC
/// statistics, in the suite's result shapes.  As for the rest of ZfsJson,
/// only this file depends on how zpool and kstat print them.
/// </summary>
public static partial class ZfsJson
{
    public const string IostatProperties = "read_ops,write_ops,read_bytes,write_bytes";

    /// <summary>
    /// pool.errors from `zpool status -j --json-int -p -v &lt;pool&gt;`:
    /// the permanent-error list ("errlist"), every device's error counters
    /// and the scan.
    /// </summary>
    public static JsonNode PoolErrors(JsonNode zpoolStatus, string pool)
    {
        JsonNode? entry = Entries(zpoolStatus["pools"]).FirstOrDefault(e => e.Name == pool).Value;
        var files = new JsonArray();
        // A string instead of a list when zpool could not read the log (strerror).
        string? logError = null;
        switch (entry?["errlist"])
        {
            case JsonArray list:
                foreach (JsonNode? item in list)
                    if (Text(item) is string text)
                        files.Add((JsonNode)ErrorEntry(text));
                break;
            case JsonValue v:
                logError = Text(v);
                break;
        }
        var devices = new JsonArray();
        JsonNode status = PoolStatus(new JsonObject { ["pools"] = new JsonObject { [pool] = entry?.DeepClone() } });
        JsonNode? converted = status["pools"]?.AsArray().FirstOrDefault();
        foreach (string group in new[] { "vdevs", "logs", "caches", "spares", "special", "dedup" })
            foreach (JsonNode? v in converted?[group] as JsonArray ?? [])
                AddDevices(devices, v, group);
        return new JsonObject
        {
            ["pool"] = pool,
            ["state"] = Text(entry?["state"]),
            ["errorCount"] = Number(entry?["error_count"]) ?? 0,
            ["files"] = files,
            ["logError"] = logError,
            ["devices"] = devices,
            ["scan"] = converted?["scan"]?.DeepClone(),
        };

        static void AddDevices(JsonArray devices, JsonNode? v, string group)
        {
            if (v is null || Text(v["type"]) == "root")
            {
                foreach (JsonNode? c in v?["children"] as JsonArray ?? [])
                    AddDevices(devices, c, group);
                return;
            }
            devices.Add((JsonNode)new JsonObject
            {
                ["name"] = v["name"]?.DeepClone(),
                ["type"] = v["type"]?.DeepClone(),
                ["path"] = v["path"]?.DeepClone(),
                ["group"] = group,
                ["state"] = v["state"]?.DeepClone(),
                ["readErrors"] = v["readErrors"]?.DeepClone(),
                ["writeErrors"] = v["writeErrors"]?.DeepClone(),
                ["checksumErrors"] = v["checksumErrors"]?.DeepClone(),
            });
            foreach (JsonNode? c in v["children"] as JsonArray ?? [])
                AddDevices(devices, c, group);
        }
    }

    [GeneratedRegex(@"^<(metadata|0x[0-9a-fA-F]+)>:<(0x[0-9a-fA-F]+)>$")]
    private static partial Regex UnnamedObject();

    [GeneratedRegex(@"^(?<ds>[A-Za-z][A-Za-z0-9_.:\-/ ]*?(?:@[A-Za-z0-9_.:\- ]+)?):(?<rest>(?:/|<0x).*)$")]
    private static partial Regex DatasetObject();

    /// <summary>
    /// One line of the permanent-error list, as zpool_obj_to_path() wrote
    /// it: "&lt;metadata&gt;:&lt;0x1b&gt;", "&lt;0x15&gt;:&lt;0x8&gt;" (dataset gone),
    /// "pool/ds@snap:/dir/file" or "pool/ds:&lt;0x8&gt;" (not mounted or
    /// no path), or the file's path under its mount point.
    /// </summary>
    public static JsonObject ErrorEntry(string text)
    {
        Match unnamed = UnnamedObject().Match(text);
        if (unnamed.Success)
        {
            bool metadata = unnamed.Groups[1].Value == "metadata";
            return Entry(metadata ? "metadata" : "object", null, null, null, unnamed.Groups[2].Value);
        }
        Match named = DatasetObject().Match(text);
        // "E:/dir/file" is a mounted drive letter, not a pool named E.
        if (named.Success && !(named.Groups["ds"].Value.Length == 1 && !text.Contains('@')))
        {
            string ds = named.Groups["ds"].Value, rest = named.Groups["rest"].Value;
            int at = ds.IndexOf('@');
            string dataset = at > 0 ? ds[..at] : ds;
            string? snapshot = at > 0 ? ds[(at + 1)..] : null;
            return rest.StartsWith("<0x", StringComparison.Ordinal)
                ? Entry("object", dataset, snapshot, null, rest.Trim('<', '>'))
                : Entry("file", dataset, snapshot, rest, null);
        }
        return Entry("file", null, null, WindowsPath(text), null);

        JsonObject Entry(string kind, string? dataset, string? snapshot, string? path, string? obj) => new()
        {
            ["text"] = text, ["kind"] = kind, ["dataset"] = dataset, ["snapshot"] = snapshot, ["path"] = path, ["object"] = obj,
        };
    }

    /// <summary>"E:\/dir/file" (mount point plus the ZPL path) as Windows writes it.</summary>
    internal static string WindowsPath(string path)
    {
        string p = path.Replace('/', '\\');
        while (p.Contains(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\", StringComparison.Ordinal))
            p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
        return p;
    }

    /// <summary>
    /// pool.iostat from `zpool get -j --json-int -p read_ops,... &lt;pool&gt; all-vdevs`:
    /// cumulative counters per vdev.  The root vdev (named after the pool)
    /// is the pool's total.
    /// </summary>
    public static JsonNode VdevIostat(JsonNode zpoolGet, string pool, DateTimeOffset sampledAt)
    {
        var vdevs = new JsonArray();
        JsonObject? total = null;
        foreach (var (name, v) in Entries(zpoolGet["vdevs"]))
        {
            JsonNode? props = v?["properties"];
            var item = new JsonObject
            {
                ["name"] = name,
                ["type"] = Text(v?["vdev_type"]),
                ["path"] = Text(v?["path"]),
                ["readOps"] = Number(Prop(props, "read_ops")) ?? 0,
                ["writeOps"] = Number(Prop(props, "write_ops")) ?? 0,
                ["readBytes"] = Number(Prop(props, "read_bytes")) ?? 0,
                ["writeBytes"] = Number(Prop(props, "write_bytes")) ?? 0,
            };
            if (name == pool || Text(v?["vdev_type"]) == "root")
                total = item;
            else
                vdevs.Add((JsonNode)item);
        }
        total ??= new JsonObject
        {
            ["name"] = pool, ["type"] = "root", ["path"] = null,
            ["readOps"] = 0L, ["writeOps"] = 0L, ["readBytes"] = 0L, ["writeBytes"] = 0L,
        };
        return new JsonObject { ["pool"] = pool, ["sampledAt"] = sampledAt.ToUnixTimeMilliseconds(), ["total"] = total, ["vdevs"] = vdevs };
    }

    /// <summary>`kstat -p` lines ("zfs:0:arcstats:size\t123"), as the bridge also prints them.</summary>
    public static IReadOnlyDictionary<string, long> KstatValues(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            string key = line[..tab];
            key = key[(key.LastIndexOf(':') + 1)..];
            if (long.TryParse(line[(tab + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                values[key] = value;
            else if (ulong.TryParse(line[(tab + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong big))
                values[key] = (long)Math.Min(big, long.MaxValue);
        }
        return values;
    }

    /// <summary>memory.stats's "arc" object from the arcstats kstat.</summary>
    public static JsonObject ArcStats(IReadOnlyDictionary<string, long> s)
    {
        long? Get(string name) => s.TryGetValue(name, out long v) ? v : null;
        double? Ratio(long? hits, long? misses) => hits is long h && misses is long m && h + m > 0 ? Math.Round((double)h / (h + m), 4) : null;
        return new JsonObject
        {
            ["size"] = Get("size"),
            ["target"] = Get("c"),
            ["min"] = Get("c_min"),
            ["max"] = Get("c_max"),
            ["mruSize"] = Get("mru_size"),
            ["mfuSize"] = Get("mfu_size"),
            ["metadataSize"] = Get("metadata_size"),
            ["hits"] = Get("hits"),
            ["misses"] = Get("misses"),
            ["hitRatio"] = Ratio(Get("hits"), Get("misses")),
            ["l2Size"] = Get("l2_size"),
            ["l2AllocatedSize"] = Get("l2_asize"),
            ["l2Hits"] = Get("l2_hits"),
            ["l2Misses"] = Get("l2_misses"),
            ["l2HitRatio"] = Ratio(Get("l2_hits"), Get("l2_misses")),
        };
    }

    /// <summary>The sum of `zfs userspace -H -p -o objused` lines.</summary>
    public static long? SumLines(string text)
    {
        long sum = 0;
        bool any = false;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return null;
            sum += n;
            any = true;
        }
        return any ? sum : null;
    }

    /// <summary>"name\tnumber" lines of `zfs list -H -p -o name,&lt;numeric property&gt;`.</summary>
    public static IReadOnlyList<(string Name, long Number)> NameNumberLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('\t'))
            .Where(f => f.Length == 2 && long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(f => (f[0], long.Parse(f[1], CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>The pools' dedup_table_size from `zpool get -j --json-int -p dedup_table_size`.</summary>
    public static JsonArray DedupTables(JsonNode zpoolGet)
    {
        var list = new JsonArray();
        foreach (var (name, p) in Entries(zpoolGet["pools"]))
            list.Add((JsonNode)new JsonObject { ["pool"] = name, ["bytes"] = Number(Prop(p?["properties"], "dedup_table_size")) });
        return list;
    }
}

/// <summary>Cumulative I/O counters of one vdev, as pool.iostat returns them.</summary>
public sealed record IoCounters(long ReadOps, long WriteOps, long ReadBytes, long WriteBytes)
{
    public static IoCounters From(JsonNode? n) => new(L(n, "readOps"), L(n, "writeOps"), L(n, "readBytes"), L(n, "writeBytes"));

    static long L(JsonNode? n, string name) => n?[name] is JsonValue v && v.TryGetValue(out long l) ? l : 0;
}

public sealed record IoRates(double ReadIops, double WriteIops, double ReadBytesPerSecond, double WriteBytesPerSecond)
{
    public static readonly IoRates Zero = new(0, 0, 0, 0);
}

/// <summary>One pool.iostat answer: when, the pool total and each vdev.</summary>
public sealed record IostatSample(string Pool, DateTimeOffset SampledAt, IoCounters Total, IReadOnlyDictionary<string, IoCounters> Vdevs)
{
    public static IostatSample From(JsonNode result)
    {
        var vdevs = new Dictionary<string, IoCounters>(StringComparer.Ordinal);
        foreach (JsonNode? v in result["vdevs"] as JsonArray ?? [])
            if (v?["name"]?.GetValue<string>() is string name)
                vdevs[v["path"]?.GetValue<string>() ?? name] = IoCounters.From(v);
        long ms = result["sampledAt"] is JsonValue t && t.TryGetValue(out long l) ? l : 0;
        return new IostatSample(result["pool"]?.GetValue<string>() ?? "", DateTimeOffset.FromUnixTimeMilliseconds(ms), IoCounters.From(result["total"]), vdevs);
    }

    /// <summary>
    /// The rates between two samples.  Counters only grow while a pool stays
    /// imported; a counter that went down (export and re-import) restarts
    /// the series, so that interval reads as zero rather than negative.
    /// </summary>
    public static IoRates Rates(IoCounters before, IoCounters after, TimeSpan elapsed)
    {
        double s = elapsed.TotalSeconds;
        if (s <= 0)
            return IoRates.Zero;
        static double D(long a, long b) => b >= a ? b - a : 0;
        if (after.ReadOps < before.ReadOps || after.WriteOps < before.WriteOps || after.ReadBytes < before.ReadBytes || after.WriteBytes < before.WriteBytes)
            return IoRates.Zero;
        return new IoRates(D(before.ReadOps, after.ReadOps) / s, D(before.WriteOps, after.WriteOps) / s,
            D(before.ReadBytes, after.ReadBytes) / s, D(before.WriteBytes, after.WriteBytes) / s);
    }

    public IoRates TotalRatesSince(IostatSample before) => Rates(before.Total, Total, SampledAt - before.SampledAt);

    public IoRates VdevRatesSince(IostatSample before, string vdev) =>
        Vdevs.TryGetValue(vdev, out IoCounters? now) && before.Vdevs.TryGetValue(vdev, out IoCounters? then)
            ? Rates(then, now, SampledAt - before.SampledAt) : IoRates.Zero;
}

/// <summary>How many copies of a dataset's data the pool keeps, and what that survives.</summary>
public sealed record RedundancyInfo(string Layout, int HealthyCopies, int DiskFailuresTolerated, int CopiesProperty, bool Warning, string Text);

public static partial class Redundancy
{
    [GeneratedRegex(@"^raidz(\d)?")]
    private static partial Regex RaidzName();

    /// <summary>
    /// The smallest number of healthy members across the data vdevs times
    /// the copies property.  A mirror's members each hold a copy; raidz-P
    /// survives P failed members, which counts as P+1 copies.  copies=N
    /// keeps N copies of each block, but on a single disk they all go when
    /// the disk does, so it raises the copies, not the failures tolerated.
    /// </summary>
    public static RedundancyInfo Copies(PoolStatusInfo status, int copiesProperty)
    {
        copiesProperty = Math.Max(1, copiesProperty);
        var top = status.Vdevs.SelectMany(r => r.Type == "root" ? r.Children : [r]).ToList();
        if (top.Count == 0)
            return new RedundancyInfo("no data devices", 0, 0, copiesProperty, true, "No data devices are reported.");
        int healthyMin = int.MaxValue, toleratedMin = int.MaxValue;
        var layouts = new List<string>();
        foreach (VdevInfo v in top)
        {
            int healthy, tolerated;
            if (v.Type == "mirror")
            {
                healthy = v.Children.Count(Healthy);
                tolerated = Math.Max(0, healthy - 1);
                layouts.Add($"{v.Children.Count}-way mirror");
            }
            else if (v.Type is "raidz" or "draid" || RaidzName().IsMatch(v.Name))
            {
                Match m = RaidzName().Match(v.Name);
                int parity = m.Success && m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
                int failed = v.Children.Count(c => !Healthy(c));
                tolerated = Math.Max(0, parity - failed);
                healthy = failed > parity ? 0 : tolerated + 1;
                layouts.Add($"raidz{parity} of {v.Children.Count}");
            }
            else
            {
                healthy = Healthy(v) ? 1 : 0;
                tolerated = 0;
                layouts.Add("single disk");
            }
            healthyMin = Math.Min(healthyMin, healthy);
            toleratedMin = Math.Min(toleratedMin, tolerated);
        }
        string layout = string.Join(" + ", layouts.Distinct());
        int copies = healthyMin * copiesProperty;
        bool warning = toleratedMin == 0;
        string text = $"{layout} × copies={copiesProperty} = {copies} cop{(copies == 1 ? "y" : "ies")}; " +
            (toleratedMin == 0
                ? copiesProperty > 1
                    ? "a bad block is repaired from another copy, but a failed disk loses data."
                    : "one bad block loses a file, and a failed disk loses the pool."
                : $"{toleratedMin} disk{(toleratedMin == 1 ? "" : "s")} can fail without data loss.");
        return new RedundancyInfo(layout, copies, toleratedMin, copiesProperty, warning, text);

        static bool Healthy(VdevInfo v) => v.State is "ONLINE" or null && (v.IsLeaf || v.Children.All(c => c.State is "ONLINE" or null));
    }
}

/// <summary>Counts of one walk of a mounted dataset.</summary>
public sealed record FileCount(long Files, long Folders, long Bytes, long Skipped, bool Complete);

/// <summary>
/// The files and folders below a mount point.  ZFS keeps no such counts, so
/// this walks the tree: it skips the .zfs control directory and reparse
/// points (other volumes, links), and counts what it cannot open.
/// </summary>
public static class FileCounter
{
    public static FileCount Count(string root, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false, MatchType = MatchType.Win32,
        };
        long files = 0, folders = 0, bytes = 0, skipped = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        bool top = true;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit is TimeSpan l && watch.Elapsed > l)
                return new FileCount(files, folders, bytes, skipped, Complete: false);
            DirectoryInfo dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                skipped++;
                top = false;
                continue;
            }
            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (top && sub.Name.Equals(".zfs", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    folders++;
                    pending.Push(sub);
                }
                else if (entry is FileInfo f)
                {
                    files++;
                    try
                    {
                        bytes += f.Length;
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            top = false;
        }
        return new FileCount(files, folders, bytes, skipped, Complete: true);
    }
}

/// <summary>
/// The user-mode service's bridge pipe (sys/zfs_bridge.h), for the one
/// request that is not a ZFS ioctl: the text of a named kstat.  libspl's
/// kstats live inside zfswin-um.exe, and the kernel's KSTAT ioctls on
/// \\.\ZFS have no counterpart there.
/// </summary>
public static class UserModeBridge
{
    public const string PipeName = "OpenZFS-UserMode";
    const uint Magic = 0x315a4252;	// "RBZ1"
    const uint OpKstat = 3;
    const int Sections = 5, Src = 1, Dst = 4;
    const int HeaderBytes = 4 + 4 + 8 + 8 + 8 * Sections * 2;
    const int MaxText = 1 << 20;

    /// <summary>The request header for "module:instance:name" (exposed for tests).</summary>
    internal static byte[] KstatRequest(string kstat, out byte[] name)
    {
        name = Encoding.ASCII.GetBytes(kstat);
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), OpKstat);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24 + 8 * Src), (ulong)name.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24 + 8 * Sections + 8 * Dst), MaxText);
        return header;
    }

    /// <summary>The kstat's "module:instance:name:stat\tvalue" lines, or null when no service answers.</summary>
    public static async Task<string?> ReadKstatAsync(string kstat, TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] header = KstatRequest(kstat, out byte[] name);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(2000, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        await pipe.WriteAsync(header, limit.Token).ConfigureAwait(false);
        await pipe.WriteAsync(name, limit.Token).ConfigureAwait(false);
        await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
        var response = new byte[HeaderBytes];
        await pipe.ReadExactlyAsync(response, limit.Token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(response) != Magic)
            throw new AdminException(ErrorCode.ProtocolMismatch, "The user-mode service answered with an unknown bridge header.");
        long error = BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(16));
        if (error != 0)
            throw new AdminException(error == 2 ? ErrorCode.NotFound : ErrorCode.CommandFailed,
                error == 22 ? "The user-mode service does not serve kstats; update it." : $"The user-mode service could not read the kstat {kstat} (errno {error}).");
        string text = "";
        for (int i = 0; i < Sections; i++)
        {
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(24 + 8 * i));
            if (length == 0)
                continue;
            if (length > MaxText)
                throw new AdminException(ErrorCode.ProtocolMismatch, "The user-mode service sent more kstat text than was asked for.");
            var section = new byte[length];
            await pipe.ReadExactlyAsync(section, limit.Token).ConfigureAwait(false);
            if (i == Dst)
                text = Encoding.UTF8.GetString(section).TrimEnd('\0');
        }
        return text;
    }
}

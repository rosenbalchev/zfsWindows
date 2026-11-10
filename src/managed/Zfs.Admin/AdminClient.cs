using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zfs.Admin;

public sealed record PoolInfo(string Name, string? Health, long? Size, long? Allocated, long? Free,
    long? CapacityPercent, long? FragmentationPercent, string? DedupRatio);

public sealed record ScanInfo(string? Function, string? State, bool Paused, long? Examined, long? ToExamine, long? Errors);

public sealed record VdevInfo(string Name, string? Type, string? State, string? Path,
    long ReadErrors, long WriteErrors, long ChecksumErrors, IReadOnlyList<VdevInfo> Children)
{
    public bool IsLeaf => Children.Count == 0;
}

public sealed record PoolStatusInfo(string Name, string? State, string? Status, string? Action, long ErrorCount, ScanInfo? Scan,
    IReadOnlyList<VdevInfo> Vdevs, IReadOnlyList<VdevInfo> Logs, IReadOnlyList<VdevInfo> Caches,
    IReadOnlyList<VdevInfo> Spares, IReadOnlyList<VdevInfo> Special, IReadOnlyList<VdevInfo> Dedup);

public sealed record DatasetInfo(string Name, long? Used, long? Available, string? Mountpoint, bool Mounted,
    string? DriveLetter, string? Compression, long? LogicalUsed = null, string? CompressRatio = null, int Copies = 1, string? Checksum = null)
{
    /// <summary>What compression saves: logicalused − used, never negative.</summary>
    public long Saved => LogicalUsed is long l && Used is long u && l > u ? l - u : 0;
}

public sealed record PropertyInfo(string Name, string? Value, string? Source);

/// <summary>
/// A typed client over the broker's zfswin/1 operations, for the GUI and
/// any other .NET caller.  Every call goes through ZfsWinBroker, which
/// validates and audits it; destructive operations pass confirm=true only
/// when the caller says the user confirmed them.
/// </summary>
public sealed partial class AdminClient(BrokerClient broker)
{
    public AdminClient() : this(new BrokerClient()) { }

    // For the other part of this class (AdminClient.Monitoring.cs).
    BrokerClient Broker => broker;

    // Values the dialogs offer; the broker accepts any valid value.
    public static readonly IReadOnlyList<string> CompressionValues =
        ["off", "on", "lz4", "zstd", "zstd-1", "zstd-3", "zstd-9", "zstd-19", "zstd-fast", "gzip", "gzip-1", "gzip-9", "zle", "lzjb"];
    public static readonly IReadOnlyList<string> DedupValues = ["off", "on", "verify", "sha256", "sha256,verify", "blake3", "edonr,verify"];
    public static readonly IReadOnlyList<string> SyncValues = ["standard", "always", "disabled"];

    public async Task<JsonNode?> VersionAsync(CancellationToken ct = default) =>
        await broker.CallAsync("version", null, ct);

    public async Task<IReadOnlyList<PoolInfo>> PoolsAsync(CancellationToken ct = default)
    {
        JsonNode? r = await broker.CallAsync("pool.list", null, ct);
        return Array(r?["pools"]).Select(p => new PoolInfo(Str(p, "name") ?? "", Str(p, "health"), Num(p, "size"), Num(p, "allocated"),
            Num(p, "free"), Num(p, "capacityPercent"), Num(p, "fragmentationPercent"), Str(p, "dedupRatio"))).ToList();
    }

    public async Task<PoolStatusInfo?> StatusAsync(string pool, CancellationToken ct = default)
    {
        JsonNode? r = await broker.CallAsync("pool.status", new JsonObject { ["pool"] = pool }, ct);
        JsonNode? p = Array(r?["pools"]).FirstOrDefault();
        if (p is null)
            return null;
        JsonNode? s = p["scan"];
        ScanInfo? scan = s is null ? null : new ScanInfo(Str(s, "function"), Str(s, "state"), s["paused"]?.GetValue<bool>() ?? false,
            Num(s, "examined"), Num(s, "toExamine"), Num(s, "errors"));
        return new PoolStatusInfo(Str(p, "name") ?? pool, Str(p, "state"), Str(p, "status"), Str(p, "action"), Num(p, "errorCount") ?? 0, scan,
            Vdevs(p["vdevs"]), Vdevs(p["logs"]), Vdevs(p["caches"]), Vdevs(p["spares"]), Vdevs(p["special"]), Vdevs(p["dedup"]));
    }

    public async Task<IReadOnlyList<DatasetInfo>> DatasetsAsync(string? root = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["recursive"] = true };
        if (root is not null)
            args["root"] = root;
        JsonNode? r = await broker.CallAsync("dataset.list", args, ct);
        return Array(r?["datasets"]).Select(d => new DatasetInfo(Str(d, "name") ?? "", Num(d, "used"), Num(d, "available"),
            Str(d, "mountpoint"), d?["mounted"]?.GetValue<bool>() ?? false, Str(d, "driveLetter"), Str(d, "compression"),
            Num(d, "logicalUsed"), Str(d, "compressRatio"), (int)(Num(d, "copies") ?? 1), Str(d, "checksum"))).ToList();
    }

    public async Task<IReadOnlyList<PropertyInfo>> DatasetPropertiesAsync(string dataset, IEnumerable<string>? names = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["dataset"] = dataset };
        if (names is not null)
            args["properties"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray());
        JsonNode? r = await broker.CallAsync("dataset.get", args, ct);
        return Array(r?["properties"]).Select(p => new PropertyInfo(Str(p, "name") ?? "", p?["value"]?.ToString(), Str(p, "source"))).ToList();
    }

    public Task SetDatasetPropertyAsync(string dataset, string name, string value, CancellationToken ct = default) =>
        broker.CallAsync("dataset.set", new JsonObject { ["dataset"] = dataset, ["properties"] = new JsonObject { [name] = value } }, ct);

    public Task ScrubAsync(string pool, string action, CancellationToken ct = default) =>
        broker.CallAsync(action switch
        {
            "start" => "scrub.start",
            "pause" => "scrub.pause",
            "stop" => "scrub.stop",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        }, new JsonObject { ["pool"] = pool }, ct);

    /// <summary>Mirror <paramref name="device"/> onto <paramref name="newDevice"/> (zpool attach).</summary>
    public Task AttachMirrorAsync(string pool, string device, string newDevice, bool confirmed, CancellationToken ct = default) =>
        broker.CallAsync("vdev.attach", Confirm(new JsonObject { ["pool"] = pool, ["device"] = DeviceArgument(device), ["newDevice"] = DeviceArgument(newDevice) }, confirmed), ct);

    public Task DetachMirrorAsync(string pool, string device, bool confirmed, CancellationToken ct = default) =>
        broker.CallAsync("vdev.detach", Confirm(new JsonObject { ["pool"] = pool, ["device"] = DeviceArgument(device) }, confirmed), ct);

    /// <summary>Add read-cache (L2ARC, role "cache") or write-log (SLOG, role "log") devices.</summary>
    public Task AddCacheDevicesAsync(string pool, string role, IEnumerable<string> devices, bool mirror, bool confirmed, CancellationToken ct = default) =>
        broker.CallAsync("vdev.add", Confirm(new JsonObject
        {
            ["pool"] = pool, ["role"] = role, ["mirror"] = mirror,
            ["devices"] = new JsonArray(devices.Select(d => (JsonNode?)DeviceArgument(d)).ToArray()),
        }, confirmed), ct);

    public Task RemoveDeviceAsync(string pool, string device, bool confirmed, CancellationToken ct = default) =>
        broker.CallAsync("vdev.remove", Confirm(new JsonObject { ["pool"] = pool, ["device"] = DeviceArgument(device) }, confirmed), ct);

    /// <summary>
    /// A vdev path as zpool reports it, in the form the broker validates:
    /// "\\?\C:\x.img" becomes "C:\x.img"; any form naming PhysicalDriveN
    /// (including the "#offset#length#" prefix of Windows disk vdevs)
    /// becomes "\\?\PhysicalDriveN".
    /// </summary>
    public static string DeviceArgument(string path)
    {
        Match drive = Regex.Match(path, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
        if (drive.Success)
            return @"\\?\PhysicalDrive" + drive.Groups[1].Value;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':' ? path[4..] : path;
    }

    static JsonObject Confirm(JsonObject args, bool confirmed)
    {
        if (confirmed)
            args["confirm"] = true;
        return args;
    }

    static IEnumerable<JsonNode> Array(JsonNode? node) =>
        node is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

    static IReadOnlyList<VdevInfo> Vdevs(JsonNode? node) =>
        Array(node).Select(v => new VdevInfo(Str(v, "name") ?? "", Str(v, "type"), Str(v, "state"), Str(v, "path"),
            Num(v, "readErrors") ?? 0, Num(v, "writeErrors") ?? 0, Num(v, "checksumErrors") ?? 0, Vdevs(v["children"]))).ToList();

    static string? Str(JsonNode? n, string name) => n?[name] is JsonValue v ? v.ToString() : null;

    static long? Num(JsonNode? n, string name) =>
        n?[name] is JsonValue v && v.TryGetValue(out long l) ? l : null;
}

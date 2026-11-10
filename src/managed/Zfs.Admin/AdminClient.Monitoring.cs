using System.Text.Json.Nodes;

namespace Zfs.Admin;

/// <summary>A permanent error: a file (or object) with a block that failed its checksum.</summary>
public sealed record DamagedFile(string Text, string Kind, string? Dataset, string? Snapshot, string? Path, string? Object)
{
    /// <summary>"&lt;metadata&gt;", "tank/a:&lt;0x8&gt;" or the path, as the Integrity tab lists it.</summary>
    public string Display => Kind switch
    {
        "metadata" => "<metadata>",
        "object" => $"{Dataset ?? "<unknown dataset>"}{(Snapshot is null ? "" : "@" + Snapshot)}:<{Object}>",
        _ => Dataset is null ? Path ?? Text : $"{Dataset}{(Snapshot is null ? "" : "@" + Snapshot)}:{Path}",
    };
}

public sealed record DeviceErrors(string Name, string? Type, string? Path, string Group, string? State, long ReadErrors, long WriteErrors, long ChecksumErrors);

public sealed record PoolErrorsInfo(string Pool, string? State, long ErrorCount, IReadOnlyList<DamagedFile> Files, string? LogError,
    IReadOnlyList<DeviceErrors> Devices, ScanInfo? Scan);

public sealed record ArcInfo(long? Size, long? Target, long? Min, long? Max, long? MruSize, long? MfuSize, long? Hits, long? Misses,
    double? HitRatio, long? L2Size, long? L2Hits, long? L2Misses, double? L2HitRatio);

public sealed record MemoryInfo(string? Source, ArcInfo? Arc, IReadOnlyList<(string Pool, long? Bytes)> DedupTables, long? UserModeServicePrivateBytes);

public sealed record DatasetCountInfo(string Dataset, string? MountPoint, bool Mounted, long? Objects, long? Files, long? Folders,
    long? Bytes, long? Skipped, bool Complete, DateTimeOffset CountedAt);

public sealed record SnapshotInfo(string Name, string Dataset, string Snapshot, long? Used, long? Referenced, DateTimeOffset? Creation);

public sealed record TemporaryMount(string Snapshot, string CloneName, string MountPoint);

public sealed partial class AdminClient
{
    public async Task<PoolErrorsInfo> PoolErrorsAsync(string pool, CancellationToken ct = default)
    {
        JsonNode? r = await Broker.CallAsync("pool.errors", new JsonObject { ["pool"] = pool }, ct);
        JsonNode? s = r?["scan"];
        return new PoolErrorsInfo(Str(r, "pool") ?? pool, Str(r, "state"), Num(r, "errorCount") ?? 0,
            Array(r?["files"]).Select(f => new DamagedFile(Str(f, "text") ?? "", Str(f, "kind") ?? "file", Str(f, "dataset"), Str(f, "snapshot"), Str(f, "path"), Str(f, "object"))).ToList(),
            Str(r, "logError"),
            Array(r?["devices"]).Select(d => new DeviceErrors(Str(d, "name") ?? "", Str(d, "type"), Str(d, "path"), Str(d, "group") ?? "vdevs", Str(d, "state"),
                Num(d, "readErrors") ?? 0, Num(d, "writeErrors") ?? 0, Num(d, "checksumErrors") ?? 0)).ToList(),
            s is null ? null : new ScanInfo(Str(s, "function"), Str(s, "state"), s["paused"]?.GetValue<bool>() ?? false, Num(s, "examined"), Num(s, "toExamine"), Num(s, "errors")));
    }

    public Task ClearErrorsAsync(string pool, CancellationToken ct = default) =>
        Broker.CallAsync("pool.clear", new JsonObject { ["pool"] = pool }, ct);

    public async Task<IostatSample> IostatAsync(string pool, CancellationToken ct = default) =>
        IostatSample.From(await Broker.CallAsync("pool.iostat", new JsonObject { ["pool"] = pool }, ct) ?? new JsonObject());

    public async Task<MemoryInfo> MemoryAsync(CancellationToken ct = default)
    {
        JsonNode? r = await Broker.CallAsync("memory.stats", null, ct);
        JsonNode? a = r?["arc"];
        ArcInfo? arc = a is null ? null : new ArcInfo(Num(a, "size"), Num(a, "target"), Num(a, "min"), Num(a, "max"), Num(a, "mruSize"), Num(a, "mfuSize"),
            Num(a, "hits"), Num(a, "misses"), Dbl(a, "hitRatio"), Num(a, "l2Size"), Num(a, "l2Hits"), Num(a, "l2Misses"), Dbl(a, "l2HitRatio"));
        return new MemoryInfo(Str(r, "source"), arc, Array(r?["dedupTables"]).Select(d => (Str(d, "pool") ?? "", Num(d, "bytes"))).ToList(),
            Num(r, "userModeServicePrivateBytes"));
    }

    public async Task<DatasetCountInfo> CountAsync(string dataset, bool objectsOnly = false, CancellationToken ct = default)
    {
        var args = new JsonObject { ["dataset"] = dataset };
        if (objectsOnly)
            args["objectsOnly"] = true;
        JsonNode? r = await Broker.CallAsync("dataset.count", args, ct);
        return new DatasetCountInfo(Str(r, "dataset") ?? dataset, Str(r, "mountpoint"), r?["mounted"]?.GetValue<bool>() ?? false, Num(r, "objects"),
            Num(r, "files"), Num(r, "folders"), Num(r, "bytes"), Num(r, "skipped"), r?["complete"]?.GetValue<bool>() ?? false,
            DateTimeOffset.FromUnixTimeSeconds(Num(r, "countedAt") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    public async Task<IReadOnlyList<PropertyInfo>> PoolPropertiesAsync(string pool, IEnumerable<string> names, CancellationToken ct = default)
    {
        JsonNode? r = await Broker.CallAsync("pool.get", new JsonObject { ["pool"] = pool, ["properties"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()) }, ct);
        return Array(r?["properties"]).Select(p => new PropertyInfo(Str(p, "name") ?? "", p?["value"]?.ToString(), Str(p, "source"))).ToList();
    }

    /// <summary>The snapshots of one dataset, oldest first.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> SnapshotsAsync(string dataset, CancellationToken ct = default)
    {
        JsonNode? r = await Broker.CallAsync("snapshot.list", new JsonObject { ["root"] = dataset }, ct);
        return Array(r?["snapshots"]).Select(s => new SnapshotInfo(Str(s, "name") ?? "", Str(s, "dataset") ?? dataset, Str(s, "snapshot") ?? "",
                Num(s, "used"), Num(s, "referenced"), Num(s, "creation") is long c ? DateTimeOffset.FromUnixTimeSeconds(c) : null))
            .OrderBy(s => s.Creation).ToList();
    }

    public Task CreateSnapshotAsync(string snapshot, bool recursive, CancellationToken ct = default) =>
        Broker.CallAsync("snapshot.create", new JsonObject { ["snapshot"] = snapshot, ["recursive"] = recursive }, ct);

    public Task DestroySnapshotAsync(string snapshot, bool confirmed, CancellationToken ct = default) =>
        Broker.CallAsync("snapshot.destroy", Confirm(new JsonObject { ["snapshot"] = snapshot }, confirmed), ct);

    /// <summary>Rolls the dataset back; <paramref name="destroyNewer"/> confirms that newer snapshots go.</summary>
    public Task RollbackAsync(string snapshot, bool destroyNewer, bool confirmed, CancellationToken ct = default) =>
        Broker.CallAsync("snapshot.rollback", Confirm(new JsonObject { ["snapshot"] = snapshot, ["destroyNewer"] = destroyNewer }, confirmed), ct);

    public async Task<TemporaryMount> MountSnapshotAsync(string snapshot, string? mountPoint = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["snapshot"] = snapshot };
        if (!string.IsNullOrWhiteSpace(mountPoint))
            args["mountPoint"] = mountPoint;
        JsonNode? r = await Broker.CallAsync("snapshot.mount", args, ct);
        return new TemporaryMount(snapshot, Str(r, "clone") ?? "", Str(r, "mountPoint") ?? "");
    }

    public Task UnmountSnapshotAsync(string snapshot, CancellationToken ct = default) =>
        Broker.CallAsync("snapshot.unmount", new JsonObject { ["snapshot"] = snapshot }, ct);

    /// <summary>Which of these snapshots are mounted temporarily, and where.</summary>
    public async Task<IReadOnlyDictionary<string, string>> TemporaryMountsAsync(IEnumerable<SnapshotInfo> snapshots, CancellationToken ct = default)
    {
        JsonNode? r = await Broker.CallAsync("mount.list", null, ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SnapshotInfo s in snapshots)
        {
            string clone = Operations.TemporaryCloneName(s.Name);
            if (Array(r?["mounts"]).FirstOrDefault(m => Str(m, "dataset") == clone) is JsonNode m)
                result[s.Name] = Operations.MountRoot(Str(m, "mountpoint") ?? "");
        }
        return result;
    }

    static double? Dbl(JsonNode? n, string name) =>
        n?[name] is JsonValue v && v.TryGetValue(out double d) ? d : null;
}

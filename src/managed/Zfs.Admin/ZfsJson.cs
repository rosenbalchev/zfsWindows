using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zfs.Admin;

/// <summary>
/// Converts the JSON that zpool/zfs print with -j --json-int into the
/// suite's own result shapes (schemas/zfswin-*.schema.json).  Those shapes
/// are the stable contract; OpenZFS's own JSON layout ("output_version" 0.x)
/// may change between releases, so only this class depends on it.
/// </summary>
public static partial class ZfsJson
{
    public const string DatasetColumns = "name,type,used,available,referenced,mountpoint,mounted,driveletter,compression";
    public const string SnapshotColumns = "name,used,referenced,creation";

    public static JsonNode PoolList(JsonNode zpoolList)
    {
        var pools = new JsonArray();
        foreach (var (name, pool) in Entries(zpoolList["pools"]))
        {
            JsonNode? props = pool?["properties"];
            pools.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["guid"] = Text(pool?["pool_guid"]),
                ["health"] = Text(Prop(props, "health")) ?? Text(pool?["state"]),
                ["size"] = Number(Prop(props, "size")),
                ["allocated"] = Number(Prop(props, "allocated")),
                ["free"] = Number(Prop(props, "free")),
                ["capacityPercent"] = Number(Prop(props, "capacity")),
                ["fragmentationPercent"] = Number(Prop(props, "fragmentation")),
                ["dedupRatio"] = Text(Prop(props, "dedupratio")),
            });
        }
        return new JsonObject { ["pools"] = pools };
    }

    public static JsonNode PoolStatus(JsonNode zpoolStatus)
    {
        var pools = new JsonArray();
        foreach (var (name, pool) in Entries(zpoolStatus["pools"]))
        {
            JsonNode? scan = pool?["scan_stats"];
            pools.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["guid"] = Text(pool?["pool_guid"]),
                ["state"] = Text(pool?["state"]),
                ["status"] = Text(pool?["status"]),
                ["action"] = Text(pool?["action"]),
                ["errorCount"] = Number(pool?["error_count"]) ?? 0,
                ["scan"] = scan is null ? null : new JsonObject
                {
                    ["function"] = Text(scan["function"]),
                    ["state"] = Text(scan["state"]),
                    ["paused"] = (Number(scan["scrub_pause"]) ?? 0) != 0,
                    ["startTime"] = Number(scan["start_time"]),
                    ["endTime"] = Number(scan["end_time"]),
                    ["toExamine"] = Number(scan["to_examine"]),
                    ["examined"] = Number(scan["examined"]),
                    ["issued"] = Number(scan["issued"]),
                    ["errors"] = Number(scan["errors"]),
                },
                ["vdevs"] = Vdevs(pool?["vdevs"]),
                // Separate maps in zpool status -j (class_vdevs_nvlist,
                // l2cache_nvlist, spares_nvlist); empty when absent.
                ["logs"] = Vdevs(pool?["logs"]),
                ["caches"] = Vdevs(pool?["l2cache"]),
                ["spares"] = Vdevs(pool?["spares"]),
                ["special"] = Vdevs(pool?["special"]),
                ["dedup"] = Vdevs(pool?["dedup"]),
            });
        }
        return new JsonObject { ["pools"] = pools };
    }

    static JsonArray Vdevs(JsonNode? vdevs)
    {
        var list = new JsonArray();
        foreach (var (name, v) in Entries(vdevs))
        {
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["type"] = Text(v?["vdev_type"]),
                ["class"] = Text(v?["class"]),
                ["state"] = Text(v?["state"]),
                ["path"] = Text(v?["path"]),
                ["guid"] = Text(v?["guid"]),
                ["readErrors"] = Number(v?["read_errors"]) ?? 0,
                ["writeErrors"] = Number(v?["write_errors"]) ?? 0,
                ["checksumErrors"] = Number(v?["checksum_errors"]) ?? 0,
                ["children"] = Vdevs(v?["vdevs"]),
            });
        }
        return list;
    }

    public static JsonNode PoolProperties(JsonNode zpoolGet, string pool)
    {
        JsonNode? entry = Entries(zpoolGet["pools"]).FirstOrDefault(e => e.Name == pool).Value;
        return new JsonObject { ["pool"] = pool, ["properties"] = PropertyArray(entry?["properties"]) };
    }

    public static JsonNode DatasetProperties(JsonNode zfsGet, string dataset)
    {
        JsonNode? entry = Entries(zfsGet["datasets"]).FirstOrDefault(e => e.Name == dataset).Value;
        return new JsonObject { ["dataset"] = dataset, ["properties"] = PropertyArray(entry?["properties"]) };
    }

    static JsonArray PropertyArray(JsonNode? properties)
    {
        var list = new JsonArray();
        foreach (var (name, p) in Entries(properties))
        {
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["value"] = p?["value"]?.DeepClone(),
                ["source"] = Text(p?["source"]?["type"])?.ToLowerInvariant(),
                ["inheritedFrom"] = Text(p?["source"]?["type"]) == "INHERITED" ? Text(p?["source"]?["data"]) : null,
            });
        }
        return list;
    }

    public static JsonNode DatasetList(JsonNode zfsList)
    {
        var list = new JsonArray();
        foreach (var (name, d) in Entries(zfsList["datasets"]))
        {
            JsonNode? props = d?["properties"];
            string? letter = Text(Prop(props, "driveletter"));
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["type"] = Text(d?["type"])?.ToLowerInvariant(),
                ["pool"] = Text(d?["pool"]),
                ["used"] = Number(Prop(props, "used")),
                ["available"] = Number(Prop(props, "available")),
                ["referenced"] = Number(Prop(props, "referenced")),
                ["mountpoint"] = Text(Prop(props, "mountpoint")),
                ["mounted"] = Text(Prop(props, "mounted")) == "yes",
                ["driveLetter"] = letter is null or "-" ? null : letter,
                ["compression"] = Text(Prop(props, "compression")),
            });
        }
        return new JsonObject { ["datasets"] = list };
    }

    public static JsonNode SnapshotList(JsonNode zfsList)
    {
        var list = new JsonArray();
        foreach (var (name, d) in Entries(zfsList["datasets"]))
        {
            JsonNode? props = d?["properties"];
            int at = name.IndexOf('@');
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["dataset"] = at > 0 ? name[..at] : Text(d?["dataset"]),
                ["snapshot"] = at > 0 ? name[(at + 1)..] : Text(d?["snapshot_name"]),
                ["used"] = Number(Prop(props, "used")),
                ["referenced"] = Number(Prop(props, "referenced")),
                ["creation"] = Number(Prop(props, "creation")),
            });
        }
        return new JsonObject { ["snapshots"] = list };
    }

    /// <summary>`zfs mount` prints "dataset  mountpoint" per mounted filesystem.</summary>
    public static JsonNode MountList(string text)
    {
        var list = new JsonArray();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match m = MountLine().Match(line);
            if (m.Success)
                list.Add((JsonNode)new JsonObject { ["dataset"] = m.Groups[1].Value, ["mountpoint"] = m.Groups[2].Value.Trim() });
        }
        return new JsonObject { ["mounts"] = list };
    }

    [GeneratedRegex(@"^(\S+)\s+(.+)$")]
    private static partial Regex MountLine();

    /// <summary>
    /// `zpool import` with no pool argument has no JSON form; it prints a
    /// "pool: / id: / state:" block per importable pool.
    /// </summary>
    public static JsonNode ImportablePools(string text)
    {
        var list = new JsonArray();
        JsonObject? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon];
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "pool":
                    current = new JsonObject { ["name"] = value };
                    list.Add((JsonNode)current);
                    break;
                case "id" when current is not null:
                    current["guid"] = value;
                    break;
                case "state" when current is not null:
                    current["state"] = value;
                    break;
                case "status" when current is not null:
                    current["status"] = value;
                    break;
                case "action" when current is not null:
                    current["action"] = value;
                    break;
            }
        }
        return new JsonObject { ["pools"] = list };
    }

    // --- helpers ------------------------------------------------------------

    static IEnumerable<(string Name, JsonNode? Value)> Entries(JsonNode? node) =>
        node is JsonObject obj ? obj.Select(p => (p.Key, p.Value)) : [];

    static JsonNode? Prop(JsonNode? properties, string name) => properties?[name]?["value"];

    static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    /// <summary>A number printed either as a JSON number (--json-int) or a numeric string.</summary>
    static long? Number(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue(out long l))
            return l;
        if (v.TryGetValue(out double d))
            return (long)d;
        if (v.TryGetValue(out string? s) && long.TryParse(s.TrimEnd('%', 'x'), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
            return parsed;
        return null;
    }
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace Zfs.Admin.Cli;

/// <summary>Plain-text rendering of broker results; scripts should use --json.</summary>
internal static class HumanOutput
{
    public static void Write(string operation, JsonNode? result, TextWriter output)
    {
        switch (operation)
        {
            case "version":
                output.WriteLine($"zfswin {result?["broker"]} (protocol {result?["protocol"]})");
                output.WriteLine(result?["userland"]?.GetValue<string>());
                output.WriteLine(result?["kernel"]?.GetValue<string>());
                break;
            case "pool.list":
                Table(output, Items(result, "pools"), ("NAME", "name", null), ("SIZE", "size", Size), ("ALLOC", "allocated", Size),
                    ("FREE", "free", Size), ("CAP", "capacityPercent", Percent), ("FRAG", "fragmentationPercent", Percent),
                    ("DEDUP", "dedupRatio", null), ("HEALTH", "health", null));
                break;
            case "pool.status":
                foreach (JsonNode? pool in Items(result, "pools"))
                    PoolStatus(output, pool);
                break;
            case "scrub.status":
                output.WriteLine($"  pool: {result?["pool"]}");
                output.WriteLine($"  scan: {Scan(result?["scan"])}");
                break;
            case "pool.get":
            case "dataset.get":
                Table(output, Items(result, "properties"), ("PROPERTY", "name", null), ("VALUE", "value", null), ("SOURCE", "source", null));
                break;
            case "dataset.list":
                Table(output, Items(result, "datasets"), ("NAME", "name", null), ("USED", "used", Size), ("AVAIL", "available", Size),
                    ("REFER", "referenced", Size), ("MOUNTED", "mounted", YesNo), ("MOUNTPOINT", "mountpoint", null));
                break;
            case "snapshot.list":
                Table(output, Items(result, "snapshots"), ("NAME", "name", null), ("USED", "used", Size), ("REFER", "referenced", Size),
                    ("CREATION", "creation", Time));
                break;
            case "mount.list":
                Table(output, Items(result, "mounts"), ("DATASET", "dataset", null), ("MOUNTPOINT", "mountpoint", null));
                break;
            case "pool.discover":
                var pools = Items(result, "pools").ToList();
                if (pools.Count == 0)
                    output.WriteLine("No importable pools were found.");
                Table(output, pools, ("NAME", "name", null), ("GUID", "guid", null), ("STATE", "state", null), ("STATUS", "status", null));
                break;
            default:
                // Mutations: one line naming what changed.
                output.WriteLine(Summary(operation, result));
                break;
        }
    }

    static string Summary(string operation, JsonNode? r) => operation switch
    {
        "pool.import" => $"Imported pool {r?["pool"]}.",
        "pool.export" => $"Exported pool {r?["pool"]}.",
        "pool.create" => $"Created pool {r?["pool"]} ({r?["layout"]}).",
        "scrub.start" => $"Started a scrub of {r?["pool"]}.",
        "scrub.pause" => $"Paused the scrub of {r?["pool"]}.",
        "scrub.stop" => $"Stopped the scrub of {r?["pool"]}.",
        "dataset.create" => $"Created {r?["dataset"]}.",
        "dataset.set" => $"Set {string.Join(", ", (r?["set"] as JsonArray ?? []).Select(p => p?.ToString()))} on {r?["dataset"]}.",
        "dataset.destroy" => $"Destroyed {r?["dataset"]}.",
        "dataset.mount" => $"Mounted {r?["dataset"]}.",
        "dataset.unmount" => $"Unmounted {r?["dataset"]}.",
        "snapshot.create" => $"Created snapshot {r?["snapshot"]}.",
        "snapshot.destroy" => $"Destroyed snapshot {r?["snapshot"]}.",
        "vdev.attach" => $"Attached {r?["attached"]} to {r?["to"]} in {r?["pool"]}; resilvering starts automatically.",
        "vdev.detach" => $"Detached {r?["detached"]} from {r?["pool"]}.",
        "vdev.replace" => $"Replacing {r?["replaced"]} with {r?["with"]} in {r?["pool"]}; see 'zfswin pool status'.",
        _ => r?.ToJsonString() ?? "Done.",
    };

    static void PoolStatus(TextWriter output, JsonNode? pool)
    {
        output.WriteLine($"  pool: {pool?["name"]}");
        output.WriteLine($" state: {pool?["state"]}");
        if (pool?["status"] is JsonNode status)
            output.WriteLine($"status: {status}");
        if (pool?["action"] is JsonNode action)
            output.WriteLine($"action: {action}");
        output.WriteLine($"  scan: {Scan(pool?["scan"])}");
        output.WriteLine("config:");
        var rows = new List<string[]> { new[] { "NAME", "STATE", "READ", "WRITE", "CKSUM" } };
        void Walk(JsonNode? vdev, int depth)
        {
            string name = vdev?["path"]?.ToString() is { Length: > 0 } path && vdev?["type"]?.ToString() is "file" or "disk" ? path : vdev?["name"]?.ToString() ?? "";
            rows.Add([new string(' ', depth * 2) + name, vdev?["state"]?.ToString() ?? "", vdev?["readErrors"]?.ToString() ?? "",
                vdev?["writeErrors"]?.ToString() ?? "", vdev?["checksumErrors"]?.ToString() ?? ""]);
            foreach (JsonNode? child in vdev?["children"] as JsonArray ?? [])
                Walk(child, depth + 1);
        }
        foreach (JsonNode? vdev in pool?["vdevs"] as JsonArray ?? [])
            Walk(vdev, 1);
        WriteRows(output, rows, indent: "\t");
        output.WriteLine($"errors: {(pool?["errorCount"]?.GetValue<long>() is long n && n > 0 ? $"{n} data errors" : "No known data errors")}");
        output.WriteLine();
    }

    static string Scan(JsonNode? scan)
    {
        if (scan is null)
            return "none requested";
        string function = scan["function"]?.ToString()?.ToLowerInvariant() ?? "scan";
        string state = scan["state"]?.ToString() ?? "";
        long errors = scan["errors"]?.GetValue<long>() ?? 0;
        return state switch
        {
            "FINISHED" => $"{function} finished with {errors} errors at {Time(scan["endTime"])}",
            "SCANNING" when scan["paused"]?.GetValue<bool>() == true => $"{function} paused, {Size(scan["examined"])} of {Size(scan["toExamine"])} examined",
            "SCANNING" => $"{function} in progress, {Size(scan["examined"])} of {Size(scan["toExamine"])} examined",
            "CANCELED" => $"{function} canceled at {Time(scan["endTime"])}",
            _ => $"{function} {state.ToLowerInvariant()}",
        };
    }

    static IEnumerable<JsonNode?> Items(JsonNode? result, string name) => result?[name] as JsonArray ?? [];

    static void Table(TextWriter output, IEnumerable<JsonNode?> items, params (string Header, string Field, Func<JsonNode?, string>? Format)[] columns)
    {
        var rows = new List<string[]> { columns.Select(c => c.Header).ToArray() };
        foreach (JsonNode? item in items)
            rows.Add(columns.Select(c => c.Format is null ? item?[c.Field]?.ToString() ?? "-" : c.Format(item?[c.Field])).ToArray());
        if (rows.Count > 1)
            WriteRows(output, rows, "");
    }

    static void WriteRows(TextWriter output, List<string[]> rows, string indent)
    {
        int[] widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        foreach (string[] row in rows)
            output.WriteLine(indent + string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
    }

    internal static string Size(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue(out long bytes))
            return "-";
        string[] units = ["B", "K", "M", "G", "T", "P", "E"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes}B" : value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + units[unit];
    }

    static string Percent(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long p) ? $"{p}%" : "-";

    static string YesNo(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? (b ? "yes" : "no") : "-";

    static string Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out long seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
}

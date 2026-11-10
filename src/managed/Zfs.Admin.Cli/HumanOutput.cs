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
            case "pool.errors":
                PoolErrors(output, result);
                break;
            case "pool.iostat":
                Table(output, new[] { result?["total"] }.Concat(Items(result, "vdevs")), ("VDEV", "name", null), ("READ OPS", "readOps", null),
                    ("WRITE OPS", "writeOps", null), ("READ", "readBytes", Size), ("WRITTEN", "writeBytes", Size));
                break;
            case "memory.stats":
                Memory(output, result);
                break;
            case "dataset.count":
                DatasetCount(output, result);
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
        "vdev.add" => $"Added {(r?["role"]?.GetValue<string>() == "cache" ? "read cache (L2ARC)" : (r?["mirror"]?.GetValue<bool>() == true ? "mirrored write log (SLOG)" : "write log (SLOG)"))} " +
            $"{string.Join(", ", r?["devices"]?.AsArray().Select(d => d?.GetValue<string>()) ?? [])} to {r?["pool"]}.",
        "vdev.remove" => $"Removed {r?["removed"]} from {r?["pool"]}.",
        "pool.clear" => $"Cleared the error counters of {r?["pool"]}.",
        "snapshot.rollback" => $"Rolled {r?["dataset"]} back to {r?["snapshot"]}" +
            (r?["destroyed"] is JsonArray { Count: > 0 } gone ? $"; destroyed {string.Join(", ", gone.Select(g => g?.ToString()))}." : "."),
        "snapshot.mount" => $"Mounted {r?["snapshot"]} read-only at {r?["mountPoint"]} (temporary clone {r?["clone"]}).",
        "snapshot.unmount" => $"Unmounted {r?["snapshot"]}; removed {r?["clone"]}.",
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
        // The device classes zpool status prints below the data vdevs.
        foreach (var (key, title) in new[] { ("special", "special"), ("dedup", "dedup"), ("logs", "logs"), ("caches", "cache"), ("spares", "spares") })
        {
            if (pool?[key] is not JsonArray { Count: > 0 } group)
                continue;
            rows.Add([title, "", "", "", ""]);
            foreach (JsonNode? vdev in group)
                Walk(vdev, 1);
        }
        WriteRows(output, rows, indent: "\t");
        output.WriteLine($"errors: {(pool?["errorCount"]?.GetValue<long>() is long n && n > 0 ? $"{n} data errors" : "No known data errors")}");
        output.WriteLine();
    }

    static void PoolErrors(TextWriter output, JsonNode? r)
    {
        output.WriteLine($"  pool: {r?["pool"]}");
        output.WriteLine($" state: {r?["state"]}");
        output.WriteLine($"  scan: {Scan(r?["scan"])}");
        Table(output, Items(r, "devices"), ("DEVICE", "name", null), ("STATE", "state", null), ("READ", "readErrors", null),
            ("WRITE", "writeErrors", null), ("CKSUM", "checksumErrors", null));
        var files = Items(r, "files").ToList();
        if (files.Count == 0)
        {
            output.WriteLine(r?["errorCount"]?.GetValue<long>() is long n && n > 0
                ? $"errors: {n} data errors; run 'zfswin scrub start {r?["pool"]}' to list them."
                : "errors: No known data errors");
            return;
        }
        output.WriteLine($"errors: Permanent errors in {files.Count} file(s) or object(s):");
        foreach (JsonNode? f in files)
            output.WriteLine($"        {f?["text"]}");
    }

    static void Memory(TextWriter output, JsonNode? r)
    {
        JsonNode? a = r?["arc"];
        if (a is null)
        {
            output.WriteLine("ARC statistics are not available (no kernel driver and no user-mode service answered).");
            return;
        }
        output.WriteLine($"source: {r?["source"]}");
        output.WriteLine($"ARC: {Size(a["size"])} (target {Size(a["target"])}, min {Size(a["min"])}, max {Size(a["max"])})");
        output.WriteLine($"     MRU {Size(a["mruSize"])}, MFU {Size(a["mfuSize"])}, hit ratio {Ratio(a["hitRatio"])}");
        output.WriteLine($"L2ARC: {Size(a["l2Size"])}, hit ratio {Ratio(a["l2HitRatio"])}");
        foreach (JsonNode? t in Items(r, "dedupTables"))
            output.WriteLine($"dedup table {t?["pool"]}: {Size(t?["bytes"])}");
        if (r?["userModeServicePrivateBytes"] is JsonNode svc)
            output.WriteLine($"ZfsWinUserMode process: {Size(svc)} private");

        static string Ratio(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue(out double d) ? (d * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-";
    }

    static void DatasetCount(TextWriter output, JsonNode? r)
    {
        bool mounted = r?["mounted"]?.GetValue<bool>() == true;
        output.WriteLine($"{r?["dataset"]}: {(mounted ? $"mounted at {r?["mountpoint"]}" : "not mounted")}");
        if (r?["files"] is JsonNode files)
        {
            string incomplete = r["complete"]?.GetValue<bool>() == false ? " (incomplete: time limit reached)" : "";
            string skipped = r["skipped"]?.GetValue<long>() is long sk && sk > 0 ? $", {sk} skipped" : "";
            output.WriteLine($"  {files} files, {r["folders"]} folders, {Size(r["bytes"])}{skipped}{incomplete}");
        }
        if (r?["objects"] is JsonNode objects)
            output.WriteLine($"  {objects} objects (files + folders + internal)");
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

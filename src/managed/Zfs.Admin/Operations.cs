using System.Text.Json.Nodes;

namespace Zfs.Admin;

public sealed class OperationContext(IToolRunner runner, CancellationToken cancellationToken)
{
    public IToolRunner Runner { get; } = runner;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public async Task<ToolResult> RunAsync(ZfsTool tool, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ToolResult result = await Runner.RunAsync(tool, arguments, timeout, CancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw Failure(tool, arguments, result);
        return result;
    }

    internal static AdminException Failure(ZfsTool tool, IReadOnlyList<string> arguments, ToolResult result)
    {
        string detail = (result.StandardError.Trim() + "\n" + result.StandardOutput.Trim()).Trim();
        if (detail.Length > 4096)
            detail = detail[..4096];
        string lower = detail.ToLowerInvariant();
        ErrorCode code = lower.Contains("no such pool") || lower.Contains("does not exist") || lower.Contains("no such device")
            || lower.Contains("could not find any snapshots") || lower.Contains("no pools available")
            ? ErrorCode.NotFound
            : lower.Contains("permission denied") ? ErrorCode.AccessDenied
            : lower.Contains("is busy") || lower.Contains("pool is busy") ? ErrorCode.Busy
            : ErrorCode.CommandFailed;
        string first = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("Assuming ", StringComparison.Ordinal)) ?? "";
        return new AdminException(code,
            $"{ProcessToolRunner.Describe(tool, arguments)} failed (exit {result.ExitCode}){(first.Length > 0 ? ": " + first : ".")}", detail);
    }
}

/// <summary>One broker operation: its privilege, its arguments and how it runs.</summary>
public sealed record OperationSpec(
    string Name,
    bool Privileged,
    bool Destructive,
    IReadOnlyCollection<string> Arguments,
    Func<OperationContext, JsonObject, Task<JsonNode?>> Execute);

/// <summary>
/// Every operation the broker accepts.  Each builds a fixed zpool/zfs
/// argument vector from validated arguments; nothing a caller sends is passed
/// through unchecked.  Mirror attach/detach/replace are included because the
/// driver-created mirror passed step 12 and these are verified end to end in
/// the step-14 VM test.  Pool destruction is deliberately absent.
/// </summary>
public static class Operations
{
    static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(20);

    public static IReadOnlyDictionary<string, OperationSpec> All { get; } = Build().ToDictionary(o => o.Name, StringComparer.Ordinal);

    static IEnumerable<OperationSpec> Build()
    {
        // --- read-only ------------------------------------------------------
        yield return Read("version", [], async (c, a) =>
        {
            ToolResult r = await c.RunAsync(ZfsTool.Zfs, ["version"], ReadTimeout);
            string[] lines = r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new JsonObject
            {
                ["broker"] = typeof(Operations).Assembly.GetName().Version?.ToString(3),
                ["protocol"] = Protocol.Version,
                ["userland"] = lines.ElementAtOrDefault(0),
                ["kernel"] = lines.ElementAtOrDefault(1),
            };
        });

        yield return Read("pool.list", [], async (c, a) =>
            ZfsJson.PoolList(await RunJson(c, ZfsTool.Zpool, ["list", "-j", "--json-int"], emptyMeansNone: true)));

        yield return Read("pool.status", ["pool"], async (c, a) =>
        {
            string? pool = Validation.OptionalString(a, "pool");
            var args = new List<string> { "status", "-j", "--json-int", "-p" };
            if (pool is not null)
                args.Add(Validation.PoolName(pool));
            return ZfsJson.PoolStatus(await RunJson(c, ZfsTool.Zpool, args, emptyMeansNone: pool is null));
        });

        yield return Read("pool.get", ["pool", "properties"], async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            return ZfsJson.PoolProperties(await RunJson(c, ZfsTool.Zpool, ["get", "-j", "--json-int", PropertyList(a), pool]), pool);
        });

        yield return Read("dataset.list", ["root", "recursive", "depth"], async (c, a) =>
        {
            var args = new List<string> { "list", "-j", "--json-int", "-t", "filesystem,volume", "-o", ZfsJson.DatasetColumns };
            AddRoot(a, args);
            return ZfsJson.DatasetList(await RunJson(c, ZfsTool.Zfs, args));
        });

        yield return Read("dataset.get", ["dataset", "properties"], async (c, a) =>
        {
            string name = DatasetOrSnapshot(Validation.OptionalString(a, "dataset"));
            return ZfsJson.DatasetProperties(await RunJson(c, ZfsTool.Zfs, ["get", "-j", "--json-int", PropertyList(a), name]), name);
        });

        yield return Read("snapshot.list", ["root", "recursive", "depth"], async (c, a) =>
        {
            var args = new List<string> { "list", "-j", "--json-int", "-t", "snapshot", "-o", ZfsJson.SnapshotColumns };
            AddRoot(a, args, snapshotsOfRootOnly: true);
            return ZfsJson.SnapshotList(await RunJson(c, ZfsTool.Zfs, args));
        });

        yield return Read("mount.list", [], async (c, a) =>
            ZfsJson.MountList((await c.RunAsync(ZfsTool.Zfs, ["mount"], ReadTimeout)).StandardOutput));

        yield return Read("scrub.status", ["pool"], async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            JsonNode status = ZfsJson.PoolStatus(await RunJson(c, ZfsTool.Zpool, ["status", "-j", "--json-int", "-p", pool]));
            JsonNode? entry = status["pools"]?.AsArray().FirstOrDefault();
            return new JsonObject { ["pool"] = pool, ["scan"] = entry?["scan"]?.DeepClone() };
        });

        // --- privileged: device discovery reads raw disks ------------------
        yield return Mutation("pool.discover", ["dirs"], destructive: false, async (c, a) =>
        {
            var args = new List<string> { "import" };
            foreach (string dir in Validation.StringList(a, "dirs"))
                args.AddRange(["-d", Validation.SearchDirectory(dir)]);
            ToolResult r = await c.Runner.RunAsync(ZfsTool.Zpool, args, ImportTimeout, c.CancellationToken);
            // With nothing to import zpool prints "no pools available to import" and exits 1.
            if (r.ExitCode != 0 && !(r.StandardError + r.StandardOutput).Contains("no pools available", StringComparison.OrdinalIgnoreCase))
                throw OperationContext.Failure(ZfsTool.Zpool, args, r);
            return ZfsJson.ImportablePools(r.StandardOutput);
        });

        // --- privileged mutations --------------------------------------------
        yield return Mutation("pool.import", ["pool", "dirs", "newName", "noMount", "readOnly", "force"], destructive: false, async (c, a) =>
        {
            string pool = Validation.PoolNameOrGuid(Validation.OptionalString(a, "pool"));
            var args = new List<string> { "import" };
            foreach (string dir in Validation.StringList(a, "dirs"))
                args.AddRange(["-d", Validation.SearchDirectory(dir)]);
            if (Validation.Flag(a, "noMount")) args.Add("-N");
            if (Validation.Flag(a, "readOnly")) args.AddRange(["-o", "readonly=on"]);
            if (Validation.Flag(a, "force")) args.Add("-f");
            args.Add(pool);
            if (Validation.OptionalString(a, "newName") is string newName)
                args.Add(Validation.PoolName(newName, "newName"));
            await c.RunAsync(ZfsTool.Zpool, args, ImportTimeout);
            return new JsonObject { ["pool"] = Validation.OptionalString(a, "newName") ?? pool, ["imported"] = true };
        });

        yield return Mutation("pool.export", ["pool", "force"], destructive: false, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            var args = new List<string> { "export" };
            if (Validation.Flag(a, "force")) args.Add("-f");
            args.Add(pool);
            await c.RunAsync(ZfsTool.Zpool, args, MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["exported"] = true };
        });

        // Overwrites every listed device; the caller must name each one.
        yield return Mutation("pool.create", ["pool", "layout", "devices", "poolProperties", "datasetProperties", "mountpoint"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"), forCreate: true);
            string layout = Validation.OptionalString(a, "layout") ?? "single";
            IReadOnlyList<string> devices = Validation.StringList(a, "devices").Select(d => Validation.DevicePath(d, "devices")).ToList();
            if (devices.Count == 0)
                throw Validation.Invalid("devices", "at least one device must be named explicitly.");
            if (devices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
                throw Validation.Invalid("devices", "a device is listed twice.");
            var args = new List<string> { "create" };
            foreach (var p in Validation.Properties(a, "poolProperties"))
                args.AddRange(["-o", $"{p.Key}={p.Value}"]);
            foreach (var p in Validation.Properties(a, "datasetProperties"))
                args.AddRange(["-O", $"{p.Key}={p.Value}"]);
            if (Validation.OptionalString(a, "mountpoint") is string mp)
                args.AddRange(["-m", Validation.PropertyValue(mp, "mountpoint")]);
            args.Add(pool);
            switch (layout)
            {
                case "single":
                    break;
                case "mirror":
                    if (devices.Count < 2)
                        throw Validation.Invalid("devices", "a mirror needs at least two devices.");
                    args.Add("mirror");
                    break;
                default:
                    throw Validation.Invalid("layout", "must be 'single' or 'mirror'.");
            }
            args.AddRange(devices);
            await c.RunAsync(ZfsTool.Zpool, args, MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["layout"] = layout, ["devices"] = new JsonArray(devices.Select(d => (JsonNode?)d).ToArray()) };
        });

        foreach (var (op, flag) in new[] { ("scrub.start", (string?)null), ("scrub.pause", "-p"), ("scrub.stop", "-s") })
        {
            yield return Mutation(op, ["pool"], destructive: false, async (c, a) =>
            {
                string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
                var args = new List<string> { "scrub" };
                if (flag is not null) args.Add(flag);
                args.Add(pool);
                await c.RunAsync(ZfsTool.Zpool, args, MutationTimeout);
                return new JsonObject { ["pool"] = pool, ["scrub"] = op["scrub.".Length..] };
            });
        }

        yield return Mutation("dataset.create", ["dataset", "properties", "parents"], destructive: false, async (c, a) =>
        {
            string name = Validation.DatasetName(Validation.OptionalString(a, "dataset"));
            var args = new List<string> { "create" };
            if (Validation.Flag(a, "parents")) args.Add("-p");
            foreach (var p in Validation.Properties(a, "properties"))
                args.AddRange(["-o", $"{p.Key}={p.Value}"]);
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["dataset"] = name, ["created"] = true };
        });

        yield return Mutation("dataset.set", ["dataset", "properties"], destructive: false, async (c, a) =>
        {
            string name = DatasetOrSnapshot(Validation.OptionalString(a, "dataset"));
            var props = Validation.Properties(a, "properties");
            if (props.Count == 0)
                throw Validation.Invalid("properties", "at least one property is required.");
            var args = new List<string> { "set" };
            args.AddRange(props.Select(p => $"{p.Key}={p.Value}"));
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["dataset"] = name, ["set"] = new JsonArray(props.Select(p => (JsonNode?)p.Key).ToArray()) };
        });

        yield return Mutation("dataset.destroy", ["dataset", "recursive"], destructive: true, async (c, a) =>
        {
            string name = Validation.DatasetName(Validation.OptionalString(a, "dataset"));
            if (!name.Contains('/'))
                throw Validation.Invalid("dataset", "a pool's root dataset cannot be destroyed; export the pool instead.");
            var args = new List<string> { "destroy" };
            if (Validation.Flag(a, "recursive")) args.Add("-r");
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["dataset"] = name, ["destroyed"] = true };
        });

        yield return Mutation("dataset.mount", ["dataset"], destructive: false, async (c, a) =>
        {
            string name = Validation.DatasetName(Validation.OptionalString(a, "dataset"));
            await c.RunAsync(ZfsTool.Zfs, ["mount", name], MutationTimeout);
            return new JsonObject { ["dataset"] = name, ["mounted"] = true };
        });

        yield return Mutation("dataset.unmount", ["dataset", "force"], destructive: false, async (c, a) =>
        {
            string name = Validation.DatasetName(Validation.OptionalString(a, "dataset"));
            var args = new List<string> { "unmount" };
            if (Validation.Flag(a, "force")) args.Add("-f");
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["dataset"] = name, ["mounted"] = false };
        });

        yield return Mutation("snapshot.create", ["snapshot", "recursive"], destructive: false, async (c, a) =>
        {
            string name = Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));
            var args = new List<string> { "snapshot" };
            if (Validation.Flag(a, "recursive")) args.Add("-r");
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["snapshot"] = name, ["created"] = true };
        });

        yield return Mutation("snapshot.destroy", ["snapshot", "recursive"], destructive: true, async (c, a) =>
        {
            string name = Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));
            var args = new List<string> { "destroy" };
            if (Validation.Flag(a, "recursive")) args.Add("-r");
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["snapshot"] = name, ["destroyed"] = true };
        });

        yield return Mutation("vdev.attach", ["pool", "device", "newDevice"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            string device = Validation.DevicePath(Validation.OptionalString(a, "device"), "device", mustExist: false);
            string newDevice = Validation.DevicePath(Validation.OptionalString(a, "newDevice"), "newDevice");
            await c.RunAsync(ZfsTool.Zpool, ["attach", pool, await PoolDevice(c, pool, device), newDevice], MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["attached"] = newDevice, ["to"] = device };
        });

        yield return Mutation("vdev.detach", ["pool", "device"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            string device = Validation.DevicePath(Validation.OptionalString(a, "device"), "device", mustExist: false);
            await c.RunAsync(ZfsTool.Zpool, ["detach", pool, await PoolDevice(c, pool, device)], MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["detached"] = device };
        });

        yield return Mutation("vdev.replace", ["pool", "device", "newDevice"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            string device = Validation.DevicePath(Validation.OptionalString(a, "device"), "device", mustExist: false);
            var args = new List<string> { "replace", pool, await PoolDevice(c, pool, device) };
            if (Validation.OptionalString(a, "newDevice") is string nd)
                args.Add(Validation.DevicePath(nd, "newDevice"));
            await c.RunAsync(ZfsTool.Zpool, args, MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["replaced"] = device, ["with"] = args.Count > 3 ? args[3] : device };
        });

        // Read cache (L2ARC) or write log (SLOG) devices.  The devices are
        // overwritten, so this is destructive like create; a log may be a
        // mirror.  Data vdevs are not added here: a mistaken top-level data
        // vdev cannot always be removed again.
        yield return Mutation("vdev.add", ["pool", "role", "devices", "mirror"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            string role = Validation.OptionalString(a, "role") ?? throw Validation.Invalid("role", "must be 'cache' or 'log'.");
            if (role is not ("cache" or "log"))
                throw Validation.Invalid("role", "must be 'cache' or 'log'.");
            IReadOnlyList<string> devices = Validation.StringList(a, "devices").Select(d => Validation.DevicePath(d, "devices")).ToList();
            if (devices.Count == 0)
                throw Validation.Invalid("devices", "at least one device must be named explicitly.");
            if (devices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
                throw Validation.Invalid("devices", "a device is listed twice.");
            bool mirror = Validation.Flag(a, "mirror");
            if (mirror && role != "log")
                throw Validation.Invalid("mirror", "only a log can be mirrored; cache devices are independent.");
            if (mirror && devices.Count < 2)
                throw Validation.Invalid("devices", "a mirror needs at least two devices.");
            var args = new List<string> { "add", pool, role };
            if (mirror)
                args.Add("mirror");
            args.AddRange(devices);
            await c.RunAsync(ZfsTool.Zpool, args, MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["role"] = role, ["mirror"] = mirror, ["devices"] = new JsonArray(devices.Select(d => (JsonNode?)d).ToArray()) };
        });

        // zpool remove: cache and log devices go at once; a top-level data
        // vdev is evacuated first (zpool refuses where that is impossible).
        yield return Mutation("vdev.remove", ["pool", "device"], destructive: true, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            string device = Validation.DevicePath(Validation.OptionalString(a, "device"), "device", mustExist: false);
            await c.RunAsync(ZfsTool.Zpool, ["remove", pool, await PoolDevice(c, pool, device)], MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["removed"] = device };
        });
    }

    /// <summary>
    /// A device already in the pool, as zpool will find it.  After an import
    /// the pool records file vdevs as //./C:/..., which zpool does not match
    /// to the \\?\C:\... form DevicePath() returns ("no such device in
    /// pool"); the device's GUID always matches.  Falls back to the path when
    /// the pool's status cannot be read or does not list the device.
    /// </summary>
    static async Task<string> PoolDevice(OperationContext c, string pool, string device)
    {
        try
        {
            // Without --json-int the GUID is a string: a 64-bit number read
            // through JsonNode loses digits in the Native AOT broker
            // (2817751521488806255 became 2346718527818993400).
            ToolResult r = await c.Runner.RunAsync(ZfsTool.Zpool, ["status", "-j", "-p", pool], ReadTimeout, c.CancellationToken);
            if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StandardOutput) && JsonNode.Parse(r.StandardOutput)?["pools"]?[pool] is JsonNode status)
            {
                string want = DeviceKey(device);
                foreach (JsonObject leaf in DeviceLeaves(status))
                    if (DeviceKey(leaf["path"]?.GetValue<string>()) == want && leaf["guid"] is JsonValue guid && guid.TryGetValue(out string? text) && ulong.TryParse(text, out _))
                        return text;
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
        }
        return device;
    }

    static IEnumerable<JsonObject> DeviceLeaves(JsonNode? node)
    {
        if (node is not JsonObject o)
            yield break;
        if (o["path"] is not null)
        {
            yield return o;
            yield break;
        }
        foreach (string group in new[] { "vdevs", "logs", "l2cache", "spares" })
            if (o[group] is JsonObject children)
                foreach (var child in children)
                    foreach (JsonObject leaf in DeviceLeaves(child.Value))
                        yield return leaf;
    }

    // "\\?\C:\x.img", "//./C:/x.img" and "C:\x.img" name the same file.
    static string DeviceKey(string? path)
    {
        string s = (path ?? "").Replace('/', '\\');
        foreach (string prefix in new[] { @"\\?\", @"\\.\" })
            if (s.StartsWith(prefix, StringComparison.Ordinal))
                s = s[prefix.Length..];
        return s.TrimEnd('\\').ToUpperInvariant();
    }

    static OperationSpec Read(string name, string[] arguments, Func<OperationContext, JsonObject, Task<JsonNode?>> execute) =>
        new(name, Privileged: false, Destructive: false, arguments, execute);

    static OperationSpec Mutation(string name, string[] arguments, bool destructive, Func<OperationContext, JsonObject, Task<JsonNode?>> execute) =>
        new(name, Privileged: true, destructive, arguments, execute);

    static async Task<JsonNode> RunJson(OperationContext c, ZfsTool tool, IReadOnlyList<string> args, bool emptyMeansNone = false)
    {
        ToolResult r = await c.RunAsync(tool, args, ReadTimeout);
        // With no pool imported, "zpool list -j" and "zpool status -j" print
        // nothing on stdout ("no pools available" goes to stderr) and exit 0.
        if (emptyMeansNone && string.IsNullOrWhiteSpace(r.StandardOutput))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(r.StandardOutput) ?? throw new FormatException("empty output");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            throw new AdminException(ErrorCode.Internal, $"{ProcessToolRunner.Describe(tool, args)} printed output that is not JSON: {e.Message}");
        }
    }

    static string PropertyList(JsonObject a)
    {
        IReadOnlyList<string> props = Validation.StringList(a, "properties");
        return props.Count == 0 ? "all" : string.Join(',', props.Select(p => Validation.PropertyName(p, "properties")));
    }

    static string DatasetOrSnapshot(string? name) =>
        name is not null && name.Contains('@') ? Validation.SnapshotName(name, "dataset") : Validation.DatasetName(name);

    static void AddRoot(JsonObject a, List<string> args, bool snapshotsOfRootOnly = false)
    {
        string? root = Validation.OptionalString(a, "root");
        int? depth = null;
        if (a.TryGetPropertyValue("depth", out JsonNode? node) && node is not null)
            depth = node is JsonValue v && v.TryGetValue(out int d) ? d : throw Validation.Invalid("depth", "must be a number.");
        if (depth is < 0 or > 1000)
            throw Validation.Invalid("depth", "must be between 0 and 1000.");
        if (depth is int dd)
            args.AddRange(["-d", dd.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        else if (Validation.Flag(a, "recursive") || root is null)
            args.Add("-r");
        else if (snapshotsOfRootOnly)
            args.AddRange(["-d", "1"]);	// the snapshots of root itself
        if (root is not null)
            args.Add(Validation.DatasetName(root, "root"));
    }
}

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

/// <summary>
/// One broker operation: its privilege, its arguments and how it runs.
/// Privileged operations run one at a time unless <paramref name="Exclusive"/>
/// is false (an administrator-only read such as a long file walk).
/// </summary>
public sealed record OperationSpec(
    string Name,
    bool Privileged,
    bool Destructive,
    IReadOnlyCollection<string> Arguments,
    Func<OperationContext, JsonObject, Task<JsonNode?>> Execute,
    bool Exclusive = true);

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

        // Files whose blocks failed their checksum, and every device's counters.
        yield return Read("pool.errors", ["pool"], async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            return ZfsJson.PoolErrors(await RunJson(c, ZfsTool.Zpool, ["status", "-j", "--json-int", "-p", "-v", pool]), pool);
        });

        // Cumulative counters; callers take the deltas, so nothing blocks.
        yield return Read("pool.iostat", ["pool"], async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            JsonNode get = await RunJson(c, ZfsTool.Zpool, ["get", "-j", "--json-int", "-p", ZfsJson.IostatProperties, pool, "all-vdevs"]);
            return ZfsJson.VdevIostat(get, pool, DateTimeOffset.UtcNow);
        });

        yield return Read("memory.stats", [], async (c, a) =>
        {
            KstatText? arc = await c.Runner.ReadKstatAsync("zfs:0:arcstats", ReadTimeout, c.CancellationToken);
            JsonArray dedup = ZfsJson.DedupTables(await RunJson(c, ZfsTool.Zpool, ["get", "-j", "--json-int", "-p", "dedup_table_size"], emptyMeansNone: true));
            long? service = c.Runner.UserModeServicePrivateBytes();
            return new JsonObject
            {
                ["source"] = arc?.Source,
                ["arc"] = arc is null ? null : ZfsJson.ArcStats(ZfsJson.KstatValues(arc.Text)),
                ["dedupTables"] = dedup,
                ["userModeServicePrivateBytes"] = service,
            };
        });

        // Files and folders below a mounted dataset: a walk, run as the
        // broker, so administrators only; it does not hold up mutations.
        yield return new OperationSpec("dataset.count", Privileged: true, Destructive: false, ["dataset", "objectsOnly"], async (c, a) =>
        {
            string name = Validation.DatasetName(Validation.OptionalString(a, "dataset"));
            var started = DateTimeOffset.UtcNow;
            ToolResult users = await c.Runner.RunAsync(ZfsTool.Zfs, ["userspace", "-H", "-p", "-o", "objused", name], ReadTimeout, c.CancellationToken);
            long? objects = users.ExitCode == 0 ? ZfsJson.SumLines(users.StandardOutput) : null;
            string? mountpoint = ZfsJson.MountList((await c.RunAsync(ZfsTool.Zfs, ["mount"], ReadTimeout)).StandardOutput)["mounts"]!.AsArray()
                .FirstOrDefault(m => m?["dataset"]?.GetValue<string>() == name)?["mountpoint"]?.GetValue<string>();
            var result = new JsonObject
            {
                ["dataset"] = name, ["mountpoint"] = mountpoint, ["mounted"] = mountpoint is not null, ["objects"] = objects,
                ["files"] = null, ["folders"] = null, ["bytes"] = null, ["skipped"] = null, ["complete"] = false,
                ["countedAt"] = started.ToUnixTimeSeconds(),
            };
            if (mountpoint is null || Validation.Flag(a, "objectsOnly"))
                return result;
            string root = MountRoot(mountpoint);
            FileCount count = await Task.Run(() => c.Runner.CountFiles(root, WalkLimit, c.CancellationToken), c.CancellationToken);
            result["files"] = count.Files;
            result["folders"] = count.Folders;
            result["bytes"] = count.Bytes;
            result["skipped"] = count.Skipped;
            result["complete"] = count.Complete;
            result["seconds"] = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1);
            return result;
        }, Exclusive: false);

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
            if (await TemporaryCloneOf(c, name) is string clone)
                throw new AdminException(ErrorCode.Busy, $"{name} is mounted temporarily (as {clone}); unmount it first.");
            var args = new List<string> { "destroy" };
            if (Validation.Flag(a, "recursive")) args.Add("-r");
            args.Add(name);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            return new JsonObject { ["snapshot"] = name, ["destroyed"] = true };
        });

        // Restores the whole dataset.  zfs rollback -r destroys every newer
        // snapshot, so the caller must say so (destroyNewer) when there are
        // any; the dataset is unmounted around it and mounted again where
        // its properties say (the user-mode engine cannot roll back a mounted
        // file system).
        yield return Mutation("snapshot.rollback", ["snapshot", "destroyNewer"], destructive: true, async (c, a) =>
        {
            string name = Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));
            string dataset = name[..name.IndexOf('@')];
            ToolResult list = await c.RunAsync(ZfsTool.Zfs, ["list", "-H", "-p", "-o", "name,createtxg", "-t", "snapshot", "-d", "1", dataset], ReadTimeout);
            var snapshots = ZfsJson.NameNumberLines(list.StandardOutput);
            if (!snapshots.Any(s => s.Name == name))
                throw new AdminException(ErrorCode.NotFound, $"The snapshot {name} does not exist.");
            long txg = snapshots.First(s => s.Name == name).Number;
            var newer = snapshots.Where(s => s.Number > txg).OrderBy(s => s.Number).Select(s => s.Name).ToList();
            if (newer.Count > 0 && !Validation.Flag(a, "destroyNewer"))
                throw new AdminException(ErrorCode.ConfirmationRequired,
                    $"Rolling {dataset} back to {name} destroys {newer.Count} newer snapshot(s): {string.Join(", ", newer)}.  Confirm with destroyNewer.");
            foreach (string s in newer)
                if (await TemporaryCloneOf(c, s) is string clone)
                    throw new AdminException(ErrorCode.Busy, $"{s} is mounted temporarily (as {clone}); unmount it first.");
            bool mounted = (await c.RunAsync(ZfsTool.Zfs, ["get", "-H", "-o", "value", "mounted", dataset], ReadTimeout)).StandardOutput.Trim() == "yes";
            if (mounted)
                await c.RunAsync(ZfsTool.Zfs, ["unmount", dataset], MutationTimeout);
            try
            {
                var args = new List<string> { "rollback" };
                if (newer.Count > 0) args.Add("-r");
                args.Add(name);
                await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            }
            finally
            {
                if (mounted)
                    await c.Runner.RunAsync(ZfsTool.Zfs, ["mount", dataset], MutationTimeout, CancellationToken.None);
            }
            return new JsonObject
            {
                ["snapshot"] = name, ["dataset"] = dataset, ["rolledBack"] = true, ["remounted"] = mounted,
                ["destroyed"] = new JsonArray(newer.Select(n => (JsonNode?)n).ToArray()),
            };
        });

        // A snapshot, read-only, at a drive letter or an empty folder: a
        // read-only clone that shares every block, marked temporary so the
        // broker removes it at its next start if nobody unmounts it.
        yield return Mutation("snapshot.mount", ["snapshot", "mountPoint"], destructive: false, async (c, a) =>
        {
            string name = Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));
            string clone = TemporaryCloneName(name);
            string parent = clone[..clone.LastIndexOf('/')];
            string target = Validation.OptionalString(a, "mountPoint") is string mp ? TemporaryMountPoint(mp) : FreeDriveLetter();
            if ((await c.Runner.RunAsync(ZfsTool.Zfs, ["list", "-H", "-o", "name", clone], ReadTimeout, c.CancellationToken)).ExitCode == 0)
                throw new AdminException(ErrorCode.Busy, $"{name} is already mounted temporarily (as {clone}).");
            if ((await c.Runner.RunAsync(ZfsTool.Zfs, ["list", "-H", "-o", "name", parent], ReadTimeout, c.CancellationToken)).ExitCode != 0)
                await c.RunAsync(ZfsTool.Zfs, ["create", "-o", "canmount=off", "-o", "driveletter=off", "-o", $"{TemporaryProperty}=on", parent], MutationTimeout);
            var args = new List<string> { "clone", "-o", "readonly=on", "-o", "canmount=noauto", "-o", $"{TemporaryProperty}=on" };
            if (target.Length == 2)
                args.AddRange(["-o", $"driveletter={target[0]}"]);
            else
                args.AddRange(["-o", "driveletter=off", "-o", $"mountpoint={target}"]);
            args.AddRange([name, clone]);
            await c.RunAsync(ZfsTool.Zfs, args, MutationTimeout);
            try
            {
                await c.RunAsync(ZfsTool.Zfs, ["mount", clone], MutationTimeout);
            }
            catch
            {
                await c.Runner.RunAsync(ZfsTool.Zfs, ["destroy", clone], MutationTimeout, CancellationToken.None);
                throw;
            }
            string? at = ZfsJson.MountList((await c.Runner.RunAsync(ZfsTool.Zfs, ["mount"], ReadTimeout, c.CancellationToken)).StandardOutput)["mounts"]!.AsArray()
                .FirstOrDefault(m => m?["dataset"]?.GetValue<string>() == clone)?["mountpoint"]?.GetValue<string>();
            return new JsonObject { ["snapshot"] = name, ["clone"] = clone, ["mountPoint"] = at is null ? target : MountRoot(at), ["readOnly"] = true };
        });

        yield return Mutation("snapshot.unmount", ["snapshot"], destructive: false, async (c, a) =>
        {
            string name = Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));
            string clone = TemporaryCloneName(name);
            await DestroyTemporaryAsync(c, clone);
            return new JsonObject { ["snapshot"] = name, ["clone"] = clone, ["unmounted"] = true };
        });

        yield return Mutation("pool.clear", ["pool"], destructive: false, async (c, a) =>
        {
            string pool = Validation.PoolName(Validation.OptionalString(a, "pool"));
            await c.RunAsync(ZfsTool.Zpool, ["clear", pool], MutationTimeout);
            return new JsonObject { ["pool"] = pool, ["cleared"] = true };
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

    // --- temporary snapshot mounts --------------------------------------------

    public const string TemporaryProperty = "org.openzfs.windows:temporary";
    public const string TemporaryParent = ".zfswin-tmp";
    static readonly TimeSpan WalkLimit = TimeSpan.FromMinutes(30);

    /// <summary>"tank/a/b@s1" → "tank/.zfswin-tmp/a_b-s1" ("tank@s1" → "tank/.zfswin-tmp/tank-s1").</summary>
    public static string TemporaryCloneName(string snapshot)
    {
        int at = snapshot.IndexOf('@');
        string dataset = snapshot[..at], snap = snapshot[(at + 1)..];
        int slash = dataset.IndexOf('/');
        string pool = slash < 0 ? dataset : dataset[..slash];
        string flat = slash < 0 ? dataset : dataset[(slash + 1)..].Replace('/', '_');
        return Validation.DatasetName($"{pool}/{TemporaryParent}/{flat}-{snap}", "snapshot");
    }

    static async Task<string?> TemporaryCloneOf(OperationContext c, string snapshot)
    {
        ToolResult r = await c.Runner.RunAsync(ZfsTool.Zfs, ["get", "-H", "-o", "value", "clones", snapshot], ReadTimeout, c.CancellationToken);
        if (r.ExitCode != 0)
            return null;
        return r.StandardOutput.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(n => n.Contains($"/{TemporaryParent}/", StringComparison.Ordinal));
    }

    /// <summary>Destroys a temporary clone, after checking it is one.</summary>
    static async Task DestroyTemporaryAsync(OperationContext c, string clone)
    {
        ToolResult r = await c.Runner.RunAsync(ZfsTool.Zfs, ["get", "-H", "-o", "value", TemporaryProperty, clone], ReadTimeout, c.CancellationToken);
        if (r.ExitCode != 0)
            throw new AdminException(ErrorCode.NotFound, $"{clone} does not exist; the snapshot is not mounted temporarily.");
        if (r.StandardOutput.Trim() != "on")
            throw new AdminException(ErrorCode.InvalidArgument, $"{clone} is not a temporary mount; it is left alone.");
        await c.RunAsync(ZfsTool.Zfs, ["destroy", "-f", clone], MutationTimeout);
    }

    /// <summary>
    /// Removes the temporary mounts left by a GUI that did not unmount them
    /// (closed with "keep mounted", crashed, or a reboot).  The broker runs
    /// this when it starts; returns the clones destroyed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RemoveTemporaryMountsAsync(IToolRunner runner, CancellationToken cancellationToken)
    {
        var c = new OperationContext(runner, cancellationToken);
        ToolResult r = await runner.RunAsync(ZfsTool.Zfs, ["list", "-H", "-o", $"name,{TemporaryProperty}", "-t", "filesystem"], ReadTimeout, cancellationToken);
        if (r.ExitCode != 0)
            return [];
        var removed = new List<string>();
        foreach (string line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = line.Split('\t');
            // Clones only: the .zfswin-tmp parent itself holds nothing.
            if (f.Length == 2 && f[1] == "on" && f[0].Contains($"/{TemporaryParent}/", StringComparison.Ordinal))
            {
                try
                {
                    await DestroyTemporaryAsync(c, f[0]);
                    removed.Add(f[0]);
                }
                catch (AdminException)
                {
                }
            }
        }
        return removed;
    }

    static string TemporaryMountPoint(string value)
    {
        if (value.Length is 2 or 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value.Length == 2 || value[2] == '\\'))
        {
            string letter = char.ToUpperInvariant(value[0]) + ":";
            if (Environment.GetLogicalDrives().Any(d => d.StartsWith(letter, StringComparison.OrdinalIgnoreCase)))
                throw Validation.Invalid("mountPoint", $"{letter} is in use.");
            return letter;
        }
        string folder = value.TrimEnd('\\');
        // A fully qualified local path whose parent exists.
        Validation.SearchDirectory(Path.GetDirectoryName(folder) ?? throw Validation.Invalid("mountPoint", "must be a free drive letter or a folder path."), "mountPoint");
        Validation.PropertyValue(folder, "mountPoint");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw Validation.Invalid("mountPoint", $"{folder} is not empty.");
        return folder;
    }

    static string FreeDriveLetter()
    {
        var used = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        for (char l = 'Z'; l >= 'G'; l--)
            if (!used.Contains(l))
                return l + ":";
        throw new AdminException(ErrorCode.Busy, "No drive letter between G: and Z: is free; give an empty folder instead.");
    }

    /// <summary>`zfs mount` prints "D:/docs/"; Windows wants "D:\docs\".</summary>
    internal static string MountRoot(string mountpoint)
    {
        string p = mountpoint.Replace('/', '\\');
        return p.Length == 2 && p[1] == ':' ? p + "\\" : p;
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

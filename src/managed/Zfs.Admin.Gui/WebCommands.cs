using System.Text.Json;
using System.Text.Json.Nodes;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>What the web page asks the window for: native dialogs, Explorer, the clipboard, Windows services.</summary>
interface IWebHost
{
    string Edition { get; }
    /// <summary>The Overview figures for every pool (DashboardData without the HTML).</summary>
    Task<DashboardData> DashboardAsync();
    (bool Running, string Text) CountState { get; }
    void StartCount();
    void CancelCount();
    Task<IReadOnlyDictionary<int, DriveHealth>> DriveHealthAsync();
    /// <summary>The native device picker; null when cancelled.</summary>
    DevicePick? PickDevices(string title, string explanation, int maxDevices, bool offerMirror, IReadOnlyList<string> inUse);
    string? PickFolder(string description);
    void OpenFolder(string path);
    void RevealFile(string path);
    void SetClipboard(string text);
    /// <summary>Asks where, then saves the HTML report; the file name, or null when cancelled.</summary>
    Task<string?> SaveReportAsync();
    IReadOnlyList<string> FreeDriveLetters();
    /// <summary>Snapshots this window mounted: unmounted when it closes unless <see cref="KeepMounted"/>.</summary>
    ISet<string> MountedHere { get; }
    bool KeepMounted { get; set; }
    string ServiceState();
    Task ControlServiceAsync(string action);
    UserModeMounts Mounts { get; }
}

sealed record DevicePick(IReadOnlyList<string> Devices, string Summary, bool Mirror);

/// <summary>
/// The web page's only way to read or change anything: a fixed table of
/// commands.  Each validates its arguments, destructive ones check the
/// confirmation the user typed against the target again, and devices for
/// attach and add must come from the native picker this window opened.  The
/// broker validates and audits every pool operation once more.
/// </summary>
sealed class WebCommands
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    readonly AdminClient client;
    readonly IWebHost host;
    readonly Dictionary<string, Func<JsonObject, Task<object?>>> table;
    readonly Dictionary<string, DevicePick> picks = [];
    readonly Dictionary<string, IostatSample> lastSamples = [];
    readonly Dictionary<string, PoolErrorsInfo> lastErrors = [];
    bool mutating;

    public WebCommands(AdminClient client, IWebHost host)
    {
        this.client = client;
        this.host = host;
        table = new()
        {
            // Reads.
            ["app.state"] = _ => AppStateAsync(),
            ["overview.get"] = _ => OverviewAsync(),
            ["datasets.get"] = a => DatasetsAsync(Pool(a)),
            ["dataset.properties"] = async a => await client.DatasetPropertiesAsync(Dataset(a)),
            ["snapshots.get"] = a => SnapshotsAsync(Dataset(a)),
            ["devices.get"] = a => DevicesAsync(Pool(a)),
            ["health.get"] = a => HealthAsync(Pool(a)),
            ["rates.sample"] = a => RatesAsync(Pool(a)),
            ["service.get"] = _ => Task.FromResult<object?>(ServiceInfo()),

            // Changes.
            ["pool.scrub"] = a => Mutate(() => client.ScrubAsync(Pool(a), OneOf(a, "action", "start", "pause", "stop"))),
            ["pool.clear"] = a => Mutate(() => client.ClearErrorsAsync(Confirmed(a, Pool(a)))),
            ["dataset.set"] = a => Mutate(() => SetPropertyAsync(a)),
            ["snapshot.create"] = a => Mutate(() => CreateSnapshotAsync(a)),
            ["snapshot.mount"] = a => MutateWith(() => MountSnapshotAsync(a)),
            ["snapshot.unmount"] = a => Mutate(() => UnmountSnapshotAsync(Snapshot(a))),
            ["snapshot.open"] = a => OpenSnapshotAsync(Snapshot(a)),
            ["snapshot.rollback"] = a => Mutate(() => RollbackAsync(a)),
            ["snapshot.destroy"] = a => Mutate(() => DestroySnapshotAsync(a)),
            ["snapshots.keepMounted"] = a => { host.KeepMounted = Validation.Flag(a, "value"); return Task.FromResult<object?>(null); },
            ["devices.pick"] = a => PickDevicesAsync(a),
            ["device.attach"] = a => Mutate(() => AttachAsync(a)),
            ["device.detach"] = a => Mutate(() => DetachAsync(a)),
            ["device.add"] = a => Mutate(() => AddDevicesAsync(a)),
            ["device.remove"] = a => Mutate(() => RemoveDeviceAsync(a)),
            ["file.reveal"] = a => Task.FromResult(RevealDamagedFile(a)),
            ["files.copy"] = a => Task.FromResult(CopyDamagedFiles(a)),
            ["count.start"] = _ => { host.StartCount(); return Task.FromResult<object?>(null); },
            ["count.cancel"] = _ => { host.CancelCount(); return Task.FromResult<object?>(null); },
            ["report.save"] = async _ => await host.SaveReportAsync(),
            ["service.control"] = a => Mutate(() => ControlServiceAsync(a)),
            ["mount.save"] = a => Task.FromResult(SaveMount(a)),
            ["mount.remove"] = a => Task.FromResult(RemoveMount(a)),
            ["pick.folder"] = a => Task.FromResult<object?>(host.PickFolder(Validation.OptionalString(a, "description") ?? "Choose a folder")),
        };
    }

    /// <summary>Every command the page may send.</summary>
    public IReadOnlyCollection<string> Names => table.Keys;

    /// <summary>
    /// One message from the page: {"id","cmd","args"} → {"type":"result","id","ok", "data" | "error"}.
    /// Anything malformed is answered with bad-request; nothing outside the table runs.
    /// </summary>
    public async Task<JsonObject> HandleAsync(string message)
    {
        string? id = null;
        try
        {
            JsonObject request = JsonNode.Parse(message) as JsonObject ?? throw Bad("the message is not a JSON object.");
            id = request["id"]?.GetValueKind() == JsonValueKind.String ? request["id"]!.GetValue<string>() : throw Bad("the message has no id.");
            string cmd = request["cmd"]?.GetValueKind() == JsonValueKind.String ? request["cmd"]!.GetValue<string>() : throw Bad("the message has no command.");
            if (!table.TryGetValue(cmd, out var handler))
                throw Bad($"unknown command '{cmd}'.");
            JsonObject args = request["args"] switch
            {
                null => [],
                JsonObject o => o,
                _ => throw Bad("args must be an object."),
            };
            object? data = await handler(args);
            return new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = true, ["data"] = JsonSerializer.SerializeToNode(data, Json) };
        }
        catch (Exception e) when (e is AdminException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            AdminException a = e as AdminException ?? new AdminException(e is JsonException ? ErrorCode.BadRequest : ErrorCode.CommandFailed, e.Message);
            return new JsonObject
            {
                ["type"] = "result", ["id"] = id, ["ok"] = false,
                ["error"] = new JsonObject { ["code"] = ErrorCodes.ToWire(a.Code), ["message"] = a.Message, ["detail"] = a.Detail },
            };
        }
    }

    static AdminException Bad(string message) => new(ErrorCode.BadRequest, message);

    // --- arguments ------------------------------------------------------------

    static string Pool(JsonObject a) => Validation.PoolName(Validation.OptionalString(a, "pool"));
    static string Dataset(JsonObject a) => Validation.DatasetName(Validation.OptionalString(a, "dataset"));
    static string Snapshot(JsonObject a) => Validation.SnapshotName(Validation.OptionalString(a, "snapshot"));

    static string OneOf(JsonObject a, string name, params string[] allowed) =>
        Validation.OptionalString(a, name) is string v && allowed.Contains(v, StringComparer.Ordinal)
            ? v
            : throw Validation.Invalid(name, $"one of {string.Join(", ", allowed)} is required.");

    /// <summary>The text the user typed in the confirmation must equal <paramref name="expected"/>.</summary>
    static T Confirmed<T>(JsonObject a, T expected)
    {
        if (Validation.OptionalString(a, "confirm") != expected?.ToString())
            throw new AdminException(ErrorCode.ConfirmationRequired, $"Type {expected} to confirm.");
        return expected;
    }

    /// <summary>One change at a time: a second click while the first runs is refused, not queued.</summary>
    async Task<object?> Mutate(Func<Task> action) => await MutateWith(async () => { await action(); return null; });

    async Task<object?> MutateWith(Func<Task<object?>> action)
    {
        if (mutating)
            throw new AdminException(ErrorCode.Busy, "Another change is still running.");
        mutating = true;
        try
        {
            return await action();
        }
        finally
        {
            mutating = false;
        }
    }

    // --- reads ------------------------------------------------------------------

    async Task<object?> AppStateAsync()
    {
        var state = new JsonObject { ["edition"] = host.Edition, ["service"] = host.ServiceState() };
        try
        {
            JsonNode? version = await client.VersionAsync();
            state["broker"] = version?["broker"]?.DeepClone();
            state["openzfs"] = (version?["kernel"] ?? version?["userland"])?.DeepClone();
            state["brokerAvailable"] = true;
            var pools = new JsonArray();
            foreach (PoolInfo p in await client.PoolsAsync())
            {
                PoolStatusInfo? s = await client.StatusAsync(p.Name);
                IReadOnlyList<VdevInfo> leaves = s is null ? [] : AllLeaves(s);
                pools.Add((JsonNode)new JsonObject
                {
                    ["name"] = p.Name, ["health"] = p.Health ?? s?.State,
                    ["capacityPercent"] = p.CapacityPercent,
                    ["errorCount"] = s?.ErrorCount ?? 0,
                    ["deviceProblems"] = leaves.Count(v => v.State is not ("ONLINE" or null) || v.ReadErrors + v.WriteErrors + v.ChecksumErrors > 0),
                    ["scan"] = JsonSerializer.SerializeToNode(s?.Scan, Json),
                });
            }
            state["pools"] = pools;
            state["datasets"] = JsonSerializer.SerializeToNode((await client.DatasetsAsync()).Where(d => !IsTemporary(d.Name)).Select(d => d.Name), Json);
        }
        catch (AdminException e) when (e.Code == ErrorCode.BrokerUnavailable)
        {
            state["brokerAvailable"] = false;
            state["brokerError"] = e.Message;
            state["pools"] = new JsonArray();
            state["datasets"] = new JsonArray();
        }
        return state;
    }

    async Task<object?> OverviewAsync()
    {
        DashboardData data = await host.DashboardAsync();
        var (running, text) = host.CountState;
        return new { data.GeneratedAt, data.Pools, data.Memory, Counting = running, CountText = text };
    }

    async Task<object?> DatasetsAsync(string pool) =>
        (await client.DatasetsAsync(pool)).Where(d => !IsTemporary(d.Name)).ToList();

    async Task<object?> SnapshotsAsync(string dataset)
    {
        IReadOnlyList<SnapshotInfo> list = await client.SnapshotsAsync(dataset);
        IReadOnlyDictionary<string, string> mounts = await client.TemporaryMountsAsync(list);
        return new
        {
            Dataset = dataset,
            Snapshots = list.Reverse().ToList(),
            Mounts = mounts,
            MountedHere = host.MountedHere.ToList(),
            host.KeepMounted,
            FreeLetters = host.FreeDriveLetters(),
        };
    }

    async Task<object?> DevicesAsync(string pool)
    {
        PoolStatusInfo status = await client.StatusAsync(pool) ?? throw new AdminException(ErrorCode.NotFound, $"The pool {pool} was not found.");
        var datasets = (await client.DatasetsAsync(pool)).Where(d => !IsTemporary(d.Name)).ToList();
        int rootCopies = datasets.FirstOrDefault(d => d.Name == pool)?.Copies ?? 1;
        return new
        {
            Status = status,
            Redundancy = Redundancy.Copies(status, rootCopies),
            Copies = datasets.Select(d => new { d.Name, d.Copies, Redundancy = Redundancy.Copies(status, d.Copies) }).ToList(),
            Leaves = LeafHealth.Of(status, await host.DriveHealthAsync()),
        };
    }

    async Task<object?> HealthAsync(string pool)
    {
        PoolErrorsInfo errors = await client.PoolErrorsAsync(pool);
        lastErrors[pool] = errors;
        string checksum = (await client.DatasetPropertiesAsync(pool, ["checksum"])).FirstOrDefault(p => p.Name == "checksum")?.Value ?? "on";
        return new
        {
            Errors = errors,
            Files = errors.Files.Select((f, i) => new { Index = i, f.Display, f.Kind, f.Dataset, f.Snapshot, CanReveal = RevealablePath(f) is not null }).ToList(),
            Checksum = checksum == "on" ? "fletcher4" : checksum,
        };
    }

    async Task<object?> RatesAsync(string pool)
    {
        IostatSample sample = await client.IostatAsync(pool);
        MemoryInfo? memory = null;
        try
        {
            memory = await client.MemoryAsync();
        }
        catch (AdminException)
        {
        }
        lastSamples.TryGetValue(pool, out IostatSample? before);
        lastSamples[pool] = sample;
        if (before is null || before.Pool != sample.Pool || sample.SampledAt <= before.SampledAt)
            return new { Pending = true, Memory = memory };
        return new
        {
            Pending = false,
            sample.SampledAt,
            Total = sample.TotalRatesSince(before),
            Devices = sample.Vdevs.Keys.Select(d => new { Device = d, Rates = sample.VdevRatesSince(before, d) }).ToList(),
            Memory = memory,
        };
    }

    object ServiceInfo() => new { State = host.ServiceState(), Mounts = host.Mounts.List() };

    // --- changes ----------------------------------------------------------------

    Task SetPropertyAsync(JsonObject a)
    {
        string dataset = Dataset(a);
        string property = OneOf(a, "property", "compression", "dedup", "sync");
        IReadOnlyList<string> allowed = property switch
        {
            "compression" => AdminClient.CompressionValues,
            "dedup" => AdminClient.DedupValues,
            _ => AdminClient.SyncValues,
        };
        string value = OneOf(a, "value", [.. allowed]);
        return client.SetDatasetPropertyAsync(dataset, property, value);
    }

    Task CreateSnapshotAsync(JsonObject a)
    {
        string dataset = Dataset(a);
        string name = Validation.SnapshotName($"{dataset}@{Validation.OptionalString(a, "name")?.Trim()}", "name");
        return client.CreateSnapshotAsync(name, Validation.Flag(a, "recursive"));
    }

    async Task<object?> MountSnapshotAsync(JsonObject a)
    {
        string snapshot = Snapshot(a);
        string? mountPoint = Validation.OptionalString(a, "mountPoint")?.Trim();
        if (mountPoint is { Length: > 0 } && !(mountPoint.Length == 2 && char.IsAsciiLetter(mountPoint[0]) && mountPoint[1] == ':') && !Path.IsPathFullyQualified(mountPoint))
            throw Validation.Invalid("mountPoint", "a drive letter (R:) or a full folder path is required.");
        TemporaryMount mount = await client.MountSnapshotAsync(snapshot, mountPoint is { Length: > 0 } ? mountPoint : null);
        host.MountedHere.Add(snapshot);
        host.OpenFolder(mount.MountPoint);
        return mount;
    }

    async Task UnmountSnapshotAsync(string snapshot)
    {
        await client.UnmountSnapshotAsync(snapshot);
        host.MountedHere.Remove(snapshot);
    }

    async Task<object?> OpenSnapshotAsync(string snapshot)
    {
        SnapshotInfo s = await FindSnapshotAsync(snapshot);
        IReadOnlyDictionary<string, string> mounts = await client.TemporaryMountsAsync([s]);
        host.OpenFolder(mounts.TryGetValue(snapshot, out string? at) ? at : throw new AdminException(ErrorCode.InvalidArgument, $"{snapshot} is not mounted."));
        return null;
    }

    async Task<SnapshotInfo> FindSnapshotAsync(string snapshot)
    {
        string dataset = snapshot[..snapshot.IndexOf('@')];
        return (await client.SnapshotsAsync(dataset)).FirstOrDefault(s => s.Name == snapshot)
            ?? throw new AdminException(ErrorCode.NotFound, $"The snapshot {snapshot} was not found.");
    }

    async Task RollbackAsync(JsonObject a)
    {
        string snapshot = Snapshot(a);
        string dataset = snapshot[..snapshot.IndexOf('@')];
        IReadOnlyList<SnapshotInfo> all = await client.SnapshotsAsync(dataset);
        SnapshotInfo s = all.FirstOrDefault(x => x.Name == snapshot) ?? throw new AdminException(ErrorCode.NotFound, $"The snapshot {snapshot} was not found.");
        Confirmed(a, s.Snapshot);
        var newer = all.Where(x => x.Creation > s.Creation).ToList();
        IReadOnlyDictionary<string, string> mounts = await client.TemporaryMountsAsync(newer);
        if (mounts.Count > 0)
            throw new AdminException(ErrorCode.InvalidArgument, $"Unmount the newer snapshots first: {string.Join(", ", mounts.Keys)}.");
        await client.RollbackAsync(snapshot, destroyNewer: newer.Count > 0, confirmed: true);
    }

    async Task DestroySnapshotAsync(JsonObject a)
    {
        SnapshotInfo s = await FindSnapshotAsync(Snapshot(a));
        Confirmed(a, s.Snapshot);
        if ((await client.TemporaryMountsAsync([s])).ContainsKey(s.Name))
            throw new AdminException(ErrorCode.InvalidArgument, $"{s.Name} is mounted temporarily; unmount it first.");
        await client.DestroySnapshotAsync(s.Name, confirmed: true);
    }

    async Task<object?> PickDevicesAsync(JsonObject a)
    {
        string pool = Pool(a);
        string purpose = OneOf(a, "purpose", "mirror", "cache", "log");
        IReadOnlyList<string> inUse = await DevicePathsInUseAsync();
        DevicePick? pick = purpose switch
        {
            "mirror" => host.PickDevices("Add mirror device", $"The new device becomes a mirror of {Validation.OptionalString(a, "device")} in {pool}; it must be at least as large.", 1, false, inUse),
            "cache" => host.PickDevices("Add read cache (L2ARC)", "Read cache devices extend the RAM cache with fast storage (SSD).  Several are used independently.", 8, false, inUse),
            _ => host.PickDevices("Add write log (SLOG)", "A write log takes synchronous writes until they reach the pool.  Use fast, power-safe storage and mirror it.", 8, true, inUse),
        };
        if (pick is null)
            return null;
        string token = Guid.NewGuid().ToString("N");
        picks[token] = pick;
        return new { Token = token, pick.Devices, pick.Summary, pick.Mirror };
    }

    DevicePick TakePick(JsonObject a) =>
        Validation.OptionalString(a, "pick") is string token && picks.Remove(token, out DevicePick? pick)
            ? pick
            : throw Validation.Invalid("pick", "choose the devices in the device picker first.");

    async Task<IReadOnlyList<string>> DevicePathsInUseAsync()
    {
        var paths = new List<string>();
        foreach (PoolInfo pool in await client.PoolsAsync())
            if (await client.StatusAsync(pool.Name) is PoolStatusInfo status)
                paths.AddRange(AllLeaves(status).Select(v => v.Path ?? v.Name));
        return paths;
    }

    async Task<PoolStatusInfo> StatusAsync(string pool) =>
        await client.StatusAsync(pool) ?? throw new AdminException(ErrorCode.NotFound, $"The pool {pool} was not found.");

    async Task AttachAsync(JsonObject a)
    {
        string pool = Pool(a);
        string device = Validation.Required(Validation.OptionalString(a, "device"), "device");
        PoolStatusInfo status = await StatusAsync(pool);
        if (!DataVdevs(status).SelectMany(Flatten).Any(v => v.IsLeaf && (v.Path ?? v.Name) == device))
            throw Validation.Invalid("device", $"{device} is not a data disk of {pool}.");
        Confirmed(a, "attach");
        DevicePick pick = TakePick(a);
        if (pick.Devices.Count != 1)
            throw Validation.Invalid("pick", "a mirror is added one device at a time.");
        await client.AttachMirrorAsync(pool, device, pick.Devices[0], confirmed: true);
    }

    async Task DetachAsync(JsonObject a)
    {
        string pool = Pool(a);
        string device = Validation.Required(Validation.OptionalString(a, "device"), "device");
        PoolStatusInfo status = await StatusAsync(pool);
        bool inMirror = DataVdevs(status).SelectMany(Flatten).Any(v => v.Type == "mirror" && v.Children.Any(c => c.IsLeaf && (c.Path ?? c.Name) == device));
        if (!inMirror)
            throw Validation.Invalid("device", $"{device} is not part of a mirror; a disk that is not mirrored holds the only copy.");
        Confirmed(a, "detach");
        await client.DetachMirrorAsync(pool, device, confirmed: true);
    }

    async Task AddDevicesAsync(JsonObject a)
    {
        string pool = Pool(a);
        string role = OneOf(a, "role", "cache", "log");
        Confirmed(a, "add");
        DevicePick pick = TakePick(a);
        await client.AddCacheDevicesAsync(pool, role, pick.Devices, role == "log" && pick.Mirror, confirmed: true);
    }

    async Task RemoveDeviceAsync(JsonObject a)
    {
        string pool = Pool(a);
        string device = Validation.Required(Validation.OptionalString(a, "device"), "device");
        PoolStatusInfo status = await StatusAsync(pool);
        bool removable = status.Caches.Concat(status.Logs).SelectMany(Flatten).Any(v => (v.Path ?? v.Name) == device);
        if (!removable)
            throw Validation.Invalid("device", $"{device} is not a read cache or write log of {pool}.");
        Confirmed(a, "remove");
        await client.RemoveDeviceAsync(pool, device, confirmed: true);
    }

    object? RevealDamagedFile(JsonObject a)
    {
        string pool = Pool(a);
        int index = a["index"]?.GetValueKind() == JsonValueKind.Number ? a["index"]!.GetValue<int>() : -1;
        if (!lastErrors.TryGetValue(pool, out PoolErrorsInfo? errors) || index < 0 || index >= errors.Files.Count)
            throw Validation.Invalid("index", "the damaged file is no longer listed; refresh the view.");
        DamagedFile f = errors.Files[index];
        string path = RevealablePath(f) ?? throw new AdminException(ErrorCode.InvalidArgument, f.Snapshot is not null
            ? $"The damaged block is only in the snapshot {f.Dataset}@{f.Snapshot}.  Mount the snapshot to look at it, or destroy the snapshot to free the block."
            : $"{f.Display} has no path on a mounted dataset.");
        host.RevealFile(path);
        return null;
    }

    /// <summary>A path under a mounted dataset; snapshot files and unmounted datasets have none.</summary>
    static string? RevealablePath(DamagedFile f) =>
        f.Kind == "file" && f.Dataset is null && f.Path is string p && Path.IsPathFullyQualified(p) ? p : null;

    object? CopyDamagedFiles(JsonObject a)
    {
        string pool = Pool(a);
        if (lastErrors.TryGetValue(pool, out PoolErrorsInfo? e) && e.Files.Count > 0)
            host.SetClipboard(string.Join(Environment.NewLine, e.Files.Select(f => f.Display)));
        return null;
    }

    Task ControlServiceAsync(JsonObject a)
    {
        string action = OneOf(a, "action", "start", "stop", "restart");
        if (action != "start")
            Confirmed(a, action);
        return host.ControlServiceAsync(action);
    }

    object? SaveMount(JsonObject a)
    {
        var m = new UserModeMount(
            Validation.OptionalString(a, "name")?.Trim() ?? "",
            Validation.OptionalString(a, "source")?.Trim() ?? "",
            Validation.OptionalString(a, "pool")?.Trim() ?? "",
            Validation.OptionalString(a, "dataset")?.Trim() is { Length: > 0 } ds ? ds : Validation.OptionalString(a, "pool")?.Trim() ?? "",
            Validation.OptionalString(a, "mountPoint")?.Trim() ?? "",
            Validation.Flag(a, "readOnly"));
        bool replacing = Validation.Flag(a, "replace");
        if (!replacing && host.Mounts.List().Any(x => x.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
            throw Validation.Invalid("name", $"a mount named {m.Name} exists already.");
        host.Mounts.Save(m);
        return new { RestartSuggested = host.ServiceState() == "Running" };
    }

    object? RemoveMount(JsonObject a)
    {
        string name = Validation.Required(Validation.OptionalString(a, "name"), "name");
        if (!host.Mounts.List().Any(m => m.Name == name))
            throw new AdminException(ErrorCode.NotFound, $"There is no mount named {name}.");
        host.Mounts.Remove(Confirmed(a, name));
        return null;
    }

    // --- helpers ----------------------------------------------------------------

    static IEnumerable<VdevInfo> DataVdevs(PoolStatusInfo s) => s.Vdevs.SelectMany(r => r.Type == "root" ? r.Children : [r]);

    static IEnumerable<VdevInfo> Flatten(VdevInfo v) => [v, .. v.Children.SelectMany(Flatten)];

    static IReadOnlyList<VdevInfo> AllLeaves(PoolStatusInfo s) =>
        s.Vdevs.Concat(s.Logs).Concat(s.Caches).Concat(s.Spares).Concat(s.Special).Concat(s.Dedup).SelectMany(Flatten).Where(v => v.IsLeaf).ToList();

    internal static bool IsTemporary(string dataset) => dataset.Contains($"/{Operations.TemporaryParent}", StringComparison.Ordinal);
}

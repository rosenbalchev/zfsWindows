using System.Text.Json;
using System.Text.Json.Nodes;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Headless check for automated tests (a remoting session has no
        // desktop): the same queries the windows make, as a JSON report.
        // --roundtrip <dataset> also snapshots it, mounts that snapshot
        // temporarily, reads it, and removes both again.
        if (args is ["--self-check", string output])
            return SelfCheck(output, null).GetAwaiter().GetResult();
        if (args is ["--self-check", string output2, "--roundtrip", string dataset])
            return SelfCheck(output2, dataset).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        // Every tab as a PNG (evidence for the VM suites); needs a desktop.
        if (args is ["--screenshots", string directory])
        {
            Application.Run(new MainForm { ScreenshotDirectory = Path.GetFullPath(directory) });
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.png").Any() ? 0 : 1;
        }
        Application.Run(new MainForm());
        return 0;
    }

    static async Task<int> SelfCheck(string outputPath, string? roundtripDataset)
    {
        var report = new JsonObject { ["tool"] = "ZfsWinGui", ["version"] = typeof(Program).Assembly.GetName().Version?.ToString() };
        var failures = new JsonArray();
        var client = new AdminClient();
        try
        {
            report["broker"] = (await client.VersionAsync())?.DeepClone();
            var pools = new JsonArray();
            foreach (PoolInfo p in await client.PoolsAsync())
            {
                PoolStatusInfo? s = await client.StatusAsync(p.Name);
                pools.Add((JsonNode)new JsonObject
                {
                    ["name"] = p.Name, ["health"] = p.Health, ["dedupRatio"] = p.DedupRatio,
                    ["dataVdevs"] = s?.Vdevs.Sum(Count) ?? 0, ["logs"] = s?.Logs.Count ?? 0, ["caches"] = s?.Caches.Count ?? 0,
                    ["scan"] = s?.Scan is { } scan ? $"{scan.Function}/{scan.State}" : null,
                });
            }
            report["pools"] = pools;
            var datasets = new JsonArray();
            foreach (DatasetInfo d in await client.DatasetsAsync())
            {
                var props = await client.DatasetPropertiesAsync(d.Name, ["compression", "dedup", "sync"]);
                datasets.Add((JsonNode)new JsonObject
                {
                    ["name"] = d.Name, ["mounted"] = d.Mounted,
                    ["compression"] = props.FirstOrDefault(x => x.Name == "compression")?.Value,
                    ["dedup"] = props.FirstOrDefault(x => x.Name == "dedup")?.Value,
                });
            }
            report["datasets"] = datasets;

            // Step 20: the Integrity, Rates and Overview queries.
            var monitoring = new JsonArray();
            foreach (PoolInfo p in await client.PoolsAsync())
            {
                try
                {
                    PoolErrorsInfo errors = await client.PoolErrorsAsync(p.Name);
                    IostatSample first = await client.IostatAsync(p.Name);
                    await Task.Delay(1000);
                    IostatSample second = await client.IostatAsync(p.Name);
                    monitoring.Add((JsonNode)new JsonObject
                    {
                        ["pool"] = p.Name, ["damagedFiles"] = errors.Files.Count, ["errorCount"] = errors.ErrorCount,
                        ["devices"] = errors.Devices.Count, ["iostatVdevs"] = second.Vdevs.Count,
                        ["readOps"] = second.Total.ReadOps, ["writeOps"] = second.Total.WriteOps,
                        ["writeBytesPerSecond"] = second.TotalRatesSince(first).WriteBytesPerSecond,
                    });
                }
                catch (AdminException e)
                {
                    failures.Add((JsonNode)$"{p.Name}: {ErrorCodes.ToWire(e.Code)}: {e.Message}");
                }
            }
            report["monitoring"] = monitoring;
            try
            {
                MemoryInfo memory = await client.MemoryAsync();
                report["memory"] = new JsonObject
                {
                    ["source"] = memory.Source, ["arcSize"] = memory.Arc?.Size, ["arcMax"] = memory.Arc?.Max,
                    ["userModeServicePrivateBytes"] = memory.UserModeServicePrivateBytes,
                };
                if (memory.Arc is not { Size: > 0, Max: > 0 })
                    failures.Add((JsonNode)"memory.stats: no ARC size or maximum");
            }
            catch (AdminException e)
            {
                failures.Add((JsonNode)$"memory.stats: {ErrorCodes.ToWire(e.Code)}: {e.Message}");
            }
            report["overviewBytes"] = DashboardHtml.Render(new DashboardData("self-check", DateTimeOffset.UtcNow, [], null)).Length;
            if (roundtripDataset is not null)
                report["roundtrip"] = await SnapshotRoundTrip(client, roundtripDataset, failures);
        }
        catch (AdminException e)
        {
            // Expected on the user-mode edition, which has no broker.
            report["brokerError"] = $"{ErrorCodes.ToWire(e.Code)}: {e.Message}";
        }
        try
        {
            report["userModeMounts"] = UserModeMounts.Machine().List().Count;
            report["userModeService"] = UserModeService.State();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
        {
            failures.Add((JsonNode)$"user mode: {e.Message}");
        }
        report["failures"] = failures;
        File.WriteAllText(outputPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;

        static int Count(VdevInfo v) => v.IsLeaf ? 1 : v.Children.Sum(Count);
    }

    /// <summary>Snapshot, mount it temporarily, list it, try a write, unmount, destroy: the Snapshots tab's path.</summary>
    static async Task<JsonObject> SnapshotRoundTrip(AdminClient client, string dataset, JsonArray failures)
    {
        string snapshot = $"{dataset}@zfswin-selfcheck-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = new JsonObject { ["snapshot"] = snapshot };
        try
        {
            await client.CreateSnapshotAsync(snapshot, recursive: false);
            TemporaryMount mount = await client.MountSnapshotAsync(snapshot);
            result["mountPoint"] = mount.MountPoint;
            result["clone"] = mount.CloneName;
            result["entries"] = Directory.Exists(mount.MountPoint) ? Directory.EnumerateFileSystemEntries(mount.MountPoint).Count() : -1;
            try
            {
                File.WriteAllText(Path.Combine(mount.MountPoint, "zfswin-selfcheck.txt"), "x");
                result["writeRefused"] = false;
                failures.Add((JsonNode)"roundtrip: the temporary mount accepted a write");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result["writeRefused"] = true;
            }
            await client.UnmountSnapshotAsync(snapshot);
            result["unmounted"] = true;
        }
        catch (AdminException e)
        {
            failures.Add((JsonNode)$"roundtrip: {ErrorCodes.ToWire(e.Code)}: {e.Message}");
        }
        finally
        {
            try
            {
                await client.DestroySnapshotAsync(snapshot, confirmed: true);
            }
            catch (AdminException)
            {
            }
        }
        return result;
    }
}

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
        if (args.Length == 2 && args[0] == "--self-check")
            return SelfCheck(args[1]).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    static async Task<int> SelfCheck(string outputPath)
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
}

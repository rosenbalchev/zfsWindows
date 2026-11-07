using System.Text.Json.Nodes;
using Zfs.Admin;
using Zfs.Admin.Cli;

namespace Zfs.Admin.Tests;

/// <summary>Read cache (L2ARC) and write log (SLOG) devices: vdev.add / vdev.remove.</summary>
public class CacheDeviceTests
{
    static async Task<AdminException> Fails(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    [Fact]
    public async Task CacheAndLogDevicesAreAddedWithExplicitDevices()
    {
        await using var broker = new TestBroker();
        string dir = Directory.CreateTempSubdirectory("zfswin-test").FullName;
        try
        {
            string a = Path.Combine(dir, "a.img"), b = Path.Combine(dir, "b.img");
            File.WriteAllBytes(a, [0]);
            File.WriteAllBytes(b, [0]);

            await broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "cache", ["devices"] = new JsonArray(a), ["confirm"] = true });
            await broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "log", ["mirror"] = true, ["devices"] = new JsonArray(a, b), ["confirm"] = true });
            await broker.Client.CallAsync("vdev.remove", new JsonObject { ["pool"] = "tank", ["device"] = a, ["confirm"] = true });
            var calls = broker.Runner.Calls.Select(c => c.Args).Where(c => c[0] != "status").ToArray();
            Assert.Equal(["add", "tank", "cache", @"\\?\" + a], calls[0]);
            Assert.Equal(["add", "tank", "log", "mirror", @"\\?\" + a, @"\\?\" + b], calls[1]);
            // The fake pool status lists no devices, so the path is passed on.
            Assert.Equal(["remove", "tank", @"\\?\" + a], calls[2]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DevicesOfAnImportedPoolAreNamedByGuid()
    {
        await using var broker = new TestBroker();
        string dir = Directory.CreateTempSubdirectory("zfswin-test").FullName;
        try
        {
            string a = Path.Combine(dir, "a.img"), c = Path.Combine(dir, "c.img"), log = Path.Combine(dir, "log.img");
            File.WriteAllBytes(c, [0]);
            // After an import zpool records file vdevs as //./C:/... ; without
            // --json-int, GUIDs are strings.
            string Slashed(string p) => "//./" + p.Replace('\\', '/');
            string status = new JsonObject
            {
                ["pools"] = new JsonObject
                {
                    ["tank"] = new JsonObject
                    {
                        ["vdevs"] = new JsonObject { ["tank"] = new JsonObject { ["vdev_type"] = "root", ["vdevs"] = new JsonObject {
                            ["mirror-0"] = new JsonObject { ["vdev_type"] = "mirror", ["vdevs"] = new JsonObject {
                                [Slashed(a)] = new JsonObject { ["path"] = Slashed(a), ["guid"] = "1111" } } } } } },
                        ["logs"] = new JsonObject { [Slashed(log)] = new JsonObject { ["path"] = Slashed(log), ["guid"] = "2222" } },
                    },
                },
            }.ToJsonString();
            broker.Runner.Respond = (_, args, _) => Task.FromResult(new ToolResult(0, args[0] == "status" ? status : "{}", ""));

            await broker.Client.CallAsync("vdev.replace", new JsonObject { ["pool"] = "tank", ["device"] = a, ["newDevice"] = c, ["confirm"] = true });
            await broker.Client.CallAsync("vdev.remove", new JsonObject { ["pool"] = "tank", ["device"] = log, ["confirm"] = true });
            await broker.Client.CallAsync("vdev.detach", new JsonObject { ["pool"] = "tank", ["device"] = c, ["confirm"] = true });
            var calls = broker.Runner.Calls.Select(x => x.Args).Where(x => x[0] != "status").ToArray();
            Assert.Equal(["replace", "tank", "1111", @"\\?\" + c], calls[0]);
            Assert.Equal(["remove", "tank", "2222"], calls[1]);
            // Not in the pool's status: the path goes to zpool as before.
            Assert.Equal(["detach", "tank", @"\\?\" + c], calls[2]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnsafeRequestsAreRefusedBeforeAnyToolRuns()
    {
        await using var broker = new TestBroker();
        string dir = Directory.CreateTempSubdirectory("zfswin-test").FullName;
        try
        {
            string a = Path.Combine(dir, "a.img");
            File.WriteAllBytes(a, [0]);
            // Not confirmed.
            Assert.Equal(ErrorCode.ConfirmationRequired, (await Fails(broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "cache", ["devices"] = new JsonArray(a) }))).Code);
            // Data vdevs are not added through this operation.
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "data", ["devices"] = new JsonArray(a), ["confirm"] = true }))).Code);
            // A cache cannot be a mirror; a mirror needs two devices.
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "cache", ["mirror"] = true, ["devices"] = new JsonArray(a), ["confirm"] = true }))).Code);
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "log", ["mirror"] = true, ["devices"] = new JsonArray(a), ["confirm"] = true }))).Code);
            // Missing device file.
            Assert.Equal(ErrorCode.NotFound, (await Fails(broker.Client.CallAsync("vdev.add", new JsonObject { ["pool"] = "tank", ["role"] = "log", ["devices"] = new JsonArray(a + ".missing"), ["confirm"] = true }))).Code);
            Assert.Empty(broker.Runner.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CommandLineMapsCacheAndLogCommands()
    {
        var cache = CommandLine.Parse(["vdev", "add-cache", "tank", @"D:\c.img", "--yes"]);
        Assert.Equal("vdev.add", cache.Operation);
        Assert.Equal("cache", cache.Arguments["role"]!.GetValue<string>());
        Assert.True(cache.Destructive);
        var log = CommandLine.Parse(["vdev", "add-log", "tank", "--mirror", @"D:\a.img", @"D:\b.img", "--yes"]);
        Assert.Equal("log", log.Arguments["role"]!.GetValue<string>());
        Assert.True(log.Arguments["mirror"]!.GetValue<bool>());
        Assert.False(log.Arguments.ContainsKey("layout"));
        var remove = CommandLine.Parse(["vdev", "remove", "tank", @"D:\c.img", "--yes"]);
        Assert.Equal("vdev.remove", remove.Operation);
    }

    [Fact]
    public void StatusReportsLogAndCacheDevices()
    {
        var status = JsonNode.Parse("""
            { "pools": { "tank": { "name": "tank", "state": "ONLINE", "error_count": "0",
              "vdevs": { "tank": { "name": "tank", "vdev_type": "root", "state": "ONLINE", "vdevs": {} } },
              "logs": { "\\\\?\\D:\\log.img": { "name": "\\\\?\\D:\\log.img", "vdev_type": "file", "class": "log", "state": "ONLINE" } },
              "l2cache": { "\\\\?\\D:\\c.img": { "name": "\\\\?\\D:\\c.img", "vdev_type": "file", "class": "l2cache", "state": "ONLINE" } } } } }
            """)!;
        JsonNode pool = ZfsJson.PoolStatus(status)["pools"]![0]!;
        Assert.Equal(@"\\?\D:\log.img", Assert.Single(pool["logs"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal(@"\\?\D:\c.img", Assert.Single(pool["caches"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Empty(pool["spares"]!.AsArray());
    }
}

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Zfs.Admin;
using Zfs.Admin.Gui;

namespace Zfs.Admin.Tests;

/// <summary>The web app's files as WebView2 receives them, and the rules they keep.</summary>
public sealed class GuiWebShellTests
{
    [Theory]
    [InlineData("https://zfswin-app.example/index.html", "text/html; charset=utf-8")]
    [InlineData("https://zfswin-app.example/", "text/html; charset=utf-8")]
    [InlineData("https://zfswin-app.example/app.js", "text/javascript; charset=utf-8")]
    [InlineData("https://zfswin-app.example/app.css", "text/css; charset=utf-8")]
    [InlineData("https://zfswin-app.example/theme.css", "text/css; charset=utf-8")]
    public void ServesTheAppsOwnFiles(string uri, string contentType)
    {
        var (content, type) = WebShell.Resolve(uri) ?? throw new Xunit.Sdk.XunitException($"{uri} was not served");
        Assert.Equal(contentType, type);
        Assert.NotEmpty(content);
    }

    [Theory]
    [InlineData("https://example.com/index.html")]
    [InlineData("http://zfswin-app.example/index.html")]
    [InlineData("https://zfswin-app.example:8443/index.html")]
    [InlineData("https://zfswin-app.example/%2e%2e/secret")]
    [InlineData("https://zfswin-app.example/missing.js")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("not a uri")]
    public void ServesNothingElse(string uri) => Assert.Null(WebShell.Resolve(uri));

    [Fact]
    public void ThemeIsTheSharedPalette()
    {
        var (content, _) = WebShell.Resolve(WebShell.Origin + "/theme.css")!.Value;
        Assert.Equal(WebTheme.Tokens(null), Encoding.UTF8.GetString(content));
    }

    [Fact]
    public void OnlyItsOwnOriginCounts()
    {
        Assert.True(WebShell.IsOwnOrigin(WebShell.StartPage));
        Assert.False(WebShell.IsOwnOrigin("https://zfswin-app.example.evil.com/"));
        Assert.False(WebShell.IsOwnOrigin("data:text/html,hi"));
        Assert.False(WebShell.IsOwnOrigin("about:blank"));
    }

    [Fact]
    public void FilesKeepTheContentSecurityPolicy()
    {
        Assert.Contains("default-src 'none'", WebShell.ContentSecurityPolicy);
        Assert.Contains("connect-src 'none'", WebShell.ContentSecurityPolicy);
        Assert.DoesNotContain("unsafe-inline", WebShell.ContentSecurityPolicy);
        Assert.Contains("Content-Security-Policy: " + WebShell.ContentSecurityPolicy, WebShell.Headers("text/html"));
        foreach (string file in new[] { "index.html", "app.js", "app.css" })
        {
            string text = Encoding.UTF8.GetString(WebShell.Resolve($"{WebShell.Origin}/{file}")!.Value.Content);
            // No network and nothing the policy would silently drop: no
            // remote URLs, inline style attributes, or inline event handlers.
            Assert.DoesNotMatch(@"https?://", text);
            Assert.DoesNotContain("style=\"", text);
            Assert.DoesNotMatch(@"\son[a-z]+=", text);
            Assert.DoesNotContain("eval(", text);
        }
        string index = Encoding.UTF8.GetString(WebShell.Resolve(WebShell.StartPage)!.Value.Content);
        Assert.Equal(Regex.Matches(index, "<script").Count, Regex.Matches(index, "<script src=").Count);
    }

    [Fact]
    public void SavedSpaceCountsEachBlockOnce()
    {
        // used and logicalused include children: only top-most datasets count.
        DatasetInfo D(string name) => new(name, 1, 1, null, true, null, null);
        string[] top = DashboardHtml.TopMost([D("tank"), D("tank/a"), D("tank/a/b"), D("other/x"), D("other/x/y")]).Select(d => d.Name).ToArray();
        Assert.Equal(["tank", "other/x"], top);
    }
}

/// <summary>
/// The web app's command table (WebCommands): every command it accepts, the
/// arguments it refuses, and the confirmations it checks before the broker
/// is asked.  The broker is the test broker with a fake zpool/zfs.
/// </summary>
public sealed class GuiWebCommandTests : IAsyncDisposable
{
    const string StatusJson = """
        { "pools": { "tank": { "name": "tank", "state": "ONLINE", "error_count": "0",
          "scan_stats": { "function": "SCRUB", "state": "FINISHED", "errors": "0" },
          "vdevs": { "tank": { "name": "tank", "vdev_type": "root", "state": "ONLINE",
            "vdevs": { "mirror-0": { "name": "mirror-0", "vdev_type": "mirror", "state": "ONLINE",
              "vdevs": { "a": { "name": "a", "vdev_type": "file", "path": "\\\\?\\D:\\a.img", "state": "ONLINE" },
                         "b": { "name": "b", "vdev_type": "file", "path": "\\\\?\\D:\\b.img", "state": "ONLINE" } } } } } },
          "logs": { "l": { "name": "l", "vdev_type": "file", "path": "\\\\?\\D:\\log.img", "state": "ONLINE" } },
          "l2cache": { "c": { "name": "c", "vdev_type": "file", "path": "\\\\?\\D:\\c.img", "state": "ONLINE" } } } } }
        """;

    const string SnapshotsJson = """
        { "datasets": {
          "tank/a@old": { "properties": { "used": { "value": 1048576 }, "referenced": { "value": 2097152 }, "creation": { "value": 1700000000 } } },
          "tank/a@new": { "properties": { "used": { "value": 4096 }, "referenced": { "value": 2097152 }, "creation": { "value": 1800000000 } } } } }
        """;

    readonly TestBroker broker = new();
    readonly FakeHost host = new();
    readonly WebCommands commands;
    readonly string temp = Directory.CreateTempSubdirectory("zfswin-web").FullName;

    public GuiWebCommandTests()
    {
        broker.Runner.Respond = (tool, args, _) => Task.FromResult(new ToolResult(0, (tool, args[0]) switch
        {
            (ZfsTool.Zpool, "status") => StatusJson,
            (ZfsTool.Zpool, "list") => """{ "pools": { "tank": { "name": "tank", "properties": { "health": { "value": "ONLINE" } } } } }""",
            (ZfsTool.Zfs, "list") when args.Contains("-H") => "tank/a@old\t10\ntank/a@new\t20\n",
            (ZfsTool.Zfs, "list") when args.Contains("snapshot") => SnapshotsJson,
            (ZfsTool.Zfs, "list") => """{ "datasets": { "tank": { "properties": {} }, "tank/a": { "properties": {} } } }""",
            (ZfsTool.Zfs, "mount") => "",
            _ => "{}",
        }, ""));
        commands = new WebCommands(new AdminClient(broker.Client), host);
    }

    public async ValueTask DisposeAsync()
    {
        await broker.DisposeAsync();
        Directory.Delete(temp, recursive: true);
        Registry.CurrentUser.DeleteSubKeyTree(host.MountsKey, throwOnMissingSubKey: false);
    }

    sealed class FakeHost : IWebHost
    {
        public string MountsKey { get; } = @"Software\ZfsWinTests\" + Guid.NewGuid().ToString("N");
        public List<string> Opened { get; } = [];
        public List<string> Revealed { get; } = [];
        public List<string> Service { get; } = [];
        public string? Clipboard { get; private set; }
        public DevicePick? NextPick { get; set; }
        public int Counts { get; private set; }

        public string Edition => "Kernel edition";
        public Task<DashboardData> DashboardAsync() => Task.FromResult(new DashboardData(Edition, DateTimeOffset.UtcNow, [], null));
        public (bool Running, string Text) CountState => (Counts > 0, "Counting tank/a (1 of 1)…");
        public void StartCount() => Counts++;
        public void CancelCount() => Counts = 0;
        public Task<IReadOnlyDictionary<int, DriveHealth>> DriveHealthAsync() => Task.FromResult<IReadOnlyDictionary<int, DriveHealth>>(new Dictionary<int, DriveHealth>());
        public DevicePick? PickDevices(string title, string explanation, int maxDevices, bool offerMirror, IReadOnlyList<string> inUse) => NextPick;
        public string? PickFolder(string description) => @"D:\picked";
        public void OpenFolder(string path) => Opened.Add(path);
        public void RevealFile(string path) => Revealed.Add(path);
        public void SetClipboard(string text) => Clipboard = text;
        public Task<string?> SaveReportAsync() => Task.FromResult<string?>(@"D:\report.html");
        public IReadOnlyList<string> FreeDriveLetters() => ["Z:", "Y:"];
        public ISet<string> MountedHere { get; } = new HashSet<string>();
        public bool KeepMounted { get; set; }
        public string ServiceState() => "Running";
        public Task ControlServiceAsync(string action) { Service.Add(action); return Task.CompletedTask; }
        public UserModeMounts Mounts => new(Registry.CurrentUser, MountsKey);
    }

    async Task<JsonObject> Send(string cmd, JsonObject? args = null) =>
        await commands.HandleAsync(new JsonObject { ["id"] = "t1", ["cmd"] = cmd, ["args"] = args?.DeepClone() ?? new JsonObject() }.ToJsonString());

    static JsonNode? Ok(JsonObject reply)
    {
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal("t1", reply["id"]!.GetValue<string>());
        return reply["data"];
    }

    static string Refused(JsonObject reply)
    {
        Assert.False(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["error"]!["code"]!.GetValue<string>();
    }

    string[][] Changes() => broker.Runner.Calls.Select(c => c.Args)
        .Where(a => a[0] is not ("status" or "list" or "mount" or "get" or "iostat" or "version")).ToArray();

    [Fact]
    public void TheTableIsExactlyTheseCommands()
    {
        // A new command needs a test here before it is added.
        string[] expected =
        [
            "app.state", "overview.get", "datasets.get", "dataset.properties", "snapshots.get", "devices.get", "health.get", "rates.sample", "service.get",
            "pool.scrub", "pool.clear", "dataset.set", "snapshot.create", "snapshot.mount", "snapshot.unmount", "snapshot.open", "snapshot.rollback",
            "snapshot.destroy", "snapshots.keepMounted", "devices.pick", "device.attach", "device.detach", "device.add", "device.remove",
            "file.reveal", "files.copy", "count.start", "count.cancel", "report.save", "service.control", "mount.save", "mount.remove", "pick.folder",
        ];
        Assert.Equal(expected.Order(), commands.Names.Order());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"cmd":"app.state"}""")]
    [InlineData("""{"id":"x","cmd":"zpool.destroy"}""")]
    [InlineData("""{"id":"x","cmd":"pool.scrub","args":["tank"]}""")]
    [InlineData("""{"id":5,"cmd":"app.state"}""")]
    public async Task MalformedMessagesAreRefused(string message)
    {
        JsonObject reply = await commands.HandleAsync(message);
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Equal("bad-request", reply["error"]!["code"]!.GetValue<string>());
        Assert.Empty(broker.Runner.Calls);
    }

    [Fact]
    public async Task ReadsAnswerWithData()
    {
        JsonNode state = Ok(await Send("app.state"))!;
        Assert.True(state["brokerAvailable"]!.GetValue<bool>());
        Assert.Equal("tank", state["pools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(["tank", "tank/a"], state["datasets"]!.AsArray().Select(d => d!.GetValue<string>()));

        Assert.NotNull(Ok(await Send("overview.get"))!["generatedAt"]);
        Assert.Equal(2, Ok(await Send("datasets.get", new() { ["pool"] = "tank" }))!.AsArray().Count);
        Ok(await Send("dataset.properties", new() { ["dataset"] = "tank/a" }));

        JsonNode snapshots = Ok(await Send("snapshots.get", new() { ["dataset"] = "tank/a" }))!;
        Assert.Equal("tank/a@new", snapshots["snapshots"]![0]!["name"]!.GetValue<string>());	// newest first
        Assert.Equal(["Z:", "Y:"], snapshots["freeLetters"]!.AsArray().Select(l => l!.GetValue<string>()));

        JsonNode devices = Ok(await Send("devices.get", new() { ["pool"] = "tank" }))!;
        Assert.Equal(4, devices["leaves"]!.AsArray().Count);
        Assert.Equal("2-way mirror", devices["redundancy"]!["layout"]!.GetValue<string>());

        Assert.NotNull(Ok(await Send("health.get", new() { ["pool"] = "tank" }))!["errors"]);
        Assert.Equal("Running", Ok(await Send("service.get"))!["state"]!.GetValue<string>());
        Assert.Equal(@"D:\picked", Ok(await Send("pick.folder"))!.GetValue<string>());
        Assert.Equal(@"D:\report.html", Ok(await Send("report.save"))!.GetValue<string>());
        Ok(await Send("count.start"));
        Assert.Equal(1, host.Counts);
        Ok(await Send("count.cancel"));
        Assert.Equal(0, host.Counts);
        Assert.Empty(Changes());
    }

    [Fact]
    public async Task RatesNeedTwoSamples()
    {
        JsonObject first = await Send("rates.sample", new() { ["pool"] = "tank" });
        if (first["ok"]!.GetValue<bool>())
            Assert.True(first["data"]!["pending"]!.GetValue<bool>());
        Assert.Equal("invalid-argument", Refused(await Send("rates.sample", new() { ["pool"] = "-tank" })));
    }

    [Fact]
    public async Task WithoutTheBrokerTheStateSaysSo()
    {
        var offline = new WebCommands(new AdminClient(new BrokerClient("zfswin-missing-" + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(200) }), host);
        JsonObject reply = await offline.HandleAsync("""{"id":"t1","cmd":"app.state"}""");
        JsonNode state = Ok(reply)!;
        Assert.False(state["brokerAvailable"]!.GetValue<bool>());
        Assert.Empty(state["pools"]!.AsArray());
    }

    [Fact]
    public async Task ScrubAndClear()
    {
        Assert.Equal("invalid-argument", Refused(await Send("pool.scrub", new() { ["pool"] = "tank", ["action"] = "resilver" })));
        Ok(await Send("pool.scrub", new() { ["pool"] = "tank", ["action"] = "start" }));
        Assert.Equal("confirmation-required", Refused(await Send("pool.clear", new() { ["pool"] = "tank" })));
        Assert.Equal("confirmation-required", Refused(await Send("pool.clear", new() { ["pool"] = "tank", ["confirm"] = "TANK" })));
        Ok(await Send("pool.clear", new() { ["pool"] = "tank", ["confirm"] = "tank" }));
        string[][] changes = Changes();
        Assert.Equal(2, changes.Length);
        Assert.Equal("scrub", changes[0][0]);
        Assert.Equal(["clear", "tank"], changes[1]);
    }

    [Fact]
    public async Task OnlyTheFeaturePropertiesAndTheirValuesCanBeSet()
    {
        Assert.Equal("invalid-argument", Refused(await Send("dataset.set", new() { ["dataset"] = "tank/a", ["property"] = "mountpoint", ["value"] = "/x" })));
        Assert.Equal("invalid-argument", Refused(await Send("dataset.set", new() { ["dataset"] = "tank/a", ["property"] = "compression", ["value"] = "best" })));
        Ok(await Send("dataset.set", new() { ["dataset"] = "tank/a", ["property"] = "compression", ["value"] = "zstd" }));
        Assert.Equal(["set", "compression=zstd", "tank/a"], Assert.Single(Changes()));
    }

    [Fact]
    public async Task SnapshotChangesCheckTheTypedName()
    {
        Assert.Equal("invalid-argument", Refused(await Send("snapshot.create", new() { ["dataset"] = "tank/a", ["name"] = "two@signs" })));
        Ok(await Send("snapshot.create", new() { ["dataset"] = "tank/a", ["name"] = "gui-1", ["recursive"] = true }));

        Assert.Equal("confirmation-required", Refused(await Send("snapshot.destroy", new() { ["snapshot"] = "tank/a@old", ["confirm"] = "new" })));
        Assert.Equal("not-found", Refused(await Send("snapshot.destroy", new() { ["snapshot"] = "tank/a@gone", ["confirm"] = "gone" })));
        Ok(await Send("snapshot.destroy", new() { ["snapshot"] = "tank/a@old", ["confirm"] = "old" }));

        Assert.Equal("confirmation-required", Refused(await Send("snapshot.rollback", new() { ["snapshot"] = "tank/a@old" })));
        Ok(await Send("snapshot.rollback", new() { ["snapshot"] = "tank/a@old", ["confirm"] = "old" }));

        string[][] changes = Changes();
        Assert.Equal(["snapshot", "-r", "tank/a@gui-1"], changes[0]);
        Assert.Equal(["destroy", "tank/a@old"], changes[1]);
        Assert.Equal("rollback", changes[2][0]);
        Assert.Contains("-r", changes[2]);	// @new is newer and goes too
        Assert.Equal("tank/a@old", changes[2][^1]);
    }

    [Fact]
    public async Task TemporaryMountsAreChecked()
    {
        Assert.Equal("invalid-argument", Refused(await Send("snapshot.mount", new() { ["snapshot"] = "tank/a@old", ["mountPoint"] = @"relative\folder" })));
        Assert.Equal("invalid-argument", Refused(await Send("snapshot.open", new() { ["snapshot"] = "tank/a@old" })));
        Assert.Empty(host.Opened);
        JsonObject unmount = await Send("snapshot.unmount", new() { ["snapshot"] = "tank/a@old" });
        Assert.Equal("t1", unmount["id"]!.GetValue<string>());
        Ok(await Send("snapshots.keepMounted", new() { ["value"] = true }));
        Assert.True(host.KeepMounted);
        Assert.Equal("invalid-argument", Refused(await Send("snapshots.keepMounted", new() { ["value"] = "yes" })));
    }

    [Fact]
    public async Task DeviceChangesNeedTheDevicePickerAndTheRightDevice()
    {
        string disk = Path.Combine(temp, "new.img");
        File.WriteAllBytes(disk, [0]);
        const string a = @"\\?\D:\a.img";

        // Attach: a data disk of the pool, a device from this window's picker, "attach" typed.
        Assert.Equal("invalid-argument", Refused(await Send("device.attach", new() { ["pool"] = "tank", ["device"] = a, ["confirm"] = "attach" })));
        host.NextPick = new DevicePick([@"\\?\" + disk], disk, false);
        string token = Ok(await Send("devices.pick", new() { ["pool"] = "tank", ["purpose"] = "mirror", ["device"] = a }))!["token"]!.GetValue<string>();
        Assert.Equal("invalid-argument", Refused(await Send("device.attach", new() { ["pool"] = "tank", ["device"] = @"\\?\D:\log.img", ["pick"] = token, ["confirm"] = "attach" })));
        Assert.Equal("confirmation-required", Refused(await Send("device.attach", new() { ["pool"] = "tank", ["device"] = a, ["pick"] = token })));
        Ok(await Send("device.attach", new() { ["pool"] = "tank", ["device"] = a, ["pick"] = token, ["confirm"] = "attach" }));
        // A pick is used once.
        Assert.Equal("invalid-argument", Refused(await Send("device.attach", new() { ["pool"] = "tank", ["device"] = a, ["pick"] = token, ["confirm"] = "attach" })));

        // Detach: only a member of a mirror.
        Assert.Equal("invalid-argument", Refused(await Send("device.detach", new() { ["pool"] = "tank", ["device"] = @"\\?\D:\log.img", ["confirm"] = "detach" })));
        Assert.Equal("confirmation-required", Refused(await Send("device.detach", new() { ["pool"] = "tank", ["device"] = a, ["confirm"] = "yes" })));
        Ok(await Send("device.detach", new() { ["pool"] = "tank", ["device"] = a, ["confirm"] = "detach" }));

        // Remove: only a read cache or write log.
        Assert.Equal("invalid-argument", Refused(await Send("device.remove", new() { ["pool"] = "tank", ["device"] = a, ["confirm"] = "remove" })));
        Ok(await Send("device.remove", new() { ["pool"] = "tank", ["device"] = @"\\?\D:\c.img", ["confirm"] = "remove" }));

        // Add: a cancelled picker gives nothing to add.
        host.NextPick = null;
        Assert.Null(Ok(await Send("devices.pick", new() { ["pool"] = "tank", ["purpose"] = "cache" })));
        Assert.Equal("invalid-argument", Refused(await Send("device.add", new() { ["pool"] = "tank", ["role"] = "cache", ["confirm"] = "add" })));
        host.NextPick = new DevicePick([@"\\?\" + disk], disk, false);
        token = Ok(await Send("devices.pick", new() { ["pool"] = "tank", ["purpose"] = "cache" }))!["token"]!.GetValue<string>();
        Assert.Equal("invalid-argument", Refused(await Send("device.add", new() { ["pool"] = "tank", ["role"] = "special", ["pick"] = token, ["confirm"] = "add" })));
        Ok(await Send("device.add", new() { ["pool"] = "tank", ["role"] = "cache", ["pick"] = token, ["confirm"] = "add" }));

        string[][] changes = Changes();
        Assert.Equal(4, changes.Length);
        Assert.Equal("attach", changes[0][0]);
        Assert.Equal(["detach", "tank", a], changes[1]);
        Assert.Equal("remove", changes[2][0]);
        Assert.Equal("add", changes[3][0]);
    }

    [Fact]
    public async Task DamagedFilesComeFromTheLastList()
    {
        Assert.Equal("invalid-argument", Refused(await Send("file.reveal", new() { ["pool"] = "tank", ["index"] = 0 })));
        Ok(await Send("health.get", new() { ["pool"] = "tank" }));
        Assert.Equal("invalid-argument", Refused(await Send("file.reveal", new() { ["pool"] = "tank", ["index"] = 0 })));
        Ok(await Send("files.copy", new() { ["pool"] = "tank" }));
        Assert.Null(host.Clipboard);	// nothing listed, nothing copied
        Assert.Empty(host.Revealed);
    }

    [Fact]
    public async Task ServiceAndMounts()
    {
        Assert.Equal("confirmation-required", Refused(await Send("service.control", new() { ["action"] = "stop" })));
        Assert.Equal("invalid-argument", Refused(await Send("service.control", new() { ["action"] = "delete" })));
        Ok(await Send("service.control", new() { ["action"] = "start" }));
        Ok(await Send("service.control", new() { ["action"] = "restart", ["confirm"] = "restart" }));
        Assert.Equal(["start", "restart"], host.Service);

        var mount = new JsonObject { ["name"] = "docs", ["source"] = @"D:\pools", ["pool"] = "tank", ["dataset"] = "", ["mountPoint"] = "Z:", ["readOnly"] = true };
        Assert.True(Ok(await Send("mount.save", mount))!["restartSuggested"]!.GetValue<bool>());
        UserModeMount saved = Assert.Single(host.Mounts.List());
        Assert.Equal("tank", saved.Dataset);	// empty: the pool's root
        Assert.True(saved.ReadOnly);
        Assert.Equal("invalid-argument", Refused(await Send("mount.save", mount)));
        mount["replace"] = true;
        mount["mountPoint"] = "relative";
        Assert.Equal("invalid-argument", Refused(await Send("mount.save", mount)));

        Assert.Equal("confirmation-required", Refused(await Send("mount.remove", new() { ["name"] = "docs", ["confirm"] = "doc" })));
        Assert.Equal("not-found", Refused(await Send("mount.remove", new() { ["name"] = "other", ["confirm"] = "other" })));
        Ok(await Send("mount.remove", new() { ["name"] = "docs", ["confirm"] = "docs" }));
        Assert.Empty(host.Mounts.List());
    }
}

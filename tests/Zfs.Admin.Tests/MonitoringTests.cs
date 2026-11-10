using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zfs.Admin;

namespace Zfs.Admin.Tests;

/// <summary>
/// Step 20 (issue #17): damaged files, I/O rates, ARC statistics, copies,
/// the Overview HTML and the snapshot operations, without a pool.
/// </summary>
public class MonitoringTests
{
    // zpool status -j --json-int -p -v, shaped as OpenZFS 2.4 prints it
    // (errors_nvlist: "errlist" is a string array when the log was read).
    const string StatusWithErrors = """
        { "output_version": { "command": "zpool status", "vers_major": 0, "vers_minor": 1 },
          "pools": { "tank": { "name": "tank", "state": "ONLINE", "error_count": 5,
            "scan_stats": { "function": "SCRUB", "state": "FINISHED", "errors": 3 },
            "vdevs": { "tank": { "name": "tank", "vdev_type": "root", "state": "ONLINE",
              "vdevs": { "D:\\p\\a.img": { "name": "D:\\p\\a.img", "vdev_type": "file", "path": "\\\\?\\D:\\p\\a.img", "state": "ONLINE",
                "read_errors": 0, "write_errors": 0, "checksum_errors": 7 } } } },
            "errlist": [ "E:\\/data/victim.bin", "tank/data@before:/old/report.docx", "<metadata>:<0x1b>", "tank/data:<0x8>", "<0x15>:<0x9>" ] } } }
        """;

    [Fact]
    public void PoolErrorsListsFilesObjectsAndCounters()
    {
        JsonNode r = ZfsJson.PoolErrors(JsonNode.Parse(StatusWithErrors)!, "tank");
        Assert.Equal(5, r["errorCount"]!.GetValue<long>());
        var files = r["files"]!.AsArray();
        Assert.Equal(5, files.Count);

        Assert.Equal("file", files[0]!["kind"]!.GetValue<string>());
        Assert.Equal(@"E:\data\victim.bin", files[0]!["path"]!.GetValue<string>());
        Assert.Null(files[0]!["dataset"]);

        Assert.Equal("tank/data", files[1]!["dataset"]!.GetValue<string>());
        Assert.Equal("before", files[1]!["snapshot"]!.GetValue<string>());
        Assert.Equal("/old/report.docx", files[1]!["path"]!.GetValue<string>());

        Assert.Equal("metadata", files[2]!["kind"]!.GetValue<string>());
        Assert.Equal("0x1b", files[2]!["object"]!.GetValue<string>());

        Assert.Equal("object", files[3]!["kind"]!.GetValue<string>());
        Assert.Equal("tank/data", files[3]!["dataset"]!.GetValue<string>());
        Assert.Equal("0x8", files[3]!["object"]!.GetValue<string>());

        Assert.Equal("object", files[4]!["kind"]!.GetValue<string>());
        Assert.Null(files[4]!["dataset"]);

        JsonNode device = Assert.Single(r["devices"]!.AsArray())!;
        Assert.Equal(7, device["checksumErrors"]!.GetValue<long>());
        Assert.Equal("FINISHED", r["scan"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void PoolErrorsWithoutErrorsIsAnEmptyList()
    {
        string clean = """{ "pools": { "tank": { "name": "tank", "state": "ONLINE", "error_count": 0, "vdevs": {} } } }""";
        JsonNode r = ZfsJson.PoolErrors(JsonNode.Parse(clean)!, "tank");
        Assert.Empty(r["files"]!.AsArray());
        Assert.Equal(0, r["errorCount"]!.GetValue<long>());
        // zpool could not read the log: strerror() instead of the list.
        string failed = """{ "pools": { "tank": { "name": "tank", "error_count": 2, "errlist": "Permission denied" } } }""";
        Assert.Equal("Permission denied", ZfsJson.PoolErrors(JsonNode.Parse(failed)!, "tank")["logError"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("<metadata>:<0x0>", "metadata", null, null, null)]
    [InlineData("tank:/a b/c.txt", "file", "tank", null, "/a b/c.txt")]
    [InlineData("tank/x/y@daily 1:/f", "file", "tank/x/y", "daily 1", "/f")]
    [InlineData(@"D:\docs\f.txt", "file", null, null, @"D:\docs\f.txt")]
    [InlineData("D:/docs//f.txt", "file", null, null, @"D:\docs\f.txt")]
    public void ErrorEntriesAreClassified(string text, string kind, string? dataset, string? snapshot, string? path)
    {
        JsonObject e = ZfsJson.ErrorEntry(text);
        Assert.Equal(kind, e["kind"]!.GetValue<string>());
        Assert.Equal(dataset, e["dataset"]?.GetValue<string>());
        Assert.Equal(snapshot, e["snapshot"]?.GetValue<string>());
        Assert.Equal(path, e["path"]?.GetValue<string>());
    }

    const string VdevGet = """
        { "output_version": { "command": "zpool get" },
          "vdevs": {
            "tank": { "name": "tank", "type": "VDEV", "vdev_type": "root", "properties": {
              "read_ops": { "value": 1000 }, "write_ops": { "value": 2000 }, "read_bytes": { "value": 4096000 }, "write_bytes": { "value": 8192000 } } },
            "mirror-0": { "name": "mirror-0", "type": "VDEV", "vdev_type": "mirror", "properties": {
              "read_ops": { "value": 1000 }, "write_ops": { "value": 2000 }, "read_bytes": { "value": 4096000 }, "write_bytes": { "value": 8192000 } } },
            "a": { "name": "a", "type": "VDEV", "vdev_type": "file", "path": "\\\\?\\D:\\a.img", "properties": {
              "read_ops": { "value": 600 }, "write_ops": { "value": 2000 }, "read_bytes": { "value": "2457600" }, "write_bytes": { "value": 8192000 } } } } }
        """;

    [Fact]
    public void IostatTotalsAndRates()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        JsonNode first = ZfsJson.VdevIostat(JsonNode.Parse(VdevGet)!, "tank", t0);
        Assert.Equal(1000, first["total"]!["readOps"]!.GetValue<long>());
        Assert.Equal(2, first["vdevs"]!.AsArray().Count);
        Assert.Equal(2457600, first["vdevs"]![1]!["readBytes"]!.GetValue<long>());	// numeric strings too

        IostatSample before = IostatSample.From(first);
        IostatSample after = before with
        {
            SampledAt = t0.AddSeconds(5),
            Total = new IoCounters(1500, 2100, 4096000 + 5 * 1048576, 8192000),
        };
        IoRates rates = after.TotalRatesSince(before);
        Assert.Equal(100, rates.ReadIops, 3);
        Assert.Equal(20, rates.WriteIops, 3);
        Assert.Equal(1048576, rates.ReadBytesPerSecond, 3);
        Assert.Equal(0, rates.WriteBytesPerSecond, 3);
        Assert.Equal(IoRates.Zero, before.VdevRatesSince(before, @"\\?\D:\a.img"));
    }

    [Fact]
    public void IostatCounterResetIsZeroNotNegative()
    {
        var before = new IoCounters(5000, 5000, 1 << 30, 1 << 30);
        var afterReimport = new IoCounters(10, 20, 4096, 8192);
        Assert.Equal(IoRates.Zero, IostatSample.Rates(before, afterReimport, TimeSpan.FromSeconds(5)));
        Assert.Equal(IoRates.Zero, IostatSample.Rates(before, before, TimeSpan.Zero));
    }

    [Fact]
    public void ArcStatsFromKstatText()
    {
        string text = "zfs:0:arcstats:class\tmisc\nzfs:0:arcstats:size\t1073741824\r\nzfs:0:arcstats:c\t2147483648\n" +
            "zfs:0:arcstats:c_max\t4294967296\nzfs:0:arcstats:hits\t900\nzfs:0:arcstats:misses\t100\n" +
            "zfs:0:arcstats:l2_size\t0\nzfs:0:arcstats:mru_size\t536870912\nzfs:0:arcstats:mfu_size\t268435456\n";
        JsonObject arc = ZfsJson.ArcStats(ZfsJson.KstatValues(text));
        Assert.Equal(1073741824, arc["size"]!.GetValue<long>());
        Assert.Equal(2147483648, arc["target"]!.GetValue<long>());
        Assert.Equal(4294967296, arc["max"]!.GetValue<long>());
        Assert.Equal(0.9, arc["hitRatio"]!.GetValue<double>(), 4);
        Assert.Null(arc["l2HitRatio"]);
    }

    static VdevInfo Leaf(string name, string state = "ONLINE") => new(name, "file", state, name, 0, 0, 0, []);

    static PoolStatusInfo Pool(params VdevInfo[] top) =>
        new("tank", "ONLINE", null, null, 0, null, [new VdevInfo("tank", "root", "ONLINE", null, 0, 0, 0, top)], [], [], [], [], []);

    [Fact]
    public void CopiesOfTheData()
    {
        RedundancyInfo single = Redundancy.Copies(Pool(Leaf("a")), 1);
        Assert.Equal(1, single.HealthyCopies);
        Assert.Equal(0, single.DiskFailuresTolerated);
        Assert.True(single.Warning);
        Assert.Contains("one bad block loses a file", single.Text);

        RedundancyInfo ditto = Redundancy.Copies(Pool(Leaf("a")), 2);
        Assert.Equal(2, ditto.HealthyCopies);
        Assert.Equal(0, ditto.DiskFailuresTolerated);

        var mirror = new VdevInfo("mirror-0", "mirror", "ONLINE", null, 0, 0, 0, [Leaf("a"), Leaf("b")]);
        RedundancyInfo m = Redundancy.Copies(Pool(mirror), 1);
        Assert.Equal("2-way mirror × copies=1 = 2 copies; 1 disk can fail without data loss.", m.Text);
        Assert.False(m.Warning);

        var degraded = mirror with { State = "DEGRADED", Children = [Leaf("a"), Leaf("b", "FAULTED")] };
        Assert.Equal(1, Redundancy.Copies(Pool(degraded), 1).HealthyCopies);

        var raidz2 = new VdevInfo("raidz2-0", "raidz", "ONLINE", null, 0, 0, 0, [Leaf("a"), Leaf("b"), Leaf("c"), Leaf("d")]);
        RedundancyInfo z = Redundancy.Copies(Pool(raidz2), 1);
        Assert.Equal(2, z.DiskFailuresTolerated);
        Assert.Equal(3, z.HealthyCopies);

        // The weakest top-level vdev decides.
        Assert.Equal(0, Redundancy.Copies(Pool(mirror, Leaf("c")), 1).DiskFailuresTolerated);
    }

    [Fact]
    public void DashboardIsSelfContainedAndCarriesEveryValue()
    {
        var pool = new PoolInfo("zwin", "DEGRADED", 1000L << 30, 600L << 30, 400L << 30, 60, 7, "1.06x");
        PoolStatusInfo status = Pool(new VdevInfo("mirror-0", "mirror", "DEGRADED", null, 0, 0, 0, [Leaf("a"), Leaf("b", "FAULTED")]));
        var datasets = new List<DatasetInfo>
        {
            new("zwin", 600L << 30, 400L << 30, "/zwin", true, "D:", "zstd", 700L << 30, "1.41", 1, "fletcher4"),
            new("zwin/<docs>", 10L << 30, 400L << 30, "/zwin/docs", true, null, "lz4", 12L << 30, "1.20", 2, "sha256"),
        };
        var counts = new Dictionary<string, DatasetCountInfo>
        {
            ["zwin"] = new("zwin", @"D:\", true, 2_000_000, 1_284_907, 96_412, 1L << 40, 0, true, DateTimeOffset.UtcNow.AddMinutes(-12)),
        };
        var memory = new MemoryInfo("user-mode", new ArcInfo(1L << 30, 2L << 30, 1L << 28, 4L << 30, 1L << 29, 1L << 28, 900, 100, 0.9, 0, 0, 0, null),
            [("zwin", 64L << 20)], 1500L << 20);
        var data = new DashboardData("User-mode edition", DateTimeOffset.UtcNow,
            [new DashboardPool(pool, status, 3, 13L << 30, datasets, counts, Redundancy.Copies(status, 1))], memory, Dark: true, RefreshSeconds: 5);

        string html = DashboardHtml.Render(data);

        // Nothing fetched: no URL, no script, no external reference.
        Assert.DoesNotContain("http:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"5\">", html);
        // Well-formed: every element opened is closed.
        foreach (string tag in new[] { "section", "div", "table", "tr", "td", "th", "svg", "text", "span", "h3", "p", "b" })
            Assert.Equal(Regex.Matches(html, $"<{tag}[ >]").Count, Regex.Matches(html, $"</{tag}>").Count);
        // Names are escaped.
        Assert.Contains("zwin/&lt;docs&gt;", html);
        Assert.DoesNotContain("<docs>", html);
        // The values.
        foreach (string value in new[] { "DEGRADED", "1,284,907", "96,412", "2,000,000 objects", "12 min ago", "60%", "7%",
                     "100 GiB", "2.0 GiB", "13 GiB", "1.0 GiB", "64 MiB", "1.5 GiB private", "90%", "one bad block loses a file", "copies=1 = 1 copy" })
            Assert.Contains(value, html);
        Assert.Contains(">3</div>", html);	// damaged files
    }

    [Fact]
    public void DashboardWithoutPools()
    {
        string html = DashboardHtml.Render(new DashboardData("Kernel edition", DateTimeOffset.UtcNow, [], null));
        Assert.Contains("No pool is imported.", html);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html);
    }

    [Fact]
    public void FileCounterSkipsControlDirectoryAndReparsePoints()
    {
        string root = Directory.CreateTempSubdirectory("zfswin-count").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            Directory.CreateDirectory(Path.Combine(root, ".zfs", "snapshot", "s1"));
            File.WriteAllBytes(Path.Combine(root, "a", "1.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root, "a", "b", "2.bin"), new byte[50]);
            File.WriteAllBytes(Path.Combine(root, ".zfs", "snapshot", "s1", "3.bin"), new byte[7]);
            File.WriteAllBytes(Path.Combine(root, "top.bin"), []);
            bool linked;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "a"));
                linked = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                linked = false;	// symbolic links need the privilege or developer mode
            }
            FileCount c = FileCounter.Count(root, CancellationToken.None);
            Assert.Equal(3, c.Files);
            Assert.Equal(2, c.Folders);
            Assert.Equal(150, c.Bytes);
            Assert.Equal(linked ? 1 : 0, c.Skipped);
            Assert.True(c.Complete);
            Assert.Throws<OperationCanceledException>(() => FileCounter.Count(root, new CancellationToken(true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BridgeKstatRequestMatchesTheHeader()
    {
        // zfs_bridge_hdr_t: magic, op, request, error, len[5], cap[5].
        byte[] header = UserModeBridge.KstatRequest("zfs:0:arcstats", out byte[] name);
        Assert.Equal(104, header.Length);
        Assert.Equal(0x315a4252u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal((ulong)name.Length, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24 + 8)));		// len[SRC]
        Assert.Equal(1UL << 20, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24 + 40 + 32)));	// cap[DST]
    }

    [Theory]
    [InlineData("tank/data@s1", "tank/.zfswin-tmp/data-s1")]
    [InlineData("tank/a/b@daily", "tank/.zfswin-tmp/a_b-daily")]
    [InlineData("tank@s1", "tank/.zfswin-tmp/tank-s1")]
    public void TemporaryCloneNames(string snapshot, string clone) =>
        Assert.Equal(clone, Operations.TemporaryCloneName(snapshot));

    // --- broker operations --------------------------------------------------

    static Task<ToolResult> Ok(string stdout = "") => Task.FromResult(new ToolResult(0, stdout, ""));

    [Fact]
    public async Task RollbackNeedsConsentForNewerSnapshotsAndRemounts()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => a switch
        {
            ["list", ..] => Ok("tank/data@a\t10\ntank/data@b\t20\ntank/data@c\t30\n"),
            ["get", "-H", "-o", "value", "mounted", _] => Ok("yes\n"),
            ["get", "-H", "-o", "value", "clones", _] => Ok("-\n"),
            _ => Ok(),
        };
        var args = new JsonObject { ["snapshot"] = "tank/data@a", ["confirm"] = true };
        var refused = await Assert.ThrowsAsync<AdminException>(() => broker.Client.CallAsync("snapshot.rollback", args));
        Assert.Equal(ErrorCode.ConfirmationRequired, refused.Code);
        Assert.Contains("tank/data@b, tank/data@c", refused.Message);
        Assert.DoesNotContain(broker.Runner.Calls, c => c.Args[0] == "rollback");

        // Without the general confirmation the broker refuses before anything runs.
        int before = broker.Runner.Calls.Count;
        Assert.Equal(ErrorCode.ConfirmationRequired, (await Assert.ThrowsAsync<AdminException>(() =>
            broker.Client.CallAsync("snapshot.rollback", new JsonObject { ["snapshot"] = "tank/data@a", ["destroyNewer"] = true }))).Code);
        Assert.Equal(before, broker.Runner.Calls.Count);

        JsonNode? r = await broker.Client.CallAsync("snapshot.rollback", new JsonObject { ["snapshot"] = "tank/data@a", ["destroyNewer"] = true, ["confirm"] = true });
        Assert.Equal(["tank/data@b", "tank/data@c"], r!["destroyed"]!.AsArray().Select(n => n!.GetValue<string>()));
        var sequence = broker.Runner.Calls.Skip(before).Select(c => c.Args).Where(x => x[0] is "unmount" or "rollback" or "mount").ToList();
        Assert.Equal(["unmount", "tank/data"], sequence[0]);
        Assert.Equal(["rollback", "-r", "tank/data@a"], sequence[1]);
        Assert.Equal(["mount", "tank/data"], sequence[2]);
    }

    [Fact]
    public async Task RollbackToTheLatestSnapshotNeedsNoConsent()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => a switch
        {
            ["list", ..] => Ok("tank/data@a\t10\n"),
            ["get", "-H", "-o", "value", "mounted", _] => Ok("no\n"),
            _ => Ok(),
        };
        await broker.Client.CallAsync("snapshot.rollback", new JsonObject { ["snapshot"] = "tank/data@a", ["confirm"] = true });
        Assert.Contains(broker.Runner.Calls, c => c.Args.SequenceEqual(["rollback", "tank/data@a"]));
        Assert.DoesNotContain(broker.Runner.Calls, c => c.Args[0] is "unmount" or "mount");
    }

    [Fact]
    public async Task TemporaryMountIsAReadOnlyMarkedClone()
    {
        await using var broker = new TestBroker();
        bool parentExists = false;
        broker.Runner.Respond = (_, a, _) => a switch
        {
            ["list", "-H", "-o", "name", "tank/.zfswin-tmp"] => Task.FromResult(new ToolResult(parentExists ? 0 : 1, "", "dataset does not exist")),
            ["list", "-H", "-o", "name", _] => Task.FromResult(new ToolResult(1, "", "dataset does not exist")),
            ["mount"] => Ok("tank/data   D:/data/\ntank/.zfswin-tmp/data-s1   Y:/\n"),
            _ => Ok(),
        };
        JsonNode? r = await broker.Client.CallAsync("snapshot.mount", new JsonObject { ["snapshot"] = "tank/data@s1", ["mountPoint"] = "y:" });
        Assert.Equal("tank/.zfswin-tmp/data-s1", r!["clone"]!.GetValue<string>());
        Assert.Equal(@"Y:\", r["mountPoint"]!.GetValue<string>());
        var calls = broker.Runner.Calls.Select(c => c.Args).ToList();
        Assert.Contains(calls, x => x.SequenceEqual(["create", "-o", "canmount=off", "-o", "driveletter=off", "-o", "org.openzfs.windows:temporary=on", "tank/.zfswin-tmp"]));
        Assert.Contains(calls, x => x.SequenceEqual(["clone", "-o", "readonly=on", "-o", "canmount=noauto", "-o", "org.openzfs.windows:temporary=on",
            "-o", "driveletter=Y", "tank/data@s1", "tank/.zfswin-tmp/data-s1"]));
        Assert.Contains(calls, x => x.SequenceEqual(["mount", "tank/.zfswin-tmp/data-s1"]));
        Assert.True(broker.Audit.Last().Ok);
    }

    [Fact]
    public async Task FailedTemporaryMountLeavesNoClone()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => a switch
        {
            ["list", ..] => Task.FromResult(new ToolResult(a[^1] == "tank/.zfswin-tmp" ? 0 : 1, "", "")),
            ["mount", _] => Task.FromResult(new ToolResult(1, "", "cannot mount")),
            _ => Ok(),
        };
        await Assert.ThrowsAsync<AdminException>(() => broker.Client.CallAsync("snapshot.mount", new JsonObject { ["snapshot"] = "tank/data@s1", ["mountPoint"] = "Y:" }));
        Assert.Equal(["destroy", "tank/.zfswin-tmp/data-s1"], broker.Runner.Calls.Last().Args);
    }

    [Fact]
    public async Task UnmountDestroysOnlyTemporaryClones()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => Ok(a is ["get", ..] ? "-\n" : "");
        Assert.Equal(ErrorCode.InvalidArgument, (await Assert.ThrowsAsync<AdminException>(() =>
            broker.Client.CallAsync("snapshot.unmount", new JsonObject { ["snapshot"] = "tank/data@s1" }))).Code);
        Assert.DoesNotContain(broker.Runner.Calls, c => c.Args[0] == "destroy");

        broker.Runner.Respond = (_, a, _) => Ok(a is ["get", ..] ? "on\n" : "");
        await broker.Client.CallAsync("snapshot.unmount", new JsonObject { ["snapshot"] = "tank/data@s1" });
        Assert.Equal(["destroy", "-f", "tank/.zfswin-tmp/data-s1"], broker.Runner.Calls.Last().Args);
    }

    [Fact]
    public async Task SnapshotWithTemporaryMountCannotBeDestroyed()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => Ok(a is ["get", "-H", "-o", "value", "clones", _] ? "tank/.zfswin-tmp/data-s1\n" : "");
        var e = await Assert.ThrowsAsync<AdminException>(() =>
            broker.Client.CallAsync("snapshot.destroy", new JsonObject { ["snapshot"] = "tank/data@s1", ["confirm"] = true }));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Contains("mounted temporarily", e.Message);
        Assert.DoesNotContain(broker.Runner.Calls, c => c.Args[0] == "destroy");
    }

    [Fact]
    public async Task LeftoverTemporaryMountsAreRemoved()
    {
        var runner = new FakeRunner
        {
            Respond = (_, a, _) => a switch
            {
                ["list", ..] => Ok("tank\t-\ntank/.zfswin-tmp\ton\ntank/.zfswin-tmp/data-s1\ton\ntank/data\t-\ntank/fake/.zfswin-tmp\ton\n"),
                ["get", ..] => Ok("on\n"),
                _ => Ok(),
            },
        };
        var removed = await Operations.RemoveTemporaryMountsAsync(runner, CancellationToken.None);
        Assert.Equal(["tank/.zfswin-tmp/data-s1"], removed);
        Assert.Single(runner.Calls, c => c.Args[0] == "destroy");
    }

    [Fact]
    public async Task IostatMemoryAndCountOperations()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, a, _) => a switch
        {
            ["get", "-j", "--json-int", "-p", "dedup_table_size"] => Ok("""{"pools":{"tank":{"properties":{"dedup_table_size":{"value":4096}}}}}"""),
            ["get", ..] => Ok(VdevGet),
            ["userspace", ..] => Ok("10\n32\n"),
            ["mount"] => Ok("tank   D:/\ntank/data   D:/data/\n"),
            _ => Ok(),
        };
        broker.Runner.Kstat = new KstatText("zfs:0:arcstats:size\t42\nzfs:0:arcstats:c_max\t100\n", "user-mode");
        broker.Runner.Count = _ => new FileCount(5, 2, 1000, 1, true);

        JsonNode? io = await broker.Client.CallAsync("pool.iostat", new JsonObject { ["pool"] = "tank" });
        Assert.Equal(["get", "-j", "--json-int", "-p", "read_ops,write_ops,read_bytes,write_bytes", "tank", "all-vdevs"], broker.Runner.Calls.Last().Args);
        Assert.True(io!["sampledAt"]!.GetValue<long>() > 0);

        JsonNode? mem = await broker.Client.CallAsync("memory.stats");
        Assert.Equal("user-mode", mem!["source"]!.GetValue<string>());
        Assert.Equal(42, mem["arc"]!["size"]!.GetValue<long>());
        Assert.Equal(4096, mem["dedupTables"]![0]!["bytes"]!.GetValue<long>());

        JsonNode? count = await broker.Client.CallAsync("dataset.count", new JsonObject { ["dataset"] = "tank/data" });
        Assert.Equal(@"D:\data\", Assert.Single(broker.Runner.Walked));
        Assert.Equal(5, count!["files"]!.GetValue<long>());
        Assert.Equal(2, count["folders"]!.GetValue<long>());
        Assert.Equal(42, count["objects"]!.GetValue<long>());

        JsonNode? objectsOnly = await broker.Client.CallAsync("dataset.count", new JsonObject { ["dataset"] = "tank/data", ["objectsOnly"] = true });
        Assert.Null(objectsOnly!["files"]);
        Assert.Single(broker.Runner.Walked);
    }

    [Fact]
    public async Task CountIsForAdministratorsButDoesNotWaitForMutations()
    {
        await using var user = new TestBroker(admin: false);
        Assert.Equal(ErrorCode.AccessDenied, (await Assert.ThrowsAsync<AdminException>(() =>
            user.Client.CallAsync("dataset.count", new JsonObject { ["dataset"] = "tank" }))).Code);
        Assert.False(Operations.All["dataset.count"].Exclusive);
        Assert.True(Operations.All["snapshot.rollback"].Destructive);
        Assert.False(Operations.All["pool.errors"].Privileged);
        Assert.False(Operations.All["pool.iostat"].Privileged);
        Assert.False(Operations.All["memory.stats"].Privileged);
    }

    [Fact]
    public async Task ClientParsesTheNewResults()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, _, _) => Ok(StatusWithErrors);
        var client = new AdminClient(broker.Client);
        PoolErrorsInfo errors = await client.PoolErrorsAsync("tank");
        Assert.Equal(5, errors.Files.Count);
        Assert.Equal("tank/data@before:/old/report.docx", errors.Files[1].Display);
        Assert.Equal("<metadata>", errors.Files[2].Display);
        Assert.Equal(7, Assert.Single(errors.Devices).ChecksumErrors);
    }
}

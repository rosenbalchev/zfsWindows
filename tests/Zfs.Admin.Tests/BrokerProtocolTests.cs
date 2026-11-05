using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Zfs.Admin;

namespace Zfs.Admin.Tests;

/// <summary>Records every tool invocation and answers from a script.</summary>
sealed class FakeRunner : IToolRunner
{
    public ConcurrentQueue<(ZfsTool Tool, string[] Args)> Calls { get; } = new();
    public Func<ZfsTool, string[], CancellationToken, Task<ToolResult>> Respond { get; set; } =
        (_, _, _) => Task.FromResult(new ToolResult(0, "{}", ""));

    public Task<ToolResult> RunAsync(ZfsTool tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Enqueue((tool, arguments.ToArray()));
        return Respond(tool, arguments.ToArray(), cancellationToken);
    }
}

sealed class FakeAuthenticator(bool admin) : ICallerAuthenticator
{
    public CallerIdentity Identify(NamedPipeServerStream pipe) => new(admin ? @"TEST\admin" : @"TEST\user", admin);
}

/// <summary>A broker on a private pipe for one test.</summary>
sealed class TestBroker : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task running;

    public string PipeName { get; } = "zfswin-test-" + Guid.NewGuid().ToString("N");
    public FakeRunner Runner { get; } = new();
    public List<AuditEntry> Audit { get; } = [];
    public BrokerClient Client => new(PipeName);

    public TestBroker(bool admin = true)
    {
        var server = new BrokerServer(Runner, new FakeAuthenticator(admin),
            new BrokerOptions { PipeName = PipeName, MaxConnections = 4, RequestReadTimeout = TimeSpan.FromSeconds(2) },
            e => { lock (Audit) Audit.Add(e); });
        running = server.RunAsync(stop.Token);
    }

    /// <summary>Sends raw bytes and returns the raw response frame (or null).</summary>
    public async Task<BrokerResponse?> SendRawAsync(byte[] bytes)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        byte[]? frame = await Protocol.ReadFrameAsync(pipe, Protocol.MaxResponseBytes, CancellationToken.None);
        return frame is null ? null : BrokerResponse.Parse(frame);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await running;
    }
}

public class BrokerProtocolTests
{
    static byte[] Frame(byte[] payload)
    {
        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return bytes;
    }

    static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    static async Task<AdminException> Fails(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    [Fact]
    public async Task ReadOperationBuildsFixedArguments()
    {
        await using var broker = new TestBroker(admin: false);
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(0, """{"output_version":{},"pools":{}}""", ""));
        JsonNode? result = await broker.Client.CallAsync("pool.list");
        Assert.Equal("""{"pools":[]}""", result!.ToJsonString());
        var call = Assert.Single(broker.Runner.Calls);
        Assert.Equal(ZfsTool.Zpool, call.Tool);
        Assert.Equal(["list", "-j", "--json-int"], call.Args);
        Assert.Empty(broker.Audit);	// reads are not audited
    }

    [Fact]
    public async Task NoImportedPoolsIsAnEmptyListNotAnError()
    {
        // What zpool.exe really does (step-14 VM run): nothing on stdout.
        await using var broker = new TestBroker(admin: false);
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(0, "", "no pools available\n"));
        Assert.Equal("""{"pools":[]}""", (await broker.Client.CallAsync("pool.list"))!.ToJsonString());
        Assert.Equal("""{"pools":[]}""", (await broker.Client.CallAsync("pool.status"))!.ToJsonString());
    }

    [Fact]
    public async Task StandardUserCannotMutate()
    {
        await using var broker = new TestBroker(admin: false);
        var e = await Fails(broker.Client.CallAsync("pool.export", new JsonObject { ["pool"] = "tank" }));
        Assert.Equal(ErrorCode.AccessDenied, e.Code);
        Assert.Empty(broker.Runner.Calls);
        Assert.False(Assert.Single(broker.Audit).Ok);
    }

    [Fact]
    public async Task DestructiveOperationNeedsConfirmation()
    {
        await using var broker = new TestBroker();
        var args = new JsonObject { ["dataset"] = "tank/a" };
        Assert.Equal(ErrorCode.ConfirmationRequired, (await Fails(broker.Client.CallAsync("dataset.destroy", args))).Code);
        Assert.Empty(broker.Runner.Calls);
        args = new JsonObject { ["dataset"] = "tank/a", ["confirm"] = true, ["recursive"] = true };
        await broker.Client.CallAsync("dataset.destroy", args);
        Assert.Equal(["destroy", "-r", "tank/a"], Assert.Single(broker.Runner.Calls).Args);
        Assert.True(broker.Audit.Last().Ok);
        Assert.Equal(@"TEST\admin", broker.Audit.Last().Caller);
    }

    [Fact]
    public async Task RootDatasetCannotBeDestroyed()
    {
        await using var broker = new TestBroker();
        var e = await Fails(broker.Client.CallAsync("dataset.destroy", new JsonObject { ["dataset"] = "tank", ["confirm"] = true }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Empty(broker.Runner.Calls);
    }

    [Fact]
    public async Task OptionInjectionIsRejectedBeforeAnyCommandRuns()
    {
        await using var broker = new TestBroker();
        foreach (var (op, args) in new[]
        {
            ("pool.export", new JsonObject { ["pool"] = "-a" }),
            ("dataset.set", new JsonObject { ["dataset"] = "tank/a", ["properties"] = new JsonObject { ["-o"] = "x" } }),
            ("dataset.mount", new JsonObject { ["dataset"] = "-a" }),
            ("pool.import", new JsonObject { ["pool"] = "tank", ["dirs"] = new JsonArray("-d") }),
        })
        {
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync(op, args))).Code);
        }
        Assert.Empty(broker.Runner.Calls);
    }

    [Fact]
    public async Task UnknownOperationAndArgumentAreRejected()
    {
        await using var broker = new TestBroker();
        Assert.Equal(ErrorCode.BadRequest, (await Fails(broker.Client.CallAsync("pool.destroy", new JsonObject { ["pool"] = "tank" }))).Code);
        Assert.Equal(ErrorCode.BadRequest, (await Fails(broker.Client.CallAsync("pool.export", new JsonObject { ["pool"] = "tank", ["all"] = true }))).Code);
        Assert.Empty(broker.Runner.Calls);
    }

    [Fact]
    public async Task WrongProtocolVersionIsRefused()
    {
        await using var broker = new TestBroker();
        BrokerResponse? r = await broker.SendRawAsync(Frame("""{"protocol":2,"id":"x","operation":"pool.list","arguments":{}}"""));
        Assert.False(r!.Ok);
        Assert.Equal("protocol-mismatch", r.Error!.Code);
        Assert.Equal("x", r.Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"protocol":"one"}""")]
    [InlineData("""{"protocol":1,"id":"x","operation":"pool.list","arguments":[1]}""")]
    public async Task MalformedRequestsAreAnsweredNotCrashed(string json)
    {
        await using var broker = new TestBroker();
        BrokerResponse? r = await broker.SendRawAsync(Frame(json));
        Assert.False(r!.Ok);
        Assert.Equal("bad-request", r.Error!.Code);
        // The broker keeps serving.
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(0, """{"pools":{}}""", ""));
        Assert.NotNull(await broker.Client.CallAsync("pool.list"));
    }

    [Fact]
    public async Task OversizedFrameIsRefusedBeforeAllocation()
    {
        await using var broker = new TestBroker();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        BrokerResponse? r = await broker.SendRawAsync(header);
        Assert.Equal("bad-request", r!.Error!.Code);
        BinaryPrimitives.WriteInt32LittleEndian(header, -5);
        Assert.Equal("bad-request", (await broker.SendRawAsync(header))!.Error!.Code);
    }

    [Fact]
    public async Task TruncatedFrameIsRefused()
    {
        await using var broker = new TestBroker();
        byte[] frame = Frame("""{"protocol":1,"id":"x","operation":"pool.list"}""");
        // Header promises more than arrives; the read times out after RequestReadTimeout.
        BrokerResponse? r = await broker.SendRawAsync(frame[..10]);
        Assert.Null(r);
    }

    [Fact]
    public async Task ClientCancellationCancelsTheCommand()
    {
        await using var broker = new TestBroker();
        var cancelled = new TaskCompletionSource();
        broker.Runner.Respond = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw new AdminException(ErrorCode.Cancelled, "cancelled");
            }
            return new ToolResult(0, "", "");
        };
        using var cts = new CancellationTokenSource();
        Task call = broker.Client.CallAsync("scrub.start", new JsonObject { ["pool"] = "tank" }, cts.Token);
        while (broker.Runner.Calls.IsEmpty)
            await Task.Delay(10);
        cts.Cancel();
        Assert.Equal(ErrorCode.Cancelled, (await Fails(call)).Code);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MutationsAreSerializedReadsAreNot()
    {
        await using var broker = new TestBroker();
        int running = 0, maxMutations = 0;
        broker.Runner.Respond = async (tool, args, token) =>
        {
            if (args[0] == "export")
            {
                int now = Interlocked.Increment(ref running);
                lock (broker) maxMutations = Math.Max(maxMutations, now);
                await Task.Delay(100, token);
                Interlocked.Decrement(ref running);
            }
            return new ToolResult(0, """{"pools":{}}""", "");
        };
        var exports = Enumerable.Range(0, 3).Select(i => broker.Client.CallAsync("pool.export", new JsonObject { ["pool"] = $"p{i}" }));
        var reads = Enumerable.Range(0, 3).Select(_ => broker.Client.CallAsync("pool.list"));
        await Task.WhenAll(exports.Concat(reads));
        Assert.Equal(1, maxMutations);
    }

    [Fact]
    public async Task ToolFailuresAreClassified()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(1, "", "cannot open 'nope': no such pool\n"));
        var e = await Fails(broker.Client.CallAsync("pool.status", new JsonObject { ["pool"] = "nope" }));
        Assert.Equal(ErrorCode.NotFound, e.Code);
        Assert.Contains("no such pool", e.Detail);

        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(1, "", "cannot export 'tank': pool is busy\n"));
        Assert.Equal(ErrorCode.Busy, (await Fails(broker.Client.CallAsync("pool.export", new JsonObject { ["pool"] = "tank" }))).Code);

        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(1, "", "internal error: something\n"));
        Assert.Equal(ErrorCode.CommandFailed, (await Fails(broker.Client.CallAsync("pool.export", new JsonObject { ["pool"] = "tank" }))).Code);

        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(0, "not json", ""));
        Assert.Equal(ErrorCode.Internal, (await Fails(broker.Client.CallAsync("pool.list"))).Code);
    }

    [Fact]
    public async Task DiscoverWithNothingToImportIsAnEmptyList()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(1, "", "no pools available to import\n"));
        Assert.Equal("""{"pools":[]}""", (await broker.Client.CallAsync("pool.discover"))!.ToJsonString());
    }

    [Fact]
    public async Task PoolCreateNamesEveryDeviceExplicitly()
    {
        await using var broker = new TestBroker();
        string dir = Directory.CreateTempSubdirectory("zfswin-test").FullName;
        try
        {
            string a = Path.Combine(dir, "a.img"), b = Path.Combine(dir, "b.img");
            File.WriteAllBytes(a, [0]);
            File.WriteAllBytes(b, [0]);
            var noDevices = new JsonObject { ["pool"] = "tank", ["confirm"] = true };
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync("pool.create", noDevices))).Code);
            var twice = new JsonObject { ["pool"] = "tank", ["confirm"] = true, ["devices"] = new JsonArray(a, a.ToUpperInvariant()) };
            Assert.Equal(ErrorCode.InvalidArgument, (await Fails(broker.Client.CallAsync("pool.create", twice))).Code);
            var mirror = new JsonObject
            {
                ["pool"] = "tank", ["confirm"] = true, ["layout"] = "mirror", ["devices"] = new JsonArray(a, b),
                ["poolProperties"] = new JsonObject { ["ashift"] = "12" }, ["datasetProperties"] = new JsonObject { ["compression"] = "lz4" },
            };
            await broker.Client.CallAsync("pool.create", mirror);
            // zpool.exe takes file vdevs only in \\?\ form.
            Assert.Equal(["create", "-o", "ashift=12", "-O", "compression=lz4", "tank", "mirror", @"\\?\" + a, @"\\?\" + b], Assert.Single(broker.Runner.Calls).Args);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BrokerUnavailableIsReported()
    {
        var client = new BrokerClient("zfswin-test-absent-" + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        Assert.Equal(ErrorCode.BrokerUnavailable, (await Fails(client.CallAsync("pool.list"))).Code);
    }
}

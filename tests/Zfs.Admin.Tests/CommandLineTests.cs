using Zfs.Admin;
using Zfs.Admin.Cli;

namespace Zfs.Admin.Tests;

public class CommandLineTests
{
    static ParsedCommand Parse(string line) => CommandLine.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("pool list", "pool.list", "{}")]
    [InlineData("pool status tank", "pool.status", """{"pool":"tank"}""")]
    [InlineData("pool get tank size health", "pool.get", """{"pool":"tank","properties":["size","health"]}""")]
    [InlineData(@"pool discover --dir D:\p", "pool.discover", """{"dirs":["D:\\p"]}""")]
    [InlineData(@"pool import tank --dir D:\p --no-mount --readonly", "pool.import", """{"noMount":true,"readOnly":true,"pool":"tank","dirs":["D:\\p"]}""")]
    [InlineData("pool import tank --new-name tank2", "pool.import", """{"newName":"tank2","pool":"tank"}""")]
    [InlineData("pool export tank --force", "pool.export", """{"force":true,"pool":"tank"}""")]
    [InlineData("scrub start tank", "scrub.start", """{"pool":"tank"}""")]
    [InlineData("scrub status tank", "scrub.status", """{"pool":"tank"}""")]
    [InlineData("dataset list tank -r", "dataset.list", """{"recursive":true,"root":"tank"}""")]
    [InlineData("dataset list -d 1", "dataset.list", """{"depth":1}""")]
    [InlineData("dataset set tank/a compression=zstd atime=off", "dataset.set", """{"dataset":"tank/a","properties":{"compression":"zstd","atime":"off"}}""")]
    [InlineData("dataset create tank/a -o compression=lz4 -p", "dataset.create", """{"parents":true,"dataset":"tank/a","properties":{"compression":"lz4"}}""")]
    [InlineData("dataset mount tank/a", "dataset.mount", """{"dataset":"tank/a"}""")]
    [InlineData("mount list", "mount.list", "{}")]
    [InlineData("snapshot list tank/a", "snapshot.list", """{"root":"tank/a"}""")]
    [InlineData("snapshot create tank/a@s1 -r", "snapshot.create", """{"recursive":true,"snapshot":"tank/a@s1"}""")]
    [InlineData("version", "version", "{}")]
    public void MapsCommandsToOperations(string line, string operation, string arguments)
    {
        ParsedCommand c = Parse(line);
        Assert.Equal(operation, c.Operation);
        Assert.Equal(arguments, c.Arguments.ToJsonString());
        Assert.False(c.Destructive);
    }

    [Theory]
    [InlineData(@"pool create tank D:\p\a.img --yes", "pool.create")]
    [InlineData(@"pool create tank --mirror D:\p\a.img D:\p\b.img -o ashift=12 -O compression=lz4 --yes", "pool.create")]
    [InlineData("dataset destroy tank/a -r --yes", "dataset.destroy")]
    [InlineData("snapshot destroy tank/a@s1 --yes", "snapshot.destroy")]
    [InlineData(@"vdev attach tank D:\p\a.img D:\p\b.img --yes", "vdev.attach")]
    [InlineData(@"vdev detach tank D:\p\b.img --yes", "vdev.detach")]
    [InlineData(@"vdev replace tank D:\p\a.img D:\p\c.img --yes", "vdev.replace")]
    public void DestructiveCommandsAreFlagged(string line, string operation)
    {
        ParsedCommand c = Parse(line);
        Assert.Equal(operation, c.Operation);
        Assert.True(c.Destructive);
        Assert.True(c.Confirmed);
        Assert.False(Parse(line.Replace(" --yes", "")).Confirmed);
    }

    [Fact]
    public void PoolCreateCarriesLayoutAndProperties()
    {
        ParsedCommand c = Parse(@"pool create tank --mirror D:\p\a.img D:\p\b.img -o ashift=12 -O compression=lz4 --yes");
        Assert.Equal("""{"layout":"mirror","pool":"tank","devices":["D:\\p\\a.img","D:\\p\\b.img"],"poolProperties":{"ashift":"12"},"datasetProperties":{"compression":"lz4"}}""",
            c.Arguments.ToJsonString());
    }

    [Theory]
    [InlineData("--json --timeout 30 pool list", true, 30)]
    [InlineData("pool list", false, null)]
    public void GlobalOptions(string line, bool json, int? timeout)
    {
        ParsedCommand c = Parse(line);
        Assert.Equal(json, c.Json);
        Assert.Equal(timeout is null ? null : TimeSpan.FromSeconds(timeout.Value), c.Timeout);
    }

    [Theory]
    [InlineData("pool")]
    [InlineData("pool frobnicate")]
    [InlineData("pool list extra")]
    [InlineData("pool export")]
    [InlineData("pool export tank --yes")]
    [InlineData("pool create tank")]
    [InlineData("dataset set tank/a compression")]
    [InlineData("dataset set tank/a compression=lz4 compression=zstd")]
    [InlineData("dataset list -d many")]
    [InlineData("--timeout -1 pool list")]
    [InlineData("--bogus pool list")]
    [InlineData("pool import tank --dir")]
    public void RejectsBadCommandLines(string line) =>
        Assert.Equal(ErrorCode.Usage, Assert.Throws<AdminException>(() => Parse(line)).Code);

    [Fact]
    public void EveryCliOperationExistsInTheBroker()
    {
        string[] lines =
        [
            "pool list", "pool status", "pool get t", "pool discover", "pool import t", "pool export t", @"pool create t D:\a --yes",
            "scrub status t", "scrub start t", "scrub pause t", "scrub stop t", "dataset list", "dataset get t", "dataset set t a=b",
            "dataset create t/a", "dataset destroy t/a --yes", "dataset mount t/a", "dataset unmount t/a", "mount list",
            "snapshot list", "snapshot create t@s", "snapshot destroy t@s --yes", @"vdev attach t D:\a D:\b --yes",
            @"vdev detach t D:\a --yes", @"vdev replace t D:\a --yes", "version",
        ];
        var used = lines.Select(l => Parse(l).Operation).ToHashSet();
        foreach (string op in used)
            Assert.True(Operations.All.ContainsKey(op), op);
        // ...and the CLI covers every broker operation.
        Assert.Empty(Operations.All.Keys.Except(used));
        // The CLI's destructive set is the broker's.
        foreach (string l in lines)
        {
            ParsedCommand c = Parse(l);
            Assert.Equal(Operations.All[c.Operation].Destructive, c.Destructive);
        }
    }

    [Fact]
    public void ExitCodesAreStable()
    {
        Assert.Equal(1, ErrorCode.CommandFailed.ExitCode());
        Assert.Equal(2, ErrorCode.Usage.ExitCode());
        Assert.Equal(3, ErrorCode.NotFound.ExitCode());
        Assert.Equal(4, ErrorCode.AccessDenied.ExitCode());
        Assert.Equal(5, ErrorCode.BrokerUnavailable.ExitCode());
        Assert.Equal(6, ErrorCode.ConfirmationRequired.ExitCode());
        Assert.Equal(7, ErrorCode.Timeout.ExitCode());
        Assert.Equal(7, ErrorCode.Cancelled.ExitCode());
        Assert.Equal(8, ErrorCode.ProtocolMismatch.ExitCode());
        Assert.Equal(9, ErrorCode.Busy.ExitCode());
        Assert.Equal(10, ErrorCode.Internal.ExitCode());
        foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
            Assert.Equal(code, ErrorCodes.FromWire(code.ToWire()));
    }
}

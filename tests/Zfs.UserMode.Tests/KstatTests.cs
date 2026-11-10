using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// Step 20 (issue #17): the engine publishes arcstats, which the bridge
// serves to the broker's memory.stats as "kstat -p" lines.  Before patch
// 0037 libspl's kstat_create() returned NULL and there was nothing to read.
[Collection(EngineCollection.Name)]
public sealed class KstatTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static Dictionary<string, ulong> Arcstats() =>
        ZfsUserModeEngine.KstatText("zfs:0:arcstats")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .ToDictionary(f => f[0][(f[0].LastIndexOf(':') + 1)..], f => ulong.Parse(f[1]));

    [Fact]
    public void ArcstatsAreServedInKstatForm()
    {
        var pool = Scratch.PoolName("zut_kstat");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1));
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            fs.Write(fs.CreateFile("/data.bin"), 0, Data.Random(4 << 20, 17));

        string text = ZfsUserModeEngine.KstatText("zfs:0:arcstats");
        Assert.StartsWith("zfs:0:arcstats:", text);
        var arc = Arcstats();
        Assert.True(arc["size"] > 0, "ARC size");
        Assert.True(arc["c_max"] > 0, "ARC c_max");
        Assert.True(arc["c"] <= arc["c_max"]);
        Assert.Contains("hits", arc.Keys);
        Assert.Contains("mfu_size", arc.Keys);
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Fact]
    public void OnlyRegisteredKstatsExist()
    {
        var e = Assert.ThrowsAny<Exception>(() => ZfsUserModeEngine.KstatText("zfs:0:dbufstats"));
        Assert.Contains("kstat", e.Message);
        Assert.ThrowsAny<Exception>(() => ZfsUserModeEngine.KstatText("not a spec"));
    }
}

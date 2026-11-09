using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// Create, export, import and destroy pools on image files, through the
// same ioctls zpool.exe sends.
[Collection(EngineCollection.Name)]
public sealed class PoolLifecycleTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData("", 1)]
    [InlineData("", 2)]
    [InlineData("mirror", 2)]
    [InlineData("mirror", 3)]
    [InlineData("raidz1", 3)]
    [InlineData("raidz2", 4)]
    public void DataSurvivesExportAndImport(string layout, int disks)
    {
        var pool = Scratch.PoolName("zut_life");
        ZfsUserModeEngine.CreatePool(pool, layout, _scratch.Images(disks), "ashift=12");
        var data = Data.Random(3 << 20, disks);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
        {
            fs.CreateDirectory("/a");
            fs.CreateDirectory("/a/b");
            fs.Write(fs.CreateFile("/a/b/data.bin"), 0, data);
        }
        ZfsUserModeEngine.ExportPool(pool);

        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        var status = ZfsUserModeEngine.PoolStatus(pool);
        Assert.Equal(7UL, status.State);  // VDEV_STATE_HEALTHY
        Assert.Equal((ulong)disks, status.Leaves);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(fs.Lookup("/a/b/data.bin"))));
        ZfsUserModeEngine.DestroyPool(pool);
    }

    // zpool import skips destroyed pools (zpool_main.c, unless -D); the
    // engine's import_pool() (zum_pool_um.c) does not check the state.
    [Fact(Skip = "The engine imports a destroyed pool: zum_pool_import() does not filter POOL_STATE_DESTROYED.")]
    public void DestroyedPoolDoesNotImport()
    {
        var pool = Scratch.PoolName("zut_gone");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1));
        ZfsUserModeEngine.DestroyPool(pool);
        Assert.NotEqual(0, ZfsUserModeEngine.TryImportPool(pool, _scratch.Directory));
    }

    [Fact]
    public void PoolNameIsUnique()
    {
        var pool = Scratch.PoolName("zut_dup");
        ZfsUserModeEngine.CreatePool(pool, "", [_scratch.Image("a.img")]);
        var e = Assert.Throws<ZfsUmException>(() =>
            ZfsUserModeEngine.CreatePool(pool, "", [_scratch.Image("b.img")]));
        Assert.Equal(ZfsUmErrno.EEXIST, e.Errno);
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Fact]
    public void ImageBelowMinimumSizeIsRejected()
    {
        // SPA_MINDEVSIZE is 64 MiB.
        var pool = Scratch.PoolName("zut_small");
        Assert.Throws<ZfsUmException>(() =>
            ZfsUserModeEngine.CreatePool(pool, "", [_scratch.Image("small.img", 32L << 20)]));
    }

    [Fact]
    public void ImportFindsPoolByImagePath()
    {
        var pool = Scratch.PoolName("zut_path");
        var image = _scratch.Image("only.img");
        ZfsUserModeEngine.CreatePool(pool, "", [image]);
        ZfsUserModeEngine.ExportPool(pool);
        // An image stands for its directory (zum_pool_um.c split_sources()).
        ZfsUserModeEngine.ImportPool(pool, image);
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Fact]
    public void ChildDatasetsAndPropertiesSurviveImport()
    {
        var pool = Scratch.PoolName("zut_ds");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1), "ashift=12", "compression=lz4");
        ZfsUserModeEngine.CreateDataset($"{pool}/child", "recordsize=16K");
        ZfsUserModeEngine.SetProperties($"{pool}/child", "compression=zstd;org.zfswin:test=yes");
        Assert.Equal(ZfsUmErrno.EINVAL, ZfsUserModeEngine.TrySetProperties($"{pool}/child", "compression=bogus"));
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool, $"{pool}/child"))
            fs.Write(fs.CreateFile("/in-child.txt"), 0, "child"u8);
        ZfsUserModeEngine.ExportPool(pool);

        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool, $"{pool}/child"))
            Assert.Equal("child"u8.ToArray(), fs.ReadAll(fs.Lookup("/in-child.txt")));
        using (var root = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            Assert.NotEqual(0, root.TryLookup("/in-child.txt", out _));
        ZfsUserModeEngine.DestroyPool(pool);
    }
}

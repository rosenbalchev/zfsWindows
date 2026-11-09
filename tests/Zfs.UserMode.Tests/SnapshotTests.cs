using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// Snapshots through zfs.exe's ioctls, seen through the .zfs view the
// front end gives them, rollback, and block cloning.
[Collection(EngineCollection.Name)]
public sealed class SnapshotTests : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly string _pool = Scratch.PoolName("zut_snap");

    public SnapshotTests() =>
        ZfsUserModeEngine.CreatePool(_pool, "", _scratch.Images(1, 256L << 20), "ashift=12");

    public void Dispose()
    {
        ZfsUserModeEngine.DestroyPool(_pool);
        _scratch.Dispose();
    }

    [Fact]
    public void SnapshotKeepsTheOldContent()
    {
        var v1 = Data.Random(2 << 20, 1);
        var v2 = Data.Random(2 << 20, 2);
        using var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
        var obj = fs.CreateFile("/doc.bin");
        fs.Write(obj, 0, v1);
        fs.CreateFile("/removed-later");
        fs.Sync();
        ZfsUserModeEngine.Snapshot($"{_pool}@s1");
        fs.Write(obj, 0, v2);
        fs.Remove("/removed-later");
        fs.CreateFile("/added-later");

        Assert.Contains(fs.ListSnapshots(), s => s.Name == "s1");
        using (var snap = fs.OpenSnapshot("s1"))
        {
            Assert.Equal(Data.Hash(v1), Data.Hash(snap.ReadAll(snap.Lookup("/doc.bin"))));
            Assert.Equal(0, snap.TryLookup("/removed-later", out _));
            Assert.NotEqual(0, snap.TryLookup("/added-later", out _));
            Assert.NotEqual(0, snap.TryCreate("/new", false, out _));  // read-only
            Assert.Equal(1, snap.StatFs().ReadOnly);
        }
        Assert.Equal(Data.Hash(v2), Data.Hash(fs.ReadAll(obj)));
    }

    [Fact]
    public void RollbackRestoresTheSnapshot()
    {
        var v1 = Data.Random(1 << 20, 3);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool))
        {
            fs.Write(fs.CreateFile("/r.bin"), 0, v1);
            fs.Sync();
        }
        ZfsUserModeEngine.Snapshot($"{_pool}@before");
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool))
        {
            fs.Truncate(fs.Lookup("/r.bin"), 10);
            fs.CreateFile("/junk");
        }
        ZfsUserModeEngine.Rollback(_pool);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool))
        {
            Assert.Equal(Data.Hash(v1), Data.Hash(fs.ReadAll(fs.Lookup("/r.bin"))));
            Assert.NotEqual(0, fs.TryLookup("/junk", out _));
        }
    }

    [Fact]
    public void RollbackOfAnOpenDatasetIsRefused()
    {
        ZfsUserModeEngine.Snapshot($"{_pool}@busy");
        using var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
        Assert.Throws<ZfsUmException>(() => ZfsUserModeEngine.Rollback(_pool));
    }

    [Fact]
    public void DestroyedSnapshotIsGone()
    {
        ZfsUserModeEngine.Snapshot($"{_pool}@gone");
        Assert.Equal(ZfsUmErrno.EEXIST,
            Assert.Throws<ZfsUmException>(() => ZfsUserModeEngine.Snapshot($"{_pool}@gone")).Errno);
        ZfsUserModeEngine.DestroySnapshot($"{_pool}@gone");
        using var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
        Assert.DoesNotContain(fs.ListSnapshots(), s => s.Name == "gone");
        Assert.Throws<ZfsUmException>(() => fs.OpenSnapshot("gone").Dispose());
    }

    [Fact]
    public void SnapshotsSurviveExportAndImport()
    {
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool))
            fs.Write(fs.CreateFile("/kept"), 0, "kept"u8);
        ZfsUserModeEngine.Snapshot($"{_pool}@kept");
        ZfsUserModeEngine.ExportPool(_pool);
        ZfsUserModeEngine.ImportPool(_pool, _scratch.Directory);
        using var reopened = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
        using var snap = reopened.OpenSnapshot("kept");
        Assert.Equal("kept"u8.ToArray(), snap.ReadAll(snap.Lookup("/kept")));
    }

    [Fact]
    public void BlockCloneSharesData()
    {
        var data = Data.Random(4 << 20, 5);
        using var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
        var source = fs.CreateFile("/clone-source");
        fs.Write(source, 0, data);
        fs.Sync();
        var free = fs.StatFs().FreeBytes;
        var target = fs.CreateFile("/clone-target");
        var length = (ulong)data.Length;
        var errno = fs.TryClone(source, 0, target, 0, ref length);
        Assert.True(errno == 0, $"clone: errno {errno}");
        Assert.Equal((ulong)data.Length, length);
        fs.Sync();
        Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(target)));
        // Shared, not copied: far less than 4 MiB more in use.
        Assert.True(free - fs.StatFs().FreeBytes < (1UL << 20),
            $"clone used {(free - fs.StatFs().FreeBytes) >> 10} KiB");
        // Changing the clone leaves the source alone.
        fs.Write(target, 0, new byte[4096]);
        Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(source)));
    }
}

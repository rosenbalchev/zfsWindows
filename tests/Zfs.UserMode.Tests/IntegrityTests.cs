using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// What is written comes back after export and import, with every
// compression and record size.
[Collection(EngineCollection.Name)]
public sealed class IntegrityTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData("off")]
    [InlineData("lz4")]
    [InlineData("zstd")]
    [InlineData("zstd-19")]
    [InlineData("gzip-1")]
    [InlineData("gzip-9")]
    [InlineData("zle")]
    [InlineData("lzjb")]
    public void CompressionRoundTrips(string compression)
    {
        var pool = Scratch.PoolName("zut_comp");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1), "ashift=12", $"compression={compression}");
        var text = Data.Compressible(8 << 20);
        var random = Data.Random(4 << 20, 4);
        // Partly compressible: random blocks among zero runs.
        var mixed = new byte[4 << 20];
        for (var i = 0; i < mixed.Length; i += 64 << 10) random.AsSpan(i, 16 << 10).CopyTo(mixed.AsSpan(i));
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
        {
            fs.Write(fs.CreateFile("/text"), 0, text);
            fs.Write(fs.CreateFile("/random"), 0, random);
            fs.Write(fs.CreateFile("/mixed"), 0, mixed);
            fs.Sync();
            var allocated = fs.GetAttributes("/text").Allocation;
            if (compression is "off")
                Assert.True(allocated >= (ulong)text.Length, $"off: {allocated} allocated");
            else if (compression is not "zle")  // zle compresses zero runs only
                Assert.True(allocated < (ulong)text.Length / 4, $"{compression}: {allocated} allocated for {text.Length}");
        }
        ZfsUserModeEngine.ExportPool(pool);

        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
        {
            Assert.Equal(Data.Hash(text), Data.Hash(fs.ReadAll(fs.Lookup("/text"))));
            Assert.Equal(Data.Hash(random), Data.Hash(fs.ReadAll(fs.Lookup("/random"))));
            Assert.Equal(Data.Hash(mixed), Data.Hash(fs.ReadAll(fs.Lookup("/mixed"))));
        }
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Theory]
    [InlineData("4K")]
    [InlineData("128K")]
    [InlineData("1M")]
    public void RecordSizesRoundTripUnalignedWrites(string recordsize)
    {
        var pool = Scratch.PoolName("zut_rs");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1), "ashift=12", $"recordsize={recordsize};compression=off");
        var expected = new byte[6 << 20];
        var random = new Random(8);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
        {
            var obj = fs.CreateFile("/patchwork");
            // Writes at odd offsets and lengths, overlapping each other.
            for (var i = 0; i < 400; i++)
            {
                var offset = random.Next(expected.Length - 70_000);
                var chunk = Data.Random(random.Next(1, 70_000), i);
                chunk.CopyTo(expected, offset);
                fs.Write(obj, (ulong)offset, chunk);
            }
            fs.Truncate(obj, (ulong)expected.Length);
        }
        ZfsUserModeEngine.ExportPool(pool);

        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            Assert.Equal(Data.Hash(expected), Data.Hash(fs.ReadAll(fs.Lookup("/patchwork"))));
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Fact]
    public void DedupStoresIdenticalBlocksOnce()
    {
        var pool = Scratch.PoolName("zut_dedup");
        ZfsUserModeEngine.CreatePool(pool, "", _scratch.Images(1, 256L << 20), "ashift=12", "dedup=on;compression=off");
        var data = Data.Random(32 << 20, 12);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
        {
            var free = fs.StatFs().FreeBytes;
            for (var copy = 0; copy < 4; copy++)
            {
                fs.Write(fs.CreateFile($"/copy{copy}"), 0, data);
                fs.Sync();
            }
            var used = free - fs.StatFs().FreeBytes;
            Assert.True(used < 2 * (ulong)data.Length, $"4 copies of 32 MiB used {used >> 20} MiB");
        }
        ZfsUserModeEngine.ExportPool(pool);
        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            for (var copy = 0; copy < 4; copy++)
                Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(fs.Lookup($"/copy{copy}"))));
        ZfsUserModeEngine.DestroyPool(pool);
    }
}

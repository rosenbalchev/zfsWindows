using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// Damage one image behind the engine's back: redundant pools heal it on
// read and on scrub; a pool without redundancy reports it and never
// returns the damaged data.
[Collection(EngineCollection.Name)]
public sealed class SelfHealingTests : IDisposable
{
    // Past the front labels and boot area, short of the end labels.
    private const long DamageOffset = 16L << 20, DamageLength = 96L << 20;

    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData("mirror", 2)]
    [InlineData("raidz1", 3)]
    public void RedundantPoolHealsOneDamagedImage(string layout, int disks)
    {
        var pool = Scratch.PoolName("zut_heal");
        var images = _scratch.Images(disks);
        ZfsUserModeEngine.CreatePool(pool, layout, images, "ashift=12", "compression=off");
        var data = Data.Random(48 << 20, disks);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            fs.Write(fs.CreateFile("/data.bin"), 0, data);
        ZfsUserModeEngine.ExportPool(pool);

        Data.Corrupt(images[^1], DamageOffset, DamageLength, 21);

        // Good data back, whichever side or column each read went to.
        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(fs.Lookup("/data.bin"))));

        // The scrub reads every copy: the damage is counted and repaired.
        ZfsUserModeEngine.Scrub(pool);
        var scrubbed = ZfsUserModeEngine.PoolStatus(pool);
        Assert.Equal(2UL, scrubbed.ScanState);  // DSS_FINISHED
        Assert.True(scrubbed.ChecksumErrors > 0, "96 MiB of damage without a checksum error");
        Assert.Equal(0UL, scrubbed.ScanErrors);  // nothing unrecoverable
        ZfsUserModeEngine.ExportPool(pool);

        // Repaired on disk: a second scrub after a fresh import finds nothing.
        ZfsUserModeEngine.ImportPool(pool, _scratch.Directory);
        ZfsUserModeEngine.Scrub(pool);
        var second = ZfsUserModeEngine.PoolStatus(pool);
        Assert.Equal(0UL, second.ChecksumErrors);
        Assert.Equal(0UL, second.ScanRepairedBytes);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            Assert.Equal(Data.Hash(data), Data.Hash(fs.ReadAll(fs.Lookup("/data.bin"))));
        ZfsUserModeEngine.DestroyPool(pool);
    }

    [Fact]
    public void SingleImageDamageIsAnErrorNotData()
    {
        var pool = Scratch.PoolName("zut_bad");
        var images = _scratch.Images(1);
        ZfsUserModeEngine.CreatePool(pool, "", images, "ashift=12", "compression=off");
        var data = Data.Random(48 << 20, 31);
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, pool))
            fs.Write(fs.CreateFile("/data.bin"), 0, data);
        ZfsUserModeEngine.ExportPool(pool);

        Data.Corrupt(images[0], DamageOffset, DamageLength, 32);

        // Metadata has extra copies, so the pool usually still imports;
        // either way no damaged byte may come back as file data.
        if (ZfsUserModeEngine.TryImportPool(pool, _scratch.Directory) != 0) return;
        if (ZfsUserModeEngine.TryOpen(_scratch.Directory, pool, pool, false, out var damaged) == 0)
        {
            using (var fs = damaged!)
            {
                var obj = fs.Lookup("/data.bin");
                var buffer = new byte[1 << 20];
                var failed = 0;
                for (var i = 0; i < data.Length >> 20; i++)
                {
                    try
                    {
                        fs.Read(obj, (ulong)i << 20, buffer);
                        Assert.True(buffer.AsSpan().SequenceEqual(data.AsSpan(i << 20, 1 << 20)),
                            $"MiB {i} came back damaged instead of failing");
                    }
                    catch (ZfsUmException) { failed++; }
                }
                Assert.True(failed > 0, "96 MiB of damage went unnoticed");
            }
        }
        ZfsUserModeEngine.ExportPool(pool, force: true);
    }
}

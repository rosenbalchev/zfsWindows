using Xunit;
using Xunit.Abstractions;

namespace Zfs.UserMode.Tests;

// GitHub issue #3 (zfswin-um.exe at ~35 GB after 15-20 GB written with
// dedup on): the engine's memory is bounded by its ARC ceiling, the ARC
// gives its buffers up when a pool goes, and what the engine took goes
// back to Windows when it stops.  Measured as committed private memory,
// each run in a process of its own (Program.Memory, Program.Cycles).
public sealed class MemoryTests(ITestOutputHelper output) : IDisposable
{
    private const long MiB = 1 << 20;
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private void Report(Child.Result result)
    {
        foreach (var (key, value) in result.Values)
            output.WriteLine(key.EndsWith("errno") || key.EndsWith("ms") ? $"{key} = {value}" : $"{key} = {long.Parse(value) / MiB} MiB");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    public void EngineStaysWithinItsCeilingAndGivesMemoryBack(string dedup)
    {
        // 768 MiB through a 128 MiB ARC: six times the ceiling.
        const int arcMiB = 128, writeMiB = 768;
        var result = Child.RunOk("memory", _scratch.Directory, $"{arcMiB}", $"{writeMiB}", dedup);
        Report(result);
        var baseline = result.Long("private_baseline");
        // The engine before any pool: hash tables sized by the machine's
        // memory (~180 MiB with 128 GB), the same whatever is written.
        var idle = result.Long("private_started");

        // The ceiling was applied, and held (the ARC may overshoot by
        // what is being added while it evicts).
        Assert.Equal(arcMiB * MiB, result.Long("arc_max"));
        Assert.True(result.Long("arc_peak") <= (arcMiB + 48) * MiB,
            $"ARC peaked at {result.Long("arc_peak") / MiB} MiB over a {arcMiB} MiB ceiling");

        // Above the idle engine: the ARC, plus dbufs, dnodes, the dedup
        // table and the dirty data of one sync interval.  Issue #3 was
        // twice the ARC and more; this allows the ARC once plus 160 MiB.
        var growth = result.Long("private_peak") - idle;
        Assert.True(growth <= (arcMiB + 160) * MiB,
            $"peak {growth / MiB} MiB above the idle engine with a {arcMiB} MiB ARC");

        // Export: the pool's buffers leave the ARC (asynchronously, within
        // the 30 s the scenario waits), and the process is back to idle.
        Assert.True(result.Long("arc_after_export") <= 8 * MiB,
            $"ARC still holds {result.Long("arc_after_export") / MiB} MiB {result.Long("export_flush_ms")} ms after export");
        var kept = result.Long("private_after_export") - idle;
        Assert.True(kept <= 48 * MiB,
            $"{kept / MiB} MiB above the idle engine after the pool was exported (peak {growth / MiB} MiB)");

        // Stop: the memory goes back to Windows, not just to the heap.
        Assert.Equal(0, result.Int("stop_errno"));
        var left = result.Long("private_after_stop") - baseline;
        Assert.True(left <= 32 * MiB,
            $"{left / MiB} MiB still committed after the engine stopped");
    }

    [Fact]
    public void RepeatedStartStopDoesNotAccumulate()
    {
        const int cycles = 5;
        var result = Child.RunOk("cycles", _scratch.Directory, $"{cycles}");
        Report(result);
        for (var cycle = 1; cycle <= cycles; cycle++)
            Assert.Equal(0, result.Int($"cycle{cycle}_stop_errno"));
        // The first cycle may keep one-time allocations; after that, none.
        var growth = result.Long($"cycle{cycles}_private") - result.Long("cycle2_private");
        Assert.True(growth <= 16 * MiB,
            $"{growth / MiB} MiB more committed after {cycles - 2} more start/stop cycles");
    }
}

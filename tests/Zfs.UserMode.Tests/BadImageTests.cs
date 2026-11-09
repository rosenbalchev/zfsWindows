using Xunit;

namespace Zfs.UserMode.Tests;

// Images that are not, or no longer, a healthy pool: the engine answers
// with an error and keeps running.  Each in a process of its own, so a
// crash (an assertion in the engine) fails this test and nothing else.
public sealed class BadImageTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData("import-zeroed")]
    [InlineData("import-garbage")]
    [InlineData("import-labels-wiped")]
    public void NotAPoolIsAnError(string scenario)
    {
        var result = Child.RunOk(scenario, _scratch.Directory);
        Assert.NotEqual(0, result.Int("import_errno"));
        Assert.Equal(0, result.Int("stop_errno"));
    }

    [Fact]
    public void TwoDamagedLabelsOfFourStillImport()
    {
        var result = Child.RunOk("import-front-labels-damaged", _scratch.Directory);
        Assert.Equal(0, result.Int("import_errno"));
        Assert.Equal(0, result.Int("read_errno"));
        Assert.Equal(1, result.Int("data_intact"));
    }

    [Fact]
    public void TruncatedImageDoesNotCrashTheEngine()
    {
        // Imported or not, nothing damaged comes back as data.
        var result = Child.RunOk("import-truncated", _scratch.Directory);
        if (result.Int("import_errno") == 0 && result.Int("read_errno") == 0)
            Assert.Equal(1, result.Int("data_intact"));
        Assert.Equal(0, result.Int("stop_errno"));
    }

    [Fact]
    public void ReadOnlyImageFileImportsReadOnlyOnly()
    {
        var result = Child.RunOk("import-readonly-file", _scratch.Directory);
        Assert.NotEqual(0, result.Int("rw_import_errno"));
        Assert.Equal(0, result.Int("import_errno"));
        Assert.Equal(1, result.Int("data_intact"));
        Assert.Equal(1, result.Int("image_unchanged"));
    }
}

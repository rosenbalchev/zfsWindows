using Zfs.Interop;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Sdk;

namespace Zfs.Core.Tests;

public sealed class NativeDeviceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void MissingImageIsRejected()
    {
        if (!NativeLibrary.TryLoad("ZfsCore", out var nativeHandle))
            throw SkipException.ForSkip("Build ZfsCore.dll before running native interop tests.");
        NativeLibrary.Free(nativeHandle);
        Assert.Throws<IOException>(() => ZfsReadOnlyDevice.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".raw")));
    }

    [Fact(Skip = "Requires a genuine OpenZFS image and manifest; set ZFS_TEST_IMAGE before enabling M2.")]
    [Trait("Category", "Integration")]
    public void GenuineImageReadIsNotYetEnabled()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_TEST_IMAGE")!;
        using var device = ZfsReadOnlyDevice.Open(image);
        Assert.True(device.Info.VdevLabelsFound > 0);
    }
}

using Zfs.Interop;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    [Fact]
    [Trait("Category", "Unit")]
    public void RawFileReadsAreBoundedAndDoNotModifyTheImage()
    {
        if (!NativeLibrary.TryLoad("ZfsCore", out var nativeHandle))
            throw SkipException.ForSkip("Build ZfsCore.dll before running native interop tests.");
        NativeLibrary.Free(nativeHandle);

        var path = Path.Combine(Path.GetTempPath(), $"zfs-core-{Guid.NewGuid():N}.raw");
        var expected = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        try
        {
            File.WriteAllBytes(path, expected);
            var before = SHA256.HashData(expected);
            using (var device = ZfsReadOnlyDevice.Open(path))
            {
                Assert.Equal((ulong)expected.Length, device.Info.SizeBytes);
                Assert.Equal(512U, device.Info.LogicalSectorSize);
                Assert.Equal(4096U, device.Info.PhysicalSectorSize);
                Assert.Equal(1U, device.Info.ReadOnly);
                Assert.Equal(0U, device.Info.UberblocksFound);
                Assert.False(device.IsRecognizedPool);
                Assert.Equal(expected[100..300], device.Read(100, 200));
                Assert.Throws<IOException>(() => device.Read((ulong)expected.Length - 4, 8));
            }
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenuineOpenZfsImageIsRecognizedReadOnly()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
            throw SkipException.ForSkip("Set ZFS_TEST_IMAGE to an exported OpenZFS raw image.");
        var before = SHA256.HashData(File.ReadAllBytes(image));
        using var device = ZfsReadOnlyDevice.Open(image);
        Assert.True(device.Info.VdevLabelsFound > 0);
        Assert.True(device.Info.ValidLabelsFound > 0);
        Assert.True(device.Info.UberblocksFound > 0);
        Assert.True(device.Info.PoolGuid > 0);
        Assert.True(device.Info.VdevGuid > 0);
        Assert.InRange(device.Info.Ashift, 9UL, 24UL);
        Assert.Equal(1U, device.Info.MosRootValid);
        Assert.True(device.Info.MosRootSize >= 512);
        Assert.True(device.IsRecognizedPool);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(image)));
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Zfs.Interop;
using Xunit;
using Xunit.Abstractions;

namespace Zfs.Core.Tests;

public sealed class MirrorFixtureTests(ITestOutputHelper output)
{
    [RequiresEnvironmentFact("ZFS_MIRROR_DIRECTORY", "ZFS_MIRROR_MANIFEST")]
    [Trait("Category", "Mirror")]
    [Trait("Category", "Integration")]
    public void GenuineMirrorSupportsHealthyDegradedAndChecksumFallbackReads()
    {
        var directory = Environment.GetEnvironmentVariable("ZFS_MIRROR_DIRECTORY")!;
        var manifestPath = Environment.GetEnvironmentVariable("ZFS_MIRROR_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<MirrorManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Mirror manifest is empty.");
        Assert.Equal("mirror", manifest.Topology);
        Assert.Equal(2, manifest.MemberCount);
        Assert.True(manifest.ReimportVerified);
        Assert.True(manifest.LogicalBytes >= 4L * 1024 * 1024 * 1024);

        var first = Path.Combine(directory, "disk01.raw");
        var second = Path.Combine(directory, "disk02.raw");
        var corrupt = Path.Combine(directory, "disk01-corrupt.raw");
        var missing = Path.Combine(directory, "intentionally-missing.raw");
        Assert.False(File.Exists(missing));
        Assert.True(File.Exists(corrupt));
        var firstExpected = manifest.Images.Single(image => image.Path == "disk01.raw");
        var secondExpected = manifest.Images.Single(image => image.Path == "disk02.raw");
        var firstBefore = VerifyImage(first, firstExpected);
        var secondBefore = VerifyImage(second, secondExpected);

        using (var mirror = ZfsReadOnlyDevice.OpenMirror(first, second))
        {
            AssertMirrorInfo(mirror.Info, manifest, 2);
            VerifyFiles(mirror, manifest.Files);
            Assert.Equal(0U, mirror.RefreshInfo().ChecksumFallbacks);
        }

        using (var degraded = ZfsReadOnlyDevice.OpenMirror(first, missing))
        {
            AssertMirrorInfo(degraded.Info, manifest, 1);
            VerifyFiles(degraded, manifest.Files);
        }
        using (var degraded = ZfsReadOnlyDevice.OpenMirror(missing, second))
        {
            AssertMirrorInfo(degraded.Info, manifest, 1);
            VerifyFiles(degraded, manifest.Files);
        }

        using (var recovered = ZfsReadOnlyDevice.OpenMirror(corrupt, second))
        {
            AssertMirrorInfo(recovered.Info, manifest, 2);
            VerifyFiles(recovered, manifest.Files);
            var info = recovered.RefreshInfo();
            Assert.True(info.ChecksumFallbacks > 0,
                "The corrupt first member did not exercise checksum fallback.");
            output.WriteLine("Checksum/read fallbacks: {0:N0}", info.ChecksumFallbacks);
        }

        Assert.Equal(firstBefore, HashFile(first));
        Assert.Equal(secondBefore, HashFile(second));
    }

    private static void AssertMirrorInfo(ZfsDeviceInfo info, MirrorManifest manifest,
        uint availableMembers)
    {
        Assert.Equal(2U, info.Topology);
        Assert.Equal(2U, info.MemberCount);
        Assert.Equal(availableMembers, info.AvailableMembers);
        Assert.Equal(manifest.PoolGuid, info.PoolGuid);
        Assert.Equal(0U, info.UnsupportedFeatures);
        Assert.Equal(12UL, info.Ashift);
    }

    private static void VerifyFiles(ZfsReadOnlyDevice device, IReadOnlyList<MirrorFile> files)
    {
        Assert.True(device.IsRecognizedPool);
        using var dataset = device.OpenDefaultDataset();
        foreach (var expected in files)
        {
            using var file = dataset.OpenPath(expected.Path);
            Assert.Equal((ulong)expected.Length, file.GetFileSize());
            Assert.Equal(expected.Sha256, HashObject(file));
        }
    }

    private static string VerifyImage(string path, MirrorImage expected)
    {
        Assert.Equal(expected.SizeBytes, new FileInfo(path).Length);
        var hash = HashFile(path);
        Assert.Equal(expected.Sha256, hash);
        return hash;
    }

    private static string HashObject(ZfsObject file)
    {
        using var algorithm = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true))
        {
            file.CopyTo(crypto, 16 * 1024 * 1024);
            crypto.FlushFinalBlock();
        }
        return Convert.ToHexString(algorithm.Hash!);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record MirrorManifest(string Pool, ulong PoolGuid, string ZfsVersion,
        string Topology, int MemberCount, long LogicalBytes, long AllocatedBytes,
        bool ReimportVerified, IReadOnlyList<MirrorImage> Images,
        IReadOnlyList<MirrorFile> Files);
    private sealed record MirrorImage(string Path, long SizeBytes, string Sha256);
    private sealed record MirrorFile(string Path, long Length, string Sha256, string Pattern);
}

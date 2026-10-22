using System.Security.Cryptography;
using System.Text.Json;
using Zfs.Interop;
using Xunit;

namespace Zfs.Core.Tests;

public sealed class SingleDiskFixtureTests
{
    [RequiresEnvironmentFact("ZFS_SINGLE_IMAGE", "ZFS_SINGLE_MANIFEST")]
    [Trait("Category", "Integration")]
    public void GenuineFiveGiBFixtureMatchesManifest()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_SINGLE_IMAGE");
        var manifestPath = Environment.GetEnvironmentVariable("ZFS_SINGLE_MANIFEST");
        Assert.NotNull(image);
        Assert.NotNull(manifestPath);

        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Fixture manifest is empty.");
        Assert.Equal(manifest.Image.SizeBytes, new FileInfo(image).Length);
        var before = HashFile(image);
        Assert.Equal(manifest.Image.Sha256, before);

        using (var device = ZfsReadOnlyDevice.Open(image))
        {
            Assert.True(device.IsRecognizedPool);
            Assert.Equal(manifest.Pool.Guid, device.Info.PoolGuid);
            Assert.Equal(12UL, device.Info.Ashift);
            Assert.Equal(0U, device.Info.UnsupportedFeatures);
            using var dataset = device.OpenDefaultDataset();
            VerifyDirectories(dataset, manifest.Files);

            foreach (var expected in manifest.Files)
            {
                using var file = dataset.OpenPath(expected.Path);
                Assert.Equal(8U, file.Info.Type);
                Assert.Equal((ulong)expected.Length, file.GetFileSize());
                Assert.Equal(expected.Sha256, HashObject(file));
                foreach (var sample in expected.Samples)
                    Assert.Equal(sample.Sha256,
                        Convert.ToHexString(SHA256.HashData(file.Read((ulong)sample.Offset, sample.Length))));
            }
        }

        Assert.Equal(before, HashFile(image));
    }

    private static void VerifyDirectories(ZfsDataset dataset, IReadOnlyList<FileManifest> files)
    {
        var expected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["/"] = new(StringComparer.Ordinal)
        };
        foreach (var file in files)
        {
            var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directory = "/";
            for (var index = 0; index < parts.Length; ++index)
            {
                expected[directory].Add(parts[index]);
                if (index == parts.Length - 1) continue;
                directory = directory == "/" ? '/' + parts[index] : directory + '/' + parts[index];
                expected.TryAdd(directory, new(StringComparer.Ordinal));
            }
        }

        foreach (var pair in expected)
        {
            using var directory = dataset.OpenPath(pair.Key);
            Assert.Equal(4U, directory.Info.Type);
            var actual = directory.Enumerate().Select(entry => entry.Name).ToArray();
            Assert.Equal(pair.Value.Count, actual.Length);
            Assert.Equal(pair.Value.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
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
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record FixtureManifest(PoolManifest Pool, ImageManifest Image,
        IReadOnlyList<FileManifest> Files);
    private sealed record PoolManifest(string Name, ulong Guid);
    private sealed record ImageManifest(string Path, long SizeBytes, string Sha256);
    private sealed record FileManifest(string Path, long Length, string Sha256,
        IReadOnlyList<RangeManifest> Samples);
    private sealed record RangeManifest(long Offset, int Length, string Sha256);
}

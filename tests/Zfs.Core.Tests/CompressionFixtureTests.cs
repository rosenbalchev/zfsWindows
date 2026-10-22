using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Zfs.Interop;
using Xunit;
using Xunit.Abstractions;

namespace Zfs.Core.Tests;

public sealed class CompressionFixtureTests(ITestOutputHelper output)
{
    private static readonly string[] Algorithms =
    [
        "off", "lzjb", "gzip-1", "gzip-2", "gzip-3", "gzip-4", "gzip-5",
        "gzip-6", "gzip-7", "gzip-8", "gzip-9", "zle", "lz4", "zstd"
    ];

    [RequiresEnvironmentFact("ZFS_COMPRESSION_IMAGES", "ZFS_COMPRESSION_MANIFESTS")]
    [Trait("Category", "Integration")]
    public void GenuineOpenZfsCompressionMatrixReadsExactly()
    {
        var imageDirectory = Environment.GetEnvironmentVariable("ZFS_COMPRESSION_IMAGES")!;
        var manifestDirectory = Environment.GetEnvironmentVariable("ZFS_COMPRESSION_MANIFESTS")!;
        long totalBytes = 0;
        var totalTimer = Stopwatch.StartNew();

        foreach (var algorithm in Algorithms)
        {
            var image = Path.Combine(imageDirectory, algorithm + ".raw");
            var manifestPath = Path.Combine(manifestDirectory, algorithm + ".json");
            Assert.True(File.Exists(image), $"Missing image: {image}");
            Assert.True(File.Exists(manifestPath), $"Missing manifest: {manifestPath}");
            var manifest = JsonSerializer.Deserialize<CompressionManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                throw new InvalidDataException($"Fixture manifest is empty: {manifestPath}");

            Assert.Equal(algorithm, manifest.InitialCompression);
            Assert.Equal(512L * 1024 * 1024, manifest.ImageSizeBytes);
            Assert.Equal(manifest.ImageSizeBytes, new FileInfo(image).Length);
            Assert.True(manifest.LogicalUsedBytes > 0);
            Assert.True(manifest.PhysicalUsedBytes > 0);
            Assert.EndsWith("x", manifest.CompressionRatio);
            Assert.Equal(4, manifest.Files.Count);
            Assert.Contains(manifest.Files, file => file.Compression == manifest.ChangedCompression);
            Assert.All(manifest.Files, file => Assert.True(file.AllocatedBytes > 0));
            if (algorithm != "off")
                Assert.True(manifest.Files.Single(file => file.Path == "/highly.bin").AllocatedBytes < 1024 * 1024);

            var before = HashFile(image);
            Assert.Equal(manifest.ImageSha256, before);
            var readTimer = Stopwatch.StartNew();
            using (var device = ZfsReadOnlyDevice.Open(image))
            {
                Assert.True(device.IsRecognizedPool);
                Assert.Equal(manifest.PoolGuid, device.Info.PoolGuid);
                Assert.Equal(0U, device.Info.UnsupportedFeatures);
                using var dataset = device.OpenDefaultDataset();
                foreach (var expected in manifest.Files)
                {
                    using var file = dataset.OpenPath(expected.Path);
                    Assert.Equal(8U, file.Info.Type);
                    Assert.Equal((ulong)expected.Length, file.GetFileSize());
                    Assert.Equal(expected.Sha256, HashObject(file));
                    totalBytes += expected.Length;
                }
            }
            readTimer.Stop();
            Assert.Equal(before, HashFile(image));
            var fileBytes = manifest.Files.Sum(file => file.Length);
            output.WriteLine("{0,-7} {1,7:F1} MiB/s, ratio {2}, physical {3:N0} bytes",
                algorithm, fileBytes / 1048576d / readTimer.Elapsed.TotalSeconds,
                manifest.CompressionRatio, manifest.PhysicalUsedBytes);
        }

        totalTimer.Stop();
        output.WriteLine("Matrix: {0:F1} MiB at {1:F1} MiB/s",
            totalBytes / 1048576d, totalBytes / 1048576d / totalTimer.Elapsed.TotalSeconds);
    }

    private static string HashObject(ZfsObject file)
    {
        using var algorithm = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true))
        {
            file.CopyTo(crypto, 4 * 1024 * 1024);
            crypto.FlushFinalBlock();
        }
        return Convert.ToHexString(algorithm.Hash!);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record CompressionManifest(string Pool, ulong PoolGuid, string ZfsVersion,
        string InitialCompression, string ChangedCompression, long LogicalUsedBytes,
        long PhysicalUsedBytes, string CompressionRatio, long ImageSizeBytes,
        string ImageSha256, IReadOnlyList<CompressionFile> Files);
    private sealed record CompressionFile(string Path, long Length, string Sha256, string Pattern,
        string Compression, long AllocatedBytes, double WriteMiBPerSecond);
}

using System.Security.Cryptography;
using System.Text.Json;
using Zfs.Interop;
using Xunit;

namespace Zfs.Core.Tests;

public sealed class SnapshotFixtureTests
{
    [RequiresEnvironmentFact("ZFS_SNAPSHOT_IMAGE", "ZFS_SNAPSHOT_MANIFEST")]
    [Trait("Category", "Snapshot")]
    [Trait("Category", "Integration")]
    public void GenuineSnapshotFixtureSupportsDiscoveryAndHistoricalReads()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_SNAPSHOT_IMAGE")!;
        var manifestPath = Environment.GetEnvironmentVariable("ZFS_SNAPSHOT_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Snapshot manifest is empty.");

        Assert.True(manifest.ReimportVerified);
        Assert.True(manifest.RollbackVerified);
        Assert.True(manifest.DeletionVerified);
        Assert.Equal(manifest.ImageSizeBytes, new FileInfo(image).Length);
        var before = HashFile(image);
        Assert.Equal(manifest.ImageSha256, before);

        using (var device = ZfsReadOnlyDevice.Open(image))
        {
            Assert.True(device.IsRecognizedPool);
            Assert.Equal(manifest.PoolGuid, device.Info.PoolGuid);

            var snapshots = device.GetSnapshots().OrderBy(snapshot => snapshot.Name,
                StringComparer.Ordinal).ToArray();
            Assert.Equal(manifest.Snapshots.Select(state => state.Name),
                snapshots.Select(snapshot => snapshot.Name));
            Assert.Equal(snapshots.Length,
                snapshots.Select(snapshot => snapshot.DatasetObject).Distinct().Count());
            Assert.Equal(snapshots.Length,
                snapshots.Select(snapshot => snapshot.Guid).Distinct().Count());
            Assert.True(snapshots.Select(snapshot => snapshot.CreationTxg)
                .SequenceEqual(snapshots.Select(snapshot => snapshot.CreationTxg).Order()));

            using (var current = device.OpenDefaultDataset())
                VerifyState(current, manifest.Current);

            foreach (var expected in manifest.Snapshots)
            {
                using var snapshot = device.OpenSnapshot(expected.Name);
                VerifyState(snapshot, expected);
            }

            Assert.Throws<IOException>(() => device.OpenSnapshot("missing-snapshot"));
        }

        Assert.Equal(before, HashFile(image));
    }

    private static void VerifyState(ZfsDataset dataset, StateManifest state)
    {
        var expectedDirectories = state.Directories.ToDictionary(path => path,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Contains("/", expectedDirectories.Keys);
        foreach (var directory in state.Directories.Where(path => path != "/"))
        {
            var separator = directory.LastIndexOf('/');
            var parent = separator == 0 ? "/" : directory[..separator];
            expectedDirectories[parent].Add(directory[(separator + 1)..]);
        }
        foreach (var file in state.Files)
        {
            var components = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directory = "/";
            for (var index = 0; index < components.Length; ++index)
            {
                expectedDirectories[directory].Add(components[index]);
                if (index == components.Length - 1) continue;
                directory = directory == "/" ? '/' + components[index] :
                    directory + '/' + components[index];
                Assert.Contains(directory, expectedDirectories.Keys);
            }
        }

        foreach (var expected in expectedDirectories)
        {
            using var directory = dataset.OpenPath(expected.Key);
            Assert.Equal(4U, directory.Info.Type);
            var actual = directory.Enumerate().Select(entry => entry.Name)
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Value.Order(StringComparer.Ordinal), actual);
        }

        foreach (var expected in state.Files)
        {
            using var file = dataset.OpenPath(expected.Path);
            Assert.Equal(8U, file.Info.Type);
            Assert.Equal((ulong)expected.Length, file.GetFileSize());
            Assert.Equal(expected.Sha256, HashObject(file));
        }
    }

    private static string HashObject(ZfsObject file)
    {
        using var algorithm = SHA256.Create();
        using (var output = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write,
                   leaveOpen: true))
        {
            file.CopyTo(output);
            output.FlushFinalBlock();
        }
        return Convert.ToHexString(algorithm.Hash!);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record SnapshotManifest(string Pool, ulong PoolGuid, string ZfsVersion,
        long ImageSizeBytes, string ImageSha256, bool ReimportVerified,
        bool RollbackVerified, bool DeletionVerified, StateManifest Current,
        IReadOnlyList<StateManifest> Snapshots);
    private sealed record StateManifest(string Name, IReadOnlyList<string> Directories,
        IReadOnlyList<FileManifest> Files);
    private sealed record FileManifest(string Path, long Length, string Sha256);
}

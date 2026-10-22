using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Zfs.Interop;
using Xunit;
using Xunit.Abstractions;

namespace Zfs.Core.Tests;

public sealed class ScaleFixtureTests(ITestOutputHelper output)
{
    [RequiresEnvironmentFact("ZFS_SCALE_IMAGE", "ZFS_SCALE_MANIFEST", "ZFS_SCALE_SUMMARY")]
    [Trait("Category", "Stress")]
    public void ScaledSmallFilesEnumerateOpenAndReadExactly()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_SCALE_IMAGE")!;
        var manifestPath = Environment.GetEnvironmentVariable("ZFS_SCALE_MANIFEST")!;
        var summaryPath = Environment.GetEnvironmentVariable("ZFS_SCALE_SUMMARY")!;
        var summary = JsonSerializer.Deserialize<ScaleSummary>(File.ReadAllText(summaryPath),
            JsonOptions) ?? throw new InvalidDataException("Scale summary is empty.");
        Assert.True(summary.ReimportVerified);
        Assert.Equal(summary.ImageSizeBytes, new FileInfo(image).Length);
        Assert.Equal(summary.FileCount, File.ReadLines(manifestPath).Count());

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var startingWorkingSet = process.WorkingSet64;
        var before = HashFile(image);
        Assert.Equal(summary.ImageSha256, before);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var latencies = new List<double>(summary.FileCount);
        long contentBytes = 0;
        var verifyTimer = Stopwatch.StartNew();

        using (var device = ZfsReadOnlyDevice.Open(image))
        {
            Assert.True(device.IsRecognizedPool);
            Assert.Equal(summary.PoolGuid, device.Info.PoolGuid);
            Assert.Equal(0U, device.Info.UnsupportedFeatures);
            using var dataset = device.OpenDefaultDataset();
            foreach (var line in File.ReadLines(manifestPath))
            {
                var expected = JsonSerializer.Deserialize<FileEntry>(line, JsonOptions) ??
                    throw new InvalidDataException("Invalid scale manifest line.");
                Assert.True(expectedPaths.Add(expected.Path), $"Duplicate manifest path: {expected.Path}");
                var timer = Stopwatch.GetTimestamp();
                using var file = dataset.OpenPath(expected.Path);
                Assert.Equal(8U, file.Info.Type);
                Assert.Equal((ulong)expected.Length, file.GetFileSize());
                Assert.Equal(expected.Sha256, HashObject(file));
                latencies.Add(Stopwatch.GetElapsedTime(timer).TotalMilliseconds);
                contentBytes += expected.Length;
            }

            var directories = new List<string>();
            var actualPaths = EnumerateTree(dataset, directories);
            Assert.Equal(summary.FileCount, actualPaths.Count);
            Assert.True(expectedPaths.SetEquals(actualPaths), "Enumerated paths differ from the manifest.");

            var expectedEntries = CountEntries(dataset, directories);
            var concurrentEntries = new ConcurrentBag<int>();
            Parallel.ForEach(Partitioner.Create(0, directories.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                () => (Device: ZfsReadOnlyDevice.Open(image), Dataset: (ZfsDataset?)null, Count: 0),
                (range, _, state) =>
                {
                    state.Dataset ??= state.Device.OpenDefaultDataset();
                    for (var index = range.Item1; index < range.Item2; ++index)
                    {
                        using var directory = state.Dataset.OpenPath(directories[index]);
                        state.Count += directory.Enumerate().Count;
                    }
                    return state;
                },
                state =>
                {
                    concurrentEntries.Add(state.Count);
                    state.Dataset?.Dispose();
                    state.Device.Dispose();
                });
            Assert.Equal(expectedEntries, concurrentEntries.Sum());

            foreach (var expected in ReadRandomSample(manifestPath, 1_000, 0x5CA1E))
            {
                using var first = dataset.OpenPath(expected.Path);
                Assert.Equal(expected.Sha256, HashObject(first));
                using var second = dataset.OpenPath(expected.Path);
                Assert.Equal((ulong)expected.Length, second.GetFileSize());
            }
        }

        verifyTimer.Stop();
        Assert.Equal(before, HashFile(image));
        process.Refresh();
        latencies.Sort();
        output.WriteLine("{0}: {1:N0} files, {2:N0} directories, {3:N0} content bytes",
            summary.Profile, summary.FileCount, summary.DirectoryCount, contentBytes);
        output.WriteLine("Windows initial traversal (cache state uncontrolled): {0:F1} ops/s; latency p50={1:F3} ms p95={2:F3} ms p99={3:F3} ms",
            summary.FileCount / verifyTimer.Elapsed.TotalSeconds,
            Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99));
        output.WriteLine("Working set delta: {0:N0} bytes; Linux create={1:F1} ops/s, re-import verify={2:F1} ops/s; allocated={3:N0} bytes",
            process.WorkingSet64 - startingWorkingSet, summary.CreateOperationsPerSecond,
            summary.VerifyOperationsPerSecond, summary.AllocatedBytes);
    }

    private static HashSet<string> EnumerateTree(ZfsDataset dataset, List<string> directories)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push("/");
        while (pending.TryPop(out var path))
        {
            directories.Add(path);
            using var directory = dataset.OpenPath(path);
            foreach (var entry in directory.Enumerate())
            {
                var child = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;
                if (entry.Type == 4) pending.Push(child);
                else Assert.True(files.Add(child), $"Duplicate directory entry: {child}");
            }
        }
        return files;
    }

    private static int CountEntries(ZfsDataset dataset, IEnumerable<string> directories)
    {
        var count = 0;
        foreach (var path in directories)
        {
            using var directory = dataset.OpenPath(path);
            count += directory.Enumerate().Count;
        }
        return count;
    }

    private static IReadOnlyList<FileEntry> ReadRandomSample(string manifestPath, int count, int seed)
    {
        var random = new Random(seed);
        var sample = new List<FileEntry>(count);
        var seen = 0;
        foreach (var line in File.ReadLines(manifestPath))
        {
            var item = JsonSerializer.Deserialize<FileEntry>(line, JsonOptions)!;
            ++seen;
            if (sample.Count < count) sample.Add(item);
            else
            {
                var replacement = random.Next(seen);
                if (replacement < count) sample[replacement] = item;
            }
        }
        return sample;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1,
            Math.Ceiling(sorted.Count * percentile) - 1)];

    private static string HashObject(ZfsObject file)
    {
        using var algorithm = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true))
        {
            file.CopyTo(crypto, 4096);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record FileEntry(string Path, int Length, string Sha256);
    private sealed record ScaleSummary(string Profile, int FileCount, int Fanout, int Depth,
        int DirectoryCount, int EmptyFileCount, long LogicalBytes, long AllocatedBytes,
        long ImageSizeBytes, string ImageSha256, string Pool, ulong PoolGuid,
        string ZfsVersion, bool ReimportVerified, double CreateSeconds, double VerifySeconds,
        double CreateOperationsPerSecond, double VerifyOperationsPerSecond,
        long PeakWorkingSetBytes);
}

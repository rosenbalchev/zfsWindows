using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Zfs.Interop;

namespace Zfs.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ReadBenchmarks
{
    private ZfsReadOnlyDevice _device = null!;
    private ZfsDataset _dataset = null!;
    private string _samplePath = null!;

    [GlobalSetup]
    public void Setup()
    {
        var image = Environment.GetEnvironmentVariable("ZFS_SCALE_IMAGE") ??
            throw new InvalidOperationException("Set ZFS_SCALE_IMAGE.");
        var manifest = Environment.GetEnvironmentVariable("ZFS_SCALE_MANIFEST") ??
            throw new InvalidOperationException("Set ZFS_SCALE_MANIFEST.");
        var first = JsonSerializer.Deserialize<FileEntry>(File.ReadLines(manifest).First(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Scale manifest is empty.");
        _samplePath = first.Path;
        _device = ZfsReadOnlyDevice.Open(image);
        _dataset = _device.OpenDefaultDataset();
    }

    [Benchmark]
    public int EnumerateRoot()
    {
        using var root = _dataset.OpenRoot();
        return root.Enumerate().Count;
    }

    [Benchmark]
    public int OpenAndReadSmallFile()
    {
        using var file = _dataset.OpenPath(_samplePath);
        return file.ReadAllBytes().Length;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.Dispose();
        _device.Dispose();
    }

    private sealed record FileEntry(string Path, int Length, string Sha256);
}

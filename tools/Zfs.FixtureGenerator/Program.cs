using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const long GiB = 1024L * 1024 * 1024;
const int BufferSize = 1024 * 1024;

if (!OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("Fixture generation requires Linux with OpenZFS tools.");
if (Environment.UserName != "root")
    throw new InvalidOperationException("Fixture generation must run as root for zpool/zfs.");
if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: Zfs.FixtureGenerator <image-path> <manifest-path> [pool-name]");

var imagePath = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var poolName = args.Length == 3 ? args[2] : "zfswin_single";
var workRoot = Path.Combine(Path.GetDirectoryName(imagePath)!, "generator-work");
var mountPath = Path.Combine(workRoot, "mnt");

Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
Directory.CreateDirectory(mountPath);
if (File.Exists(imagePath)) throw new IOException($"Image already exists: {imagePath}");

var specs = new[]
{
    new FileSpec("documents/empty.txt", 0, DataKind.Zero, 1),
    new FileSpec("documents/one-byte.txt", 1, DataKind.Repeating, 2),
    new FileSpec("binary/512-bytes.bin", 512, DataKind.Random, 3),
    new FileSpec("binary/4-kib.bin", 4 * 1024, DataKind.Random, 4),
    new FileSpec("binary/64-kib.bin", 64 * 1024, DataKind.Random, 5),
    new FileSpec("images/1-mib.bin", 1024 * 1024, DataKind.Random, 6),
    new FileSpec("compressed/16-mib-repeat.bin", 16L * 1024 * 1024, DataKind.Repeating, 7),
    new FileSpec("random/128-mib-random.bin", 128L * 1024 * 1024, DataKind.Random, 8),
    new FileSpec("large/512-mib-random.bin", 512L * 1024 * 1024, DataKind.Random, 9),
    new FileSpec("large/512-mib-repeat-01.bin", 512L * 1024 * 1024, DataKind.Repeating, 10),
    new FileSpec("large/512-mib-repeat-02.bin", 512L * 1024 * 1024, DataKind.Repeating, 11),
    new FileSpec("large/512-mib-repeat-03.bin", 512L * 1024 * 1024, DataKind.Repeating, 12),
    new FileSpec("large/512-mib-repeat-04.bin", 512L * 1024 * 1024, DataKind.Repeating, 13),
    new FileSpec("large/512-mib-repeat-05.bin", 512L * 1024 * 1024, DataKind.Repeating, 14),
    new FileSpec("large/512-mib-repeat-06.bin", 512L * 1024 * 1024, DataKind.Repeating, 15),
    new FileSpec("large/512-mib-repeat-07.bin", 512L * 1024 * 1024, DataKind.Repeating, 16),
    new FileSpec("documents/Grüße-東京-📦.txt", 64 * 1024, DataKind.Text, 17),
    new FileSpec("nested/l01/l02/l03/l04/l05/l06/l07/l08/deep.txt", 4 * 1024, DataKind.Text, 18),
};

var logicalBytes = specs.Sum(spec => spec.Length);
if (logicalBytes < 4L * GiB)
    throw new InvalidOperationException("The single-disk workload must contain at least 4 GiB logical data.");

var poolCreated = false;
try
{
    using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        image.SetLength(5L * GiB);

    Run("zpool", "create", "-f", "-o", "ashift=12", "-O", "mountpoint=none", poolName, imagePath);
    poolCreated = true;
    Run("zfs", "create", "-o", $"mountpoint={mountPath}", "-o", "compression=lz4",
        "-o", "recordsize=128K", $"{poolName}/data");

    var files = new List<FileManifest>(specs.Length);
    foreach (var spec in specs)
    {
        var target = Path.Combine(mountPath, spec.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var hash = WriteFile(target, spec);
        files.Add(new FileManifest('/' + spec.Path, spec.Length, hash, spec.Kind.ToString().ToLowerInvariant(),
            CreateSamples(target, spec.Length)));
        Console.WriteLine($"{spec.Length,12} {hash} /{spec.Path}");
    }

    Run("sync");
    var guid = ulong.Parse(Run("zpool", "get", "-H", "-o", "value", "guid", poolName).Trim());
    var features = Run("zpool", "get", "-H", "-o", "property,value", "all", poolName)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t'))
        .Where(parts => parts.Length == 2 && parts[0].StartsWith("feature@", StringComparison.Ordinal) &&
            parts[1] is "active" or "enabled")
        .ToDictionary(parts => parts[0][8..], parts => parts[1], StringComparer.Ordinal);
    var zfsVersion = Run("zfs", "--version").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

    Run("zpool", "export", poolName);
    poolCreated = false;

    var imageHash = HashFile(imagePath);
    var manifest = new FixtureManifest(
        new PoolManifest(poolName, guid, zfsVersion, features, new[] { $"{poolName}/data" }),
        new ImageManifest(Path.GetFileName(imagePath), 5L * GiB, imageHash),
        files, Array.Empty<string>(), logicalBytes, "lz4", 0x5A465357494E3035UL);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonContext.Default.FixtureManifest) + "\n");
    Console.WriteLine($"Image SHA-256: {imageHash}");
    Console.WriteLine($"Logical bytes: {logicalBytes}");
}
finally
{
    if (poolCreated)
    {
        try { Run("zpool", "export", poolName); }
        catch { Run("zpool", "destroy", "-f", poolName); }
    }
}

static string WriteFile(string path, FileSpec spec)
{
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        BufferSize, FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[BufferSize];
    var random = new XorShift64Star(0x5A465357494E3035UL ^ spec.Seed);
    long remaining = spec.Length;
    long position = 0;
    while (remaining > 0)
    {
        var count = (int)Math.Min(buffer.Length, remaining);
        Fill(buffer.AsSpan(0, count), spec.Kind, random, position, spec.Path);
        output.Write(buffer, 0, count);
        hash.AppendData(buffer, 0, count);
        remaining -= count;
        position += count;
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static void Fill(Span<byte> destination, DataKind kind, XorShift64Star random,
    long position, string path)
{
    switch (kind)
    {
        case DataKind.Zero:
            destination.Clear();
            break;
        case DataKind.Random:
            random.Fill(destination);
            break;
        case DataKind.Repeating:
            for (var index = 0; index < destination.Length; ++index)
                destination[index] = (byte)(((position + index) * 31 + 17) % 251);
            break;
        case DataKind.Text:
            var text = Encoding.UTF8.GetBytes($"ZFS Windows fixture | {path} | deterministic UTF-8 | Καλημέρα | 東京 |\n");
            for (var index = 0; index < destination.Length; ++index)
                destination[index] = text[(position + index) % text.Length];
            break;
    }
}

static IReadOnlyList<RangeManifest> CreateSamples(string path, long length)
{
    if (length == 0) return Array.Empty<RangeManifest>();
    var sampleLength = (int)Math.Min(4096, length);
    var offsets = new[] { 0L, Math.Max(0, length / 2 - sampleLength / 2), Math.Max(0, length - sampleLength) }
        .Distinct().Order().ToArray();
    using var stream = File.OpenRead(path);
    var buffer = new byte[sampleLength];
    var samples = new List<RangeManifest>(offsets.Length);
    foreach (var offset in offsets)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer);
        samples.Add(new RangeManifest(offset, sampleLength,
            Convert.ToHexString(SHA256.HashData(buffer))));
    }
    return samples;
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true,
        RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new IOException($"{executable} exited with {process.ExitCode}: {stderr.Trim()}");
    return stdout;
}

enum DataKind { Zero, Random, Repeating, Text }
sealed record FileSpec(string Path, long Length, DataKind Kind, ulong Seed);
sealed record RangeManifest(long Offset, int Length, string Sha256);
sealed record FileManifest(string Path, long Length, string Sha256, string Pattern,
    IReadOnlyList<RangeManifest> Samples);
sealed record PoolManifest(string Name, ulong Guid, string ZfsVersion,
    IReadOnlyDictionary<string, string> Features, IReadOnlyList<string> Datasets);
sealed record ImageManifest(string Path, long SizeBytes, string Sha256);
sealed record FixtureManifest(PoolManifest Pool, ImageManifest Image,
    IReadOnlyList<FileManifest> Files, IReadOnlyList<string> Snapshots,
    long LogicalBytes, string Compression, ulong Seed);

sealed class XorShift64Star(ulong state)
{
    private ulong _state = state == 0 ? 1 : state;

    public void Fill(Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var value = Next();
            for (var index = 0; index < sizeof(ulong) && offset < destination.Length; ++index)
            {
                destination[offset++] = (byte)value;
                value >>= 8;
            }
        }
    }

    private ulong Next()
    {
        var value = _state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        _state = value;
        return value * 2685821657736338717UL;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FixtureManifest))]
partial class JsonContext : JsonSerializerContext;

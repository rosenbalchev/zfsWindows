using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

const int MiB = 1024 * 1024;
if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
    throw new PlatformNotSupportedException("Run as root on Linux with OpenZFS.");
if (args.Length != 4)
    throw new ArgumentException("Usage: <image> <manifest> <pool> <compression>");

var imagePath = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var pool = args[2];
var compression = args[3];
var changedCompression = compression == "gzip-1" ? "lz4" : "gzip-1";
var mount = Path.Combine(Path.GetDirectoryName(imagePath)!, "mnt-" + compression);
Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
Directory.CreateDirectory(mount);
if (File.Exists(imagePath)) throw new IOException($"Image already exists: {imagePath}");

var specs = new[]
{
    new FileSpec("highly.bin", 8 * MiB, Pattern.Highly, 1, compression),
    new FileSpec("moderate.bin", 8 * MiB, Pattern.Moderate, 2, compression),
    new FileSpec("random.bin", 8 * MiB, Pattern.Random, 3, compression),
};
var created = false;
try
{
    using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
        image.SetLength(512L * MiB);
    Run("zpool", "create", "-f", "-o", "ashift=12", "-O", "mountpoint=none", pool, imagePath);
    created = true;
    Run("zfs", "create", "-o", $"mountpoint={mount}", "-o", $"compression={compression}",
        "-o", "recordsize=128K", $"{pool}/data");

    var files = new List<FileResult>();
    foreach (var spec in specs) files.Add(Write(mount, spec));
    Run("zfs", "set", $"compression={changedCompression}", $"{pool}/data");
    files.Add(Write(mount, new FileSpec("property-change/after.bin", 8 * MiB,
        Pattern.Highly, 4, changedCompression)));
    Run("sync");

    var logicalUsed = long.Parse(Run("zfs", "get", "-Hp", "-o", "value", "logicalused", $"{pool}/data").Trim());
    var physicalUsed = long.Parse(Run("zfs", "get", "-Hp", "-o", "value", "used", $"{pool}/data").Trim());
    var ratio = Run("zfs", "get", "-H", "-o", "value", "compressratio", $"{pool}/data").Trim();
    var guid = ulong.Parse(Run("zpool", "get", "-H", "-o", "value", "guid", pool).Trim());
    var version = Run("zfs", "--version").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
    Run("zpool", "export", pool);
    created = false;

    var imageHash = HashFile(imagePath);
    var result = new Manifest(pool, guid, version, compression, changedCompression,
        logicalUsed, physicalUsed, ratio, 512L * MiB, imageHash, files);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
    Console.WriteLine($"{compression}: image={imageHash} logical={logicalUsed} physical={physicalUsed} ratio={ratio}");
}
finally
{
    if (created)
    {
        try { Run("zpool", "export", pool); }
        catch { Run("zpool", "destroy", "-f", pool); }
    }
}

static FileResult Write(string mount, FileSpec spec)
{
    var path = Path.Combine(mount, spec.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var timer = Stopwatch.StartNew();
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, MiB);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[MiB];
    var random = new Prng(0x434F4D5052455353UL ^ spec.Seed);
    long position = 0;
    while (position < spec.Length)
    {
        var count = (int)Math.Min(buffer.Length, spec.Length - position);
        Fill(buffer.AsSpan(0, count), spec.Pattern, random, position);
        output.Write(buffer, 0, count);
        hash.AppendData(buffer, 0, count);
        position += count;
    }
    output.Flush(true);
    timer.Stop();
    var allocated = long.Parse(Run("stat", "-c", "%b", path).Trim()) * 512;
    return new('/' + spec.Path, spec.Length, Convert.ToHexString(hash.GetHashAndReset()),
        spec.Pattern.ToString().ToLowerInvariant(), spec.Compression, allocated,
        spec.Length / MiB / timer.Elapsed.TotalSeconds);
}

static void Fill(Span<byte> bytes, Pattern pattern, Prng random, long position)
{
    if (pattern == Pattern.Random) { random.Fill(bytes); return; }
    for (var index = 0; index < bytes.Length; ++index)
    {
        var absolute = position + index;
        bytes[index] = pattern == Pattern.Highly ? (byte)('A' + (absolute / 4096) % 3) :
            absolute % 64 < 48 ? (byte)('a' + absolute % 23) : random.NextByte();
    }
}

static string Run(string command, params string[] arguments)
{
    var start = new ProcessStartInfo(command) { RedirectStandardOutput = true,
        RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException($"Could not start {command}.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new IOException($"{command}: {error.Trim()}");
    return output;
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

enum Pattern { Highly, Moderate, Random }
sealed record FileSpec(string Path, long Length, Pattern Pattern, ulong Seed, string Compression);
sealed record FileResult(string Path, long Length, string Sha256, string Pattern,
    string Compression, long AllocatedBytes, double WriteMiBPerSecond);
sealed record Manifest(string Pool, ulong PoolGuid, string ZfsVersion, string InitialCompression,
    string ChangedCompression, long LogicalUsedBytes, long PhysicalUsedBytes,
    string CompressionRatio, long ImageSizeBytes, string ImageSha256,
    IReadOnlyList<FileResult> Files);

sealed class Prng(ulong state)
{
    private ulong _state = state;
    public byte NextByte() => (byte)Next();
    public void Fill(Span<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var value = Next();
            for (var index = 0; index < 8 && offset < bytes.Length; ++index)
            { bytes[offset++] = (byte)value; value >>= 8; }
        }
    }
    private ulong Next()
    {
        var value = _state;
        value ^= value >> 12; value ^= value << 25; value ^= value >> 27;
        _state = value;
        return value * 2685821657736338717UL;
    }
}

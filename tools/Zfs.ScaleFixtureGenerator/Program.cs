using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
    throw new PlatformNotSupportedException("Run as root on Linux with OpenZFS.");
if (args.Length is < 5 or > 7)
    throw new ArgumentException("Usage: <image> <manifest.jsonl> <summary.json> <pool> <profile> [fanout] [depth]");

var profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase)
{
    ["small"] = new("Small", 5_000, 512L << 20, 10, 3),
    ["medium"] = new("Medium", 50_000, 2L << 30, 10, 3),
    ["large"] = new("Large", 500_000, 32L << 30, 10, 4),
    ["extreme"] = new("Extreme", 5_000_000, 256L << 30, 10, 5),
};
if (!profiles.TryGetValue(args[4], out var profile))
    throw new ArgumentException($"Unknown profile: {args[4]}");
var fanout = args.Length >= 6 ? int.Parse(args[5]) : profile.Fanout;
var depth = args.Length >= 7 ? int.Parse(args[6]) : profile.Depth;
if (fanout is < 2 or > 100 || depth is < 1 or > 6)
    throw new ArgumentOutOfRangeException(nameof(args), "Fan-out must be 2-100 and depth 1-6.");

var imagePath = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var summaryPath = Path.GetFullPath(args[2]);
var pool = args[3];
var mount = Path.Combine(Path.GetDirectoryName(imagePath)!, "mnt-" + pool);
var lineOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
};
var summaryOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
Directory.CreateDirectory(mount);
if (File.Exists(imagePath) || File.Exists(manifestPath) || File.Exists(summaryPath))
    throw new IOException("Image, manifest, and summary outputs must not already exist.");

var created = false;
var createTimer = Stopwatch.StartNew();
var process = Process.GetCurrentProcess();
long peakWorkingSet = process.WorkingSet64;
try
{
    using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
        image.SetLength(profile.ImageBytes);
    Run("zpool", "create", "-f", "-o", "ashift=12", "-O", "mountpoint=none", pool, imagePath);
    created = true;
    Run("zfs", "create", "-o", $"mountpoint={mount}", "-o", "compression=lz4",
        "-o", "recordsize=16K", $"{pool}/data");

    long logicalBytes = 0;
    var emptyFiles = 0;
    string? currentDirectory = null;
    using (var manifest = new StreamWriter(manifestPath, false, new UTF8Encoding(false), 1 << 20))
    {
        for (var index = 0; index < profile.FileCount; ++index)
        {
            var relativeDirectory = DirectoryFor(index, profile.FileCount, fanout, depth);
            if (!StringComparer.Ordinal.Equals(relativeDirectory, currentDirectory))
            {
                Directory.CreateDirectory(Path.Combine(mount, relativeDirectory));
                currentDirectory = relativeDirectory;
            }
            var name = FileNameFor(index);
            var relativePath = relativeDirectory.Replace('\\', '/') + '/' + name;
            var content = ContentFor(index);
            File.WriteAllBytes(Path.Combine(mount, relativeDirectory, name), content);
            var entry = new FileEntry('/' + relativePath, content.Length,
                Convert.ToHexString(SHA256.HashData(content)));
            manifest.WriteLine(JsonSerializer.Serialize(entry, lineOptions));
            logicalBytes += content.Length;
            if (content.Length == 0) ++emptyFiles;
            if ((index + 1) % 10_000 == 0)
            {
                manifest.Flush();
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
                Console.WriteLine($"created {index + 1:N0}/{profile.FileCount:N0}");
            }
        }
    }
    Run("sync");
    createTimer.Stop();
    var allocated = long.Parse(Run("zfs", "get", "-Hp", "-o", "value", "used", $"{pool}/data").Trim());
    var guid = ulong.Parse(Run("zpool", "get", "-H", "-o", "value", "guid", pool).Trim());
    var version = Run("zfs", "--version").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

    Run("zpool", "export", pool);
    created = false;
    Run("zpool", "import", "-d", Path.GetDirectoryName(imagePath)!, pool);
    created = true;
    var verifyTimer = Stopwatch.StartNew();
    var verified = Verify(mount, manifestPath, lineOptions);
    verifyTimer.Stop();
    if (verified != profile.FileCount)
        throw new InvalidDataException($"Verified {verified:N0}, expected {profile.FileCount:N0} files.");
    Run("zpool", "export", pool);
    created = false;

    process.Refresh();
    peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
    var imageHash = HashFile(imagePath);
    var summary = new Summary(profile.Name, profile.FileCount, fanout, depth,
        DirectoryCount(fanout, depth), emptyFiles, logicalBytes, allocated,
        profile.ImageBytes, imageHash, pool, guid, version, true,
        createTimer.Elapsed.TotalSeconds, verifyTimer.Elapsed.TotalSeconds,
        profile.FileCount / createTimer.Elapsed.TotalSeconds,
        profile.FileCount / verifyTimer.Elapsed.TotalSeconds, peakWorkingSet);
    File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, summaryOptions) + "\n");
    Console.WriteLine($"{profile.Name}: {profile.FileCount:N0} files, create {summary.CreateOperationsPerSecond:N1}/s, " +
        $"verify {summary.VerifyOperationsPerSecond:N1}/s, image {imageHash}");
}
finally
{
    if (created)
    {
        try { Run("zpool", "export", pool); }
        catch { Run("zpool", "destroy", "-f", pool); }
    }
}

static string DirectoryFor(int index, int fileCount, int fanout, int depth)
{
    var leafCount = 1;
    for (var level = 0; level < depth; ++level) leafCount = checked(leafCount * fanout);
    var leaf = (long)index * leafCount / fileCount;
    var components = new string[depth];
    for (var level = depth - 1; level >= 0; --level)
    {
        components[level] = $"d{level}-{leaf % fanout:D2}";
        leaf /= fanout;
    }
    return Path.Combine(components);
}

static string FileNameFor(int index)
{
    var pair = index / 16;
    return (index % 16) switch
    {
        0 => $"CasePair-{pair:D8}.bin",
        1 => $"casepair-{pair:D8}.bin",
        2 => $"unicode-данни-{index:D8}.bin",
        3 => $"long-name-abcdefghijklmnopqrstuvwxyz-{index:D8}",
        4 => $"similar-file-{index:D8}-a.bin",
        5 => $"similar-file-{index:D8}-b.bin",
        _ => $"file-{index:D8}.bin"
    };
}

static byte[] ContentFor(int index)
{
    if (index % 16 == 0) return [];
    var length = 1 + index % 128;
    var bytes = new byte[length];
    var state = (ulong)index + 0x5343414C455A4653UL;
    for (var offset = 0; offset < bytes.Length; ++offset)
    {
        state ^= state >> 12; state ^= state << 25; state ^= state >> 27;
        bytes[offset] = (byte)(state * 2685821657736338717UL);
    }
    return bytes;
}

static int Verify(string mount, string manifestPath, JsonSerializerOptions jsonOptions)
{
    var expected = 0;
    foreach (var line in File.ReadLines(manifestPath))
    {
        var item = JsonSerializer.Deserialize<FileEntry>(line, jsonOptions) ??
            throw new InvalidDataException("Invalid manifest line.");
        var path = Path.Combine(mount, item.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != item.Length || !StringComparer.Ordinal.Equals(HashFile(path), item.Sha256))
            throw new InvalidDataException($"Fixture verification failed: {item.Path}");
        ++expected;
    }
    var actual = Directory.EnumerateFiles(mount, "*", SearchOption.AllDirectories).Count();
    if (actual != expected) throw new InvalidDataException($"Enumerated {actual:N0}, expected {expected:N0} files.");
    return expected;
}

static int DirectoryCount(int fanout, int depth)
{
    var count = 0;
    var levelCount = 1;
    for (var level = 0; level < depth; ++level) { levelCount *= fanout; count += levelCount; }
    return count;
}

static string HashFile(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        4 * 1024 * 1024, FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string Run(string command, params string[] arguments)
{
    var start = new ProcessStartInfo(command) { RedirectStandardOutput = true,
        RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new IOException($"Could not start {command}.");
    var output = child.StandardOutput.ReadToEnd();
    var error = child.StandardError.ReadToEnd();
    child.WaitForExit();
    if (child.ExitCode != 0) throw new IOException($"{command}: {error.Trim()}");
    return output;
}

sealed record Profile(string Name, int FileCount, long ImageBytes, int Fanout, int Depth);
sealed record FileEntry(string Path, int Length, string Sha256);
sealed record Summary(string Profile, int FileCount, int Fanout, int Depth,
    int DirectoryCount, int EmptyFileCount, long LogicalBytes, long AllocatedBytes,
    long ImageSizeBytes, string ImageSha256, string Pool, ulong PoolGuid,
    string ZfsVersion, bool ReimportVerified, double CreateSeconds, double VerifySeconds,
    double CreateOperationsPerSecond, double VerifyOperationsPerSecond,
    long PeakWorkingSetBytes);

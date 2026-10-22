using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const long GiB = 1024L * 1024 * 1024;
const int MiB = 1024 * 1024;
if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
    throw new PlatformNotSupportedException("Run as root on Linux with OpenZFS.");
if (args.Length != 4)
    throw new ArgumentException("Usage: <disk01.raw> <disk02.raw> <manifest.json> <pool>");

var firstImage = Path.GetFullPath(args[0]);
var secondImage = Path.GetFullPath(args[1]);
var manifestPath = Path.GetFullPath(args[2]);
var pool = args[3];
var mount = Path.Combine(Path.GetDirectoryName(firstImage)!, "mnt-" + pool);
foreach (var path in new[] { firstImage, secondImage, manifestPath })
    if (File.Exists(path)) throw new IOException($"Output already exists: {path}");
Directory.CreateDirectory(Path.GetDirectoryName(firstImage)!);
Directory.CreateDirectory(Path.GetDirectoryName(secondImage)!);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
Directory.CreateDirectory(mount);

var specs = new[]
{
    new FileSpec("documents/empty.txt", 0, Pattern.Text, 1),
    new FileSpec("documents/small.txt", 127, Pattern.Text, 2),
    new FileSpec("documents/Grüße-東京.txt", 64 * 1024, Pattern.Text, 3),
    new FileSpec("nested/l01/l02/l03/l04/deep.bin", 4 * MiB, Pattern.Random, 4),
    new FileSpec("large/random-512m.bin", 512L * MiB, Pattern.Random, 5),
    new FileSpec("large/compressible-3584m.bin", 3584L * MiB, Pattern.Repeating, 6),
};
var logicalBytes = specs.Sum(spec => spec.Length);
var created = false;
try
{
    foreach (var path in new[] { firstImage, secondImage })
        using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            image.SetLength(5L * GiB);
    Run("zpool", "create", "-f", "-o", "ashift=12", "-O", "mountpoint=none",
        pool, "mirror", firstImage, secondImage);
    created = true;
    Run("zfs", "create", "-o", $"mountpoint={mount}", "-o", "compression=lz4",
        "-o", "recordsize=128K", $"{pool}/data");

    var files = new List<FileResult>();
    foreach (var spec in specs)
    {
        var target = Path.Combine(mount, spec.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        files.Add(Write(target, spec));
        Console.WriteLine($"wrote {spec.Length,12:N0} /{spec.Path}");
    }
    Run("sync");
    var guid = ulong.Parse(Run("zpool", "get", "-H", "-o", "value", "guid", pool).Trim());
    var version = Run("zfs", "--version").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
    var allocated = long.Parse(Run("zfs", "get", "-Hp", "-o", "value", "used", $"{pool}/data").Trim());
    Run("zpool", "export", pool);
    created = false;

    Run("zpool", "import", "-d", Path.GetDirectoryName(firstImage)!, pool);
    created = true;
    foreach (var expected in files)
    {
        var path = Path.Combine(mount, expected.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (new FileInfo(path).Length != expected.Length || HashFile(path) != expected.Sha256)
            throw new InvalidDataException($"Re-import verification failed: {expected.Path}");
    }
    Run("zpool", "export", pool);
    created = false;

    var images = new[]
    {
        new ImageResult(Path.GetFileName(firstImage), new FileInfo(firstImage).Length, HashFile(firstImage)),
        new ImageResult(Path.GetFileName(secondImage), new FileInfo(secondImage).Length, HashFile(secondImage))
    };
    var result = new Manifest(pool, guid, version, "mirror", 2, logicalBytes,
        allocated, true, images, files);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true }) + "\n");
    Console.WriteLine($"Mirror: logical={logicalBytes:N0}, allocated={allocated:N0}");
    foreach (var image in images) Console.WriteLine($"{image.Path}: {image.Sha256}");
}
finally
{
    if (created)
    {
        try { Run("zpool", "export", pool); }
        catch { Run("zpool", "destroy", "-f", pool); }
    }
}

static FileResult Write(string path, FileSpec spec)
{
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, MiB, FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[MiB];
    var random = new Prng(0x4D4952524F525A46UL ^ spec.Seed);
    long position = 0;
    while (position < spec.Length)
    {
        var count = (int)Math.Min(buffer.Length, spec.Length - position);
        Fill(buffer.AsSpan(0, count), spec.Pattern, random, position, spec.Path);
        output.Write(buffer, 0, count);
        hash.AppendData(buffer, 0, count);
        position += count;
    }
    output.Flush(true);
    return new('/' + spec.Path, spec.Length, Convert.ToHexString(hash.GetHashAndReset()),
        spec.Pattern.ToString().ToLowerInvariant());
}

static void Fill(Span<byte> bytes, Pattern pattern, Prng random, long position, string path)
{
    if (pattern == Pattern.Random) { random.Fill(bytes); return; }
    if (pattern == Pattern.Repeating)
    {
        for (var index = 0; index < bytes.Length; ++index)
            bytes[index] = (byte)(((position + index) * 31 + 17) % 251);
        return;
    }
    var text = Encoding.UTF8.GetBytes($"ZFS mirror fixture | {path} | Καλημέρα | 東京 |\n");
    for (var index = 0; index < bytes.Length; ++index)
        bytes[index] = text[(position + index) % text.Length];
}

static string HashFile(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        4 * MiB, FileOptions.SequentialScan);
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

enum Pattern { Random, Repeating, Text }
sealed record FileSpec(string Path, long Length, Pattern Pattern, ulong Seed);
sealed record FileResult(string Path, long Length, string Sha256, string Pattern);
sealed record ImageResult(string Path, long SizeBytes, string Sha256);
sealed record Manifest(string Pool, ulong PoolGuid, string ZfsVersion, string Topology,
    int MemberCount, long LogicalBytes, long AllocatedBytes, bool ReimportVerified,
    IReadOnlyList<ImageResult> Images, IReadOnlyList<FileResult> Files);

sealed class Prng(ulong state)
{
    private ulong _state = state;
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

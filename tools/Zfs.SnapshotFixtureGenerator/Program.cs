using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int MiB = 1024 * 1024;
if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
    throw new PlatformNotSupportedException("Run as root on Linux with OpenZFS.");
if (args.Length != 3)
    throw new ArgumentException("Usage: <disk01.raw> <manifest.json> <pool>");

var image = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var pool = args[2];
var root = Path.GetDirectoryName(image)!;
var mount = Path.Combine(root, "mnt-" + pool);
var lifecycleRoot = Path.Combine(root, "lifecycle-check");
var lifecycleImage = Path.Combine(lifecycleRoot, "disk01.raw");
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
Directory.CreateDirectory(mount);
Directory.CreateDirectory(lifecycleRoot);
if (File.Exists(image) || File.Exists(manifestPath))
    throw new IOException("Fixture outputs already exist.");

var created = false;
try
{
    using (var stream = new FileStream(image, FileMode.CreateNew, FileAccess.Write))
        stream.SetLength(1024L * MiB);
    Run("zpool", "create", "-f", "-o", "ashift=12", "-O", "mountpoint=none", pool, image);
    created = true;
    Run("zfs", "create", "-o", $"mountpoint={mount}", "-o", "compression=lz4", $"{pool}/data");

    WriteText(mount, "documents/report.txt", "report version 1\n");
    WriteText(mount, "documents/config.json", "{\"version\":1,\"enabled\":true}\n");
    WriteBinary(mount, "binary/data.bin", 4 * MiB, 1);
    Run("zfs", "snapshot", $"{pool}/data@snapshot1");
    var snapshot1 = Capture("snapshot1", Path.Combine(mount, ".zfs", "snapshot", "snapshot1"));

    WriteText(mount, "documents/report.txt", "report version 2 - intermediate\n");
    WriteText(mount, "documents/config.json", "{\"version\":2,\"enabled\":false}\n");
    File.Delete(Path.Combine(mount, "binary", "data.bin"));
    WriteText(mount, "documents/intermediate.txt", "created after snapshot1\n");
    Run("zfs", "snapshot", $"{pool}/data@snapshot2");
    var snapshot2 = Capture("snapshot2", Path.Combine(mount, ".zfs", "snapshot", "snapshot2"));

    WriteText(mount, "documents/report.txt", "report version 3 - latest\n");
    File.Delete(Path.Combine(mount, "documents", "config.json"));
    WriteBinary(mount, "binary/latest.bin", 2 * MiB, 3);
    Run("zfs", "snapshot", $"{pool}/data@snapshot3");
    var snapshot3 = Capture("snapshot3", Path.Combine(mount, ".zfs", "snapshot", "snapshot3"));
    var current = Capture("current", mount);
    Run("sync");
    var guid = ulong.Parse(Run("zpool", "get", "-H", "-o", "value", "guid", pool).Trim());
    var version = Run("zfs", "--version").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
    Run("zpool", "export", pool);
    created = false;

    if (File.Exists(lifecycleImage)) File.Delete(lifecycleImage);
    Run("cp", "--sparse=always", image, lifecycleImage);
    Run("zpool", "import", "-d", lifecycleRoot, pool);
    created = true;
    var lifecycleMount = Path.GetFullPath(Run("zfs", "get", "-H", "-o", "value",
        "mountpoint", $"{pool}/data").Trim());
    Run("zfs", "rollback", "-r", $"{pool}/data@snapshot2");
    Verify(lifecycleMount, snapshot2);
    Run("zfs", "destroy", $"{pool}/data@snapshot1");
    var remaining = Run("zfs", "list", "-H", "-t", "snapshot", "-o", "name", "-r", $"{pool}/data")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (remaining.Length != 1 || remaining[0] != $"{pool}/data@snapshot2")
        throw new InvalidDataException("Disposable snapshot deletion verification failed.");
    Run("zpool", "export", pool);
    created = false;
    File.Delete(lifecycleImage);

    Run("zpool", "import", "-d", root, pool);
    created = true;
    Verify(mount, current);
    Verify(Path.Combine(mount, ".zfs", "snapshot", "snapshot1"), snapshot1);
    Verify(Path.Combine(mount, ".zfs", "snapshot", "snapshot2"), snapshot2);
    Verify(Path.Combine(mount, ".zfs", "snapshot", "snapshot3"), snapshot3);
    Run("zpool", "export", pool);
    created = false;

    var result = new Manifest(pool, guid, version, new FileInfo(image).Length,
        HashFile(image), true, true, true, current,
        new[] { snapshot1, snapshot2, snapshot3 });
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true }) + "\n");
    Console.WriteLine($"Snapshot fixture: {result.ImageSha256}");
}
finally
{
    if (created)
    {
        try { Run("zpool", "export", pool); }
        catch { Run("zpool", "destroy", "-f", pool); }
    }
}

static State Capture(string name, string stateRoot)
{
    stateRoot = Path.GetFullPath(stateRoot);
    var directories = new[] { "/" }.Concat(Directory.EnumerateDirectories(stateRoot, "*",
            SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(stateRoot, path).Replace('\\', '/'))
        .Where(relative => relative != ".zfs" &&
            !relative.StartsWith(".zfs/", StringComparison.Ordinal))
        .Select(relative => '/' + relative))
        .Order(StringComparer.Ordinal).ToArray();
    var files = Directory.EnumerateFiles(stateRoot, "*", SearchOption.AllDirectories)
        .Select(path => (Path: path, Relative: Path.GetRelativePath(stateRoot, path).Replace('\\', '/')))
        .Where(file => file.Relative != ".zfs" && !file.Relative.StartsWith(".zfs/", StringComparison.Ordinal))
        .Select(file => new FileResult('/' + file.Relative,
            new FileInfo(file.Path).Length, HashFile(file.Path)))
        .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    return new(name, directories, files);
}

static void Verify(string stateRoot, State expected)
{
    var actual = Capture("verify", stateRoot);
    if (!actual.Directories.SequenceEqual(expected.Directories) ||
        !actual.Files.SequenceEqual(expected.Files))
    {
        var expectedText = $"expected directories: {string.Join(", ", expected.Directories)}" +
            Environment.NewLine + string.Join(Environment.NewLine,
                expected.Files.Select(file => $"expected {file}"));
        var actualText = $"actual directories: {string.Join(", ", actual.Directories)}" +
            Environment.NewLine + string.Join(Environment.NewLine,
                actual.Files.Select(file => $"actual   {file}"));
        throw new InvalidDataException($"Snapshot state mismatch at {stateRoot}:{Environment.NewLine}" +
            expectedText + Environment.NewLine + actualText);
    }
}

static void WriteText(string mount, string relative, string text)
{
    var path = Path.Combine(mount, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text, new UTF8Encoding(false));
}

static void WriteBinary(string mount, string relative, int length, ulong seed)
{
    var path = Path.Combine(mount, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var bytes = new byte[MiB];
    using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
    var state = seed;
    var remaining = length;
    while (remaining > 0)
    {
        for (var index = 0; index < bytes.Length; ++index)
        {
            state ^= state >> 12; state ^= state << 25; state ^= state >> 27;
            bytes[index] = (byte)(state * 2685821657736338717UL);
        }
        var count = Math.Min(bytes.Length, remaining);
        output.Write(bytes, 0, count);
        remaining -= count;
    }
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
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

sealed record FileResult(string Path, long Length, string Sha256);
sealed record State(string Name, IReadOnlyList<string> Directories,
    IReadOnlyList<FileResult> Files);
sealed record Manifest(string Pool, ulong PoolGuid, string ZfsVersion,
    long ImageSizeBytes, string ImageSha256, bool ReimportVerified,
    bool RollbackVerified, bool DeletionVerified, State Current,
    IReadOnlyList<State> Snapshots);

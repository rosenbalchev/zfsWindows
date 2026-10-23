// Verifies a fixture manifest against a dataset mounted by the OpenZFS
// kernel driver, using only standard .NET file APIs (plan step 12).
//
//   Zfs.MountVerifier <manifest.json> <mounted-dataset-root> [--write <dir>]
//
// Checks: the recursive enumeration equals the manifest's file set (plus the
// directories implied by it); every file's length and full-content SHA-256
// (sequential FileStream reads); every recorded sample range (random-access
// seeks); concurrent reads of all files; and repeated opens.  --write also
// creates, appends to, renames and deletes files under <dir> and reads them
// back, so the caller can let upstream tools validate the written pool.
// Prints a JSON summary; exit code 0 means every check passed.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Zfs.MountVerifier <manifest.json> <root> [--write <dir>]");
    return 2;
}

var manifest = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var root = Path.GetFullPath(args[1]);
var writeDir = args.Length >= 4 && args[2] == "--write" ? args[3] : null;
var failures = new List<string>();
var watch = Stopwatch.StartNew();
long bytesRead = 0;

static string Hex(byte[] hash) => Convert.ToHexString(hash);
string Local(string manifestPath) =>
    Path.Combine(root, manifestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

var files = manifest.GetProperty("files").EnumerateArray().ToList();

// 1. Namespace: enumeration must produce exactly the manifest's files and
//    the directories they imply.
var expected = new HashSet<string>(StringComparer.Ordinal);
foreach (var file in files)
{
    var path = file.GetProperty("path").GetString()!.TrimStart('/');
    expected.Add("F " + path);
    for (var dir = Path.GetDirectoryName(path.Replace('/', '\\')); !string.IsNullOrEmpty(dir);
         dir = Path.GetDirectoryName(dir))
        expected.Add("D " + dir.Replace('\\', '/'));
}
var actual = new HashSet<string>(StringComparer.Ordinal);
foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*",
             new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
{
    var rel = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
    actual.Add((entry is DirectoryInfo ? "D " : "F ") + rel);
}
foreach (var missing in expected.Except(actual).Order())
    failures.Add("missing " + missing);
foreach (var extra in actual.Except(expected).Order())
    failures.Add("unexpected " + extra);

// 2. Content: length, full SHA-256, and sampled ranges.
foreach (var file in files)
{
    var path = Local(file.GetProperty("path").GetString()!);
    var length = file.GetProperty("length").GetInt64();
    try
    {
        var info = new FileInfo(path);
        if (info.Length != length)
            failures.Add($"{path}: length {info.Length}, expected {length}");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   1 << 20, FileOptions.SequentialScan))
        {
            var hash = Hex(SHA256.HashData(stream));
            bytesRead += stream.Length;
            if (!hash.Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                failures.Add($"{path}: SHA-256 {hash}");
        }
        if (file.TryGetProperty("samples", out var samples))
        {
            using var random = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.RandomAccess);
            foreach (var sample in samples.EnumerateArray())
            {
                var buffer = new byte[sample.GetProperty("length").GetInt32()];
                random.Seek(sample.GetProperty("offset").GetInt64(), SeekOrigin.Begin);
                random.ReadExactly(buffer);
                bytesRead += buffer.Length;
                if (!Hex(SHA256.HashData(buffer)).Equals(sample.GetProperty("sha256").GetString(),
                        StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{path}: sample at {sample.GetProperty("offset").GetInt64()}");
            }
        }
    }
    catch (Exception e)
    {
        failures.Add($"{path}: {e.GetType().Name}: {e.Message}");
    }
}

// 3. Concurrency: all files hashed in parallel must agree with the manifest.
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
{
    var path = Local(file.GetProperty("path").GetString()!);
    try
    {
        using var stream = File.OpenRead(path);
        if (!Hex(SHA256.HashData(stream)).Equals(file.GetProperty("sha256").GetString(),
                StringComparison.OrdinalIgnoreCase))
            lock (failures) failures.Add($"{path}: parallel read differs");
        Interlocked.Add(ref bytesRead, stream.Length);
    }
    catch (Exception e)
    {
        lock (failures) failures.Add($"{path}: parallel {e.GetType().Name}: {e.Message}");
    }
});

// 4. Repeated opens of the smallest files.
foreach (var file in files.OrderBy(f => f.GetProperty("length").GetInt64()).Take(5))
{
    var path = Local(file.GetProperty("path").GetString()!);
    for (var i = 0; i < 200; i++)
    {
        try { File.ReadAllBytes(path); }
        catch (Exception e) { failures.Add($"{path}: open {i}: {e.Message}"); break; }
    }
}

// 5. Optional writes through the driver.
var written = new Dictionary<string, string>();
if (writeDir != null)
{
    try
    {
        Directory.CreateDirectory(writeDir);
        var data = new byte[3 * 1024 * 1024 + 123];
        new Random(20261023).NextBytes(data);
        var big = Path.Combine(writeDir, "written-by-windows.bin");
        File.WriteAllBytes(big, data);
        File.AppendAllText(Path.Combine(writeDir, "log.txt"), "first line\n");
        File.AppendAllText(Path.Combine(writeDir, "log.txt"), "second line\n");
        var temp = Path.Combine(writeDir, "temp.txt");
        File.WriteAllText(temp, "to be renamed\n");
        File.Move(temp, Path.Combine(writeDir, "renamed.txt"));
        var doomed = Path.Combine(writeDir, "deleted.txt");
        File.WriteAllText(doomed, "to be deleted\n");
        File.Delete(doomed);
        Directory.CreateDirectory(Path.Combine(writeDir, "sub", "deeper"));
        File.WriteAllText(Path.Combine(writeDir, "sub", "deeper", "note.txt"), "nested\n");
        if (!File.ReadAllBytes(big).AsSpan().SequenceEqual(data))
            failures.Add("read-back of written-by-windows.bin differs");
        if (File.Exists(doomed) || File.Exists(temp))
            failures.Add("deleted or renamed file still present");
        foreach (var path in Directory.EnumerateFiles(writeDir, "*", SearchOption.AllDirectories))
            written[Path.GetRelativePath(writeDir, path).Replace('\\', '/')] =
                Hex(SHA256.HashData(File.ReadAllBytes(path)));
    }
    catch (Exception e)
    {
        failures.Add($"write: {e.GetType().Name}: {e.Message}");
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    root,
    files = files.Count,
    entries = actual.Count,
    bytesRead,
    seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
    written,
    failures,
}, new JsonSerializerOptions { WriteIndented = true }));
return failures.Count == 0 ? 0 : 1;

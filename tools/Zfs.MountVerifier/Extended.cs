// Extended driver checks (plan step 12, follow-up): alternate data streams,
// DACL edits, large directories, concurrent and memory-mapped writes,
// snapshot mutation, and dedup/compression workloads.
//
//   extended-write  <dir>                          write the workload
//   fill            <dir> <dup|compressible|random> <MiB>
//   snapshot-mutate <dir> <expect.json>            change files after a snapshot
//   check           <dir> <expect.json>            verify <dir> against expect
//   corrupt         <file> <offset> <length>       overwrite a vdev region
//
// The writers print an expectation as JSON: every file's SHA-256 (relative
// path), every directory, every alternate stream's SHA-256 ("path:stream"),
// and every file whose DACL must grant BUILTIN\Users.  `check` re-verifies it
// after a remount, through a snapshot directory, or after a rollback; the
// same JSON lets Linux validate the exported pool.
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

static class Extended
{
    public static readonly HashSet<string> Verbs =
        ["extended-write", "fill", "snapshot-mutate", "check", "corrupt"];

    // BUILTIN\Users; the DACL test grants it Modify.
    const string UsersSid = "S-1-5-32-545";
    const int LargeDirFiles = 20000, LargeDirDeleted = 5000, LargeDirRenamed = 1000;
    const int SubDirs = 2000;

    sealed class Expect
    {
        public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
        public SortedSet<string> Dirs { get; set; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, string> Streams { get; set; } = new(StringComparer.Ordinal);
        public SortedSet<string> Acls { get; set; } = new(StringComparer.Ordinal);
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    static string Abs(string root, string rel) => Path.Combine(root, rel.Replace('/', '\\'));

    static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Text-like data that every ZFS compressor shrinks several-fold.
    static byte[] Compressible(int length, int seed)
    {
        var random = new Random(seed);
        var words = new[] { "zfs", "windows", "pool", "dataset", "block", "checksum", "txg", "vdev" };
        var text = new StringBuilder(length + 64);
        while (text.Length < length)
            text.Append(words[random.Next(words.Length)]).Append(random.Next(100) < 10 ? '\n' : ' ');
        return Encoding.ASCII.GetBytes(text.ToString(0, length));
    }

    public static int Run(string[] args)
    {
        var failures = new List<string>();
        object? result = null;
        try
        {
            switch (args[0])
            {
                case "extended-write": result = ExtendedWrite(Path.GetFullPath(args[1]), failures); break;
                case "fill": result = Fill(Path.GetFullPath(args[1]), args[2], int.Parse(args[3])); break;
                case "snapshot-mutate":
                    result = SnapshotMutate(Path.GetFullPath(args[1]), Load(args[2]), failures); break;
                case "check":
                    var expect = Load(args[2]);
                    Check(Path.GetFullPath(args[1]), expect, failures);
                    result = new { files = expect.Files.Count, dirs = expect.Dirs.Count,
                                   streams = expect.Streams.Count, acls = expect.Acls.Count };
                    break;
                case "corrupt": result = Corrupt(args[1], long.Parse(args[2]), long.Parse(args[3])); break;
            }
        }
        catch (Exception e)
        {
            failures.Add($"{args[0]}: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { result, failures }, Json));
        return failures.Count == 0 ? 0 : 1;
    }

    static Expect Load(string path) => JsonSerializer.Deserialize<Expect>(
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("result").GetRawText(), Json)!;

    static void Record(string root, Expect expect)
    {
        expect.Files.Clear();
        expect.Dirs.Clear();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            expect.Dirs.Add(Rel(root, dir));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            expect.Files[Rel(root, file)] = Sha(file);
    }

    static Expect ExtendedWrite(string root, List<string> failures)
    {
        Directory.CreateDirectory(root);
        var expect = new Expect();

        // Large directory: 20,000 files in one fat ZAP, then removals and
        // renames inside it, plus a directory with 2,000 subdirectories.
        var large = Path.Combine(root, "large");
        Directory.CreateDirectory(large);
        for (var i = 0; i < LargeDirFiles; i++)
            File.WriteAllText(Path.Combine(large, $"f{i:D5}.txt"), $"file {i}\n");
        var listed = Directory.EnumerateFiles(large).Count();
        if (listed != LargeDirFiles) failures.Add($"large: listed {listed}, expected {LargeDirFiles}");
        for (var i = 0; i < LargeDirDeleted; i++)
            File.Delete(Path.Combine(large, $"f{i * 4:D5}.txt"));
        for (var i = 0; i < LargeDirRenamed; i++)
            File.Move(Path.Combine(large, $"f{i * 4 + 1:D5}.txt"), Path.Combine(large, $"renamed-{i:D4}.txt"));
        listed = Directory.EnumerateFiles(large).Count();
        if (listed != LargeDirFiles - LargeDirDeleted)
            failures.Add($"large: listed {listed} after deletes, expected {LargeDirFiles - LargeDirDeleted}");
        // Wildcard enumeration (the driver filters with
        // FsRtlIsNameInExpression): f10000..f19999 minus the deleted
        // multiples of 4, and the renamed files.
        foreach (var (pattern, expected) in new[] { ("f1*.txt", 7500), ("renamed-*", LargeDirRenamed),
                                                    ("*.txt", LargeDirFiles - LargeDirDeleted) })
        {
            var matched = Directory.EnumerateFiles(large, pattern).Count();
            if (matched != expected) failures.Add($"large: pattern {pattern} matched {matched}, expected {expected}");
        }
        var subdirs = Path.Combine(root, "subdirs");
        for (var i = 0; i < SubDirs; i++)
            Directory.CreateDirectory(Path.Combine(subdirs, $"d{i:D4}"));

        // Concurrent writers: 8 threads with 4 files each, and 8 threads
        // writing disjoint 1 MiB ranges of one shared file.
        var concurrent = Path.Combine(root, "concurrent");
        Directory.CreateDirectory(concurrent);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (var j = 0; j < 4; j++)
                File.WriteAllBytes(Path.Combine(concurrent, $"t{t}-{j}.bin"), Random(1 << 20, t * 100 + j));
        });
        var shared = Path.Combine(concurrent, "shared.bin");
        using (var create = new FileStream(shared, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            create.SetLength(8 << 20);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            using var stream = new FileStream(shared, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Seek((long)t << 20, SeekOrigin.Begin);
            stream.Write(Random(1 << 20, 900 + t));
        });
        var sharedExpected = new byte[8 << 20];
        for (var t = 0; t < 8; t++) Random(1 << 20, 900 + t).CopyTo(sharedExpected, t << 20);
        if (Sha(shared) != Sha(sharedExpected)) failures.Add("concurrent: shared.bin differs");

        // Memory-mapped writes: create, map, write, flush, unmap, then read
        // the file back through ordinary reads.
        var mapped = Path.Combine(root, "mmap.bin");
        var mappedExpected = Random(8 << 20, 4242);
        using (var map = MemoryMappedFile.CreateFromFile(mapped, FileMode.CreateNew, null, mappedExpected.Length))
        using (var view = map.CreateViewAccessor())
        {
            view.WriteArray(0, mappedExpected, 0, mappedExpected.Length);
            view.Flush();
        }
        // Then modify the middle of an existing file through a second mapping.
        using (var map = MemoryMappedFile.CreateFromFile(mapped, FileMode.Open))
        using (var view = map.CreateViewAccessor(3 << 20, 4096))
        {
            var patch = Random(4096, 4343);
            view.WriteArray(0, patch, 0, patch.Length);
            view.Flush();
            patch.CopyTo(mappedExpected, 3 << 20);
        }
        if (Sha(mapped) != Sha(mappedExpected)) failures.Add("mmap: read-back differs");

        // Alternate data streams (stored by the driver as xattrs).
        var streams = Path.Combine(root, "streams.txt");
        File.WriteAllText(streams, "main stream\n");
        foreach (var (name, length) in new[] { ("small", 100), ("large", 300000) })
        {
            var data = Random(length, length);
            File.WriteAllBytes($"{streams}:{name}", data);
            if (Sha(File.ReadAllBytes($"{streams}:{name}")) != Sha(data))
                failures.Add($"stream {name}: read-back differs");
            expect.Streams[$"streams.txt:{name}"] = Sha(data);
        }
        var listedStreams = ListStreams(streams);
        foreach (var name in new[] { ":small:$DATA", ":large:$DATA" })
            if (!listedStreams.Contains(name, StringComparer.OrdinalIgnoreCase))
                failures.Add($"stream {name} not listed (listed: {string.Join(", ", listedStreams)})");

        // DACL edit: grant BUILTIN\Users Modify on a file and a directory.
        var aclFile = Path.Combine(root, "acl.txt");
        File.WriteAllText(aclFile, "acl\n");
        var aclDir = Path.Combine(root, "acl-dir");
        Directory.CreateDirectory(aclDir);
        var users = new SecurityIdentifier(UsersSid);
        var fileSecurity = new FileInfo(aclFile).GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(aclFile).SetAccessControl(fileSecurity);
        var dirSecurity = new DirectoryInfo(aclDir).GetAccessControl();
        dirSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(aclDir).SetAccessControl(dirSecurity);
        expect.Acls.Add("acl.txt");
        expect.Acls.Add("acl-dir");

        Record(root, expect);
        Check(root, expect, failures);
        return expect;
    }

    static Expect Fill(string root, string kind, int mib)
    {
        Directory.CreateDirectory(root);
        var expect = new Expect();
        // dup: four copies of the same content, so dedup stores it once.
        var chunk = kind switch
        {
            "dup" => Random(mib << 18, 7),
            "compressible" => Compressible(mib << 20, 8),
            "random" => Random(mib << 20, 9),
            _ => throw new ArgumentException($"unknown fill kind {kind}"),
        };
        var copies = kind == "dup" ? 4 : 1;
        for (var i = 0; i < copies; i++)
            File.WriteAllBytes(Path.Combine(root, $"{kind}-{i}.bin"), chunk);
        Record(root, expect);
        return expect;
    }

    // After a snapshot: overwrite, delete, add and rename, so the snapshot
    // and the live dataset differ in every way.
    static Expect SnapshotMutate(string root, Expect before, List<string> failures)
    {
        File.WriteAllBytes(Path.Combine(root, "concurrent", "t0-0.bin"), Random(1 << 20, 5150));
        File.Delete(Path.Combine(root, "mmap.bin"));
        File.WriteAllText(Path.Combine(root, "after-snapshot.txt"), "written after the snapshot\n");
        File.Move(Path.Combine(root, "large", "f00002.txt"), Path.Combine(root, "moved-from-large.txt"));
        Directory.Delete(Path.Combine(root, "subdirs", "d0000"));
        var after = new Expect { Streams = before.Streams, Acls = before.Acls };
        Record(root, after);
        Check(root, after, failures);
        return after;
    }

    static void Check(string root, Expect expect, List<string> failures)
    {
        var dirs = new SortedSet<string>(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Rel(root, d)), StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Rel(root, f)).ToHashSet(StringComparer.Ordinal);
        foreach (var d in expect.Dirs.Except(dirs).Take(20)) failures.Add($"missing dir {d}");
        foreach (var d in dirs.Except(expect.Dirs).Take(20)) failures.Add($"unexpected dir {d}");
        foreach (var f in files.Except(expect.Files.Keys).Take(20)) failures.Add($"unexpected file {f}");
        var bad = 0;
        foreach (var (rel, sha) in expect.Files)
        {
            if (!files.Contains(rel)) { if (bad++ < 20) failures.Add($"missing file {rel}"); continue; }
            // The first read goes through a FileStream, as Sha(path) did.
            var first = new MemoryStream();
            using (var stream = File.OpenRead(Abs(root, rel)))
                stream.CopyTo(first);
            var actual = Sha(first.ToArray());
            if (actual != sha && bad++ < 20)
            {
                // Diagnostics: what was read, and whether a re-read agrees.
                var bytes = first.ToArray();
                var reread = Sha(File.ReadAllBytes(Abs(root, rel))) == sha ? "re-read correct" : "re-read also wrong";
                var nonzero = bytes.Count(b => b != 0);
                var head = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)));
                failures.Add($"{rel}: SHA-256 {actual}, expected {sha} ({reread}; read " +
                             $"{bytes.Length} bytes, {nonzero} non-zero, starts {head}, " +
                             $"first diff at {FirstDiff(bytes, File.ReadAllBytes(Abs(root, rel)))})");
            }
        }
        foreach (var (key, sha) in expect.Streams)
        {
            var path = Abs(root, key);
            try
            {
                var actual = Sha(File.ReadAllBytes(path));
                if (actual != sha) failures.Add($"stream {key}: SHA-256 {actual}");
            }
            catch (Exception e) { failures.Add($"stream {key}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var rel in expect.Acls)
        {
            var path = Abs(root, rel);
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var granted = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Value == UsersSid && r.AccessControlType == AccessControlType.Allow
                          && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            if (!granted) failures.Add($"acl {rel}: no Modify grant for BUILTIN\\Users");
        }
    }

    static string FirstDiff(byte[] a, byte[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                return $"{i} (read {Convert.ToHexString(a.AsSpan(i, Math.Min(16, a.Length - i)))}, " +
                       $"now {Convert.ToHexString(b.AsSpan(i, Math.Min(16, b.Length - i)))})";
        return a.Length == b.Length ? "none" : $"length {a.Length} vs {b.Length}";
    }

    static object Corrupt(string file, long offset, long length)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Write);
        stream.Seek(offset, SeekOrigin.Begin);
        var random = new Random(31337);
        var buffer = new byte[1 << 20];
        for (var done = 0L; done < length; done += buffer.Length)
        {
            random.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, length - done));
        }
        return new { file, offset, length };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr handle);

    static List<string> ListStreams(string path)
    {
        var names = new List<string>();
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1)) return names;
        try
        {
            do names.Add(data.Name); while (FindNextStreamW(handle, out data));
        }
        finally { FindClose(handle); }
        return names;
    }
}

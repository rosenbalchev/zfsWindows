// Cache-layer checks through the driver (plan step 13).
//
//   read-unbuffered <file>                     read with FILE_FLAG_NO_BUFFERING
//   read-random <file> <count> <seed> [blockKiB]
//   coherency <dir>                            Cache Manager / mapping / non-cached coherency
//   cache-writer <dir> <buffered|flush|writethrough> <ack.log>
//                                              write files until killed; ack each one
//   cache-check <dir> <ack.log> <durable|any>  classify every file after a power cut
//
// cache-writer writes w-<n>.bin (64 KiB from seed n) and appends n to the
// ack log (write-through and flushed, on NTFS) only after the write, and
// for "flush" the FlushFileBuffers, has returned.  The power cut is a
// Hyper-V TurnOff of the VM, so the ack log holds every acknowledgement.
// cache-check then requires every acknowledged file to be exact (durable),
// or allows lost and zero-filled files (any); content that is neither the
// expected bytes nor zeros is always a failure.
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

static class Cache
{
    public static readonly HashSet<string> Verbs =
        ["read-unbuffered", "read-random", "coherency", "cache-writer", "cache-check"];

    const FileOptions NoBuffering = (FileOptions)0x20000000;
    const int FileSize = 64 * 1024;

    static byte[] Content(int n)
    {
        var data = new byte[FileSize];
        new Random(n).NextBytes(data);
        return data;
    }

    public static int Run(string[] args)
    {
        var failures = new List<string>();
        object? result = null;
        try
        {
            result = args[0] switch
            {
                "read-unbuffered" => ReadUnbuffered(args[1]),
                "read-random" => ReadRandom(args[1], int.Parse(args[2]), int.Parse(args[3]),
                    args.Length > 4 ? int.Parse(args[4]) : 128),
                "coherency" => Coherency(Path.GetFullPath(args[1]), failures),
                "cache-writer" => Writer(Path.GetFullPath(args[1]), args[2], args[3]),
                "cache-check" => Check(Path.GetFullPath(args[1]), args[2], args[3], failures),
                _ => null,
            };
        }
        catch (Exception e)
        {
            failures.Add($"{args[0]}: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { result, failures },
            new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }

    // Non-cached reads go to ZFS (and so the ARC) on every call; Cache
    // Manager hits would hide the ARC behaviour under test.
    static unsafe object ReadUnbuffered(string path)
    {
        const int chunk = 1 << 20;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            NoBuffering);
        var length = RandomAccess.GetLength(handle);
        var buffer = (byte*)NativeMemory.AlignedAlloc(chunk, 4096);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (long offset = 0; offset < length; offset += chunk)
            {
                var span = new Span<byte>(buffer, chunk);
                var read = RandomAccess.Read(handle, span, offset);
                sha.AppendData(span[..(int)Math.Min(read, length - offset)]);
                if (read == 0) break;
            }
            return new { path, length, seconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
                         sha256 = Convert.ToHexString(sha.GetHashAndReset()) };
        }
        finally { NativeMemory.AlignedFree(buffer); }
    }

    // Non-cached random 128 KiB reads from a fixed seed: the same seed reads
    // the same blocks, so the hash must not change (for example after the
    // L2ARC device was corrupted).  Sequential rereads would be served by
    // prefetch and rarely reach the L2ARC.
    static unsafe object ReadRandom(string path, int count, int seed, int blockKiB)
    {
        if (blockKiB < 4 || (blockKiB & (blockKiB - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockKiB),
                "Use a power-of-two block size of at least 4 KiB.");
        var block = blockKiB * 1024;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            NoBuffering);
        var blocks = RandomAccess.GetLength(handle) / block;
        if (blocks == 0)
            throw new InvalidDataException("File is smaller than one aligned read block.");
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)block, 4096);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var random = new Random(seed);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var span = new Span<byte>(buffer, block);
                var read = RandomAccess.Read(handle, span, random.NextInt64(blocks) * block);
                sha.AppendData(span[..read]);
            }
            return new { path, count, seed, blockKiB,
                         seconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
                         sha256 = Convert.ToHexString(sha.GetHashAndReset()) };
        }
        finally { NativeMemory.AlignedFree(buffer); }
    }

    static object Coherency(string root, List<string> failures)
    {
        Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>();
        void Expect(string name, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
        {
            checks[name] = actual.SequenceEqual(expected);
            if (!checks[name]) failures.Add($"coherency: {name}");
        }
        byte[] ReadVia(FileStream stream, int length)
        {
            var data = new byte[length];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(data);
            return data;
        }

        var path = Path.Combine(root, "coherent.bin");
        var first = Content(1);
        File.WriteAllBytes(path, first);

        // 1. A cached reader sees a write made through another handle.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Expect("reader initial", ReadVia(reader, FileSize), first);
            var second = Content(2);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.Write(second);
            Expect("reader after other-handle write", ReadVia(reader, FileSize), second);
        }

        // 2. A mapping sees file writes, and file reads see mapping writes.
        using (var mapped0 = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var map = MemoryMappedFile.CreateFromFile(mapped0, null, 0,
                   MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
        using (var view = map.CreateViewAccessor())
        {
            var third = Content(3);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.Write(third);
            var mapped = new byte[FileSize];
            view.ReadArray(0, mapped, 0, FileSize);
            Expect("mapping after file write", mapped, third);
            var fourth = Content(4);
            view.WriteArray(0, fourth, 0, FileSize);
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Expect("file read after mapping write (unflushed)", ReadVia(reader, FileSize), fourth);
            view.Flush();
        }

        // 3. Cached and non-cached I/O on the same file stay coherent.
        var fifth = Content(5);
        using (var cached = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            cached.Write(fifth);
        Expect("non-cached read after cached write", ReadAllUnbuffered(path), fifth);
        var sixth = Content(6);
        WriteUnbuffered(path, sixth);
        using (var cached = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Expect("cached read after non-cached write", ReadVia(cached, FileSize), sixth);

        // 4. A truncate through one handle is seen by another.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.SetLength(4096);
            checks["length after other-handle truncate"] = reader.Length == 4096;
            if (reader.Length != 4096) failures.Add($"coherency: length {reader.Length} after truncate");
        }

        // 5. After remount (the caller exports and re-imports) the last
        //    state must still read back; record it.
        return new { checks, finalSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
    }

    static unsafe byte[] ReadAllUnbuffered(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            NoBuffering);
        var buffer = (byte*)NativeMemory.AlignedAlloc(FileSize, 4096);
        try
        {
            var span = new Span<byte>(buffer, FileSize);
            var read = RandomAccess.Read(handle, span, 0);
            return span[..read].ToArray();
        }
        finally { NativeMemory.AlignedFree(buffer); }
    }

    static unsafe void WriteUnbuffered(string path, byte[] data)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            NoBuffering);
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)data.Length, 4096);
        try
        {
            data.CopyTo(new Span<byte>(buffer, data.Length));
            RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, data.Length), 0);
        }
        finally { NativeMemory.AlignedFree(buffer); }
    }

    // Runs until the VM is powered off (or 10 minutes pass).
    static object Writer(string root, string mode, string ackPath)
    {
        Directory.CreateDirectory(root);
        var options = mode == "writethrough" ? FileOptions.WriteThrough : FileOptions.None;
        using var ack = new FileStream(ackPath, FileMode.Append, FileAccess.Write, FileShare.Read, 1,
            FileOptions.WriteThrough);
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var n = 0;
        for (; DateTime.UtcNow < deadline; n++)
        {
            using (var file = new FileStream(Path.Combine(root, $"w-{n:D6}.bin"), FileMode.CreateNew,
                       FileAccess.Write, FileShare.Read, 4096, options))
            {
                file.Write(Content(n));
                if (mode == "flush") file.Flush(true);
            }
            var line = System.Text.Encoding.ASCII.GetBytes($"{n}\n");
            ack.Write(line);
            ack.Flush(true);
            // About 20 files a second, so six writers stay well inside
            // their pools during a 45-second run.
            Thread.Sleep(50);
        }
        return new { files = n };
    }

    static object Check(string root, string ackPath, string expect, List<string> failures)
    {
        var acked = File.Exists(ackPath)
            ? File.ReadAllLines(ackPath).Where(l => l.Length > 0).Select(int.Parse).ToHashSet()
            : [];
        int exact = 0, zeroFilled = 0, missing = 0, garbage = 0, unackedPresent = 0;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var present = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "w-*.bin").ToDictionary(
                f => int.Parse(Path.GetFileNameWithoutExtension(f)[2..]), f => f)
            : [];
        foreach (var n in acked.Union(present.Keys).Order())
        {
            var isAcked = acked.Contains(n);
            if (!present.TryGetValue(n, out var path))
            {
                missing++;
                if (isAcked && expect == "durable") failures.Add($"acknowledged w-{n:D6}.bin is missing");
                continue;
            }
            var data = File.ReadAllBytes(path);
            files[Path.GetFileName(path)] = Convert.ToHexString(SHA256.HashData(data));
            if (!isAcked) unackedPresent++;
            var expected = Content(n);
            if (data.AsSpan().SequenceEqual(expected)) { exact++; continue; }
            // Not exact: every byte must be the expected byte or a zero
            // (a write that had not reached a committed TXG).
            var consistent = data.Length <= expected.Length &&
                data.Select((b, i) => b == expected[i] || b == 0).All(x => x);
            if (consistent) zeroFilled++; else garbage++;
            if (!consistent)
                failures.Add($"w-{n:D6}.bin holds bytes that are neither its data nor zeros");
            else if (isAcked && expect == "durable")
                failures.Add($"acknowledged w-{n:D6}.bin is incomplete ({data.Length} bytes)");
        }
        return new { acked = acked.Count, present = present.Count, exact, zeroFilled, missing,
                     garbage, unackedPresent, maxAcked = acked.Count == 0 ? -1 : acked.Max(), files };
    }
}

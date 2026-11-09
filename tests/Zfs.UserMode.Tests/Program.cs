using System.Diagnostics;
using Zfs.Interop;

namespace Zfs.UserMode.Tests;

// Scenarios the tests run in a process of their own (Child.Run): the test
// host's engine never stops, its memory is not a baseline, and a damaged
// image that crashes the engine must fail only its own test.  Results are
// "key=value" lines on stdout; exit code 0 means the scenario ran to its
// end (an engine error is a result, not a failure).
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Zfs.UserMode.Tests <scenario> <scratch directory> [arguments]");
            return 2;
        }
        var dir = args[1];
        Directory.CreateDirectory(dir);
        return args[0] switch
        {
            "memory" => Memory(dir, int.Parse(args[2]), int.Parse(args[3]), args[4]),
            "cycles" => Cycles(dir, int.Parse(args[2])),
            "import-zeroed" => ImportZeroed(dir),
            "import-garbage" => ImportGarbage(dir),
            "import-labels-wiped" => ImportLabelsWiped(dir),
            "import-front-labels-damaged" => ImportFrontLabelsDamaged(dir),
            "import-truncated" => ImportTruncated(dir),
            "import-readonly-file" => ImportReadOnlyFile(dir),
            _ => 2,
        };
    }

    private static void Put(string key, object value) => Console.WriteLine($"{key}={value}");

    // Committed private memory of this process (what Task Manager calls
    // "Commit size"): what the process holds whether it is resident or not.
    private static long Private()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static void Start(string dir) => ZfsUserModeEngine.Start(Path.Combine(dir, "zpool.cache"));

    private static string NewImage(string dir, string name, long bytes)
    {
        var path = Path.Combine(dir, name);
        using var stream = new FileStream(path, FileMode.Create);
        stream.SetLength(bytes);
        return path;
    }

    // A pool with one file, exported: what the import scenarios damage.
    private static (string Image, string Hash) ExportedPool(string dir, string pool)
    {
        var image = NewImage(dir, "disk.img", 128L << 20);
        ZfsUserModeEngine.CreatePool(pool, "", [image], "ashift=12");
        var data = Data.Random(4 << 20, 7);
        using (var fs = ZfsUserModeEngine.Open(dir, pool))
            fs.Write(fs.CreateFile("/data.bin"), 0, data);
        ZfsUserModeEngine.ExportPool(pool);
        return (image, Data.Hash(data));
    }

    // Import, and if that works read the file back: no crash either way.
    private static void TryImportAndRead(string dir, string pool, string expectedHash, bool readOnly = false)
    {
        var errno = ZfsUserModeEngine.TryOpen(dir, pool, pool, readOnly, out var fs);
        Put("import_errno", errno);
        if (fs is null) return;
        using (fs)
        {
            try
            {
                var data = fs.ReadAll(fs.Lookup("/data.bin"));
                Put("read_errno", 0);
                Put("data_intact", Data.Hash(data) == expectedHash ? 1 : 0);
            }
            catch (ZfsUmException e) { Put("read_errno", e.Errno); }
        }
        ZfsUserModeEngine.ExportPool(pool);
    }

    // GitHub issue #3: the engine's memory stays within the ARC ceiling
    // plus a bounded overhead while data is written and read, and goes back
    // to Windows when the pool is exported and the engine stops.
    private static int Memory(string dir, int arcMiB, int writeMiB, string dedup)
    {
        const string pool = "zut_memory";
        var buffer = new byte[1 << 20];
        _ = ZfsUserModeEngine.Version;  // the DLL loaded before the baseline
        var baseline = Private();
        ZfsUserModeEngine.SetArcMax((ulong)arcMiB << 20);
        Start(dir);
        Put("private_started", Private());

        var image = NewImage(dir, "memory.img", ((long)writeMiB << 20) + (384L << 20));
        ZfsUserModeEngine.CreatePool(pool, "", [image], "ashift=12", $"compression=off;dedup={dedup}");
        long peakPrivate = 0;
        ulong peakArc = 0;
        void Sample()
        {
            peakPrivate = Math.Max(peakPrivate, Private());
            peakArc = Math.Max(peakArc, ZfsUserModeEngine.ArcStats.Size);
        }

        using (var fs = ZfsUserModeEngine.Open(dir, pool))
        {
            var obj = fs.CreateFile("/fill.bin");
            var random = new Random(3);
            for (var i = 0; i < writeMiB; i++)
            {
                random.NextBytes(buffer);
                fs.Write(obj, (ulong)i << 20, buffer);
                // As the WinFsp front end flushes: dirty data stays bounded.
                if (i % 32 == 31) { fs.Sync(); Sample(); }
            }
            fs.Sync();
            // Read it all back twice: the ARC fills with clean buffers.
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < writeMiB; i++)
                {
                    fs.Read(obj, (ulong)i << 20, buffer);
                    if (i % 32 == 31) Sample();
                }
            Sample();
        }

        var arc = ZfsUserModeEngine.ArcStats;
        Put("arc_max", arc.Max);
        Put("arc_min", arc.Min);
        Put("arc_peak", peakArc);
        Put("private_baseline", baseline);
        Put("private_peak", peakPrivate);
        Put("private_after_work", Private());

        // Export flushes the pool's buffers from the ARC asynchronously
        // (dsl_pool_close(): arc_flush_async()).
        ZfsUserModeEngine.ExportPool(pool);
        var flush = Stopwatch.StartNew();
        while (ZfsUserModeEngine.ArcStats.Size > 8 << 20 && flush.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(50);
        Put("arc_after_export", ZfsUserModeEngine.ArcStats.Size);
        Put("export_flush_ms", flush.ElapsedMilliseconds);
        Put("private_after_export", Private());

        Put("stop_errno", ZfsUserModeEngine.Stop());
        Put("private_after_stop", Private());
        return 0;
    }

    // Engine start/stop, again and again, each time with a pool used and
    // destroyed: nothing may stay behind per cycle.
    private static int Cycles(string dir, int count)
    {
        _ = ZfsUserModeEngine.Version;
        var data = Data.Random(1 << 20, 11);
        Put("private_baseline", Private());
        for (var cycle = 1; cycle <= count; cycle++)
        {
            var pool = $"zut_cycle{cycle}";
            Start(dir);
            var image = NewImage(dir, $"cycle{cycle}.img", 256L << 20);
            ZfsUserModeEngine.CreatePool(pool, "", [image], "ashift=12");
            using (var fs = ZfsUserModeEngine.Open(dir, pool))
            {
                var obj = fs.CreateFile("/data.bin");
                for (var i = 0; i < 64; i++) fs.Write(obj, (ulong)i << 20, data);
            }
            ZfsUserModeEngine.DestroyPool(pool);
            var errno = ZfsUserModeEngine.Stop();
            File.Delete(image);
            Put($"cycle{cycle}_stop_errno", errno);
            Put($"cycle{cycle}_private", Private());
        }
        return 0;
    }

    private static int ImportZeroed(string dir)
    {
        Start(dir);
        NewImage(dir, "zeroed.img", 128L << 20);
        Put("import_errno", ZfsUserModeEngine.TryImportPool("zut_none", dir));
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }

    private static int ImportGarbage(string dir)
    {
        Start(dir);
        var image = NewImage(dir, "garbage.img", 128L << 20);
        Data.Corrupt(image, 0, 128L << 20, 5);
        Put("import_errno", ZfsUserModeEngine.TryImportPool("zut_none", dir));
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }

    // All four labels gone (two in front, two at the end, 256 KiB each).
    private static int ImportLabelsWiped(string dir)
    {
        Start(dir);
        var (image, _) = ExportedPool(dir, "zut_wiped");
        var length = new FileInfo(image).Length;
        Data.Corrupt(image, 0, 512 << 10, 1);
        Data.Corrupt(image, length - (512 << 10), 512 << 10, 2);
        Put("import_errno", ZfsUserModeEngine.TryImportPool("zut_wiped", dir));
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }

    // Labels 0 and 1 damaged: the two at the end must still import it.
    private static int ImportFrontLabelsDamaged(string dir)
    {
        Start(dir);
        var (image, hash) = ExportedPool(dir, "zut_front");
        Data.Corrupt(image, 0, 512 << 10, 3);
        TryImportAndRead(dir, "zut_front", hash);
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }

    // Half of the image gone, the end labels with it.
    private static int ImportTruncated(string dir)
    {
        Start(dir);
        var (image, hash) = ExportedPool(dir, "zut_truncated");
        using (var stream = new FileStream(image, FileMode.Open)) stream.SetLength(64L << 20);
        TryImportAndRead(dir, "zut_truncated", hash);
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }

    // An image file Windows marks read-only: a read-write import must
    // fail without writing to it; a read-only import must work.
    private static int ImportReadOnlyFile(string dir)
    {
        Start(dir);
        var (image, hash) = ExportedPool(dir, "zut_rofile");
        var before = Data.HashFile(image);
        File.SetAttributes(image, FileAttributes.ReadOnly);
        try
        {
            var rw = ZfsUserModeEngine.TryImportPool("zut_rofile", dir);
            Put("rw_import_errno", rw);
            // Imported read-write after all: out again before the read-only try.
            if (rw == 0) ZfsUserModeEngine.ExportPool("zut_rofile", force: true);
            TryImportAndRead(dir, "zut_rofile", hash, readOnly: true);
        }
        finally
        {
            File.SetAttributes(image, FileAttributes.Normal);
        }
        Put("image_unchanged", Data.HashFile(image) == before ? 1 : 0);
        Put("stop_errno", ZfsUserModeEngine.Stop());
        return 0;
    }
}

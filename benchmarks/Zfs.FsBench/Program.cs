// File-system workload benchmark for comparing a ZFS volume (user mode or
// kernel) with NTFS/ReFS on the same machine (GitHub issue #33): small-file
// create/stat/enumerate/open/read/delete, with and without flushes, from one
// and several threads, and sequential and 4 KiB random I/O.  Every call is
// timed, so each phase reports operations per second and latency
// percentiles.
//
//   dotnet run -c Release --project benchmarks/Zfs.FsBench -- --root N:\ --label ntfs
//   options: --files 10000 --size 4096 --flush-files 500 --threads 8
//            --seq-mib 1024 --random-mib 256 --random-ops 20000 --json out.json
//            --only create,stat (phase names) --keep
//            --engine-stats [dataset]: per phase, the user-mode engine's WinFsp
//            requests (kstat zfswin:0:winfsp, or ...@<dataset> for one mount)
//            and their average time in the engine
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Zfs.Admin;

var o = Options.Parse(args);
int statsAt = Array.IndexOf(args, "--engine-stats");
if (statsAt >= 0 && statsAt + 1 < args.Length && !args[statsAt + 1].StartsWith("--"))
    EngineStats.Dataset = args[statsAt + 1];
var dir = Path.Combine(o.Root, "fsbench");
if (Directory.Exists(dir))
    Directory.Delete(dir, recursive: true);
Directory.CreateDirectory(dir);

var results = new List<Phase>();
var payload = new byte[o.Size];
new Random(1).NextBytes(payload);
string[] files = Enumerable.Range(0, o.Files)
    .Select(i => Path.Combine(dir, "small", $"d{i / 1000:D3}", $"f{i:D6}.dat")).ToArray();
foreach (var d in files.Select(Path.GetDirectoryName).Distinct())
    Directory.CreateDirectory(d!);

Run("create", files, f => { using var s = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096); s.Write(payload); });
Run("stat", files, f => { if (new FileInfo(f).Length != o.Size) throw new IOException($"{f}: wrong size"); });
Run("enumerate", [Path.Combine(dir, "small")], d =>
{
    long n = 0;
    foreach (var fi in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories))
        n += fi.Length >= 0 ? 1 : 0;
    if (n != o.Files) throw new IOException($"enumerated {n} of {o.Files}");
});
Run("open-close", files, f => File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
Run("read", files, f => { if (File.ReadAllBytes(f).Length != o.Size) throw new IOException($"{f}: short read"); });
Run("delete", files, File.Delete);

string[] flushed = Enumerable.Range(0, o.FlushFiles).Select(i => Path.Combine(dir, "flush", $"f{i:D6}.dat")).ToArray();
Directory.CreateDirectory(Path.Combine(dir, "flush"));
Run("create+flush", flushed, f => { using var s = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096); s.Write(payload); s.Flush(flushToDisk: true); });
Run("create+writethrough", flushed.Select(f => f + ".wt").ToArray(), f => { using var s = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); s.Write(payload); });

string[] parallelFiles = Enumerable.Range(0, o.Files).Select(i => Path.Combine(dir, "par", $"t{i % o.Threads:D2}", $"f{i:D6}.dat")).ToArray();
foreach (var d in parallelFiles.Select(Path.GetDirectoryName).Distinct())
    Directory.CreateDirectory(d!);
Run($"create x{o.Threads}", parallelFiles, f => { using var s = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096); s.Write(payload); }, o.Threads);
Run($"stat x{o.Threads}", parallelFiles, f => { if (new FileInfo(f).Length != o.Size) throw new IOException($"{f}: wrong size"); }, o.Threads);
Run($"read x{o.Threads}", parallelFiles, f => { if (File.ReadAllBytes(f).Length != o.Size) throw new IOException($"{f}: short read"); }, o.Threads);
Run($"delete x{o.Threads}", parallelFiles, File.Delete, o.Threads);

var big = Path.Combine(dir, "seq.dat");
Bytes("seq write 1M", (long)o.SeqMiB << 20, () => Sequential(big, write: true, unbuffered: false));
Bytes("seq read 1M", (long)o.SeqMiB << 20, () => Sequential(big, write: false, unbuffered: false));
Bytes("seq read 1M unbuffered", (long)o.SeqMiB << 20, () => Sequential(big, write: false, unbuffered: true));
File.Delete(big);

var rnd = Path.Combine(dir, "random.dat");
long rndBytes = (long)o.RandomMiB << 20;
Sequential(rnd, write: true, unbuffered: false, bytes: rndBytes);
RandomIo("random 4K read unbuffered", rnd, rndBytes, write: false);
RandomIo("random 4K write", rnd, rndBytes, write: true);

if (!o.Keep)
    Directory.Delete(dir, recursive: true);

Console.WriteLine();
Console.WriteLine($"{o.Label} ({o.Root}): {o.Files} files of {o.Size} B, {o.FlushFiles} flushed, {o.Threads} threads");
Console.WriteLine($"{"phase",-28}{"ops/s",12}{"MiB/s",10}{"p50 ms",10}{"p95 ms",10}{"p99 ms",10}{"max ms",10}{"total s",10}");
foreach (var p in results)
    Console.WriteLine($"{p.Name,-28}{p.OpsPerSecond,12:F0}{p.MiBPerSecond,10:F1}{p.P50,10:F3}{p.P95,10:F3}{p.P99,10:F3}{p.Max,10:F3}{p.Seconds,10:F2}");
if (o.EngineStats)
{
    Console.WriteLine();
    Console.WriteLine("engine requests per phase (count x average microseconds in zfswin-um; max)");
    foreach (var p in results.Where(p => p.Engine is not null))
        Console.WriteLine($"{p.Name,-28}{string.Join("  ", p.Engine!.Select(e => $"{e.Kind} {e.Count}x{e.AverageMicroseconds:F0}us (max {e.MaxMicroseconds:F0})"))}");
}
if (o.Json is not null)
    File.WriteAllText(o.Json, JsonSerializer.Serialize(new { o.Label, o.Root, o.Files, o.Size, o.FlushFiles, o.Threads, o.SeqMiB, o.RandomMiB, o.RandomOps, Phases = results }, new JsonSerializerOptions { WriteIndented = true }));

void Run(string name, string[] items, Action<string> op, int threads = 1)
{
    if (!o.Selected(name))
        return;
    var lat = new double[items.Length];
    var before = EngineStats.Read(o.EngineStats);
    var total = Stopwatch.StartNew();
    if (threads <= 1)
    {
        for (int i = 0; i < items.Length; i++)
            lat[i] = Timed(() => op(items[i]));
    }
    else
    {
        Parallel.For(0, items.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => lat[i] = Timed(() => op(items[i])));
    }
    results.Add(Phase.Of(name, lat, total.Elapsed.TotalSeconds, bytes: 0) with { Engine = EngineStats.Delta(before, EngineStats.Read(o.EngineStats)) });
}

void Bytes(string name, long bytes, Action op)
{
    if (!o.Selected(name))
        return;
    var before = EngineStats.Read(o.EngineStats);
    var total = Stopwatch.StartNew();
    double ms = Timed(op);
    results.Add(Phase.Of(name, [ms], total.Elapsed.TotalSeconds, bytes) with { Engine = EngineStats.Delta(before, EngineStats.Read(o.EngineStats)) });
}

void Sequential(string path, bool write, bool unbuffered, long bytes = -1)
{
    bytes = bytes < 0 ? (long)o.SeqMiB << 20 : bytes;
    const int chunk = 1 << 20;
    var options = unbuffered ? (FileOptions)0x20000000 : FileOptions.SequentialScan;
    using var h = File.OpenHandle(path, write ? FileMode.Create : FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, options);
    using var buf = new Aligned(chunk);
    if (write)
        new Random(2).NextBytes(buf.Span);
    for (long at = 0; at < bytes; at += chunk)
    {
        if (write)
            RandomAccess.Write(h, buf.Span, at);
        else if (RandomAccess.Read(h, buf.Span, at) != chunk)
            throw new IOException($"{path}: short read at {at}");
    }
    if (write)
        RandomAccess.FlushToDisk(h);
}

void RandomIo(string name, string path, long bytes, bool write)
{
    if (!o.Selected(name))
        return;
    var options = write ? FileOptions.RandomAccess : (FileOptions)0x20000000 | FileOptions.RandomAccess;
    using var h = File.OpenHandle(path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, options);
    using var buf = new Aligned(4096);
    new Random(3).NextBytes(buf.Span);
    var rng = new Random(4);
    long blocks = bytes / 4096;
    var lat = new double[o.RandomOps];
    var before = EngineStats.Read(o.EngineStats);
    var total = Stopwatch.StartNew();
    for (int i = 0; i < lat.Length; i++)
    {
        long at = rng.NextInt64(blocks) * 4096;
        lat[i] = Timed(() =>
        {
            if (write)
                RandomAccess.Write(h, buf.Span, at);
            else
                RandomAccess.Read(h, buf.Span, at);
        });
    }
    if (write)
        RandomAccess.FlushToDisk(h);
    results.Add(Phase.Of(name, lat, total.Elapsed.TotalSeconds, (long)lat.Length * 4096) with { Engine = EngineStats.Delta(before, EngineStats.Read(o.EngineStats)) });
}

static double Timed(Action a)
{
    long t = Stopwatch.GetTimestamp();
    a();
    return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
}

sealed record EngineKind(string Kind, long Count, double AverageMicroseconds, double MaxMicroseconds);

// The user-mode engine's request statistics (kstat zfswin:0:winfsp), as
// "<kind>_count / _ns / _max_ns" values; null when not asked for or no
// user-mode engine answers.
static class EngineStats
{
    public static string? Dataset { get; set; }

    public static Dictionary<string, long>? Read(bool enabled)
    {
        if (!enabled)
            return null;
        string spec = Dataset is null ? "zfswin:0:winfsp" : $"zfswin:0:winfsp@{Dataset}";
        string? text = UserModeBridge.ReadKstatAsync(spec, TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
        if (text is null)
            return null;
        var values = new Dictionary<string, long>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2 && long.TryParse(parts[1], out long v))
                values[parts[0][(parts[0].LastIndexOf(':') + 1)..]] = v;
        }
        return values;
    }

    // Requests made during a phase; the max is the engine's running maximum.
    public static EngineKind[]? Delta(Dictionary<string, long>? before, Dictionary<string, long>? after)
    {
        if (before is null || after is null)
            return null;
        return after.Keys.Where(k => k.EndsWith("_count")).Select(k => k[..^"_count".Length]).Select(kind =>
        {
            long count = after[kind + "_count"] - before.GetValueOrDefault(kind + "_count");
            long ns = after.GetValueOrDefault(kind + "_ns") - before.GetValueOrDefault(kind + "_ns");
            return new EngineKind(kind, count, count == 0 ? 0 : ns / 1000.0 / count, after.GetValueOrDefault(kind + "_max_ns") / 1000.0);
        }).Where(e => e.Count > 0).OrderByDescending(e => e.Count * e.AverageMicroseconds).ToArray();
    }
}

sealed record Phase(string Name, int Ops, double Seconds, double OpsPerSecond, double MiBPerSecond, double P50, double P95, double P99, double Max)
{
    public EngineKind[]? Engine { get; init; }

    public static Phase Of(string name, double[] lat, double seconds, long bytes)
    {
        var s = (double[])lat.Clone();
        Array.Sort(s);
        double P(double q) => s.Length == 0 ? 0 : s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)];
        return new Phase(name, lat.Length, seconds, lat.Length / seconds, bytes / 1048576.0 / seconds, P(0.50), P(0.95), P(0.99), s.Length == 0 ? 0 : s[^1]);
    }
}

sealed unsafe class Aligned : IDisposable
{
    readonly void* _p;
    readonly int _n;
    public Aligned(int n) { _n = n; _p = NativeMemory.AlignedAlloc((nuint)n, 4096); }
    public Span<byte> Span => new(_p, _n);
    public void Dispose() => NativeMemory.AlignedFree(_p);
}

sealed record Options(string Root, string Label, int Files, int Size, int FlushFiles, int Threads, int SeqMiB, int RandomMiB, int RandomOps, string? Json, string[]? Only, bool Keep, bool EngineStats)
{
    public bool Selected(string phase) => Only is null || Only.Any(p => phase.Equals(p, StringComparison.OrdinalIgnoreCase));

    public static Options Parse(string[] a)
    {
        string? Get(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        int Int(string name, int d) => Get(name) is string v ? int.Parse(v) : d;
        string root = Get("--root") ?? throw new ArgumentException("--root <folder> is required");
        return new Options(root, Get("--label") ?? root, Int("--files", 10000), Int("--size", 4096), Int("--flush-files", 500),
            Int("--threads", 8), Int("--seq-mib", 1024), Int("--random-mib", 256), Int("--random-ops", 20000), Get("--json"),
            Get("--only")?.Split(','), a.Contains("--keep"), a.Contains("--engine-stats"));
    }
}

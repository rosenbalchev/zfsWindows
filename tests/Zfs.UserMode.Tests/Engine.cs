using System.Diagnostics;
using System.Security.Cryptography;
using Zfs.Interop;
using Xunit;

// The engine is one per process: tests that share it run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Zfs.UserMode.Tests;

// The engine for the tests that run in the test host, started once.
public sealed class EngineFixture : IDisposable
{
    public EngineFixture()
    {
        Directory.CreateDirectory(Scratch.Root);
        ZfsUserModeEngine.Start(Path.Combine(Scratch.Root, "zpool.cache"));
    }

    public void Dispose() => ZfsUserModeEngine.Stop();
}

[CollectionDefinition(Name)]
public sealed class EngineCollection : ICollectionFixture<EngineFixture>
{
    public const string Name = "engine";
}

// Image files under %TEMP%\zfswin-um-tests, one directory per test.
public sealed class Scratch : IDisposable
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "zfswin-um-tests");
    private static int _pools;

    public Scratch()
    {
        Directory = System.IO.Directory.CreateDirectory(
            Path.Combine(Root, Guid.NewGuid().ToString("N")[..12])).FullName;
    }

    public string Directory { get; }

    // A unique pool name: the engine keeps every imported pool by name.
    public static string PoolName(string prefix) =>
        $"{prefix}{Environment.ProcessId % 10000}x{Interlocked.Increment(ref _pools)}";

    public string Image(string name, long bytes = 128L << 20)
    {
        var path = Path.Combine(Directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew);
        stream.SetLength(bytes);
        return path;
    }

    public string[] Images(int count, long bytes = 128L << 20) =>
        Enumerable.Range(0, count).Select(i => Image($"disk{i}.img", bytes)).ToArray();

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Data
{
    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Text-like and highly compressible.
    public static byte[] Compressible(int length)
    {
        var line = "The quick brown fox jumps over the lazy dog 0123456789\n"u8;
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = line[i % line.Length];
        return data;
    }

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    // A file of any size, read in 1 MiB pieces.
    public static string Hash(ZfsUserModeFileSystem fs, ulong obj)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var size = fs.GetAttributes(obj).Size;
        for (ulong offset = 0; offset < size;)
        {
            var n = fs.Read(obj, offset, buffer);
            if (n == 0) break;
            hash.AppendData(buffer, 0, n);
            offset += (ulong)n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Overwrite [offset, offset + length) of an image with random bytes.
    public static void Corrupt(string image, long offset, long length, int seed)
    {
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Write);
        var random = new Random(seed);
        var buffer = new byte[1 << 20];
        stream.Position = offset;
        for (long done = 0; done < length; done += buffer.Length)
        {
            random.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, length - done));
        }
    }
}

// A scenario of Program.cs in a process of its own: a clean memory
// baseline, an engine that can be stopped, and a crash that fails only the
// test that caused it.
public static class Child
{
    public sealed record Result(int ExitCode, string Output, Dictionary<string, string> Values)
    {
        public long Long(string key) => long.Parse(Values[key]);
        public int Int(string key) => int.Parse(Values[key]);
    }

    public static Result Run(params string[] args)
    {
        var exe = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(true);
            throw new TimeoutException($"scenario {string.Join(' ', args)} did not finish");
        }
        var output = stdout + stderr.Result;
        var values = stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains('=') && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1]);
        return new Result(process.ExitCode, output, values);
    }

    public static Result RunOk(params string[] args)
    {
        var result = Run(args);
        Assert.True(result.ExitCode == 0,
            $"scenario {args[0]} exited with 0x{result.ExitCode:X}:\n{result.Output}");
        return result;
    }
}

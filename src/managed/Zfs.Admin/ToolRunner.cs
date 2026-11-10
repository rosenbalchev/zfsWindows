using System.Diagnostics;
using System.Text;

namespace Zfs.Admin;

public enum ZfsTool { Zpool, Zfs }

public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A kstat's `kstat -p` text and where it came from.</summary>
public sealed record KstatText(string Text, string Source);

/// <summary>Runs zpool.exe or zfs.exe; the seam that the protocol tests mock.</summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(ZfsTool tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>A kstat ("zfs:0:arcstats") of whichever edition runs; null when neither answers.</summary>
    Task<KstatText?> ReadKstatAsync(string kstat, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<KstatText?>(null);

    /// <summary>Walks a mounted dataset (dataset.count).</summary>
    FileCount CountFiles(string root, TimeSpan limit, CancellationToken cancellationToken) => FileCounter.Count(root, cancellationToken, limit);

    /// <summary>Private bytes of the user-mode service's process, where the ARC lives in that edition.</summary>
    long? UserModeServicePrivateBytes() => null;
}

/// <summary>
/// Starts the upstream OpenZFS tools from the installation directory with an
/// explicit argument vector, so no shell ever parses caller input.  Output is
/// bounded; a timeout or cancellation kills the whole process tree.
/// </summary>
public sealed class ProcessToolRunner(string toolDirectory) : IToolRunner
{
    public const int MaxOutputChars = 8 * 1024 * 1024;

    public string ToolDirectory { get; } = toolDirectory;

    public async Task<ToolResult> RunAsync(ZfsTool tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string exe = Path.Combine(ToolDirectory, tool == ZfsTool.Zpool ? "zpool.exe" : "zfs.exe");
        if (!File.Exists(exe))
            throw new AdminException(ErrorCode.Internal, $"{exe} is missing; repair the installation.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ToolDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in arguments)
            start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        process.Start();
        // The tools never prompt when given complete arguments; a closed
        // stdin makes any unexpected prompt fail instead of hanging.
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
                throw new AdminException(ErrorCode.Cancelled, $"{Describe(tool, arguments)} was cancelled.");
            throw new AdminException(ErrorCode.Timeout, $"{Describe(tool, arguments)} did not finish within {timeout.TotalSeconds:0} s.");
        }
        var (outText, outTruncated) = await stdout.ConfigureAwait(false);
        var (errText, _) = await stderr.ConfigureAwait(false);
        if (outTruncated)
            throw new AdminException(ErrorCode.Internal, $"{Describe(tool, arguments)} produced more than {MaxOutputChars} characters of output.");
        return new ToolResult(process.ExitCode, outText, errText);
    }

    /// <summary>
    /// The kernel driver's kstats through kstat.exe (libkstat on \\.\ZFS);
    /// without the driver, the user-mode service's through its bridge pipe.
    /// </summary>
    public async Task<KstatText?> ReadKstatAsync(string kstat, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string exe = Path.Combine(ToolDirectory, "kstat.exe");
        if (File.Exists(exe))
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = ToolDirectory, StandardOutputEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(kstat);
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = ReadBoundedAsync(process.StandardOutput);
            var stderr = ReadBoundedAsync(process.StandardError);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw new AdminException(cancellationToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.Timeout, $"kstat {kstat} did not finish.");
            }
            var (text, _) = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            // kstat_open() fails without \\.\ZFS: the user-mode edition.
            if (process.ExitCode == 0 && text.Contains('\t'))
                return new KstatText(text, "kernel");
        }
        string? bridged = await UserModeBridge.ReadKstatAsync(kstat, timeout, cancellationToken).ConfigureAwait(false);
        return bridged is null ? null : new KstatText(bridged, "user-mode");
    }

    public long? UserModeServicePrivateBytes()
    {
        foreach (Process p in Process.GetProcessesByName("zfswin-um"))
        {
            using (p)
            {
                try
                {
                    return p.PrivateMemorySize64;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return null;
    }

    static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        bool truncated = false;
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + n > MaxOutputChars)
                truncated = true;	// keep draining so the child never blocks on a full pipe
            else
                builder.Append(buffer, 0, n);
        }
        return (builder.ToString(), truncated);
    }

    internal static string Describe(ZfsTool tool, IReadOnlyList<string> arguments) =>
        $"{(tool == ZfsTool.Zpool ? "zpool" : "zfs")} {(arguments.Count > 0 ? arguments[0] : "")}".TrimEnd();
}

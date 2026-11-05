using System.Diagnostics;
using System.Text;

namespace Zfs.Admin;

public enum ZfsTool { Zpool, Zfs }

public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs zpool.exe or zfs.exe; the seam that the protocol tests mock.</summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(ZfsTool tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
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

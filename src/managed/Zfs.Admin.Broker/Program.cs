using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Zfs.Admin;

namespace Zfs.Admin.Broker;

/// <summary>
/// ZfsWinBroker: the Windows service that runs privileged ZFS commands for
/// zfswin.exe and the ZfsWin PowerShell module (plan step 14).  It runs the
/// zpool.exe and zfs.exe installed next to it.  `ZfsWinBroker.exe --console`
/// runs it in the foreground for diagnostics (elevated prompt required).
/// </summary>
public static class Program
{
    public const string ServiceName = "ZfsWinBroker";

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--console")
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.WriteLine($"{ServiceName} {typeof(Program).Assembly.GetName().Version?.ToString(3)}: serving \\\\.\\pipe\\{Protocol.PipeName}; Ctrl+C stops.");
            _ = RemoveTemporaryMountsAsync(Console.WriteLine, stop.Token);
            CreateServer(entry => Console.WriteLine(Describe(entry))).RunAsync(stop.Token).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--setup")
            return Setup.Run(args);
        if (args.Length != 0)
        {
            Console.Error.WriteLine($"usage: {ServiceName}.exe [--console | --setup <step> ...]");
            return 2;
        }
        ServiceBase.Run(new BrokerService());
        return 0;
    }

    internal static BrokerServer CreateServer(Action<AuditEntry> audit) =>
        new(new ProcessToolRunner(AppContext.BaseDirectory), new WindowsCallerAuthenticator(), new BrokerOptions(), audit);

    /// <summary>
    /// Destroys the temporary snapshot mounts (read-only clones) a GUI left
    /// behind: after a crash, a reboot, or "keep mounted".  Pools may still
    /// be importing when the broker starts (the user-mode service imports
    /// its pools at its own start), so it looks again a little later.
    /// </summary>
    internal static async Task RemoveTemporaryMountsAsync(Action<string> log, CancellationToken stopping)
    {
        var runner = new ProcessToolRunner(AppContext.BaseDirectory);
        foreach (TimeSpan delay in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) })
        {
            try
            {
                await Task.Delay(delay, stopping).ConfigureAwait(false);
                foreach (string clone in await Operations.RemoveTemporaryMountsAsync(runner, stopping).ConfigureAwait(false))
                    log($"Removed the temporary snapshot mount {clone}.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AdminException)
            {
                // zfs.exe missing or no pool yet; the next round tries again.
            }
        }
    }

    internal static string Describe(AuditEntry e)
    {
        var text = new StringBuilder();
        text.Append(e.Ok ? "Completed " : "Refused or failed ").Append(e.Operation)
            .Append(" for ").Append(e.Caller)
            .Append(" in ").Append(e.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
        if (e.ErrorCode is not null)
            text.Append(" (").Append(e.ErrorCode).Append(')');
        text.Append(". Arguments: ").Append(e.Arguments.ToJsonString());
        return text.ToString();
    }
}

sealed class BrokerService : ServiceBase
{
    // Registered by the installer with the .NET Framework's EventLogMessages.dll.
    const string EventSource = Program.ServiceName;

    CancellationTokenSource? stop;
    Task? running;

    public BrokerService()
    {
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        stop = new CancellationTokenSource();
        running = Task.Run(() => Program.CreateServer(Audit).RunAsync(stop.Token));
        _ = Program.RemoveTemporaryMountsAsync(m => Write(m, EventLogEntryType.Information, 3), stop.Token);
        Write($"{ServiceName} {typeof(Program).Assembly.GetName().Version?.ToString(3)} started (protocol {Protocol.Version}).", EventLogEntryType.Information, 1);
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    void Shutdown()
    {
        stop?.Cancel();
        try
        {
            // Running ZFS commands are cancelled (their process trees killed).
            running?.Wait(TimeSpan.FromSeconds(20));
        }
        catch (AggregateException)
        {
        }
        Write($"{ServiceName} stopped.", EventLogEntryType.Information, 2);
    }

    static void Audit(AuditEntry entry) =>
        Write(Program.Describe(entry), entry.Ok ? EventLogEntryType.Information : EventLogEntryType.Warning, entry.Ok ? 100 : 101);

    static void Write(string message, EventLogEntryType type, int id)
    {
        try
        {
            EventLog.WriteEntry(EventSource, message, type, id);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException)
        {
            // The event source is missing (not installed by setup); auditing
            // must never stop the broker.
        }
    }
}

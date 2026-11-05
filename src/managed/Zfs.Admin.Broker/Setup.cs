using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using Microsoft.Win32;
using Zfs.Admin;

namespace Zfs.Admin.Broker;

/// <summary>
/// Setup steps the MSI runs as deferred custom actions
/// (`ZfsWinBroker.exe --setup <step> ...`).  Each prints what it checks or
/// does, so the MSI log records it, and exits 0 on success, 1 on a refusal
/// and 2 on bad arguments.  zfsinstaller.exe does the driver-store work
/// (SetupCopyOEMInf, UpdateDriverForPlugAndPlayDevices, SetupUninstallOEMInf);
/// its exit code does not reflect install failures, so every step checks the
/// result itself.
/// </summary>
static class Setup
{
    public const int MinimumClientBuild = 18362;	// Windows 10 1903, the catalog's 10_19H1 floor
    public const int MinimumServerBuild = 17763;	// Windows Server 2019

    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var options = ParseOptions(args);
        if (options is null)
            return Usage();
        string bin = AppContext.BaseDirectory;
        try
        {
            return args[1] switch
            {
                "preflight" => Preflight(options),
                "check-pools" => CheckPools(bin, options.ContainsKey("--export")),
                "driver-install" => DriverInstall(bin, Required(options, "--driver-dir")),
                "driver-uninstall" => DriverUninstall(bin, Required(options, "--driver-dir")),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or AdminException)
        {
            Console.Error.WriteLine($"setup {args[1]}: {e.Message}");
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            usage: ZfsWinBroker.exe --setup preflight [--test-signing-thumbprint <sha1>] [--allow-pending-reboot] [--fresh-install]
                   ZfsWinBroker.exe --setup check-pools [--export]
                   ZfsWinBroker.exe --setup driver-install --driver-dir <dir>
                   ZfsWinBroker.exe --setup driver-uninstall --driver-dir <dir>
            """);
        return 2;
    }

    /// <summary>"--setup step --name [value] ..." options, or null if malformed.</summary>
    internal static Dictionary<string, string?>? ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i++)
        {
            string name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
                return null;
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            options[name] = value;
        }
        return options;
    }

    static string Required(Dictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out string? v) && !string.IsNullOrEmpty(v) ? v : throw new InvalidOperationException($"{name} is required.");

    static int Refuse(string message)
    {
        Console.Error.WriteLine("REFUSED: " + message);
        return 1;
    }

    // --- preflight -------------------------------------------------------------

    static int Preflight(Dictionary<string, string?> options)
    {
        Version os = Environment.OSVersion.Version;	// the real version (no compatibility shim in .NET)
        bool server = IsServer();
        int floor = server ? MinimumServerBuild : MinimumClientBuild;
        Console.WriteLine($"Windows {(server ? "Server" : "client")} {os}, {RuntimeInformation.OSArchitecture}; minimum build {floor}.");
        if (os.Major < 10 || os.Build < floor)
            return Refuse($"OpenZFS for Windows needs Windows 10 version 1903 (build {MinimumClientBuild}) or Windows Server 2019 (build {MinimumServerBuild}) or later.");
        if (RuntimeInformation.OSArchitecture != RuntimeInformation.ProcessArchitecture)
            return Refuse($"This package is for {RuntimeInformation.ProcessArchitecture}; this system is {RuntimeInformation.OSArchitecture}.");

        if (options.ContainsKey("--fresh-install") && ServiceExists("OpenZFS"))
            return Refuse("An OpenZFS driver that this package did not install is present (service OpenZFS). Remove it first (zfsinstaller uninstall, or pnputil /delete-driver), restart, and run setup again.");

        string? pending = PendingReboot();
        if (pending is not null)
        {
            if (!options.ContainsKey("--allow-pending-reboot"))
                return Refuse($"A reboot is pending ({pending}). Restart Windows, then run setup again (or set ZFSWIN_ALLOW_PENDING_REBOOT=1).");
            Console.WriteLine($"A reboot is pending ({pending}); continuing because ZFSWIN_ALLOW_PENDING_REBOOT is set.");
        }

        bool secureBoot = SecureBootEnabled();
        var (testSigning, integrity) = CodeIntegrity();
        Console.WriteLine($"Secure Boot: {(secureBoot ? "on" : "off")}; test signing: {(testSigning ? "on" : "off")}; code integrity options 0x{integrity:X}.");
        if (options.TryGetValue("--test-signing-thumbprint", out string? thumbprint) && !string.IsNullOrEmpty(thumbprint))
        {
            // A test-signed package: Windows loads it only with test signing on
            // (impossible with Secure Boot) and the certificate trusted.
            if (!testSigning)
                return Refuse(secureBoot
                    ? "This is a test-signed build. Its driver cannot load while Secure Boot is on; use a release (attestation-signed) build."
                    : "This is a test-signed build. Enable test signing (bcdedit /set testsigning on), restart, and run setup again.");
            foreach (StoreName store in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var s = new X509Store(store, StoreLocation.LocalMachine);
                s.Open(OpenFlags.ReadOnly);
                if (s.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count == 0)
                    return Refuse($"The test-signing certificate {thumbprint} is not in the local machine's {store} store. Import zfswindows-test.cer into Root and TrustedPublisher first.");
            }
            Console.WriteLine($"Test-signing certificate {thumbprint} is trusted.");
        }
        Console.WriteLine("Preflight passed.");
        return 0;
    }

    static bool IsServer()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return key?.GetValue("ProductType") is string type && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The standard servicing reboot flags (not PendingFileRenameOperations, which is almost always set).</summary>
    static string? PendingReboot()
    {
        (string Key, string Name)[] flags =
        [
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", "Component Based Servicing"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", "Windows Update"),
        ];
        foreach (var (key, name) in flags)
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(key);
            if (k is not null)
                return name;
        }
        return null;
    }

    static bool SecureBootEnabled()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int v && v != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemCodeIntegrityInformation
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, ref SystemCodeIntegrityInformation info, int length, out int returned);

    static (bool TestSigning, uint Options) CodeIntegrity()
    {
        const int SystemCodeIntegrityInformationClass = 103;
        const uint TestSign = 0x02;
        var info = new SystemCodeIntegrityInformation { Length = (uint)Marshal.SizeOf<SystemCodeIntegrityInformation>() };
        int status = NtQuerySystemInformation(SystemCodeIntegrityInformationClass, ref info, (int)info.Length, out _);
        if (status != 0)
            throw new InvalidOperationException($"NtQuerySystemInformation(SystemCodeIntegrityInformation) failed: 0x{status:X8}.");
        return ((info.CodeIntegrityOptions & TestSign) != 0, info.CodeIntegrityOptions);
    }

    // --- pools -------------------------------------------------------------------

    /// <summary>
    /// Upgrade and removal must not pull the driver out from under imported
    /// pools.  Refuses while any pool is imported; with --export, exports
    /// each pool first (never forced) and refuses if one stays busy.
    /// </summary>
    static int CheckPools(string bin, bool export)
    {
        if (!DriverRunning())
        {
            Console.WriteLine("The OpenZFS driver is not running; no pool can be imported.");
            return 0;
        }
        var runner = new ProcessToolRunner(bin);
        ToolResult list = runner.RunAsync(ZfsTool.Zpool, ["list", "-H", "-o", "name"], TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
        if (list.ExitCode != 0)
            return Refuse($"zpool list failed ({list.ExitCode}): {list.StandardError.Trim()}");
        string[] pools = list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pools.Length == 0)
        {
            Console.WriteLine("No pools are imported.");
            return 0;
        }
        Console.WriteLine($"Imported pools: {string.Join(", ", pools)}.");
        if (!export)
            return Refuse($"Pools are imported ({string.Join(", ", pools)}). Export them (zfswin pool export <pool>) or set ZFSWIN_EXPORT_POOLS=1, then run setup again. Pool data is never deleted by setup.");
        foreach (string pool in pools)
        {
            ToolResult r = runner.RunAsync(ZfsTool.Zpool, ["export", pool], TimeSpan.FromMinutes(10), CancellationToken.None).GetAwaiter().GetResult();
            if (r.ExitCode != 0)
                return Refuse($"Exporting {pool} failed ({r.ExitCode}): {r.StandardError.Trim()} Close the files that use it and run setup again.");
            Console.WriteLine($"Exported {pool}.");
        }
        return 0;
    }

    // --- driver --------------------------------------------------------------------

    static int DriverInstall(string bin, string driverDir)
    {
        string zfsInf = Path.Combine(driverDir, "OpenZFS.inf"), zvolInf = Path.Combine(driverDir, "OpenZVOL.inf");
        foreach (string f in new[] { zfsInf, zvolInf, Path.Combine(driverDir, "OpenZFS.cat"), Path.Combine(driverDir, "OpenZFS.sys") })
        {
            if (!File.Exists(f))
                return Refuse($"{f} is missing from the package.");
        }
        // Repair, and an upgrade that ships the same driver build: reinstalling
        // makes zfsinstaller remove the loaded driver first, which the zvol
        // device vetoes until a restart (step-14 VM run).  Keep it.
        var staged = DriverPackagesStaged().Select(oem => InfDriverVer(Path.Combine(InfDirectory, oem))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? zfsVer = InfDriverVer(zfsInf), zvolVer = InfDriverVer(zvolInf);
        // Not when a removal attempt has already disabled it: zfsinstaller's
        // quiesce sets the start type to Disabled before it is vetoed, and
        // that driver would be gone after the next restart.
        if (zfsVer is not null && zvolVer is not null && staged.Contains(zfsVer) && staged.Contains(zvolVer) && DriverRunning() && !DriverDisabled())
        {
            Console.WriteLine($"The OpenZFS driver {zfsVer} is already installed and running; it is kept.");
            return 0;
        }
        int exit = RunLogged(Path.Combine(bin, "zfsinstaller.exe"), driverDir, "install", zfsInf, zvolInf);
        // zfsinstaller returns 0 even when the INF install fails, so verify.
        bool isStaged = DriverPackagesStaged().Any();
        bool running = WaitForDriver(TimeSpan.FromSeconds(30));
        Console.WriteLine($"zfsinstaller exit {exit}; driver package staged: {isStaged}; OpenZFS service running: {running}.");
        if (!isStaged || !running)
            return Refuse("The OpenZFS driver did not install and start. See the zfsinstaller output above and setupapi.dev.log.");
        return 0;
    }

    static int DriverUninstall(string bin, string driverDir)
    {
        int exit = RunLogged(Path.Combine(bin, "zfsinstaller.exe"), driverDir, "uninstall",
            Path.Combine(driverDir, "OpenZFS.inf"), Path.Combine(driverDir, "OpenZVOL.inf"));
        // zfsinstaller removes OpenZFS.inf but can leave OpenZVOL.inf staged
        // (seen in the step-14 rollback test); remove what is left the
        // supported way, without /force, so a package still in use stays.
        foreach (string oem in DriverPackagesStaged().ToArray())
        {
            int r = RunLogged(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), driverDir, "/delete-driver", oem, "/uninstall");
            Console.WriteLine($"pnputil /delete-driver {oem} /uninstall: exit {r}.");
        }
        string[] left = DriverPackagesStaged().ToArray();
        bool running = DriverRunning();
        Console.WriteLine($"zfsinstaller exit {exit}; OpenZFS packages left in the driver store: {(left.Length == 0 ? "none" : string.Join(", ", left))}; driver still loaded: {running}.");
        if (left.Length > 0)
            return Refuse("Driver packages remain in the driver store; remove them with pnputil /delete-driver <oemNN.inf> /uninstall.");
        if (running || exit == 3010)
            Console.WriteLine("REBOOT REQUIRED: the driver is still loaded and is removed at the next restart.");
        return 0;
    }

    static bool ServiceExists(string name) =>
        ServiceController.GetDevices().Concat(ServiceController.GetServices()).Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    static bool DriverRunning()
    {
        try
        {
            using var service = new ServiceController("OpenZFS");
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;	// not installed
        }
    }

    static bool DriverDisabled()
    {
        try
        {
            using var service = new ServiceController("OpenZFS");
            return service.StartType == ServiceStartMode.Disabled;
        }
        catch (InvalidOperationException)
        {
            return false;	// not installed
        }
    }

    static bool WaitForDriver(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (DriverRunning())
                return true;
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(1000);
        }
    }

    static string InfDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    /// <summary>The DriverVer value of an INF ("10/28/2026,9.51.8.339"), or null.</summary>
    internal static string? InfDriverVer(string inf)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(inf), @"^\s*DriverVer\s*=\s*([^;\r\n]+?)\s*(;.*)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
            return m.Success ? System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\s+", "") : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Published names (oemNN.inf) of staged OpenZFS/OpenZVOL driver packages.</summary>
    static IEnumerable<string> DriverPackagesStaged()
    {
        foreach (string oem in Directory.EnumerateFiles(InfDirectory, "oem*.inf"))
        {
            string text;
            try
            {
                text = File.ReadAllText(oem);
            }
            catch (IOException)
            {
                continue;
            }
            // Each INF names its own catalog; the service name is a %string%.
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*CatalogFile\s*=\s*OpenZ(FS|VOL)\.cat\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline))
                yield return Path.GetFileName(oem);
        }
    }

    static int RunLogged(string exe, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string a in arguments)
            start.ArgumentList.Add(a);
        Console.WriteLine($"> {Path.GetFileName(exe)} {string.Join(' ', arguments)}");
        using Process p = Process.Start(start) ?? throw new InvalidOperationException($"{exe} did not start.");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("  " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("  " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{exe} did not finish within 5 minutes.");
        }
        p.WaitForExit();
        return p.ExitCode;
    }
}

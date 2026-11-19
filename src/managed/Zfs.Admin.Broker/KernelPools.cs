using System.ServiceProcess;
using System.Text.RegularExpressions;
using Zfs.Admin;

namespace Zfs.Admin.Broker;

/// <summary>
/// Imports the kernel driver's pools at boot (GitHub issue #46).  The driver
/// keeps zpool.cache up to date (a pool is in it from its import or creation
/// to its export) but, as OpenZFS 2.x on every platform, does not read it
/// when it loads; on Linux zfs-import-cache.service runs `zpool import -c`.
/// The user-mode edition restores its pools from its own records (its
/// service), so this runs only where the OpenZFS driver is installed.
/// </summary>
static class KernelPools
{
    const string DriverService = "OpenZFS";

    static string CacheFile => Path.Combine(Environment.SystemDirectory, "drivers", "zpool.cache");

    /// <summary>
    /// Disks can arrive after the broker starts (USB, iSCSI, a slow
    /// controller): a pool not found yet is looked for again.
    /// </summary>
    static readonly TimeSpan[] Rounds = [TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(10);

    public static async Task ImportAsync(Action<string, bool> log, CancellationToken stopping)
    {
        if (!DriverInstalled() || !File.Exists(CacheFile))
            return;
        if (!await StartDriverAsync(stopping).ConfigureAwait(false))
        {
            log($"The {DriverService} driver did not start; the pools in {CacheFile} were not imported.", false);
            return;
        }
        // The driver rewrites the file with the pools imported so far at each
        // import, which would drop a pool not imported yet: the boot's pools
        // are those of a copy taken now.
        string snapshot = Path.Combine(Path.GetTempPath(), $"zfswin-boot-{Guid.NewGuid():N}.cache");
        try
        {
            File.Copy(CacheFile, snapshot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Cannot read {CacheFile}: {e.Message}", false);
            return;
        }
        try
        {
            await ImportFromAsync(snapshot, log, stopping).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    static async Task ImportFromAsync(string snapshot, Action<string, bool> log, CancellationToken stopping)
    {
        var runner = new ProcessToolRunner(AppContext.BaseDirectory);
        // Only what was not imported yet: a pool exported meanwhile stays so.
        HashSet<string>? pending = null;
        var names = new Dictionary<string, string>();
        foreach (TimeSpan delay in Rounds)
        {
            try
            {
                await Task.Delay(delay, stopping).ConfigureAwait(false);
                // The pools of the file that are not imported.
                var listed = ParseImportList((await runner.RunAsync(ZfsTool.Zpool, ["import", "-c", snapshot], ImportTimeout, stopping).ConfigureAwait(false)).StandardOutput);
                foreach (CachedPool pool in listed)
                    names[pool.Guid] = pool.Name;
                pending ??= listed.Select(p => p.Guid).ToHashSet();
                foreach (string guid in pending.ToArray())
                {
                    // By the paths the file recorded; then, as when disks
                    // were renumbered, by a search of the disks.  By GUID, so
                    // another pool of the same name is not taken.
                    var r = await runner.RunAsync(ZfsTool.Zpool, ["import", "-c", snapshot, guid], ImportTimeout, stopping).ConfigureAwait(false);
                    if (r.ExitCode != 0)
                        r = await runner.RunAsync(ZfsTool.Zpool, ["import", guid], ImportTimeout, stopping).ConfigureAwait(false);
                    if (r.ExitCode == 0 || !listed.Any(p => p.Guid == guid))
                    {
                        pending.Remove(guid);
                        if (r.ExitCode == 0)
                            log($"Imported the pool {names[guid]} ({guid}) from {CacheFile}.", true);
                    }
                }
                if (pending.Count == 0)
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AdminException e) when (e.Code is not ErrorCode.Cancelled)
            {
                log($"Importing the pools in {CacheFile}: {e.Message}", false);
            }
            catch (AdminException)
            {
                return;
            }
        }
        if (pending is { Count: > 0 })
            log($"Not imported (their disks or images were not found): {string.Join(", ", pending.Select(g => names[g]))}.  `zpool import` lists what it can see.", false);
    }

    internal sealed record CachedPool(string Name, string Guid);

    /// <summary>The "pool:" and "id:" pairs of `zpool import`'s listing.</summary>
    internal static IReadOnlyList<CachedPool> ParseImportList(string text)
    {
        var pools = new List<CachedPool>();
        string? name = null;
        foreach (string line in text.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(pool|id):\s*(\S+)\s*$");
            if (!m.Success)
                continue;
            if (m.Groups[1].Value == "pool")
                name = m.Groups[2].Value;
            else if (name is not null && Regex.IsMatch(m.Groups[2].Value, "^[0-9]+$"))
            {
                pools.Add(new CachedPool(name, m.Groups[2].Value));
                name = null;
            }
        }
        return pools;
    }

    static bool DriverInstalled() =>
        ServiceController.GetDevices().Any(s => s.ServiceName.Equals(DriverService, StringComparison.OrdinalIgnoreCase));

    /// <summary>The driver is demand-start: nothing else loads it at boot.</summary>
    static async Task<bool> StartDriverAsync(CancellationToken stopping)
    {
        try
        {
            using var driver = new ServiceController(DriverService);
            if (driver.Status == ServiceControllerStatus.Stopped)
                driver.Start();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (true)
            {
                driver.Refresh();
                if (driver.Status == ServiceControllerStatus.Running)
                    return true;
                if (DateTime.UtcNow > deadline)
                    return false;
                await Task.Delay(1000, stopping).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

using System.Management;
using System.ServiceProcess;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>A physical disk as the Storage WMI provider (MSFT_Disk) reports it.</summary>
sealed record DiskInfo(int Number, string Name, long Size, bool IsBoot, bool IsSystem, string PartitionStyle)
{
    public string Device => $@"\\?\PhysicalDrive{Number}";
    public bool Protected => IsBoot || IsSystem;
    public override string ToString() =>
        $"Disk {Number}: {Name}, {Format.Bytes(Size)}, {PartitionStyle}{(Protected ? " (Windows disk, not offered)" : "")}";
}

static class Disks
{
    public static IReadOnlyList<DiskInfo> List()
    {
        var disks = new List<DiskInfo>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT Number, FriendlyName, Size, IsBoot, IsSystem, PartitionStyle FROM MSFT_Disk");
        foreach (ManagementBaseObject d in searcher.Get())
        {
            using (d)
            {
                ushort style = Convert.ToUInt16(d["PartitionStyle"] ?? 0);
                disks.Add(new DiskInfo(Convert.ToInt32(d["Number"]), d["FriendlyName"] as string ?? "disk",
                    Convert.ToInt64(d["Size"] ?? 0L), d["IsBoot"] is true, d["IsSystem"] is true,
                    style switch { 1 => "MBR", 2 => "GPT", _ => "not initialized" }));
            }
        }
        return disks.OrderBy(d => d.Number).ToList();
    }
}

/// <summary>
/// Windows' view of a physical disk's health (MSFT_PhysicalDisk and its
/// MSFT_StorageReliabilityCounter); a counter is null where the disk does
/// not report it.
/// </summary>
sealed record DriveHealth(int Number, string Name, string Health, string Status, string Media,
    int? TemperatureC, int? WearPercent, long? PowerOnHours, long? ReadErrorsCorrected, long? ReadErrorsUncorrected);

static class DriveHealthInfo
{
    public static IReadOnlyDictionary<int, DriveHealth> List()
    {
        var result = new Dictionary<int, DriveHealth>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_PhysicalDisk");
        foreach (ManagementObject d in searcher.Get().OfType<ManagementObject>())
        {
            using (d)
            {
                if (!int.TryParse(d["DeviceId"] as string, out int number))
                    continue;
                int? temperature = null, wear = null;
                long? hours = null, corrected = null, uncorrected = null;
                try
                {
                    foreach (ManagementObject c in d.GetRelated("MSFT_StorageReliabilityCounter").OfType<ManagementObject>())
                    {
                        using (c)
                        {
                            temperature = Int(c["Temperature"]) is int t and > 0 ? t : null;
                            wear = Int(c["Wear"]);
                            hours = Long(c["PowerOnHours"]);
                            corrected = Long(c["ReadErrorsCorrected"]);
                            uncorrected = Long(c["ReadErrorsUncorrected"]);
                        }
                    }
                }
                catch (Exception e) when (e is ManagementException or UnauthorizedAccessException)
                {
                    // Not every disk (or bus) reports reliability counters.
                }
                ushort health = Convert.ToUInt16(d["HealthStatus"] ?? (ushort)5);
                var status = d["OperationalStatus"] as ushort[] ?? [];
                result[number] = new DriveHealth(number, d["FriendlyName"] as string ?? $"Disk {number}",
                    health switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    string.Join(", ", status.Select(s => s switch { 2 => "OK", 3 => "Degraded", 5 => "Predictive failure", 0xD00F => "Removing", 0xD010 => "Missing", 6 => "Error", 10 => "Stopped", 11 => "In service", _ => s.ToString() })),
                    Convert.ToUInt16(d["MediaType"] ?? (ushort)0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "unspecified" },
                    temperature, wear, hours, corrected, uncorrected);
            }
        }
        return result;

        static int? Int(object? o) => o is null ? null : Convert.ToInt32(o);
        static long? Long(object? o) => o is null ? null : Convert.ToInt64(o);
    }
}

/// <summary>Which edition answers the ZFS tools: the kernel driver or the user-mode service.</summary>
static class Edition
{
    public static string Detect()
    {
        try
        {
            if (ServiceController.GetDevices().Any(d => d.ServiceName.Equals("OpenZFS", StringComparison.OrdinalIgnoreCase) && d.Status == ServiceControllerStatus.Running))
                return "Kernel edition";
        }
        catch (InvalidOperationException)
        {
        }
        return UserModeService.State() == "NotInstalled" ? "Kernel edition" : "User-mode edition";
    }
}

static class UserModeService
{
    public static string State()
    {
        try
        {
            using var service = new ServiceController(UserModeMounts.ServiceName);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "NotInstalled";
        }
    }

    public static async Task ControlAsync(string action)
    {
        using var service = new ServiceController(UserModeMounts.ServiceName);
        var timeout = TimeSpan.FromMinutes(2);
        // Stopping unmounts and exports every pool; that can take a moment.
        if (action is "stop" or "restart" && service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, timeout));
        }
        if (action is "start" or "restart")
        {
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, timeout));
            }
        }
    }
}

static class Format
{
    public static string Bytes(long? value)
    {
        if (value is null)
            return "";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = value.Value;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }
}

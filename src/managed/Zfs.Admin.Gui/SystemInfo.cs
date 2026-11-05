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

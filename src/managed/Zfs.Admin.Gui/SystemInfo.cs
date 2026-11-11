using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
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

sealed record DeviceChoice(int DiskNumber, int? PartitionNumber, string Device, string Description, bool WholeDisk)
{
    public override string ToString() => Description;
}

sealed record DiskPartitionInfo(int DiskNumber, int Number, long Offset, long Size, bool IsBoot, bool IsSystem,
    bool IsHidden, string? DriveLetter, string[] AccessPaths)
{
    public bool InUseByWindows => IsBoot || IsSystem || IsHidden || !string.IsNullOrEmpty(DriveLetter) || AccessPaths.Length > 0;
}

static class Disks
{
    const long Alignment = 1L << 20;
    const long MinimumDeviceSize = 64L << 20;

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

    public static IReadOnlyList<DeviceChoice> AvailableDevices(IReadOnlyCollection<string> inUsePaths)
    {
        List<DiskInfo> disks = List().Where(d => !d.Protected).ToList();
        var partitions = ListPartitions().GroupBy(p => p.DiskNumber).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Offset).ToList());
        List<UsedDevice> used = inUsePaths.Select(ParseUsedDevice).Where(x => x is not null).Select(x => x!).ToList();
        var choices = new List<DeviceChoice>();

        foreach (DiskInfo disk in disks)
        {
            partitions.TryGetValue(disk.Number, out List<DiskPartitionInfo>? diskPartitions);
            diskPartitions ??= [];
            List<UsedDevice> usedOnDisk = used.Where(u => u.DiskNumber == disk.Number).ToList();

            if (usedOnDisk.Count == 0 && !diskPartitions.Any(p => p.InUseByWindows))
                choices.Add(new DeviceChoice(disk.Number, null, disk.Device,
                    $"Disk {disk.Number}: entire {disk.Name}, {Format.Bytes(disk.Size)} (overwrites every partition)", WholeDisk: true));

            foreach (DiskPartitionInfo partition in diskPartitions)
            {
                if (partition.InUseByWindows || partition.Size < MinimumDeviceSize
                    || IsUsed(usedOnDisk, partition.Offset, partition.Size, partition.Number))
                    continue;
                choices.Add(new DeviceChoice(disk.Number, partition.Number, OffsetDevice(disk, partition.Offset, partition.Size),
                    $"Disk {disk.Number}, partition {partition.Number}: unused, {Format.Bytes(partition.Size)}", WholeDisk: false));
            }

            if (!usedOnDisk.Any(u => u.WholeDisk))
            {
                foreach ((long offset, long size) in FreeRanges(disk.Size, diskPartitions, usedOnDisk))
                    choices.Add(new DeviceChoice(disk.Number, null, OffsetDevice(disk, offset, size),
                        $"Disk {disk.Number}: unallocated space, {Format.Bytes(size)}", WholeDisk: false));
            }
        }

        return choices;
    }

    static IReadOnlyList<DiskPartitionInfo> ListPartitions()
    {
        var result = new List<DiskPartitionInfo>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT DiskNumber, PartitionNumber, Offset, Size, IsBoot, IsSystem, IsHidden, DriveLetter, AccessPaths FROM MSFT_Partition");
        foreach (ManagementBaseObject p in searcher.Get())
        {
            using (p)
            {
                result.Add(new DiskPartitionInfo(
                    Convert.ToInt32(p["DiskNumber"]),
                    Convert.ToInt32(p["PartitionNumber"]),
                    Convert.ToInt64(p["Offset"] ?? 0L),
                    Convert.ToInt64(p["Size"] ?? 0L),
                    p["IsBoot"] is true,
                    p["IsSystem"] is true,
                    p["IsHidden"] is true,
                    p["DriveLetter"] as string,
                    p["AccessPaths"] as string[] ?? []));
            }
        }
        return result;
    }

    static IEnumerable<(long Offset, long Size)> FreeRanges(long diskSize, IReadOnlyList<DiskPartitionInfo> partitions, IReadOnlyList<UsedDevice> used)
    {
        long start = Alignment;
        long limit = AlignDown(Math.Max(0, diskSize - Alignment));
        foreach (DiskPartitionInfo partition in partitions)
        {
            long partitionStart = AlignDown(Math.Max(0, partition.Offset));
            long partitionEnd = AlignUp(Math.Min(diskSize, partition.Offset + partition.Size));
            foreach ((long offset, long size) in Unused(start, Math.Min(partitionStart, limit), used))
                yield return (offset, size);
            start = Math.Max(start, partitionEnd);
        }
        foreach ((long offset, long size) in Unused(start, limit, used))
            yield return (offset, size);

        static IEnumerable<(long Offset, long Size)> Unused(long from, long to, IReadOnlyList<UsedDevice> usedRanges)
        {
            long cursor = AlignUp(from);
            foreach (UsedDevice range in usedRanges.Where(u => !u.WholeDisk && u.Offset is not null && u.Size is not null)
                         .OrderBy(u => u.Offset))
            {
                long blockedStart = AlignDown(range.Offset!.Value);
                long blockedEnd = AlignUp(range.Offset.Value + range.Size!.Value);
                if (blockedEnd <= cursor || blockedStart >= to)
                    continue;
                if (blockedStart > cursor && blockedStart - cursor >= MinimumDeviceSize)
                    yield return (cursor, blockedStart - cursor);
                cursor = Math.Max(cursor, blockedEnd);
                if (cursor >= to)
                    yield break;
            }
            long end = AlignDown(to);
            if (end > cursor && end - cursor >= MinimumDeviceSize)
                yield return (cursor, end - cursor);
        }
    }

    static bool IsUsed(IReadOnlyList<UsedDevice> used, long offset, long size, int partitionNumber)
    {
        long end = offset + size;
        return used.Any(u => u.WholeDisk
            || u.PartitionNumber == partitionNumber
            || u.Offset is long usedOffset && u.Size is long usedSize
                && offset < usedOffset + usedSize && usedOffset < end);
    }

    static UsedDevice? ParseUsedDevice(string path)
    {
        Match partition = Regex.Match(path, @"Harddisk([0-9]+)Partition([0-9]+)", RegexOptions.IgnoreCase);
        if (partition.Success && int.TryParse(partition.Groups[1].Value, out int partitionDisk)
            && int.TryParse(partition.Groups[2].Value, out int partitionNumber))
            return new UsedDevice(partitionDisk, partitionNumber, null, null, WholeDisk: false);

        Match drive = Regex.Match(path, @"PhysicalDrive([0-9]+)", RegexOptions.IgnoreCase);
        if (!drive.Success || !int.TryParse(drive.Groups[1].Value, out int diskNumber))
            return null;
        Match range = Regex.Match(path, @"^#([0-9]+)#([0-9]+)#", RegexOptions.IgnoreCase);
        return range.Success && long.TryParse(range.Groups[1].Value, out long offset) && long.TryParse(range.Groups[2].Value, out long size)
            && size > 0 && offset <= long.MaxValue - size
            ? new UsedDevice(diskNumber, null, offset, size, WholeDisk: false)
            : new UsedDevice(diskNumber, null, null, null, WholeDisk: true);
    }

    static string OffsetDevice(DiskInfo disk, long offset, long size) =>
        $"#{offset}#{size}#{disk.Device}";

    static long AlignDown(long value) => value / Alignment * Alignment;

    static long AlignUp(long value) =>
        value <= 0 ? 0 : checked((value + Alignment - 1) / Alignment * Alignment);

    sealed record UsedDevice(int DiskNumber, int? PartitionNumber, long? Offset, long? Size, bool WholeDisk);
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

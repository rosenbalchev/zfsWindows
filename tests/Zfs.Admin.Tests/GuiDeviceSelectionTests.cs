using Zfs.Admin.Gui;

namespace Zfs.Admin.Tests;

public sealed class GuiDeviceSelectionTests
{
    const long MiB = 1L << 20;
    const long GiB = 1L << 30;

    [Fact]
    public void ProtectedSystemDiskOffersItsUnallocatedRangeOnly()
    {
        var (disk, partitions, freeStart, freeEnd) = SystemDiskWithGap();

        IReadOnlyList<DeviceChoice> choices = Disks.SelectAvailableDevices(
            [disk], partitions, []);

        DeviceChoice choice = Assert.Single(choices);
        Assert.Equal(1, choice.DiskNumber);
        Assert.Null(choice.PartitionNumber);
        Assert.False(choice.WholeDisk);
        AssertRange(choice, freeStart, freeEnd - freeStart);
    }

    [Fact]
    public void ProtectedSystemDiskDoesNotOfferRegisteredZfsExtents()
    {
        var (disk, partitions, freeStart, freeEnd) = SystemDiskWithGap();
        long usedStart = freeStart + GiB;
        long usedSize = 2 * GiB;
        string usedPath = $"#{usedStart}#{usedSize}#";
        usedPath += @"\\?\PhysicalDrive1";

        IReadOnlyList<DeviceChoice> choices = Disks.SelectAvailableDevices(
            [disk], partitions, [usedPath]);

        Assert.Equal(2, choices.Count);
        AssertRange(choices[0], freeStart, usedStart - freeStart);
        AssertRange(choices[1], usedStart + usedSize, freeEnd - usedStart - usedSize);
    }

    static (DiskInfo Disk, DiskPartitionInfo[] Partitions, long FreeStart, long FreeEnd)
        SystemDiskWithGap()
    {
        long diskSize = 100 * GiB;
        long cOffset = 101 * MiB;
        long cSize = 60 * GiB;
        long recoveryOffset = 92 * GiB;
        return (
            new DiskInfo(1, "system", diskSize, true, true, "GPT"),
            [
                new DiskPartitionInfo(1, 1, MiB, 100 * MiB, false, true, true, null, []),
                new DiskPartitionInfo(1, 2, cOffset, cSize, true, false, false, "C", []),
                new DiskPartitionInfo(1, 3, recoveryOffset, diskSize - recoveryOffset, false, false, true, null, []),
            ],
            cOffset + cSize,
            recoveryOffset);
    }

    static void AssertRange(DeviceChoice choice, long offset, long size)
    {
        string[] fields = choice.Device.Split('#');
        Assert.Equal(4, fields.Length);
        Assert.Equal(offset, long.Parse(fields[1]));
        Assert.Equal(size, long.Parse(fields[2]));
        Assert.Equal(@"\\?\PhysicalDrive1", fields[3]);
    }
}

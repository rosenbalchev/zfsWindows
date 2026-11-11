using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zfs.Admin;

namespace Zfs.Admin.Tests;

/// <summary>
/// The GUI's non-visual layer: AdminClient over the broker protocol and the
/// user-mode mount configuration.  The WinForms code only calls these.
/// </summary>
public class GuiBackendTests
{
    [Theory]
    [InlineData(@"\\?\C:\pools\a.img", @"C:\pools\a.img")]
    [InlineData(@"C:\pools\a.img", @"C:\pools\a.img")]
    [InlineData(@"\\?\PhysicalDrive3", @"\\?\PhysicalDrive3")]
    [InlineData(@"#1048576#21464350720#\\?\PHYSICALDRIVE2", @"\\?\PhysicalDrive2")]
    public void DeviceArgumentAcceptsWhatZpoolReports(string reported, string expected) =>
        Assert.Equal(expected, AdminClient.DeviceArgument(reported));

    [Fact]
    public void NewDeviceArgumentPreservesOffsetLimitedPhysicalDiskRanges()
    {
        const string partition = @"#1048576#21464350720#\\?\PhysicalDrive2";
        Assert.Equal(partition, AdminClient.NewDeviceArgument(partition));
        Assert.Equal(@"\\?\PhysicalDrive2", AdminClient.NewDeviceArgument(@"\\.\PhysicalDrive2"));
    }

    [Fact]
    public async Task StatusIncludesEveryDeviceClass()
    {
        await using var broker = new TestBroker();
        broker.Runner.Respond = (_, _, _) => Task.FromResult(new ToolResult(0, """
            { "pools": { "tank": { "name": "tank", "state": "ONLINE", "error_count": "0",
              "scan_stats": { "function": "SCRUB", "state": "FINISHED", "errors": "0" },
              "vdevs": { "tank": { "name": "tank", "vdev_type": "root", "state": "ONLINE",
                "vdevs": { "mirror-0": { "name": "mirror-0", "vdev_type": "mirror", "state": "ONLINE",
                  "vdevs": { "a": { "name": "a", "vdev_type": "file", "path": "\\\\?\\D:\\a.img", "state": "ONLINE" },
                             "b": { "name": "b", "vdev_type": "file", "path": "\\\\?\\D:\\b.img", "state": "ONLINE" } } } } } },
              "logs": { "l": { "name": "l", "vdev_type": "file", "path": "\\\\?\\D:\\log.img", "state": "ONLINE" } },
              "l2cache": { "c": { "name": "c", "vdev_type": "file", "path": "\\\\?\\D:\\c.img", "state": "ONLINE" } } } } }
            """, ""));
        var client = new AdminClient(broker.Client);
        PoolStatusInfo status = (await client.StatusAsync("tank"))!;
        Assert.Equal("SCRUB", status.Scan!.Function);
        VdevInfo mirror = Assert.Single(Assert.Single(status.Vdevs).Children);
        Assert.Equal(2, mirror.Children.Count);
        Assert.Equal(@"\\?\D:\log.img", Assert.Single(status.Logs).Path);
        Assert.Equal(@"\\?\D:\c.img", Assert.Single(status.Caches).Path);
    }

    [Fact]
    public async Task MutationsCarryConfirmationOnlyWhenGiven()
    {
        await using var broker = new TestBroker();
        var client = new AdminClient(broker.Client);
        string dir = Directory.CreateTempSubdirectory("zfswin-test").FullName;
        try
        {
            string c = Path.Combine(dir, "c.img");
            File.WriteAllBytes(c, [0]);
            var refused = await Assert.ThrowsAsync<AdminException>(() => client.AddCacheDevicesAsync("tank", "cache", [c], mirror: false, confirmed: false));
            Assert.Equal(ErrorCode.ConfirmationRequired, refused.Code);
            await client.AddCacheDevicesAsync("tank", "cache", [@"\\?\" + c], mirror: false, confirmed: true);
            await client.AddCacheDevicesAsync("tank", "cache", [@"#1048576#1048576#\\?\PhysicalDrive2"], mirror: false, confirmed: true);
            await client.RemoveDeviceAsync("tank", @"\\?\" + c, confirmed: true);
            await client.SetDatasetPropertyAsync("tank/data", "dedup", "on");
            var calls = broker.Runner.Calls.Select(x => x.Args).Where(x => x[0] != "status").ToArray();
            Assert.Equal(["add", "tank", "cache", @"\\?\" + c], calls[0]);
            Assert.Equal(["add", "tank", "cache", "#1048576#1048576#\\\\?\\PhysicalDrive2"], calls[1]);
            Assert.Equal(["remove", "tank", @"\\?\" + c], calls[2]);
            Assert.Equal(["set", "dedup=on", "tank/data"], calls[3]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UserModeMountsRoundTripAndValidate()
    {
        string path = $@"Software\zfswin-tests\{Guid.NewGuid():N}\Mounts";
        var mounts = new UserModeMounts(Registry.CurrentUser, path);
        try
        {
            Assert.Empty(mounts.List());
            mounts.Save(new UserModeMount("tank", @"D:\pools", "tank", "tank/data", "Z:", ReadOnly: true));
            mounts.Save(new UserModeMount("lab", @"D:\lab", "lab", "lab", @"C:\mnt\lab", ReadOnly: false));
            Assert.Equal(["lab", "tank"], mounts.List().Select(m => m.Name));
            Assert.True(mounts.List().Single(m => m.Name == "tank").ReadOnly);
            mounts.Remove("tank");
            Assert.Equal("lab", Assert.Single(mounts.List()).Name);

            Assert.Throws<AdminException>(() => mounts.Save(new UserModeMount("x", "pools", "tank", "tank", "Z:", false)));          // relative source
            Assert.Throws<AdminException>(() => mounts.Save(new UserModeMount("x", @"D:\p", "tank", "other/data", "Z:", false)));   // dataset of another pool
            Assert.Throws<AdminException>(() => mounts.Save(new UserModeMount("x", @"D:\p", "tank", "tank", "Z", false)));          // bad mount point
            Assert.Throws<AdminException>(() => mounts.Save(new UserModeMount(@"a\b", @"D:\p", "tank", "tank", "Z:", false)));      // bad name

            // Step 18: no source for a pool the service imports anyway; an image file as source.
            mounts.Save(new UserModeMount("auto-tank_data", "", "tank", "tank/data", "Y:", false));
            mounts.Save(new UserModeMount("img", @"D:\pools\tank.img", "tank", "tank", "X:", false));
            Assert.Equal("", mounts.List().Single(m => m.Name == "auto-tank_data").Source);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path[..path.LastIndexOf('\\')], throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void FileCloneRefusesBeforeTouchingFiles()
    {
        string dir = Directory.CreateTempSubdirectory("zfswin-clone").FullName;
        try
        {
            string source = Path.Combine(dir, "a.bin"), target = Path.Combine(dir, "b.bin");
            Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<AdminException>(() => FileClone.Clone("a.bin", target, false)).Code);
            Assert.Equal(ErrorCode.NotFound, Assert.Throws<AdminException>(() => FileClone.Clone(source, target, false)).Code);
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(target, [9]);
            Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<AdminException>(() => FileClone.Clone(source, target, false)).Code);
            Assert.Equal([9], File.ReadAllBytes(target));	// untouched without --force
            // NTFS has no block cloning: refused after both standard and user-mode requests.
            Assert.Equal(ErrorCode.CommandFailed, Assert.Throws<AdminException>(() => FileClone.Clone(source, target, true)).Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

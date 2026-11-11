using System.Text.Json.Nodes;
using Zfs.Admin;

namespace Zfs.Admin.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("tank")]
    [InlineData("zfswin_single")]
    [InlineData("pool.with-dash:colon")]
    public void AcceptsPoolNames(string name) => Assert.Equal(name, Validation.PoolName(name));

    [Theory]
    [InlineData("")]
    [InlineData("-f")]
    [InlineData("1tank")]
    [InlineData("tank/child")]
    [InlineData("tank space")]
    [InlineData("mirror")]
    [InlineData("raidz")]
    [InlineData("c0d0")]
    [InlineData("tänk")]
    public void RejectsPoolNames(string name) =>
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<AdminException>(() => Validation.PoolName(name)).Code);

    [Theory]
    [InlineData("mirrored")]
    [InlineData("spare1")]
    [InlineData("log")]
    public void CreateReservesVdevKeywordPrefixes(string name)
    {
        Assert.Equal(name, Validation.PoolName(name));
        Assert.Throws<AdminException>(() => Validation.PoolName(name, forCreate: true));
    }

    [Fact]
    public void AcceptsGuidForImport() => Assert.Equal("1234567890123", Validation.PoolNameOrGuid("1234567890123"));

    [Theory]
    [InlineData("tank/data")]
    [InlineData("tank/with space/x_y.z:1-2")]
    public void AcceptsDatasetNames(string name) => Assert.Equal(name, Validation.DatasetName(name));

    [Theory]
    [InlineData("tank/")]
    [InlineData("tank//x")]
    [InlineData("tank/../x")]
    [InlineData("tank/ x")]
    [InlineData("tank/x@snap")]
    [InlineData("tank/x#book")]
    [InlineData("tank/x\ny")]
    [InlineData("--recursive")]
    public void RejectsDatasetNames(string name) => Assert.Throws<AdminException>(() => Validation.DatasetName(name));

    [Theory]
    [InlineData("tank@s1")]
    [InlineData("tank/data@before upgrade")]
    public void AcceptsSnapshotNames(string name) => Assert.Equal(name, Validation.SnapshotName(name));

    [Theory]
    [InlineData("tank")]
    [InlineData("@s1")]
    [InlineData("tank@")]
    [InlineData("tank@a@b")]
    [InlineData("tank@a/b")]
    public void RejectsSnapshotNames(string name) => Assert.Throws<AdminException>(() => Validation.SnapshotName(name));

    [Theory]
    [InlineData("compression")]
    [InlineData("com.example:owner")]
    public void AcceptsPropertyNames(string name) => Assert.Equal(name, Validation.PropertyName(name));

    [Theory]
    [InlineData("Compression")]
    [InlineData("-o")]
    [InlineData("a=b")]
    public void RejectsPropertyNames(string name) => Assert.Throws<AdminException>(() => Validation.PropertyName(name));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void RejectsPropertyValuesWithControlCharacters(string value) => Assert.Throws<AdminException>(() => Validation.PropertyValue(value));

    [Theory]
    [InlineData(@"\\?\PhysicalDrive1", @"\\?\PhysicalDrive1")]
    [InlineData(@"\\.\physicaldrive12", @"\\?\PhysicalDrive12")]
    [InlineData(@"#1048576#2097152#\\?\PhysicalDrive2", @"#1048576#2097152#\\?\PhysicalDrive2")]
    [InlineData(@"#1048576#2097152#PhysicalDrive2", @"#1048576#2097152#\\?\PhysicalDrive2")]
    public void NormalizesPhysicalDrives(string input, string expected) => Assert.Equal(expected, Validation.DevicePath(input));

    [Theory]
    [InlineData("#0#0#PhysicalDrive2")]
    [InlineData("#18446744073709551615#1#PhysicalDrive2")]
    [InlineData("#1048576#not-a-number#PhysicalDrive2")]
    public void RejectsInvalidOffsetDeviceRanges(string path) =>
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<AdminException>(() => Validation.DevicePath(path, mustExist: false)).Code);

    [Theory]
    [InlineData(@"relative\disk.img")]
    [InlineData(@"\\server\share\disk.img")]
    [InlineData(@"C:\pools\..\Windows\disk.img")]
    [InlineData(@"C:\pools\*.img")]
    [InlineData(@"\\?\C:\disk.img")]
    [InlineData(@"-d")]
    public void RejectsDevicePaths(string path) =>
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<AdminException>(() => Validation.DevicePath(path, mustExist: false)).Code);

    [Fact]
    public void DeviceFileMustExist()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".img");
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<AdminException>(() => Validation.DevicePath(missing)).Code);
        File.WriteAllBytes(missing, [0]);
        try
        {
            Assert.Equal(@"\\?\" + missing, Validation.DevicePath(missing));
        }
        finally
        {
            File.Delete(missing);
        }
    }

    [Fact]
    public void PropertiesMustBeStrings()
    {
        var args = new JsonObject { ["p"] = new JsonObject { ["compression"] = 5 } };
        Assert.Throws<AdminException>(() => Validation.Properties(args, "p"));
    }

    [Fact]
    public void ListsAreBounded()
    {
        var args = new JsonObject { ["l"] = new JsonArray(Enumerable.Range(0, Validation.MaxListItems + 1).Select(i => (JsonNode?)i.ToString()).ToArray()) };
        Assert.Throws<AdminException>(() => Validation.StringList(args, "l"));
    }
}

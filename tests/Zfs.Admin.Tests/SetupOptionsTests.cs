using Zfs.Admin.Broker;

namespace Zfs.Admin.Tests;

/// <summary>
/// ZfsWinBroker.exe --setup options, exactly as the MSI's deferred custom
/// actions pass them (installer/ZfsWin.Installer/Package.wxs).
/// </summary>
public class SetupOptionsTests
{
    [Fact]
    public void OptionsAreKeyedByNameNotValue()
    {
        var options = Setup.ParseOptions(["--setup", "driver-install", "--driver-dir", @"C:\Program Files\OpenZFS for Windows\driver\."])!;
        Assert.Equal(@"C:\Program Files\OpenZFS for Windows\driver\.", options["--driver-dir"]);
        Assert.Single(options);
    }

    [Fact]
    public void PreflightOptionsMixValuesAndFlags()
    {
        var options = Setup.ParseOptions(["--setup", "preflight", "--test-signing-thumbprint", "69BB566ADF29D019E05C8E00ABF9EF88EFAF732F", "--allow-pending-reboot", "--fresh-install"])!;
        Assert.Equal("69BB566ADF29D019E05C8E00ABF9EF88EFAF732F", options["--test-signing-thumbprint"]);
        Assert.True(options.ContainsKey("--allow-pending-reboot"));
        Assert.Null(options["--fresh-install"]);
        Assert.Equal(3, options.Count);
    }

    [Fact]
    public void AStrayValueIsRejected()
    {
        Assert.Null(Setup.ParseOptions(["--setup", "check-pools", "export"]));
    }
}

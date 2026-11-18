using Zfs.Admin.Broker;

namespace Zfs.Admin.Tests;

/// <summary>
/// The broker's boot import of the kernel driver's pools reads `zpool import
/// -c zpool.cache`'s listing (GitHub issue #46).
/// </summary>
public class KernelPoolsTests
{
    [Fact]
    public void ListingGivesEachPoolsNameAndGuid()
    {
        const string listing = """
              pool: tank
                id: 17177990545436896441
             state: ONLINE
            action: The pool can be imported using its name or numeric identifier.
            config:

            	tank                   ONLINE
            	  Harddisk1Partition0  ONLINE
            	cache
            	  Harddisk2Partition0

              pool: backup
                id: 4242
             state: UNAVAIL
            status: One or more devices are missing from the system.
            config:

            	backup                 UNAVAIL  missing device
            """;
        var pools = KernelPools.ParseImportList(listing.Replace("\n", "\r\n"));
        Assert.Equal([new KernelPools.CachedPool("tank", "17177990545436896441"), new KernelPools.CachedPool("backup", "4242")], pools);
    }

    [Fact]
    public void NoPoolsToImportIsAnEmptyList()
    {
        Assert.Empty(KernelPools.ParseImportList(""));
        Assert.Empty(KernelPools.ParseImportList("no pools available to import\r\n"));
    }
}

using System.Text.Json;
using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// Pools Linux OpenZFS 2.4 wrote (tests/fixtures manifests; the files are in
// <pool>/data), imported read-only: every file matches, and the images are
// left untouched.  The images are 5 GiB each and not in git:
// ZFSWIN_LINUX_IMAGES, or the repository's TestVolumes folder
// (TestVolumes\single, TestVolumes\mirror).
[Collection(EngineCollection.Name)]
public sealed class LinuxImageTests
{
    private static readonly string Repository = FindRepository();
    private static readonly string Images = Environment.GetEnvironmentVariable("ZFSWIN_LINUX_IMAGES")
        is { Length: > 0 } dir ? dir : Path.Combine(Repository, "TestVolumes");

    [RequiresImageFact("single/disk01.raw")]
    public void SingleDiskPoolFromLinux() =>
        Verify("zfswin_single", Path.Combine(Images, "single"), ["disk01.raw"],
            Path.Combine(Repository, "tests", "fixtures", "single-disk.manifest.json"));

    [RequiresImageFact("mirror/disk01.raw", "mirror/disk02.raw")]
    public void MirrorPoolFromLinux() =>
        Verify("zfswin_mirror", Path.Combine(Images, "mirror"), ["disk01.raw", "disk02.raw"],
            Path.Combine(Repository, "tests", "fixtures", "mirror", "mirror.json"));

    private static void Verify(string pool, string dir, string[] images, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var before = images.Select(i => Data.HashFile(Path.Combine(dir, i))).ToArray();
        // The import scan reads every image in the directory: these only.
        var search = string.Join(';', images.Select(i => Path.Combine(dir, i)));
        using (var fs = ZfsUserModeEngine.Open(search, pool, $"{pool}/data", readOnly: true))
        {
            Assert.Equal(1, fs.StatFs().ReadOnly);
            foreach (var file in manifest.Files)
            {
                var obj = fs.Lookup(file.Path);
                Assert.Equal((ulong)file.Length, fs.GetAttributes(obj).Size);
                Assert.Equal(file.Sha256, Data.Hash(fs, obj), ignoreCase: true);
            }
        }
        ZfsUserModeEngine.ExportPool(pool);
        Assert.Equal(before, images.Select(i => Data.HashFile(Path.Combine(dir, i))).ToArray());
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ZfsWindows.sln"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    private sealed record Manifest(IReadOnlyList<FileEntry> Files);
    private sealed record FileEntry(string Path, long Length, string Sha256);

    private sealed class RequiresImageFactAttribute : FactAttribute
    {
        public RequiresImageFactAttribute(params string[] images)
        {
            var missing = images.Where(i => !File.Exists(Path.Combine(Images, i))).ToArray();
            if (missing.Length > 0)
                Skip = $"Linux reference images missing in {Images}: {string.Join(", ", missing)} (set ZFSWIN_LINUX_IMAGES).";
        }
    }
}

using System.Text;
using Zfs.Interop;
using Xunit;

namespace Zfs.UserMode.Tests;

// The file-level API the WinFsp front end calls (zum.h), on one pool.
[Collection(EngineCollection.Name)]
public sealed class FileOperationTests : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly string _pool = Scratch.PoolName("zut_files");
    private readonly ZfsUserModeFileSystem _fs;

    public FileOperationTests()
    {
        ZfsUserModeEngine.CreatePool(_pool, "", _scratch.Images(1, 256L << 20), "ashift=12");
        _fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool);
    }

    public void Dispose()
    {
        _fs.Dispose();
        ZfsUserModeEngine.DestroyPool(_pool);
        _scratch.Dispose();
    }

    [Fact]
    public void WriteReadOverwriteAppend()
    {
        var obj = _fs.CreateFile("/file.txt");
        _fs.Write(obj, 0, "hello world"u8);
        _fs.Write(obj, 6, "WORLD"u8);
        Assert.Equal(11UL, _fs.Write(obj, ZfsUserModeFileSystem.Append, "!"u8));
        Assert.Equal("hello WORLD!", Encoding.UTF8.GetString(_fs.ReadAll(obj)));
        Assert.Equal(12UL, _fs.GetAttributes(obj).Size);
    }

    [Fact]
    public void ReadsStopAtEndOfFile()
    {
        var obj = _fs.CreateFile("/short.bin");
        _fs.Write(obj, 0, new byte[100]);
        Assert.Equal(100, _fs.Read(obj, 0, new byte[4096]));
        Assert.Equal(0, _fs.Read(obj, 100, new byte[4096]));
    }

    [Fact]
    public void TruncateShrinksAndGrowsWithZeros()
    {
        var obj = _fs.CreateFile("/trunc.bin");
        var data = Data.Random(1 << 20, 1);
        _fs.Write(obj, 0, data);
        _fs.Truncate(obj, 1000);
        Assert.Equal(data[..1000], _fs.ReadAll(obj));
        _fs.Truncate(obj, 5000);
        var grown = _fs.ReadAll(obj);
        Assert.Equal(data[..1000], grown[..1000]);
        Assert.All(grown[1000..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SparseFileFarBeyondPoolSize()
    {
        // 1 TiB into a 256 MiB pool: only the written block is allocated.
        var obj = _fs.CreateFile("/sparse.bin");
        const ulong offset = 1UL << 40;
        _fs.Write(obj, offset, Data.Random(4096, 2));
        _fs.Sync();
        var attr = _fs.GetAttributes(obj);
        Assert.Equal(offset + 4096, attr.Size);
        Assert.True(attr.Allocation < 1 << 20, $"allocated {attr.Allocation}");
        var hole = new byte[65536];
        Assert.Equal(hole.Length, _fs.Read(obj, 1UL << 30, hole));
        Assert.All(hole, b => Assert.Equal(0, b));
    }

    [Fact]
    public void RenameMovesAndReplaces()
    {
        _fs.CreateDirectory("/from");
        _fs.CreateDirectory("/to");
        _fs.Write(_fs.CreateFile("/from/a.txt"), 0, "a"u8);
        _fs.Write(_fs.CreateFile("/to/b.txt"), 0, "b"u8);
        _fs.Rename("/from/a.txt", "/to/b.txt");
        Assert.NotEqual(0, _fs.TryLookup("/from/a.txt", out _));
        Assert.Equal("a"u8.ToArray(), _fs.ReadAll(_fs.Lookup("/to/b.txt")));
        _fs.Rename("/to", "/moved");
        Assert.Equal("a"u8.ToArray(), _fs.ReadAll(_fs.Lookup("/moved/b.txt")));
    }

    [Fact]
    public void RemoveFilesAndDirectories()
    {
        _fs.CreateDirectory("/dir");
        _fs.CreateFile("/dir/f");
        Assert.Equal(ZfsUmErrno.ENOTEMPTY, _fs.TryRemove("/dir", true));
        _fs.Remove("/dir/f");
        _fs.Remove("/dir", directory: true);
        Assert.Equal(ZfsUmErrno.ENOENT, _fs.TryLookup("/dir", out _));
    }

    [Fact]
    public void CreateExistingFails()
    {
        _fs.CreateFile("/once");
        Assert.Equal(ZfsUmErrno.EEXIST, _fs.TryCreate("/once", false, out _));
        Assert.Equal(ZfsUmErrno.ENOENT, _fs.TryCreate("/missing/child", false, out _));
    }

    [Fact]
    public void LargeDirectoryListsEveryEntry()
    {
        _fs.CreateDirectory("/many");
        var names = Enumerable.Range(0, 5000).Select(i => $"entry-{i:D5}.dat").ToHashSet();
        foreach (var name in names) _fs.CreateFile("/many/" + name);
        var listed = _fs.ReadDirectory("/many");
        Assert.Equal(names.Count, listed.Count);
        Assert.True(names.SetEquals(listed.Select(e => e.Name)));
        Assert.All(listed, e => Assert.Equal(0, e.Attributes.IsDirectory));
    }

    [Fact]
    public void UnicodeNamesRoundTrip()
    {
        string[] names = ["Ünïcödé.txt", "文件.txt", "файл.txt", "emoji-😀.txt", "spaces and.dots..txt"];
        _fs.CreateDirectory("/unicode");
        foreach (var name in names)
            _fs.Write(_fs.CreateFile("/unicode/" + name), 0, Encoding.UTF8.GetBytes(name));
        Assert.True(names.ToHashSet().SetEquals(_fs.ReadDirectory("/unicode").Select(e => e.Name)));
        foreach (var name in names)
            Assert.Equal(name, Encoding.UTF8.GetString(_fs.ReadAll(_fs.Lookup("/unicode/" + name))));
    }

    [Fact]
    public void CaseSensitivityFollowsTheDataset()
    {
        ZfsUserModeEngine.CreateDataset($"{_pool}/insensitive", "casesensitivity=insensitive");
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool, $"{_pool}/insensitive"))
        {
            var obj = fs.CreateFile("/MixedCase.txt");
            Assert.Equal(obj, fs.Lookup("/mixedcase.TXT"));
            Assert.Equal(ZfsUmErrno.EEXIST, fs.TryCreate("/MIXEDCASE.txt", false, out _));
            Assert.Equal(0, fs.StatFs().CaseSensitive);
        }
        ZfsUserModeEngine.CreateDataset($"{_pool}/sensitive", "casesensitivity=sensitive");
        using (var fs = ZfsUserModeEngine.Open(_scratch.Directory, _pool, $"{_pool}/sensitive"))
        {
            fs.CreateFile("/MixedCase.txt");
            Assert.Equal(ZfsUmErrno.ENOENT, fs.TryLookup("/mixedcase.txt", out _));
            fs.CreateFile("/mixedcase.txt");
            Assert.Equal(2, fs.ReadDirectory("/").Count(e => e.Name.Equals("mixedcase.txt", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void WindowsAttributesAndTimesPersist()
    {
        var obj = _fs.CreateFile("/attrs.txt");
        const ulong creation = 133_000_000_000_000_000UL;  // a FILETIME in 2022
        _fs.SetAttributes(obj, ZfsUserModeFileSystem.AttributeHidden | ZfsUserModeFileSystem.AttributeReadOnly, creation);
        var attr = _fs.GetAttributes(obj);
        Assert.Equal(ZfsUserModeFileSystem.AttributeHidden | ZfsUserModeFileSystem.AttributeReadOnly,
            attr.Attributes & (ZfsUserModeFileSystem.AttributeHidden | ZfsUserModeFileSystem.AttributeReadOnly));
        Assert.Equal(creation, attr.CreationTime);
    }

    [Fact]
    public void SpaceIsAccountedAndFull()
    {
        var before = _fs.StatFs();
        Assert.True(before.TotalBytes > 0 && before.FreeBytes <= before.TotalBytes);
        var obj = _fs.CreateFile("/fill.bin");
        var chunk = Data.Random(1 << 20, 9);
        ulong written = 0;
        int errno;
        // Fill the pool: ENOSPC, not a hang or a crash.
        while ((errno = _fs.TryWrite(obj, ref written, chunk)) == 0)
        {
            written += (ulong)chunk.Length;
            if (written % (16 << 20) == 0) _fs.Sync();
            Assert.True(written < 512UL << 20, "a 256 MiB pool took 512 MiB");
        }
        Assert.Equal(ZfsUmErrno.ENOSPC, errno);
        // Deleting gives the space back.
        _fs.Remove("/fill.bin");
        _fs.Sync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_fs.StatFs().FreeBytes < before.FreeBytes * 9 / 10 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            _fs.Sync();
        }
        Assert.True(_fs.StatFs().FreeBytes >= before.FreeBytes * 9 / 10);
        _fs.Write(_fs.CreateFile("/after.bin"), 0, chunk);
    }
}

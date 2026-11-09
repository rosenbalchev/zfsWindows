using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Zfs.Interop;

// The user-mode engine (OpenZFS libzpool, as in zfswin-um.exe) in this
// process, through ZfsUserModeTest.dll (src/native/ZfsUserModeTest).  The
// engine is process-wide: one Start, any number of pools, one Stop.

[StructLayout(LayoutKind.Sequential)]
public struct ZfsUmAttributes
{
    public ulong Object;
    public ulong Size;
    public ulong Allocation;
    public ulong Links;
    public ulong Mode;
    public uint Attributes;
    public int IsDirectory;
    public ulong CreationTime;
    public ulong AccessTime;
    public ulong WriteTime;
    public ulong ChangeTime;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsUmStatFs
{
    public ulong TotalBytes;
    public ulong FreeBytes;
    public ulong FsidGuid;
    public int CaseSensitive;
    public int ReadOnly;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsUmArcStats
{
    public ulong Size;
    public ulong Target;
    public ulong Min;
    public ulong Max;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsUmPoolStatus
{
    // vdev_state_t: 7 healthy, 6 degraded, 5 faulted.
    public ulong State;
    public ulong Leaves;
    public ulong ReadErrors;
    public ulong WriteErrors;
    public ulong ChecksumErrors;
    // dsl_scan_state_t: 0 none, 1 scanning, 2 finished, 3 canceled.
    public ulong ScanState;
    public ulong ScanErrors;
    public ulong ScanRepairedBytes;
}

public sealed record ZfsUmDirectoryEntry(string Name, ZfsUmAttributes Attributes);
public sealed record ZfsUmSnapshot(string Name, ulong Id, ulong CreationTime);

// An errno value from the engine.
public sealed class ZfsUmException(string operation, int errno)
    : IOException($"{operation}: errno {errno}")
{
    public int Errno { get; } = errno;
}

public static class ZfsUmErrno
{
    public const int ENOENT = 2, EIO = 5, EBUSY = 16, EEXIST = 17, EINVAL = 22,
        ENOSPC = 28, ENOTEMPTY = 41;
}

internal static unsafe partial class ZfsUmNative
{
    internal const string Library = "ZfsUserModeTest";

    // ZFSWIN_UM_TEST_LIBRARY: the DLL to load instead of the one next to
    // this assembly (another configuration or architecture's build tree).
    static ZfsUmNative() =>
        NativeLibrary.SetDllImportResolver(typeof(ZfsUmNative).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        var overridden = Environment.GetEnvironmentVariable("ZFSWIN_UM_TEST_LIBRARY");
        return name == Library && !string.IsNullOrEmpty(overridden)
            ? NativeLibrary.Load(overridden)
            : 0;
    }

    [LibraryImport(Library, EntryPoint = "zut_engine_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int EngineStart(string? cachefile);
    [LibraryImport(Library, EntryPoint = "zut_engine_stop")]
    internal static partial int EngineStop();
    [LibraryImport(Library, EntryPoint = "zut_arc_set_max")]
    internal static partial void ArcSetMax(ulong bytes);
    [LibraryImport(Library, EntryPoint = "zut_arc_stats")]
    internal static partial void ArcStats(out ZfsUmArcStats stats);
    [LibraryImport(Library, EntryPoint = "zum_engine_version")]
    internal static partial nint EngineVersion();

    [LibraryImport(Library, EntryPoint = "zut_pool_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolCreate(string pool, string layout, string vdevs, string poolProps, string fsProps);
    [LibraryImport(Library, EntryPoint = "zut_pool_import", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolImport(string pool, string searchPath);
    [LibraryImport(Library, EntryPoint = "zut_pool_export", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolExport(string pool, int force);
    [LibraryImport(Library, EntryPoint = "zut_pool_destroy", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolDestroy(string pool);
    [LibraryImport(Library, EntryPoint = "zut_pool_scrub", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolScrub(string pool);
    [LibraryImport(Library, EntryPoint = "zut_pool_status", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PoolStatus(string pool, out ZfsUmPoolStatus status);

    [LibraryImport(Library, EntryPoint = "zut_dataset_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DatasetCreate(string name, string props);
    [LibraryImport(Library, EntryPoint = "zut_dataset_set", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DatasetSet(string name, string props);
    [LibraryImport(Library, EntryPoint = "zut_snapshot", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Snapshot(string snapshot);
    [LibraryImport(Library, EntryPoint = "zut_snapshot_destroy", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SnapshotDestroy(string snapshot);
    [LibraryImport(Library, EntryPoint = "zut_rollback", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Rollback(string dataset);

    [LibraryImport(Library, EntryPoint = "zut_fs_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsOpen(string searchPath, string pool, string dataset, int readOnly, out nint fs);
    [LibraryImport(Library, EntryPoint = "zum_fs_close")]
    internal static partial int FsClose(nint fs);
    [LibraryImport(Library, EntryPoint = "zum_fs_statfs")]
    internal static partial int FsStatFs(nint fs, out ZfsUmStatFs st);
    [LibraryImport(Library, EntryPoint = "zum_fs_sync")]
    internal static partial int FsSync(nint fs);
    [LibraryImport(Library, EntryPoint = "zum_fs_lookup", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsLookup(nint fs, string path, out ulong obj);
    [LibraryImport(Library, EntryPoint = "zum_fs_getattr")]
    internal static partial int FsGetAttr(nint fs, ulong obj, out ZfsUmAttributes attr);
    [LibraryImport(Library, EntryPoint = "zum_fs_setattr")]
    internal static partial int FsSetAttr(nint fs, ulong obj, uint attributes,
        ulong creation, ulong access, ulong write, ulong change);
    [LibraryImport(Library, EntryPoint = "zum_fs_read")]
    internal static partial int FsRead(nint fs, ulong obj, ulong offset, uint length, byte* buffer, out uint done);
    [LibraryImport(Library, EntryPoint = "zum_fs_write")]
    internal static partial int FsWrite(nint fs, ulong obj, ref ulong offset, byte* buffer, uint length);
    [LibraryImport(Library, EntryPoint = "zum_fs_truncate")]
    internal static partial int FsTruncate(nint fs, ulong obj, ulong size);
    [LibraryImport(Library, EntryPoint = "zum_fs_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsCreate(nint fs, string path, int isDir, out ulong obj);
    [LibraryImport(Library, EntryPoint = "zum_fs_remove", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsRemove(nint fs, string path, int isDir);
    [LibraryImport(Library, EntryPoint = "zum_fs_rename", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsRename(nint fs, string from, string to);
    [LibraryImport(Library, EntryPoint = "zum_fs_readdir")]
    internal static partial int FsReadDir(nint fs, ulong obj,
        delegate* unmanaged<nint, byte*, ZfsUmAttributes*, int> callback, nint arg);
    [LibraryImport(Library, EntryPoint = "zum_fs_get_security")]
    internal static partial int FsGetSecurity(nint fs, ulong obj, byte* buffer, uint size, out uint length);
    [LibraryImport(Library, EntryPoint = "zum_fs_set_security")]
    internal static partial int FsSetSecurity(nint fs, ulong obj, byte* descriptor, uint length);
    [LibraryImport(Library, EntryPoint = "zum_fs_stream_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsStreamOpen(nint fs, ulong obj, string name, int create, out ulong stream);
    [LibraryImport(Library, EntryPoint = "zum_fs_stream_remove", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsStreamRemove(nint fs, ulong obj, string name);
    [LibraryImport(Library, EntryPoint = "zum_fs_stream_list")]
    internal static partial int FsStreamList(nint fs, ulong obj,
        delegate* unmanaged<nint, byte*, ZfsUmAttributes*, int> callback, nint arg);
    [LibraryImport(Library, EntryPoint = "zum_fs_clone")]
    internal static partial int FsClone(nint fs, ulong source, ulong sourceOffset,
        ulong target, ulong targetOffset, ref ulong length);
    [LibraryImport(Library, EntryPoint = "zum_fs_snapshot_list")]
    internal static partial int FsSnapshotList(nint fs,
        delegate* unmanaged<nint, byte*, ulong, ulong, int> callback, nint arg);
    [LibraryImport(Library, EntryPoint = "zum_fs_snapshot_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FsSnapshotOpen(nint fs, string snapshot, out nint snapshotFs);
    [LibraryImport(Library, EntryPoint = "zum_fs_snapshot_close")]
    internal static partial void FsSnapshotClose(nint snapshotFs);

    // Directory and stream listings collect into the List behind arg.
    [UnmanagedCallersOnly]
    internal static int CollectEntry(nint arg, byte* name, ZfsUmAttributes* attr)
    {
        var list = (List<ZfsUmDirectoryEntry>)GCHandle.FromIntPtr(arg).Target!;
        list.Add(new ZfsUmDirectoryEntry(Marshal.PtrToStringUTF8((nint)name)!, *attr));
        return 0;
    }

    [UnmanagedCallersOnly]
    internal static int CollectSnapshot(nint arg, byte* name, ulong id, ulong creation)
    {
        var list = (List<ZfsUmSnapshot>)GCHandle.FromIntPtr(arg).Target!;
        list.Add(new ZfsUmSnapshot(Marshal.PtrToStringUTF8((nint)name)!, id, creation));
        return 0;
    }

    internal static void Check(string operation, int errno)
    {
        if (errno != 0) throw new ZfsUmException(operation, errno);
    }
}

public static class ZfsUserModeEngine
{
    public static string Version => Marshal.PtrToStringUTF8(ZfsUmNative.EngineVersion()) ?? "";

    // ARC ceiling for the next Start, the floor at half of it (0: the
    // engine's defaults, RAM/4 and RAM/32).
    public static void SetArcMax(ulong bytes) => ZfsUmNative.ArcSetMax(bytes);

    public static ZfsUmArcStats ArcStats
    {
        get { ZfsUmNative.ArcStats(out var stats); return stats; }
    }

    public static void Start(string? cachefile = null) =>
        ZfsUmNative.Check("engine start", ZfsUmNative.EngineStart(cachefile));

    public static int Stop() => ZfsUmNative.EngineStop();

    // layout: "" (a top-level vdev per image), "mirror", "raidz1".."raidz3".
    public static void CreatePool(string pool, string layout, IEnumerable<string> images,
        string poolProperties = "", string rootProperties = "") =>
        ZfsUmNative.Check($"create {pool}", ZfsUmNative.PoolCreate(pool, layout,
            string.Join(';', images.Select(VdevPath)), poolProperties, rootProperties));

    public static int TryImportPool(string pool, string searchPath) =>
        ZfsUmNative.PoolImport(pool, searchPath);

    public static void ImportPool(string pool, string searchPath) =>
        ZfsUmNative.Check($"import {pool}", TryImportPool(pool, searchPath));

    public static void ExportPool(string pool, bool force = false) =>
        ZfsUmNative.Check($"export {pool}", ZfsUmNative.PoolExport(pool, force ? 1 : 0));

    public static void DestroyPool(string pool) =>
        ZfsUmNative.Check($"destroy {pool}", ZfsUmNative.PoolDestroy(pool));

    public static void Scrub(string pool) =>
        ZfsUmNative.Check($"scrub {pool}", ZfsUmNative.PoolScrub(pool));

    public static ZfsUmPoolStatus PoolStatus(string pool)
    {
        ZfsUmNative.Check($"status {pool}", ZfsUmNative.PoolStatus(pool, out var status));
        return status;
    }

    public static void CreateDataset(string name, string properties = "") =>
        ZfsUmNative.Check($"create {name}", ZfsUmNative.DatasetCreate(name, properties));

    public static int TrySetProperties(string name, string properties) =>
        ZfsUmNative.DatasetSet(name, properties);

    public static void SetProperties(string name, string properties) =>
        ZfsUmNative.Check($"set {name}", TrySetProperties(name, properties));

    public static void Snapshot(string snapshot) =>
        ZfsUmNative.Check($"snapshot {snapshot}", ZfsUmNative.Snapshot(snapshot));

    public static void DestroySnapshot(string snapshot) =>
        ZfsUmNative.Check($"destroy {snapshot}", ZfsUmNative.SnapshotDestroy(snapshot));

    public static void Rollback(string dataset) =>
        ZfsUmNative.Check($"rollback {dataset}", ZfsUmNative.Rollback(dataset));

    public static int TryOpen(string searchPath, string pool, string dataset, bool readOnly,
        out ZfsUserModeFileSystem? fs)
    {
        var errno = ZfsUmNative.FsOpen(searchPath, pool, dataset, readOnly ? 1 : 0, out var handle);
        fs = errno == 0 ? new ZfsUserModeFileSystem(handle, false) : null;
        return errno;
    }

    public static ZfsUserModeFileSystem Open(string searchPath, string pool, string? dataset = null,
        bool readOnly = false)
    {
        ZfsUmNative.Check($"open {dataset ?? pool}", TryOpen(searchPath, pool, dataset ?? pool, readOnly, out var fs));
        return fs!;
    }

    // What zpool.exe records for a file vdev.
    private static string VdevPath(string image) => @"\\?\" + Path.GetFullPath(image);
}

public sealed unsafe class ZfsUserModeFileSystem : IDisposable
{
    public const uint AttributeReadOnly = 0x1, AttributeHidden = 0x2, AttributeSystem = 0x4,
        AttributeArchive = 0x20;
    public const ulong Append = ulong.MaxValue;

    private nint _handle;
    private readonly bool _snapshot;

    internal ZfsUserModeFileSystem(nint handle, bool snapshot)
    {
        _handle = handle;
        _snapshot = snapshot;
    }

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(ZfsUserModeFileSystem));

    public ZfsUmStatFs StatFs()
    {
        ZfsUmNative.Check("statfs", ZfsUmNative.FsStatFs(Handle, out var st));
        return st;
    }

    public void Sync() => ZfsUmNative.Check("sync", ZfsUmNative.FsSync(Handle));

    public int TryLookup(string path, out ulong obj) => ZfsUmNative.FsLookup(Handle, path, out obj);

    public ulong Lookup(string path)
    {
        ZfsUmNative.Check($"lookup {path}", TryLookup(path, out var obj));
        return obj;
    }

    public ZfsUmAttributes GetAttributes(ulong obj)
    {
        ZfsUmNative.Check("getattr", ZfsUmNative.FsGetAttr(Handle, obj, out var attr));
        return attr;
    }

    public ZfsUmAttributes GetAttributes(string path) => GetAttributes(Lookup(path));

    // uint.MaxValue leaves the attributes, 0 a time unchanged.
    public void SetAttributes(ulong obj, uint attributes, ulong creation = 0, ulong access = 0,
        ulong write = 0, ulong change = 0) =>
        ZfsUmNative.Check("setattr", ZfsUmNative.FsSetAttr(Handle, obj, attributes, creation, access, write, change));

    public int TryCreate(string path, bool directory, out ulong obj) =>
        ZfsUmNative.FsCreate(Handle, path, directory ? 1 : 0, out obj);

    public ulong CreateFile(string path)
    {
        ZfsUmNative.Check($"create {path}", TryCreate(path, false, out var obj));
        return obj;
    }

    public ulong CreateDirectory(string path)
    {
        ZfsUmNative.Check($"mkdir {path}", TryCreate(path, true, out var obj));
        return obj;
    }

    // Returns where the data went (offset Append: the end of the file).
    public int TryWrite(ulong obj, ref ulong offset, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
            return ZfsUmNative.FsWrite(Handle, obj, ref offset, p, (uint)data.Length);
    }

    public ulong Write(ulong obj, ulong offset, ReadOnlySpan<byte> data)
    {
        ZfsUmNative.Check("write", TryWrite(obj, ref offset, data));
        return offset;
    }

    public int Read(ulong obj, ulong offset, Span<byte> buffer)
    {
        uint done;
        fixed (byte* p = buffer)
            ZfsUmNative.Check("read", ZfsUmNative.FsRead(Handle, obj, offset, (uint)buffer.Length, p, out done));
        return (int)done;
    }

    public byte[] ReadAll(ulong obj)
    {
        var data = new byte[GetAttributes(obj).Size];
        var done = 0;
        while (done < data.Length)
        {
            var n = Read(obj, (ulong)done, data.AsSpan(done, Math.Min(data.Length - done, 1 << 20)));
            if (n == 0) break;
            done += n;
        }
        return data;
    }

    public void Truncate(ulong obj, ulong size) => ZfsUmNative.Check("truncate", ZfsUmNative.FsTruncate(Handle, obj, size));

    public int TryRemove(string path, bool directory) => ZfsUmNative.FsRemove(Handle, path, directory ? 1 : 0);

    public void Remove(string path, bool directory = false) =>
        ZfsUmNative.Check($"remove {path}", TryRemove(path, directory));

    public int TryRename(string from, string to) => ZfsUmNative.FsRename(Handle, from, to);

    public void Rename(string from, string to) => ZfsUmNative.Check($"rename {from}", TryRename(from, to));

    public List<ZfsUmDirectoryEntry> ReadDirectory(string path)
    {
        var obj = Lookup(path);
        var list = new List<ZfsUmDirectoryEntry>();
        var handle = GCHandle.Alloc(list);
        try { ZfsUmNative.Check($"readdir {path}", ZfsUmNative.FsReadDir(Handle, obj, &ZfsUmNative.CollectEntry, GCHandle.ToIntPtr(handle))); }
        finally { handle.Free(); }
        return list;
    }

    public byte[]? GetSecurity(ulong obj)
    {
        var errno = ZfsUmNative.FsGetSecurity(Handle, obj, null, 0, out var length);
        if (errno == ZfsUmErrno.ENOENT) return null;
        if (errno != 0 && errno != 34 /* ERANGE */) ZfsUmNative.Check("get security", errno);
        var buffer = new byte[length];
        fixed (byte* p = buffer)
            ZfsUmNative.Check("get security", ZfsUmNative.FsGetSecurity(Handle, obj, p, length, out length));
        return buffer[..(int)length];
    }

    public void SetSecurity(ulong obj, byte[] descriptor)
    {
        fixed (byte* p = descriptor)
            ZfsUmNative.Check("set security", ZfsUmNative.FsSetSecurity(Handle, obj, p, (uint)descriptor.Length));
    }

    public ulong OpenStream(ulong obj, string name, bool create)
    {
        ZfsUmNative.Check($"stream {name}", ZfsUmNative.FsStreamOpen(Handle, obj, name, create ? 1 : 0, out var stream));
        return stream;
    }

    public void RemoveStream(ulong obj, string name) =>
        ZfsUmNative.Check($"remove stream {name}", ZfsUmNative.FsStreamRemove(Handle, obj, name));

    public List<ZfsUmDirectoryEntry> ListStreams(ulong obj)
    {
        var list = new List<ZfsUmDirectoryEntry>();
        var handle = GCHandle.Alloc(list);
        try { ZfsUmNative.Check("stream list", ZfsUmNative.FsStreamList(Handle, obj, &ZfsUmNative.CollectEntry, GCHandle.ToIntPtr(handle))); }
        finally { handle.Free(); }
        return list;
    }

    // Block cloning; returns the bytes cloned.
    public int TryClone(ulong source, ulong sourceOffset, ulong target, ulong targetOffset, ref ulong length) =>
        ZfsUmNative.FsClone(Handle, source, sourceOffset, target, targetOffset, ref length);

    public List<ZfsUmSnapshot> ListSnapshots()
    {
        var list = new List<ZfsUmSnapshot>();
        var handle = GCHandle.Alloc(list);
        try { ZfsUmNative.Check("snapshot list", ZfsUmNative.FsSnapshotList(Handle, &ZfsUmNative.CollectSnapshot, GCHandle.ToIntPtr(handle))); }
        finally { handle.Free(); }
        return list;
    }

    // The snapshot as a read-only file system of its own.
    public ZfsUserModeFileSystem OpenSnapshot(string snapshot)
    {
        ZfsUmNative.Check($"open {snapshot}", ZfsUmNative.FsSnapshotOpen(Handle, snapshot, out var fs));
        return new ZfsUserModeFileSystem(fs, true);
    }

    public int Close()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0) return 0;
        if (_snapshot) { ZfsUmNative.FsSnapshotClose(handle); return 0; }
        return ZfsUmNative.FsClose(handle);
    }

    public void Dispose() => Close();
}

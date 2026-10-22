using System.Runtime.InteropServices;

namespace Zfs.Interop;

public enum ZfsStatus : uint
{
    Ok = 0, InvalidArgument = 1, IoError = 2, OutOfRange = 3,
    NotSupported = 4, NotAZfsDevice = 5, CorruptMetadata = 6
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsDeviceInfo
{
    public ulong SizeBytes;
    public uint LogicalSectorSize;
    public uint PhysicalSectorSize;
    public uint ReadOnly;
    public uint VdevLabelsFound;
    public uint UberblocksFound;
    public uint ValidLabelsFound;
    public uint UnsupportedFeatures;
    public ulong PoolGuid;
    public ulong VdevGuid;
    public ulong Ashift;
    public uint MosRootValid;
    public ulong MosRootOffset;
    public ulong MosRootSize;
    public ulong SelectedTxg;
    public ulong SelectedUberblockOffset;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsDslDirInfo
{
    public ulong HeadDatasetObject;
    public ulong ChildDirZapObject;
    public ulong PropsZapObject;
}

internal static partial class NativeMethods
{
    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_readonly", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus OpenReadOnly(string path, out nint device);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_close")]
    internal static partial void Close(nint device);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_info")]
    internal static partial ZfsStatus GetInfo(nint device, out ZfsDeviceInfo info);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_probe_pool")]
    internal static partial ZfsStatus ProbePool(nint device);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read")]
    internal static partial ZfsStatus Read(nint device, ulong offset, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_mos_root")]
    internal static partial ZfsStatus ReadMosRoot(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_mos_first_dnode_block")]
    internal static partial ZfsStatus ReadMosFirstDnodeBlock(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_mos_object_directory_block")]
    internal static partial ZfsStatus ReadMosObjectDirectoryBlock(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_lookup_mos_object_directory", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus LookupMosObjectDirectory(nint device, string name, out ulong value);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_root_dataset_info")]
    internal static partial ZfsStatus GetRootDatasetInfo(nint device, out ZfsDslDirInfo info);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_dataset_objset_root")]
    internal static partial ZfsStatus ReadRootDatasetObjsetRoot(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_filesystem_master_block")]
    internal static partial ZfsStatus ReadRootFilesystemMasterBlock(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_lookup_root_filesystem_master", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus LookupRootFilesystemMaster(nint device, string name, out ulong value);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_filesystem_object_dnode_block")]
    internal static partial ZfsStatus ReadRootFilesystemObjectDnodeBlock(nint device, ulong objectNumber,
        nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_directory_data_block")]
    internal static partial ZfsStatus ReadRootDirectoryDataBlock(nint device, nint buffer, ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_lookup_root_directory", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus LookupRootDirectory(nint device, string name, out ulong objectNumber);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_file")]
    internal static partial ZfsStatus ReadRootFile(nint device, ulong objectNumber, nint buffer,
        ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_root_file_size")]
    internal static partial ZfsStatus GetRootFileSize(nint device, ulong objectNumber, out ulong size);
}

public sealed class ZfsReadOnlyDevice : IDisposable
{
    private nint _handle;
    public ZfsDeviceInfo Info { get; }

    private ZfsReadOnlyDevice(nint handle, ZfsDeviceInfo info) { _handle = handle; Info = info; }

    public static ZfsReadOnlyDevice Open(string path)
    {
        var status = NativeMethods.OpenReadOnly(path, out var handle);
        if (status != ZfsStatus.Ok) throw new IOException($"ZFS open failed: {status}");
        status = NativeMethods.GetInfo(handle, out var info);
        if (status != ZfsStatus.Ok) { NativeMethods.Close(handle); throw new IOException($"ZFS info failed: {status}"); }
        return new(handle, info);
    }

    public byte[] Read(ulong offset, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[length];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.Read(_handle, offset, handle.AddrOfPinnedObject(), (ulong)length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"ZFS read failed: {status}");
            if (bytesRead != (ulong)length) throw new EndOfStreamException();
            return buffer;
        }
        finally { handle.Free(); }
    }

    public bool IsRecognizedPool => NativeMethods.ProbePool(_handle) == ZfsStatus.Ok;

    public byte[] ReadMosRoot()
    {
        if (Info.MosRootValid == 0 || Info.MosRootSize > int.MaxValue)
            throw new IOException("MOS root is not available");
        var buffer = new byte[(int)Info.MosRootSize];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadMosRoot(_handle, pinned.AddrOfPinnedObject(),
                (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"MOS root read failed: {status}");
            if (bytesRead != (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer;
        }
        finally { pinned.Free(); }
    }

    public byte[] ReadMosFirstDnodeBlock()
    {
        const int length = 128 * 1024;
        var buffer = new byte[length];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadMosFirstDnodeBlock(_handle, pinned.AddrOfPinnedObject(),
                (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"MOS dnode block read failed: {status}");
            if (bytesRead == 0 || bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public byte[] ReadMosObjectDirectoryBlock()
    {
        const int capacity = 128 * 1024;
        var buffer = new byte[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadMosObjectDirectoryBlock(_handle,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"MOS object-directory block read failed: {status}");
            if (bytesRead == 0 || bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public ulong LookupMosObjectDirectory(string name)
    {
        var status = NativeMethods.LookupMosObjectDirectory(_handle, name, out var value);
        if (status != ZfsStatus.Ok) throw new IOException($"MOS object-directory lookup failed: {status}");
        return value;
    }

    public ZfsDslDirInfo GetRootDatasetInfo()
    {
        var status = NativeMethods.GetRootDatasetInfo(_handle, out var info);
        if (status != ZfsStatus.Ok) throw new IOException($"Root dataset read failed: {status}");
        return info;
    }

    public byte[] ReadRootDatasetObjsetRoot()
    {
        const int capacity = 128 * 1024;
        var buffer = new byte[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadRootDatasetObjsetRoot(_handle,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"Dataset objset root read failed: {status}");
            if (bytesRead == 0 || bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public byte[] ReadRootFilesystemMasterBlock()
    {
        const int capacity = 128 * 1024;
        var buffer = new byte[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadRootFilesystemMasterBlock(_handle,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"Filesystem master-node read failed: {status}");
            if (bytesRead == 0 || bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public ulong LookupRootFilesystemMaster(string name)
    {
        var status = NativeMethods.LookupRootFilesystemMaster(_handle, name, out var value);
        if (status != ZfsStatus.Ok) throw new IOException($"Filesystem master lookup failed: {status}");
        return value;
    }

    public byte[] ReadRootFilesystemObjectDnodeBlock(ulong objectNumber)
    {
        const int capacity = 128 * 1024;
        var buffer = new byte[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadRootFilesystemObjectDnodeBlock(_handle, objectNumber,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"Filesystem object dnode read failed: {status}");
            if (bytesRead == 0 || bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public byte[] ReadRootDirectoryDataBlock()
    {
        const int capacity = 128 * 1024;
        var buffer = new byte[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadRootDirectoryDataBlock(_handle,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"Root directory data read failed: {status}");
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public ulong LookupRootDirectory(string name)
    {
        var status = NativeMethods.LookupRootDirectory(_handle, name, out var objectNumber);
        if (status != ZfsStatus.Ok) throw new IOException($"Root directory lookup failed: {status}");
        return objectNumber;
    }

    public byte[] ReadRootFile(ulong objectNumber)
    {
        var status = NativeMethods.GetRootFileSize(_handle, objectNumber, out var size);
        if (status != ZfsStatus.Ok) throw new IOException($"Root file size read failed: {status}");
        if (size > int.MaxValue) throw new IOException("File is too large for a single managed buffer.");
        if (size == 0) return Array.Empty<byte>();
        var buffer = new byte[(int)size];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            status = NativeMethods.ReadRootFile(_handle, objectNumber,
                pinned.AddrOfPinnedObject(), (ulong)buffer.Length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"Root file read failed: {status}");
            if (bytesRead > (ulong)buffer.Length) throw new EndOfStreamException();
            return buffer[..(int)bytesRead];
        }
        finally { pinned.Free(); }
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;
        NativeMethods.Close(_handle);
        _handle = nint.Zero;
        GC.SuppressFinalize(this);
    }
}

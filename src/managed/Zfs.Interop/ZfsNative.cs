using System.Runtime.InteropServices;
using System.Text;

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
    public uint Topology;
    public uint MemberCount;
    public uint AvailableMembers;
    public uint ChecksumFallbacks;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsDslDirInfo
{
    public ulong HeadDatasetObject;
    public ulong ChildDirZapObject;
    public ulong PropsZapObject;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZfsObjectInfo
{
    public ulong ObjectNumber;
    public uint Type;
}

internal static partial class NativeMethods
{
    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_readonly", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus OpenReadOnly(string path, out nint device);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_mirror_readonly", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus OpenMirrorReadOnly(string firstPath, string secondPath, out nint device);

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

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_lookup_directory", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus LookupDirectory(nint device, ulong directory, string name,
        out ulong objectNumber);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_directory_entry_count")]
    internal static partial ZfsStatus GetDirectoryEntryCount(nint device, ulong directory, out ulong count);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_directory_entry")]
    internal static partial ZfsStatus GetDirectoryEntry(nint device, ulong directory, ulong index,
        nint name, ulong nameCapacity, out ulong nameBytes, out ulong objectNumber, out uint type);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_root_file")]
    internal static partial ZfsStatus ReadRootFile(nint device, ulong objectNumber, nint buffer,
        ulong length, out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_root_file_size")]
    internal static partial ZfsStatus GetRootFileSize(nint device, ulong objectNumber, out ulong size);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_default_dataset")]
    internal static partial ZfsStatus OpenDefaultDataset(nint device, out nint dataset);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_close_dataset")]
    internal static partial void CloseDataset(nint dataset);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_dataset_root")]
    internal static partial ZfsStatus OpenDatasetRoot(nint dataset, out nint objectHandle);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_open_child", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ZfsStatus OpenChild(nint directory, string name, out nint objectHandle);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_close_object")]
    internal static partial void CloseObject(nint objectHandle);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_object_info")]
    internal static partial ZfsStatus GetObjectInfo(nint objectHandle, out ZfsObjectInfo info);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_object_entry_count")]
    internal static partial ZfsStatus GetObjectEntryCount(nint directory, out ulong count);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_object_entry")]
    internal static partial ZfsStatus GetObjectEntry(nint directory, ulong index, nint name,
        ulong nameCapacity, out ulong nameBytes, out ulong objectNumber, out uint type);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_get_object_file_size")]
    internal static partial ZfsStatus GetObjectFileSize(nint file, out ulong size);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_object_file")]
    internal static partial ZfsStatus ReadObjectFile(nint file, nint buffer, ulong length,
        out ulong bytesRead);

    [LibraryImport("ZfsCore", EntryPoint = "zfs_core_read_object_file_range")]
    internal static partial ZfsStatus ReadObjectFileRange(nint file, ulong fileOffset,
        nint buffer, ulong length, out ulong bytesRead);
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

    public static ZfsReadOnlyDevice OpenMirror(string firstPath, string secondPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondPath);
        var status = NativeMethods.OpenMirrorReadOnly(firstPath, secondPath, out var handle);
        if (status != ZfsStatus.Ok) throw new IOException($"ZFS mirror open failed: {status}");
        status = NativeMethods.GetInfo(handle, out var info);
        if (status != ZfsStatus.Ok)
        {
            NativeMethods.Close(handle);
            throw new IOException($"ZFS mirror info failed: {status}");
        }
        return new(handle, info);
    }

    public ZfsDeviceInfo RefreshInfo()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        var status = NativeMethods.GetInfo(_handle, out var info);
        if (status != ZfsStatus.Ok) throw new IOException($"ZFS info failed: {status}");
        return info;
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

    public ulong LookupDirectory(ulong directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var status = NativeMethods.LookupDirectory(_handle, directory, name, out var objectNumber);
        if (status != ZfsStatus.Ok) throw new IOException($"Directory lookup failed for '{name}': {status}");
        return objectNumber;
    }

    public ulong ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = LookupRootFilesystemMaster("ROOT");
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
                throw new ArgumentException("Parent path components are not supported.", nameof(path));
            current = LookupDirectory(current, component);
        }
        return current;
    }

    public IReadOnlyList<ZfsDirectoryEntry> EnumerateDirectory(ulong directory)
    {
        var status = NativeMethods.GetDirectoryEntryCount(_handle, directory, out var count);
        if (status != ZfsStatus.Ok) throw new IOException($"Directory count failed: {status}");
        if (count > int.MaxValue) throw new IOException("Directory contains too many entries.");
        var entries = new List<ZfsDirectoryEntry>((int)count);
        var nameBuffer = new byte[51];
        var pinned = GCHandle.Alloc(nameBuffer, GCHandleType.Pinned);
        try
        {
            for (ulong index = 0; index < count; ++index)
            {
                status = NativeMethods.GetDirectoryEntry(_handle, directory, index,
                    pinned.AddrOfPinnedObject(), (ulong)nameBuffer.Length, out var nameBytes,
                    out var objectNumber, out var type);
                if (status != ZfsStatus.Ok) throw new IOException($"Directory entry {index} failed: {status}");
                if (nameBytes is 0 or > 51 || nameBuffer[(int)nameBytes - 1] != 0)
                    throw new InvalidDataException("Native directory entry returned an invalid name.");
                var name = new UTF8Encoding(false, true).GetString(nameBuffer, 0, (int)nameBytes - 1);
                entries.Add(new(name, objectNumber, type));
            }
        }
        finally { pinned.Free(); }
        return entries;
    }

    public IReadOnlyList<ZfsDirectoryEntry> EnumerateDirectory(string path) =>
        EnumerateDirectory(ResolvePath(path));

    public byte[] ReadFile(string path) => ReadRootFile(ResolvePath(path));

    public ZfsDataset OpenDefaultDataset()
    {
        var status = NativeMethods.OpenDefaultDataset(_handle, out var dataset);
        if (status != ZfsStatus.Ok) throw new IOException($"Default dataset open failed: {status}");
        return new ZfsDataset(this, dataset);
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

public readonly record struct ZfsDirectoryEntry(string Name, ulong ObjectNumber, uint Type);

public sealed class ZfsDataset : IDisposable
{
    private readonly ZfsReadOnlyDevice _owner;
    private nint _handle;

    internal ZfsDataset(ZfsReadOnlyDevice owner, nint handle)
    {
        _owner = owner;
        _handle = handle;
    }

    public ZfsObject OpenRoot()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        var status = NativeMethods.OpenDatasetRoot(_handle, out var objectHandle);
        if (status != ZfsStatus.Ok) throw new IOException($"Dataset root open failed: {status}");
        return new ZfsObject(this, objectHandle);
    }

    public ZfsObject OpenPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = OpenRoot();
        try
        {
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".") continue;
                if (component == "..")
                    throw new ArgumentException("Parent path components are not supported.", nameof(path));
                var next = current.OpenChild(component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;
        NativeMethods.CloseDataset(_handle);
        _handle = nint.Zero;
        GC.KeepAlive(_owner);
        GC.SuppressFinalize(this);
    }
}

public sealed class ZfsObject : IDisposable
{
    private readonly ZfsDataset _owner;
    private nint _handle;

    internal ZfsObject(ZfsDataset owner, nint handle)
    {
        _owner = owner;
        _handle = handle;
        var status = NativeMethods.GetObjectInfo(_handle, out var info);
        if (status != ZfsStatus.Ok)
        {
            NativeMethods.CloseObject(_handle);
            _handle = nint.Zero;
            throw new IOException($"Object info failed: {status}");
        }
        Info = info;
    }

    public ZfsObjectInfo Info { get; }

    public ZfsObject OpenChild(string name)
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var status = NativeMethods.OpenChild(_handle, name, out var child);
        if (status != ZfsStatus.Ok) throw new IOException($"Child open failed for '{name}': {status}");
        return new ZfsObject(_owner, child);
    }

    public IReadOnlyList<ZfsDirectoryEntry> Enumerate()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        var status = NativeMethods.GetObjectEntryCount(_handle, out var count);
        if (status != ZfsStatus.Ok) throw new IOException($"Directory count failed: {status}");
        if (count > int.MaxValue) throw new IOException("Directory contains too many entries.");
        var entries = new List<ZfsDirectoryEntry>((int)count);
        var nameBuffer = new byte[51];
        var pinned = GCHandle.Alloc(nameBuffer, GCHandleType.Pinned);
        try
        {
            for (ulong index = 0; index < count; ++index)
            {
                status = NativeMethods.GetObjectEntry(_handle, index, pinned.AddrOfPinnedObject(),
                    (ulong)nameBuffer.Length, out var nameBytes, out var objectNumber, out var type);
                if (status != ZfsStatus.Ok) throw new IOException($"Directory entry {index} failed: {status}");
                if (nameBytes is 0 or > 51 || nameBuffer[(int)nameBytes - 1] != 0)
                    throw new InvalidDataException("Native directory entry returned an invalid name.");
                var name = new UTF8Encoding(false, true).GetString(nameBuffer, 0, (int)nameBytes - 1);
                entries.Add(new(name, objectNumber, type));
            }
        }
        finally { pinned.Free(); }
        return entries;
    }

    public byte[] ReadAllBytes()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        var status = NativeMethods.GetObjectFileSize(_handle, out var size);
        if (status != ZfsStatus.Ok) throw new IOException($"File size read failed: {status}");
        if (size > int.MaxValue) throw new IOException("File is too large for a single managed buffer.");
        if (size == 0) return Array.Empty<byte>();
        var buffer = new byte[(int)size];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            status = NativeMethods.ReadObjectFile(_handle, pinned.AddrOfPinnedObject(), size,
                out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"File read failed: {status}");
            if (bytesRead != size) throw new EndOfStreamException();
            return buffer;
        }
        finally { pinned.Free(); }
    }

    public ulong GetFileSize()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        var status = NativeMethods.GetObjectFileSize(_handle, out var size);
        if (status != ZfsStatus.Ok) throw new IOException($"File size read failed: {status}");
        return size;
    }

    public byte[] Read(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[length];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NativeMethods.ReadObjectFileRange(_handle, offset,
                pinned.AddrOfPinnedObject(), (ulong)length, out var bytesRead);
            if (status != ZfsStatus.Ok) throw new IOException($"File range read failed: {status}");
            if (bytesRead != (ulong)length) throw new EndOfStreamException();
            return buffer;
        }
        finally { pinned.Free(); }
    }

    public void CopyTo(Stream destination, int bufferSize = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        var length = GetFileSize();
        ulong offset = 0;
        while (offset < length)
        {
            var count = (int)Math.Min((ulong)bufferSize, length - offset);
            var data = Read(offset, count);
            destination.Write(data);
            offset += (ulong)count;
        }
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;
        NativeMethods.CloseObject(_handle);
        _handle = nint.Zero;
        GC.KeepAlive(_owner);
        GC.SuppressFinalize(this);
    }
}

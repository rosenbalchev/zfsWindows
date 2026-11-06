using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Zfs.Admin;

/// <summary>
/// Copies a file by sharing its blocks (ZFS block cloning) instead of its
/// data.  The kernel-driver edition answers Windows' own
/// FSCTL_DUPLICATE_EXTENTS_TO_FILE.  The user-mode edition cannot: WinFsp
/// passes a file system only control codes of a custom device type, so
/// zfswin-um takes the same request under ZUM_IOCTL_DUPLICATE_EXTENTS.
/// Runs as the caller; no broker is involved.
/// </summary>
public static class FileClone
{
    public const uint FsctlDuplicateExtentsToFile = 0x00098344;
    /// <summary>CTL_CODE(0x8F5A, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS), as in zum_winfsp.c.</summary>
    public const uint UserModeDuplicateExtents = 0x8F5A2004;

    const int ErrorInvalidFunction = 1;
    const int ErrorNotSupported = 50;

    [StructLayout(LayoutKind.Sequential)]
    struct DuplicateExtentsData
    {
        public nint FileHandle;
        public long SourceFileOffset;
        public long TargetFileOffset;
        public long ByteCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref DuplicateExtentsData input, int inputSize,
        nint output, int outputSize, out int returned, nint overlapped);

    /// <summary>Clones source into target (created, or replaced with overwrite).</summary>
    public static JsonObject Clone(string source, string target, bool overwrite)
    {
        foreach (var (name, path) in new[] { ("source", source), ("target", target) })
            if (!Path.IsPathFullyQualified(path))
                throw Validation.Invalid(name, "a fully qualified file path is required.");
        if (!File.Exists(source))
            throw new AdminException(ErrorCode.NotFound, $"'{source}' does not exist.");
        if (File.Exists(target) && !overwrite)
            throw new AdminException(ErrorCode.InvalidArgument, $"'{target}' exists; add --force to replace it.");

        using SafeFileHandle input = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = RandomAccess.GetLength(input);
        using SafeFileHandle output = File.OpenHandle(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // The target must already span the range; the clone replaces its blocks.
        RandomAccess.SetLength(output, length);
        if (length == 0)
            return Result(source, target, 0, "empty");

        var request = new DuplicateExtentsData
        {
            FileHandle = input.DangerousGetHandle(),
            ByteCount = (length + 4095) & ~4095L,	// whole clusters; the tail ends at end of file
        };
        string method = "FSCTL_DUPLICATE_EXTENTS_TO_FILE";
        if (!DeviceIoControl(output, FsctlDuplicateExtentsToFile, ref request, Marshal.SizeOf<DuplicateExtentsData>(), 0, 0, out _, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (ErrorInvalidFunction or ErrorNotSupported))
                throw Failed(error, target);
            method = "zfswin-um";
            if (!DeviceIoControl(output, UserModeDuplicateExtents, ref request, Marshal.SizeOf<DuplicateExtentsData>(), 0, 0, out _, 0))
            {
                error = Marshal.GetLastWin32Error();
                throw error is ErrorInvalidFunction or ErrorNotSupported
                    ? new AdminException(ErrorCode.CommandFailed, $"'{target}' is not on a ZFS volume that supports block cloning (or the pool's block_cloning feature is off).")
                    : Failed(error, target);
            }
        }
        return Result(source, target, length, method);
    }

    static AdminException Failed(int error, string target) =>
        new(ErrorCode.CommandFailed, $"Cloning into '{target}' failed: {new Win32Exception(error).Message} ({error}).");

    static JsonObject Result(string source, string target, long bytes, string method) =>
        new() { ["source"] = source, ["target"] = target, ["bytes"] = bytes, ["method"] = method };
}

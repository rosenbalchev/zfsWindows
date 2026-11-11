using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zfs.Admin;

/// <summary>
/// Checks every value the broker passes to zpool.exe or zfs.exe.  The tools
/// are started with an argument vector (no shell), so the threats are
/// option injection (a value starting with '-'), names the tools would read
/// differently than the caller meant, and unbounded input.
/// </summary>
public static partial class Validation
{
    public const int MaxNameLength = 255;	// ZFS_MAX_DATASET_NAME_LEN - 1
    public const int MaxPropertyValueLength = 8192;	// ZFS_MAXPROPLEN
    public const int MaxListItems = 64;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.:-]*$")]
    private static partial Regex PoolNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.: -]+$")]
    private static partial Regex ComponentPattern();

    // Native properties, or user properties, which must contain a colon.
    [GeneratedRegex(@"^([a-z][a-z0-9_]*|[a-z0-9_.-]+:[a-z0-9_.:-]*)$")]
    private static partial Regex PropertyNamePattern();

    [GeneratedRegex(@"^\\\\[?.]\\PhysicalDrive[0-9]{1,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex PhysicalDrivePattern();

    [GeneratedRegex(@"^#([0-9]{1,20})#([0-9]{1,20})#(?:\\\\[?.]\\)?PhysicalDrive([0-9]{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetDevicePattern();

    [GeneratedRegex(@"^[0-9]{1,20}$")]
    private static partial Regex GuidPattern();

    /// <summary>
    /// A pool name, with zfs_namecheck's rules; <paramref name="forCreate"/>
    /// adds the prefixes zpool create reserves for vdev keywords.
    /// </summary>
    public static string PoolName(string? value, string argument = "pool", bool forCreate = false)
    {
        value = Required(value, argument);
        if (value.Length > MaxNameLength || !PoolNamePattern().IsMatch(value))
            throw Invalid(argument, $"'{value}' is not a valid pool name.");
        bool reserved = value is "mirror" or "raidz" or "draid"
            || value[0] == 'c' && value.Length > 1 && char.IsAsciiDigit(value[1])
            || forCreate && (value.StartsWith("mirror", StringComparison.Ordinal) || value.StartsWith("raidz", StringComparison.Ordinal)
                || value.StartsWith("draid", StringComparison.Ordinal) || value.StartsWith("spare", StringComparison.Ordinal) || value == "log");
        if (reserved)
            throw Invalid(argument, $"'{value}' is a reserved pool name.");
        return value;
    }

    /// <summary>A pool name or a numeric pool GUID (import only).</summary>
    public static string PoolNameOrGuid(string? value, string argument = "pool") =>
        value is not null && GuidPattern().IsMatch(value) ? value : PoolName(value, argument);

    public static string DatasetName(string? value, string argument = "dataset")
    {
        value = Required(value, argument);
        if (value.Contains('@') || value.Contains('#'))
            throw Invalid(argument, $"'{value}' is a snapshot or bookmark, not a dataset.");
        CheckPath(value, argument);
        return value;
    }

    public static string SnapshotName(string? value, string argument = "snapshot")
    {
        value = Required(value, argument);
        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            throw Invalid(argument, $"'{value}' is not of the form dataset@snapshot.");
        CheckPath(value[..at], argument);
        string snap = value[(at + 1)..];
        if (!ComponentPattern().IsMatch(snap) || snap.Trim() != snap)
            throw Invalid(argument, $"'{snap}' is not a valid snapshot name.");
        if (value.Length > MaxNameLength)
            throw Invalid(argument, "The snapshot name is too long.");
        return value;
    }

    static void CheckPath(string value, string argument)
    {
        if (value.Length > MaxNameLength)
            throw Invalid(argument, "The name is too long.");
        string[] parts = value.Split('/');
        PoolName(parts[0], argument);
        foreach (string part in parts.Skip(1))
        {
            if (part.Length == 0 || part is "." or ".." || !ComponentPattern().IsMatch(part) || part.Trim() != part)
                throw Invalid(argument, $"'{value}' is not a valid dataset name.");
        }
    }

    public static string PropertyName(string? value, string argument = "property")
    {
        value = Required(value, argument);
        if (value.Length > MaxNameLength || !PropertyNamePattern().IsMatch(value))
            throw Invalid(argument, $"'{value}' is not a valid property name.");
        return value;
    }

    public static string PropertyValue(string? value, string argument = "value")
    {
        if (value is null)
            throw Invalid(argument, "A property value is required.");
        if (value.Length > MaxPropertyValueLength || value.Any(c => c == '\0' || c == '\r' || c == '\n'))
            throw Invalid(argument, "The property value is too long or contains a line break or NUL.");
        return value;
    }

    /// <summary>
    /// A device for a vdev, in the form zpool.exe takes: an existing file
    /// (given as an absolute path, returned as \\?\X:\...; zpool refuses a
    /// plain path with "must be a full path or shorthand device name") or
    /// \\?\PhysicalDriveN.
    /// </summary>
    public static string DevicePath(string? value, string argument = "device", bool mustExist = true)
    {
        value = Required(value, argument);
        Match offsetDevice = OffsetDevicePattern().Match(value);
        if (offsetDevice.Success)
        {
            if (!ulong.TryParse(offsetDevice.Groups[1].Value, out ulong offset)
                || !ulong.TryParse(offsetDevice.Groups[2].Value, out ulong size)
                || size == 0
                || offset > ulong.MaxValue - size)
                throw Invalid(argument, "the physical-disk range has an invalid offset or length.");
            return $"#{offset}#{size}#\\\\?\\PhysicalDrive{offsetDevice.Groups[3].Value}";
        }
        if (PhysicalDrivePattern().IsMatch(value))
            return @"\\?\PhysicalDrive" + value[(value.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) + 5)..];
        CheckFilePath(value, argument);
        if (mustExist && !File.Exists(value))
            throw new AdminException(ErrorCode.NotFound, $"The device file '{value}' does not exist.");
        return @"\\?\" + value;
    }

    /// <summary>A directory to search for pool devices (zpool import -d).</summary>
    public static string SearchDirectory(string? value, string argument = "dir")
    {
        value = Required(value, argument);
        CheckFilePath(value, argument);
        if (!Directory.Exists(value))
            throw new AdminException(ErrorCode.NotFound, $"The directory '{value}' does not exist.");
        return value;
    }

    static void CheckFilePath(string value, string argument)
    {
        if (value.Length > 1024 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.Contains('*') || value.Contains('?'))
            throw Invalid(argument, $"'{value}' is not a valid path.");
        // Fully qualified local paths only: no relative paths (the broker's
        // working directory is not the caller's), no UNC or device paths.
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\' || value.Contains(@"\..\") || value.EndsWith(@"\.."))
            throw Invalid(argument, $"'{value}' must be a fully qualified local path such as D:\\pools\\disk1.img.");
    }

    public static string Required(string? value, string argument)
    {
        if (string.IsNullOrEmpty(value))
            throw Invalid(argument, $"'{argument}' is required.");
        if (value[0] == '-')
            throw Invalid(argument, $"'{value}' must not start with '-'.");
        return value;
    }

    public static AdminException Invalid(string argument, string message) =>
        new(ErrorCode.InvalidArgument, $"{argument}: {message}");

    // --- typed access to request arguments ----------------------------------

    public static string? OptionalString(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return null;
        if (node is JsonValue v && v.TryGetValue(out string? s))
            return s;
        throw Invalid(name, "must be a string.");
    }

    public static bool Flag(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return false;
        if (node is JsonValue v && v.TryGetValue(out bool b))
            return b;
        throw Invalid(name, "must be true or false.");
    }

    public static IReadOnlyList<string> StringList(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return [];
        if (node is not JsonArray array)
            throw Invalid(name, "must be a list of strings.");
        if (array.Count > MaxListItems)
            throw Invalid(name, $"at most {MaxListItems} items are allowed.");
        return array.Select(item => item is JsonValue v && v.TryGetValue(out string? s) && s is not null
            ? s : throw Invalid(name, "must be a list of strings.")).ToList();
    }

    /// <summary>A name → value object such as {"compression": "zstd"}, validated.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Properties(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return [];
        if (node is not JsonObject obj)
            throw Invalid(name, "must be an object of property names to values.");
        if (obj.Count > MaxListItems)
            throw Invalid(name, $"at most {MaxListItems} properties are allowed.");
        return obj.Select(p => new KeyValuePair<string, string>(PropertyName(p.Key, name),
            PropertyValue(p.Value is JsonValue v && v.TryGetValue(out string? s) ? s : throw Invalid(name, $"the value of '{p.Key}' must be a string."), name))).ToList();
    }

    public static void RejectUnknown(JsonObject args, IReadOnlyCollection<string> known)
    {
        foreach (var p in args)
        {
            if (!known.Contains(p.Key))
                throw new AdminException(ErrorCode.BadRequest, $"Unknown argument '{p.Key}'.");
        }
    }
}

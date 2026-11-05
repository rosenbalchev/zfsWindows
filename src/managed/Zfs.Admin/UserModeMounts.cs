using Microsoft.Win32;

namespace Zfs.Admin;

public sealed record UserModeMount(string Name, string Source, string Pool, string Dataset, string MountPoint, bool ReadOnly);

/// <summary>
/// The user-mode edition's mount configuration: one subkey per dataset under
/// HKLM\SOFTWARE\OpenZFS for Windows\UserMode\Mounts, which the
/// ZfsWinUserMode service (zfswin-um.exe) mounts whenever it starts.
/// </summary>
public sealed class UserModeMounts(RegistryKey root, string path = UserModeMounts.DefaultPath)
{
    public const string DefaultPath = @"SOFTWARE\OpenZFS for Windows\UserMode\Mounts";
    public const string ServiceName = "ZfsWinUserMode";

    public static UserModeMounts Machine() => new(Registry.LocalMachine);

    public IReadOnlyList<UserModeMount> List()
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key is null)
            return [];
        var mounts = new List<UserModeMount>();
        foreach (string name in key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            using RegistryKey? m = key.OpenSubKey(name);
            if (m is null)
                continue;
            mounts.Add(new UserModeMount(name, m.GetValue("Source") as string ?? "", m.GetValue("Pool") as string ?? "",
                m.GetValue("Dataset") as string ?? "", m.GetValue("MountPoint") as string ?? "", m.GetValue("ReadOnly") is int r && r != 0));
        }
        return mounts;
    }

    /// <summary>Adds or replaces a mount; takes effect when the service (re)starts.</summary>
    public void Save(UserModeMount mount)
    {
        Validate(mount);
        using RegistryKey key = root.CreateSubKey($@"{path}\{mount.Name}");
        key.SetValue("Source", mount.Source, RegistryValueKind.String);
        key.SetValue("Pool", mount.Pool, RegistryValueKind.String);
        key.SetValue("Dataset", mount.Dataset, RegistryValueKind.String);
        key.SetValue("MountPoint", mount.MountPoint, RegistryValueKind.String);
        key.SetValue("ReadOnly", mount.ReadOnly ? 1 : 0, RegistryValueKind.DWord);
    }

    public void Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\'))
            throw Validation.Invalid("name", "a mount name is required and cannot contain '\\'.");
        root.DeleteSubKeyTree($@"{path}\{name}", throwOnMissingSubKey: false);
    }

    /// <summary>The checks the GUI applies before anything is written.</summary>
    public static void Validate(UserModeMount m)
    {
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Contains('\\') || m.Name.Length > 64)
            throw Validation.Invalid("name", "a mount name of up to 64 characters, without '\\', is required.");
        if (!Path.IsPathFullyQualified(m.Source) && !m.Source.StartsWith(@"\\?\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
            throw Validation.Invalid("source", "the source must be a full folder path (image files) or \\\\?\\PhysicalDriveN.");
        Validation.PoolName(m.Pool);
        Validation.DatasetName(m.Dataset);
        if (!m.Dataset.Equals(m.Pool, StringComparison.Ordinal) && !m.Dataset.StartsWith(m.Pool + "/", StringComparison.Ordinal))
            throw Validation.Invalid("dataset", "the dataset must belong to the pool.");
        bool letter = m.MountPoint.Length == 2 && char.IsAsciiLetter(m.MountPoint[0]) && m.MountPoint[1] == ':';
        if (!letter && !Path.IsPathFullyQualified(m.MountPoint))
            throw Validation.Invalid("mountpoint", "the mount point must be a drive letter (Z:) or a full folder path.");
    }
}

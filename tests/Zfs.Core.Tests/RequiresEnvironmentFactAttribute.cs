using Xunit;

namespace Zfs.Core.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresEnvironmentFactAttribute : FactAttribute
{
    public RequiresEnvironmentFactAttribute(params string[] variables)
    {
        foreach (var variable in variables)
        {
            var path = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path))) continue;
            Skip = $"Set {string.Join(" and ", variables)} to existing fixture files or directories.";
            break;
        }
    }
}

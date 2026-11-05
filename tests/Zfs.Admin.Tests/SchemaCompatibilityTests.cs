using System.Text.Json.Nodes;
using Json.Schema;
using Zfs.Admin;
using CliProgram = Zfs.Admin.Cli.Program;

namespace Zfs.Admin.Tests;

/// <summary>
/// The zfswin --json contract (schemas/zfswin-output.schema.json) against
/// real OpenZFS output.  samples/ holds what the installed zpool.exe and
/// zfs.exe printed in the step-14 VM run (scripts/vm/guest-installer.ps1
/// -Stage samples), so an OpenZFS JSON change shows up here.
/// </summary>
public class SchemaCompatibilityTests
{
    static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "zfswin-output.schema.json")));

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    static void AssertValid(JsonNode envelope)
    {
        EvaluationResults result = Schema.Evaluate(envelope, new EvaluationOptions { OutputFormat = OutputFormat.List });
        string errors = string.Join("; ", result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));
        Assert.True(result.IsValid, errors + "\n" + envelope.ToJsonString());
    }

    public static TheoryData<string, string> Conversions => new()
    {
        { "pool.list", "zpool-list.json" },
        { "pool.status", "zpool-status.json" },
        { "pool.get", "zpool-get.json" },
        { "dataset.list", "zfs-list.json" },
        { "dataset.get", "zfs-get.json" },
        { "snapshot.list", "zfs-snapshots.json" },
        { "mount.list", "zfs-mount.txt" },
        { "pool.discover", "zpool-import.txt" },
    };

    static JsonNode Convert(string operation, string text) => operation switch
    {
        "pool.list" => ZfsJson.PoolList(JsonNode.Parse(text)!),
        "pool.status" => ZfsJson.PoolStatus(JsonNode.Parse(text)!),
        "pool.get" => ZfsJson.PoolProperties(JsonNode.Parse(text)!, "zwin"),
        "dataset.list" => ZfsJson.DatasetList(JsonNode.Parse(text)!),
        "dataset.get" => ZfsJson.DatasetProperties(JsonNode.Parse(text)!, "zwin/docs"),
        "snapshot.list" => ZfsJson.SnapshotList(JsonNode.Parse(text)!),
        "mount.list" => ZfsJson.MountList(text),
        "pool.discover" => ZfsJson.ImportablePools(text),
        _ => throw new ArgumentException(operation),
    };

    [Theory]
    [MemberData(nameof(Conversions))]
    public void RealOutputConvertsToTheSchema(string operation, string sample)
    {
        JsonNode result = Convert(operation, Sample(sample));
        AssertValid(CliProgram.Success(operation, result));
    }

    [Fact]
    public void RealOutputCarriesTheExpectedValues()
    {
        var pools = ZfsJson.PoolList(JsonNode.Parse(Sample("zpool-list.json"))!)["pools"]!.AsArray();
        JsonNode zwin = Assert.Single(pools, p => p!["name"]!.GetValue<string>() == "zwin")!;
        Assert.Equal("ONLINE", zwin["health"]!.GetValue<string>());
        Assert.True(zwin["size"]!.GetValue<long>() > 0);

        var status = ZfsJson.PoolStatus(JsonNode.Parse(Sample("zpool-status.json"))!)["pools"]!.AsArray();
        JsonNode root = Assert.Single(status.Single(p => p!["name"]!.GetValue<string>() == "zwin")!["vdevs"]!.AsArray())!;
        JsonNode mirror = Assert.Single(root["children"]!.AsArray())!;
        Assert.Equal("mirror", mirror["type"]!.GetValue<string>());
        Assert.Equal(2, mirror["children"]!.AsArray().Count);

        var props = ZfsJson.DatasetProperties(JsonNode.Parse(Sample("zfs-get.json"))!, "zwin/docs")["properties"]!.AsArray();
        Assert.Equal("zstd", props.Single(p => p!["name"]!.GetValue<string>() == "compression")!["value"]!.GetValue<string>());
        Assert.Equal("local", props.Single(p => p!["name"]!.GetValue<string>() == "compression")!["source"]!.GetValue<string>());

        var datasets = ZfsJson.DatasetList(JsonNode.Parse(Sample("zfs-list.json"))!)["datasets"]!.AsArray();
        Assert.Contains(datasets, d => d!["name"]!.GetValue<string>() == "zwin/docs" && d["mounted"]!.GetValue<bool>());

        var snapshots = ZfsJson.SnapshotList(JsonNode.Parse(Sample("zfs-snapshots.json"))!)["snapshots"]!.AsArray();
        Assert.Contains(snapshots, s => s!["name"]!.GetValue<string>() == "zwin/docs@s1" && s["creation"]!.GetValue<long>() > 1_700_000_000);

        var importable = ZfsJson.ImportablePools(Sample("zpool-import.txt"))["pools"]!.AsArray();
        Assert.Contains(importable, p => p!["name"]!.GetValue<string>() == "zwin" && p["state"]!.GetValue<string>() == "ONLINE");
    }

    [Fact]
    public void EveryErrorCodeIsInTheSchema()
    {
        foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
            AssertValid(CliProgram.Failure("pool.list", new AdminException(code, "message", "detail")));
        AssertValid(CliProgram.Failure(null, new AdminException(ErrorCode.Usage, "usage")));
    }

    [Fact]
    public void SchemaRejectsBreakingChanges()
    {
        JsonNode good = CliProgram.Success("pool.list", ZfsJson.PoolList(JsonNode.Parse(Sample("zpool-list.json"))!));
        AssertValid(good);
        var renamed = good.DeepClone();
        renamed["result"]!["pools"]![0]!.AsObject().Remove("health");
        Assert.False(Schema.Evaluate(renamed).IsValid);
        var retyped = good.DeepClone();
        retyped["result"]!["pools"]![0]!["size"] = "12G";
        Assert.False(Schema.Evaluate(retyped).IsValid);
        var wrongSchema = good.DeepClone();
        wrongSchema["schema"] = "zfswin/2";
        Assert.False(Schema.Evaluate(wrongSchema).IsValid);
        var failureWithoutError = CliProgram.Failure("pool.list", new AdminException(ErrorCode.NotFound, "x"));
        failureWithoutError.Remove("error");
        Assert.False(Schema.Evaluate(failureWithoutError).IsValid);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zfs.Admin.Cli;

/// <summary>
/// zfswin.exe: the scriptable command-line front end of the OpenZFS broker
/// service.  --json prints one object per invocation with a stable schema
/// (schemas/zfswin-output.schema.json); the exit code is always meaningful.
/// </summary>
public static class Program
{
    public const string OutputSchema = "zfswin/1";

    public static async Task<int> Main(string[] argv)
    {
        if (argv.Length == 0 || argv is ["help"] or ["--help"] or ["-h"] or ["/?"])
        {
            Console.Out.WriteLine(CommandLine.Usage);
            return argv.Length == 0 ? ErrorCode.Usage.ExitCode() : 0;
        }
        bool json = argv.Contains("--json");
        ParsedCommand? command = null;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;	// let the broker cancel and answer
            consoleCancelled = true;
            cancel.Cancel();
        };
        try
        {
            command = CommandLine.Parse(argv);
            if (command.Destructive && !command.Confirmed)
                throw new AdminException(ErrorCode.ConfirmationRequired,
                    $"'{string.Join(' ', argv.Where(a => a != "--json"))}' changes or destroys data on the named devices or datasets; repeat it with --yes to confirm.");
            if (command.Timeout is TimeSpan t)
                cancel.CancelAfter(t);
            JsonObject arguments = command.Arguments;
            if (command.Destructive)
                arguments["confirm"] = true;
            JsonNode? result;
            try
            {
                result = await new BrokerClient().CallAsync(command.Operation, arguments, cancel.Token).ConfigureAwait(false);
            }
            catch (AdminException e) when (e.Code == ErrorCode.Cancelled && command.Timeout is not null && !consoleCancelled)
            {
                throw new AdminException(ErrorCode.Timeout, $"'{command.Operation}' did not finish within {command.Timeout.Value.TotalSeconds:0} s and was cancelled.");
            }
            if (json)
                WriteJson(Success(command.Operation, result));
            else
                HumanOutput.Write(command.Operation, result, Console.Out);
            return 0;
        }
        catch (AdminException e)
        {
            if (json)
            {
                WriteJson(Failure(command?.Operation, e));
            }
            else
            {
                Console.Error.WriteLine($"zfswin: {e.Message}");
                if (!string.IsNullOrWhiteSpace(e.Detail) && !e.Message.Contains(e.Detail.Trim(), StringComparison.Ordinal))
                    Console.Error.WriteLine(e.Detail.TrimEnd());
            }
            return e.Code.ExitCode();
        }
    }

    // Distinguishes Ctrl+C from the --timeout expiry; both cancel the same token.
    static volatile bool consoleCancelled;

    internal static string ToJson(JsonNode value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            value.WriteTo(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static JsonObject Success(string operation, JsonNode? result) =>
        new() { ["schema"] = OutputSchema, ["operation"] = operation, ["ok"] = true, ["result"] = result };

    internal static JsonObject Failure(string? operation, AdminException e) => new()
    {
        ["schema"] = OutputSchema,
        ["operation"] = operation,
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = e.Code.ToWire(), ["message"] = e.Message, ["detail"] = e.Detail },
    };

    static void WriteJson(JsonObject value) => Console.Out.WriteLine(ToJson(value));
}

using System.Text.Json.Nodes;

namespace Zfs.Admin.Cli;

/// <summary>A parsed zfswin command: the broker operation and its arguments.</summary>
internal sealed record ParsedCommand(string Operation, JsonObject Arguments, bool Destructive, bool Json, TimeSpan? Timeout, bool Confirmed);

/// <summary>
/// Parses zfswin's command line.  Every command maps to exactly one broker
/// operation; the broker validates the values again.  Destructive commands
/// need --yes; nothing ever picks a device, a pool or a dataset implicitly.
/// </summary>
internal static class CommandLine
{
    public const string Usage = """
        usage: zfswin [--json] [--timeout <seconds>] <command>

        Pools
          pool list
          pool status [<pool>]
          pool get <pool> [<property>...]
          pool discover [--dir <directory>]...                        (admin)
          pool import <pool|guid> [--dir <directory>]... [--new-name <name>]
                      [--no-mount] [--readonly] [--force]              (admin)
          pool export <pool> [--force]                                 (admin)
          pool create <pool> [--mirror] <device>... [-o <prop>=<value>]...
                      [-O <prop>=<value>]... [--mountpoint <path>] --yes  (admin)
        Scrub
          scrub status <pool>
          scrub start|pause|stop <pool>                                (admin)
        Datasets
          dataset list [<root>] [-r] [-d <depth>]
          dataset get <dataset> [<property>...]
          dataset set <dataset> <prop>=<value>...                      (admin)
          dataset create <dataset> [-o <prop>=<value>]... [-p]         (admin)
          dataset destroy <dataset> [-r] --yes                         (admin)
          dataset mount <dataset>                                      (admin)
          dataset unmount <dataset> [--force]                          (admin)
          mount list
        Snapshots
          snapshot list [<dataset>] [-r]
          snapshot create <dataset>@<name> [-r]                        (admin)
          snapshot destroy <dataset>@<name> [-r] --yes                 (admin)
        Mirror devices
          vdev attach <pool> <device> <new-device> --yes               (admin)
          vdev detach <pool> <device> --yes                            (admin)
          vdev replace <pool> <device> [<new-device>] --yes            (admin)
          vdev add-cache <pool> <device>... --yes                      (admin; read cache, L2ARC)
          vdev add-log <pool> [--mirror] <device>... --yes             (admin; write log, SLOG)
          vdev remove <pool> <device> --yes                            (admin; cache, log or data vdev)
        Other
          version
          help

        A device is a fully qualified file path (D:\pools\disk1.img) or
        \\?\PhysicalDriveN.  Exit codes: 0 ok, 1 command failed, 2 usage,
        3 not found, 4 access denied, 5 broker unavailable, 6 confirmation
        required, 7 timeout or cancelled, 8 protocol error, 9 busy, 10 internal.
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> argv)
    {
        var args = new Queue<string>(argv);
        bool json = false, yes = false;
        TimeSpan? timeout = null;
        while (args.Count > 0 && args.Peek().StartsWith("--", StringComparison.Ordinal))
        {
            string global = args.Dequeue();
            switch (global)
            {
                case "--json":
                    json = true;
                    break;
                case "--timeout":
                    timeout = TimeSpan.FromSeconds(Seconds(Next(args, global)));
                    break;
                case "--version":
                    return new ParsedCommand("version", new JsonObject(), false, json, timeout, false);
                case "--help":
                    throw new AdminException(ErrorCode.Usage, Usage);
                default:
                    throw Bad($"Unknown option '{global}'.");
            }
        }
        string group = args.Count > 0 ? args.Dequeue() : throw new AdminException(ErrorCode.Usage, Usage);
        if (group is "help")
            throw new AdminException(ErrorCode.Usage, Usage);
        if (group is "version")
            return Done("version", new JsonObject(), args);
        string verb = args.Count > 0 ? args.Dequeue() : throw Bad($"'{group}' needs a subcommand; see 'zfswin help'.");

        var a = new JsonObject();
        var positional = new List<string>();
        string operation;
        bool destructive = false;

        // Options shared by several commands; each command lists those it accepts.
        var dirs = new JsonArray();
        var props = new JsonObject();
        var datasetProps = new JsonObject();

        void Options(params string[] allowed)
        {
            while (args.Count > 0)
            {
                string token = args.Dequeue();
                if (!token.StartsWith('-') || token == "-")
                {
                    positional.Add(token);
                    continue;
                }
                if (!allowed.Contains(token))
                    throw Bad($"'{group} {verb}' does not accept '{token}'.");
                switch (token)
                {
                    case "--yes": yes = true; break;
                    case "--dir": dirs.Add((JsonNode?)Next(args, token)); break;
                    case "-o": AddProperty(props, Next(args, token)); break;
                    case "-O": AddProperty(datasetProps, Next(args, token)); break;
                    case "--new-name": a["newName"] = Next(args, token); break;
                    case "--mountpoint": a["mountpoint"] = Next(args, token); break;
                    case "--no-mount": a["noMount"] = true; break;
                    case "--readonly": a["readOnly"] = true; break;
                    case "--force": a["force"] = true; break;
                    case "--mirror": a["layout"] = "mirror"; break;
                    case "-r": case "--recursive": a["recursive"] = true; break;
                    case "-p": case "--parents": a["parents"] = true; break;
                    case "-d": case "--depth": a["depth"] = (int)Seconds(Next(args, token), max: 1000); break;
                }
            }
        }

        switch ($"{group} {verb}")
        {
            case "pool list": Options(); Exactly(0); operation = "pool.list"; break;
            case "pool status": Options(); AtMost(1); Optional("pool", 0); operation = "pool.status"; break;
            case "pool get":
                Options(); AtLeast(1); a["pool"] = positional[0]; PropertyNames(1); operation = "pool.get"; break;
            case "pool discover":
                Options("--dir"); Exactly(0); a["dirs"] = dirs; operation = "pool.discover"; break;
            case "pool import":
                Options("--dir", "--new-name", "--no-mount", "--readonly", "--force"); Exactly(1);
                a["pool"] = positional[0]; if (dirs.Count > 0) a["dirs"] = dirs; operation = "pool.import"; break;
            case "pool export": Options("--force"); Exactly(1); a["pool"] = positional[0]; operation = "pool.export"; break;
            case "pool create":
                Options("--mirror", "-o", "-O", "--mountpoint", "--yes"); AtLeast(2);
                a["pool"] = positional[0];
                a["devices"] = new JsonArray(positional.Skip(1).Select(d => (JsonNode?)d).ToArray());
                if (props.Count > 0) a["poolProperties"] = props;
                if (datasetProps.Count > 0) a["datasetProperties"] = datasetProps;
                operation = "pool.create"; destructive = true; break;
            case "scrub status": case "scrub start": case "scrub pause": case "scrub stop":
                Options(); Exactly(1); a["pool"] = positional[0]; operation = $"scrub.{verb}"; break;
            case "dataset list":
                Options("-r", "--recursive", "-d", "--depth"); AtMost(1); Optional("root", 0); operation = "dataset.list"; break;
            case "dataset get":
                Options(); AtLeast(1); a["dataset"] = positional[0]; PropertyNames(1); operation = "dataset.get"; break;
            case "dataset set":
                Options(); AtLeast(2); a["dataset"] = positional[0];
                foreach (string assignment in positional.Skip(1)) AddProperty(props, assignment);
                a["properties"] = props; operation = "dataset.set"; break;
            case "dataset create":
                Options("-o", "-p", "--parents"); Exactly(1); a["dataset"] = positional[0];
                if (props.Count > 0) a["properties"] = props; operation = "dataset.create"; break;
            case "dataset destroy":
                Options("-r", "--recursive", "--yes"); Exactly(1); a["dataset"] = positional[0];
                operation = "dataset.destroy"; destructive = true; break;
            case "dataset mount": Options(); Exactly(1); a["dataset"] = positional[0]; operation = "dataset.mount"; break;
            case "dataset unmount": Options("--force"); Exactly(1); a["dataset"] = positional[0]; operation = "dataset.unmount"; break;
            case "mount list": Options(); Exactly(0); operation = "mount.list"; break;
            case "snapshot list":
                Options("-r", "--recursive", "-d", "--depth"); AtMost(1); Optional("root", 0); operation = "snapshot.list"; break;
            case "snapshot create":
                Options("-r", "--recursive"); Exactly(1); a["snapshot"] = positional[0]; operation = "snapshot.create"; break;
            case "snapshot destroy":
                Options("-r", "--recursive", "--yes"); Exactly(1); a["snapshot"] = positional[0];
                operation = "snapshot.destroy"; destructive = true; break;
            case "vdev attach":
                Options("--yes"); Exactly(3); a["pool"] = positional[0]; a["device"] = positional[1]; a["newDevice"] = positional[2];
                operation = "vdev.attach"; destructive = true; break;
            case "vdev detach":
                Options("--yes"); Exactly(2); a["pool"] = positional[0]; a["device"] = positional[1];
                operation = "vdev.detach"; destructive = true; break;
            case "vdev replace":
                Options("--yes"); AtLeast(2); AtMost(3); a["pool"] = positional[0]; a["device"] = positional[1]; Optional("newDevice", 2);
                operation = "vdev.replace"; destructive = true; break;
            case "vdev add-cache": case "vdev add-log":
                Options("--mirror", "--yes"); AtLeast(2); a["pool"] = positional[0];
                a["role"] = verb == "add-cache" ? "cache" : "log";
                a["devices"] = new JsonArray(positional.Skip(1).Select(d => (JsonNode?)d).ToArray());
                // --mirror set "layout" above; vdev.add takes a flag.
                if (a.Remove("layout")) a["mirror"] = true;
                operation = "vdev.add"; destructive = true; break;
            case "vdev remove":
                Options("--yes"); Exactly(2); a["pool"] = positional[0]; a["device"] = positional[1];
                operation = "vdev.remove"; destructive = true; break;
            default:
                throw Bad($"Unknown command '{group} {verb}'; see 'zfswin help'.");
        }
        return new ParsedCommand(operation, a, destructive, json, timeout, yes);

        void Exactly(int n) { if (positional.Count != n) throw Bad($"'{group} {verb}' takes {n} argument(s); got {positional.Count}."); }
        void AtLeast(int n) { if (positional.Count < n) throw Bad($"'{group} {verb}' needs at least {n} argument(s)."); }
        void AtMost(int n) { if (positional.Count > n) throw Bad($"'{group} {verb}' takes at most {n} argument(s)."); }
        void Optional(string name, int index) { if (positional.Count > index) a[name] = positional[index]; }
        void PropertyNames(int from)
        {
            if (positional.Count > from)
                a["properties"] = new JsonArray(positional.Skip(from).Select(p => (JsonNode?)p).ToArray());
        }
        ParsedCommand Done(string op, JsonObject arguments, Queue<string> rest)
        {
            if (rest.Count > 0)
                throw Bad($"'{op}' takes no arguments.");
            return new ParsedCommand(op, arguments, false, json, timeout, false);
        }
    }

    static void AddProperty(JsonObject target, string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0)
            throw Bad($"'{assignment}' is not of the form property=value.");
        string name = assignment[..eq];
        if (target.ContainsKey(name))
            throw Bad($"Property '{name}' is given twice.");
        target[name] = assignment[(eq + 1)..];
    }

    static string Next(Queue<string> args, string option) =>
        args.Count > 0 ? args.Dequeue() : throw Bad($"'{option}' needs a value.");

    static double Seconds(string text, double max = 86400)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0 || v > max)
            throw Bad($"'{text}' is not a number between 0 and {max}.");
        return v;
    }

    static AdminException Bad(string message) => new(ErrorCode.Usage, message);
}

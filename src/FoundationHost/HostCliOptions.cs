using TiaMcp.Versioning;

namespace TiaMcp.FoundationHost;

internal sealed class HostCliOptions
{
    internal static readonly string[] Verbs = { "doctor", "audit", "schema", "version", "config", "install-plc-tools", "help", "--help", "-h" };
    private static readonly string[] HostValues = { "--release-key", "--tia-major-version", "--tia-version", "--tia-portal-location", "--public-api", "--worker-exe", "--engine-worker", "--engine-catalog", "--worker-timeout-seconds", "--worker-timeout-config", "--profile", "--logging" };
    internal required string[] Arguments { get; init; }
    internal required HostOptions Host { get; init; }

    internal static bool IsCommand(string[] args) => args.Length > 0 &&
        (!args[0].StartsWith("-", StringComparison.Ordinal) || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase));

    internal static HostCliOptions Parse(string[] args, string directory)
    {
        string verb = args[0].ToLowerInvariant();
        if (!Verbs.Contains(verb))
            throw new ArgumentException("Unknown or retired command. Run tia help for supported commands; use MCP tools and the project generation framework for project changes.");
        var allowed = new HashSet<string>(HostValues, StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(new[] { "--plc-worker", "--offline", "--with-ui", "--full", "--lite" });
        switch (verb)
        {
            case "doctor": allowed.UnionWith(new[] { "--fix", "--json" }); break;
            case "config": allowed.UnionWith(new[] { "--host", "--print" }); break;
            case "install-plc-tools": allowed.UnionWith(new[] { "--python", "--environment-path" }); break;
            case "audit": allowed.Add("--path"); break;
        }
        var values = new HashSet<string>(HostValues, StringComparer.OrdinalIgnoreCase);
        values.UnionWith(new[] { "--host", "--python", "--environment-path", "--path" });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostArgs = new List<string>();
        var commandArgs = new List<string> { verb };
        string? release = null;
        int start = 1;
        if (verb == "audit")
        {
            if (args.Length < 2 || args[1] != "verify") throw new ArgumentException("Expected tia audit verify [--path <absolute directory>].");
            commandArgs.Add("verify"); start = 2;
        }
        for (int index = start; index < args.Length; index++)
        {
            string option = args[index].ToLowerInvariant();
            if (option is "-tia-major-version" or "-tia-portal-location" or "-profile" or "-logging" or "-with-ui") option = "-" + option;
            if (!allowed.Contains(option)) throw new ArgumentException("Unknown option for " + verb + ": " + args[index]);
            if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + args[index]);
            bool hostOption = HostValues.Contains(option) || option is "--plc-worker" or "--offline" or "--with-ui";
            string? value = null;
            if (values.Contains(option))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("A value is required after " + option + ".");
                value = args[index];
            }
            if (option is "--release-key" or "--tia-version" or "--tia-major-version")
            {
                TiaVersionCatalog.RequireRunnable(value!);
                if (release != null && release != value) throw new ArgumentException("Conflicting TIA release selectors.");
                release = value; continue;
            }
            if (option == "--path" && !Path.IsPathRooted(value!)) throw new ArgumentException("Expected tia audit verify [--path <absolute directory>].");
            if (option == "--profile" && !string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "lite", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--profile must be full or lite.");
            if (option == "--logging" && (!int.TryParse(value, out int logging) || logging < 0 || logging > 3)) throw new ArgumentException("--logging must be 0..3.");
            if (hostOption) { hostArgs.Add(option); if (value != null) hostArgs.Add(value); }
            commandArgs.Add(option); if (value != null) commandArgs.Add(value);
        }
        if (seen.Contains("--full") && seen.Contains("--lite")) throw new ArgumentException("--full and --lite conflict.");
        var marker = Path.Combine(directory, "release-key.txt");
        release ??= File.Exists(marker) ? File.ReadAllText(marker).Trim() : "21";
        hostArgs.AddRange(new[] { "--release-key", release });
        return new HostCliOptions { Arguments = commandArgs.ToArray(), Host = HostOptions.Parse(hostArgs.ToArray(), directory) };
    }
}

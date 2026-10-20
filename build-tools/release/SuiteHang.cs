using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static class SuiteHang
{
    // Existing check-all TRX maxima: 61.8104231s (master-b353f5ef),
    // 60.2262008s (master-ba5a4a9d). Its longest producer-inclusive boundary
    // was 97.0151567s (FoundationChecks.Check); five minutes leaves >3x headroom.
    // A 4-vCPU CI runner needed 295-300 s for that producer, so its two longest
    // suites run as separate test cases; the timeout applies per test case.
    internal const string Timeout = "5m";
    internal static string[] TestArguments => ["--blame-hang", "--blame-hang-timeout", Timeout, "--blame-hang-dump-type", "mini"];
    internal static string VstestArgument => "--Blame:CollectHangDump;HangDumpType=mini;TestTimeout=" + Timeout;

    internal static CommandResult RunProcess(string executable, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment = null)
    {
        var args = arguments.ToList();
        if (args.FirstOrDefault() != "test") return ProcessRunner.Run(executable, args, root, environment);
        var results = args.IndexOf("--results-directory");
        var directory = Path.GetFullPath(results < 0 ? Path.Combine(root, "bin-build/test-results", Guid.NewGuid().ToString("N")) : Path.Combine(root, args[results + 1]));
        if (results < 0) args.AddRange(["--results-directory", directory]);
        var logger = args.FirstOrDefault(a => a.StartsWith("trx;LogFileName=", StringComparison.Ordinal));
        var name = logger == null ? "test" : Path.GetFileNameWithoutExtension(logger["trx;LogFileName=".Length..]);
        args.AddRange(TestArguments);
        var before = Begin(directory, name);
        return Complete(directory, name, before, ProcessRunner.Run(executable, args, root, environment));
    }

    internal static HashSet<string> Begin(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, name + ".hang.json"));
        return Artifacts(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Artifacts(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path).StartsWith("Sequence_", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).EndsWith("_Sequence.xml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));

    internal static CommandResult Complete(string directory, string name, HashSet<string> before, CommandResult result)
    {
        var files = Artifacts(directory).Where(path => !before.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        var tests = new List<string>();
        var errors = new List<string>();
        foreach (var path in files.Where(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                tests.AddRange(XDocument.Load(path).Descendants().Where(test => test.Name.LocalName == "Test"
                    && string.Equals((string?)test.Attribute("Completed"), "false", StringComparison.OrdinalIgnoreCase))
                    .Select(test => (string?)test.Attribute("DisplayName") ?? (string?)test.Attribute("Name") ?? "unknown test"));
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            { errors.Add("Cannot read blame sequence " + path + ": " + ex.Message); }
        }
        var dumps = files.Where(path => path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tests.Count > 0 || dumps.Length > 0)
            errors.Add("VSTest hang/aborted test: " + (tests.Count == 0 ? "see blame log" : string.Join("; ", tests.Distinct()))
                + "; mini dump: " + (dumps.Length == 0 ? "not produced (see collector log)" : string.Join("; ", dumps)));
        var report = new JsonObject {
            ["timeout"] = Timeout, ["tests"] = new JsonArray(tests.Distinct().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["artifacts"] = new JsonArray(files.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };
        File.WriteAllText(Path.Combine(directory, name + ".hang.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return errors.Count == 0 ? result : result with { ExitCode = 1, StandardError = result.StandardError + "\n" + string.Join("\n", errors) + "\n" };
    }

    internal static IEnumerable<string> Errors(string directory, string name)
    {
        var path = Path.Combine(directory, name + ".hang.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray() : [];
    }
}

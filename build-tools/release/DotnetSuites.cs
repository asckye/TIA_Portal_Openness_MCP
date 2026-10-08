using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal delegate CommandResult SuiteProcess(string executable, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment);

internal static class DotnetSuites
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static (JsonObject Counts, List<string> Errors) ReadTrx(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new ReleaseException("TRX is missing ResultSummary/Counters");
        var summary = root.Element(Ns + "ResultSummary");
        var counters = summary?.Element(Ns + "Counters");
        if (summary is null || counters is null) throw new ReleaseException("TRX is missing ResultSummary/Counters");
        static int Counter(XAttribute? attr) => int.Parse(attr?.Value ?? throw new ReleaseException("TRX missing counter"), CultureInfo.InvariantCulture);
        var counts = new Dictionary<string, int>();
        foreach (var key in new[] { "total", "executed", "passed", "failed", "notExecuted" }) counts[key] = Counter(counters.Attribute(key));
        if (counters.Attributes().Any(attr => Counter(attr) < 0)) throw new ReleaseException("TRX has negative counters");
        var errors = new List<string>();
        foreach (var attr in counters.Attributes())
            if (!counts.ContainsKey(attr.Name.LocalName) && Counter(attr) != 0) errors.Add($"TRX {attr.Name.LocalName}={attr.Value}");
        if ((string?)summary.Attribute("outcome") is not ("Completed" or "Passed")) errors.Add("TRX summary outcome=" + ((string?)summary.Attribute("outcome") ?? "None"));
        if (counts["executed"] != counts["passed"] + counts["failed"]) errors.Add("TRX executed count differs from passed + failed");
        // VSTest emits NotExecuted rows for xUnit skips while its notExecuted counter can be zero.
        var skipped = counts["total"] - counts["executed"];
        if (skipped < 0 || counts["notExecuted"] != 0 && counts["notExecuted"] != skipped) errors.Add("TRX skipped counters are inconsistent");
        var definitions = new Dictionary<string, (string Class, string Method)>();
        foreach (var test in root.Element(Ns + "TestDefinitions")?.Elements(Ns + "UnitTest") ?? [])
        {
            var method = test.Element(Ns + "TestMethod");
            var cls = (string?)method?.Attribute("className");
            var name = (string?)method?.Attribute("name");
            if (string.IsNullOrEmpty(cls) || string.IsNullOrEmpty(name)) throw new ReleaseException("TRX test definition is missing its class/method");
            definitions[RequiredAttribute(test, "id")] = (cls, name);
        }
        var groups = new Dictionary<(string Class, string Method), Dictionary<string, int>>();
        var actual = new Dictionary<string, int> { ["passed"] = 0, ["failed"] = 0, ["skipped"] = 0, ["other"] = 0 };
        var executions = new HashSet<string>();
        var rows = root.Element(Ns + "Results")?.Elements(Ns + "UnitTestResult").ToArray() ?? [];
        foreach (var row in rows)
        {
            if (!executions.Add(RequiredAttribute(row, "executionId"))) throw new ReleaseException("TRX repeats an executionId");
            var outcome = (string?)row.Attribute("outcome") switch { "Passed" => "passed", "Failed" => "failed", "NotExecuted" => "skipped", _ => "other" };
            actual[outcome]++;
            var method = definitions[RequiredAttribute(row, "testId")];
            if (!groups.TryGetValue(method, out var group)) groups[method] = group = new Dictionary<string, int>();
            group[outcome] = group.GetValueOrDefault(outcome) + 1;
        }
        if ((rows.Length, actual["passed"], actual["failed"], actual["skipped"]) != (counts["total"], counts["passed"], counts["failed"], skipped))
            errors.Add("TRX result rows differ from Counters");
        var methods = new JsonArray();
        foreach (var (method, values) in groups.OrderBy(g => g.Key.Class, PythonStringComparer.Instance).ThenBy(g => g.Key.Method, PythonStringComparer.Instance))
        {
            var item = new JsonObject { ["testClass"] = method.Class, ["testMethod"] = method.Method, ["total"] = values.Values.Sum() };
            foreach (var key in new[] { "passed", "failed", "skipped", "other" }) item[key] = values.GetValueOrDefault(key);
            methods.Add(item);
        }
        return (new JsonObject { ["total"] = counts["total"], ["executed"] = counts["executed"], ["passed"] = counts["passed"],
            ["failed"] = counts["failed"], ["skipped"] = skipped, ["methods"] = methods }, errors);
    }

    private static string RequiredAttribute(XElement element, string name) => (string?)element.Attribute(name) ?? throw new ReleaseException("TRX missing " + name);

    internal static (JsonObject Counts, List<string> Errors) EvaluateTrx(string path, int minimum, int maximum)
    {
        var (result, errors) = ReadTrx(path);
        int Count(string key) => result[key]!.GetValue<int>();
        if (Count("executed") == 0) errors.Add("zero tests executed");
        if (Count("failed") != 0) errors.Add($"{Count("failed")} failed checks");
        if (Count("passed") < minimum) errors.Add($"passed {Count("passed")} below minimum {minimum}");
        if (Count("skipped") > maximum) errors.Add($"skipped {Count("skipped")} above maximum {maximum}");
        return (result, errors);
    }

    internal static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : Environment.OSVersion.Platform.ToString();

    internal static JsonObject LoadCatalog(string path, string root, string? platform = null)
    {
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject catalog || catalog.Count == 0) throw new ReleaseException("Suite catalog must be a nonempty object", 64);
        platform ??= CurrentPlatform;
        foreach (var (name, node) in catalog)
        {
            if (!Regex.IsMatch(name, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")) throw new ReleaseException("Invalid suite name: " + name, 64);
            if (node is not JsonObject suite) throw new ReleaseException(name + ": suite must be an object", 64);
            var overrides = suite.ContainsKey("platforms") ? suite["platforms"] as JsonObject : new JsonObject();
            if (overrides is null) throw new ReleaseException(name + ": platforms must be an object", 64);
            foreach (var (os, item) in overrides)
            {
                if (os is not ("windows" or "linux" or "macos") || item is not JsonObject fields
                    || fields.Any(p => p.Key is not ("minimumPassed" or "maximumSkipped" or "reason"))
                    || fields["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                    throw new ReleaseException($"{name}: invalid platform override {os}", 64);
            }
            if (overrides[platform] is JsonObject selected)
                foreach (var (key, value) in selected) if (key != "reason") suite[key] = value?.DeepClone();
            foreach (var key in new[] { "minimumPassed", "maximumSkipped" })
                if (suite[key] is not JsonValue number || !number.TryGetValue<int>(out var count) || count < (key == "minimumPassed" ? 1 : 0))
                    throw new ReleaseException($"{name}: invalid {key}", 64);
            if (suite["project"] is not JsonValue project || !project.TryGetValue<string>(out var file) || !File.Exists(Path.Combine(root, file)))
                throw new ReleaseException(name + ": project does not exist", 64);
            if (suite["arguments"] is not JsonArray arguments || arguments.Any(a => a is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new ReleaseException(name + ": arguments must be a string array", 64);
        }
        return catalog;
    }

    internal static CommandResult RunSuite(string root, string name, JsonObject suite, string directory, string dotnet = "dotnet",
        IEnumerable<string>? arguments = null, IDictionary<string, string?>? environment = null, SuiteProcess? process = null)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var trx = Path.Combine(directory, name + ".trx");
        var report = Path.Combine(directory, name + ".json");
        File.Delete(trx);
        File.Delete(report);
        var apiRoot = environment is not null && environment.TryGetValue("TIA_MCP_TEST_PUBLIC_API_ROOT", out var api) ? api : Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        var command = new List<string> { "test", Path.Combine(root, suite["project"]!.GetValue<string>()), "-c", "Release" };
        command.AddRange(suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>()));
        if (suite["publicApiRoot"]?.GetValue<bool>() == true && !string.IsNullOrEmpty(apiRoot)) command.Add("-p:TiaPublicApiRoot=" + apiRoot);
        if (arguments is not null) command.AddRange(arguments);
        command.AddRange(["--logger", $"trx;LogFileName={name}.trx", "--results-directory", directory]);
        var output = new StringBuilder($"RUN {name}: {dotnet} {string.Join(' ', command)}\n");
        var stderr = "";
        var errors = new List<string>();
        var counts = new JsonObject();
        int? exit = null;
        try
        {
            var result = (process ?? RunProcess)(dotnet, command, root, environment);
            output.Append(result.StandardOutput);
            stderr = result.StandardError;
            exit = result.ExitCode;
            if (exit != 0) errors.Add("dotnet test exited " + exit);
            if (!File.Exists(trx)) errors.Add("missing TRX (no tests discovered or build/test host failed)");
            else
            {
                var evaluated = EvaluateTrx(trx, suite["minimumPassed"]!.GetValue<int>(), suite["maximumSkipped"]!.GetValue<int>());
                counts = evaluated.Counts;
                errors.AddRange(evaluated.Errors);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or FormatException or OverflowException or KeyNotFoundException or System.Xml.XmlException or ReleaseException)
        { errors.Add(ex.Message); }
        var summary = new JsonObject { ["suite"] = name, ["project"] = suite["project"]!.DeepClone(), ["minimumPassed"] = suite["minimumPassed"]!.DeepClone(), ["maximumSkipped"] = suite["maximumSkipped"]!.DeepClone(), ["exitCode"] = exit };
        foreach (var (key, value) in counts) summary[key] = value?.DeepClone();
        summary["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        File.WriteAllText(report, PythonJson.Dumps(summary, ensureAscii: false, indent: 2) + "\n", new UTF8Encoding(false));
        output.Append(errors.Count != 0 ? $"FAIL: {name}: {string.Join("; ", errors)}\n" : $"COMPLETE: {counts["passed"]} {name} checks passed\n");
        return new CommandResult(errors.Count == 0 ? 0 : 1, output.ToString(), stderr);
    }

    internal static CommandResult RunProcess(string executable, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment) => ProcessRunner.Run(executable, arguments, root, environment);

    internal static CommandResult Run(string root, Options options, IDictionary<string, string?>? environment = null, SuiteProcess? process = null)
    {
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var names = options.All("Suite");
        if (options.Has("SelfTest"))
        {
            foreach (var test in DotnetSuiteSelfTests.Cases) test.Run();
            output.Append($"COMPLETE: {DotnetSuiteSelfTests.Cases.Count} dotnet gate self-checks passed\n");
            if (names.Count == 0) return new CommandResult(0, output.ToString(), "");
        }
        if (names.Count == 0) throw new ReleaseException("at least one -Suite or -SelfTest is required", 64);
        var catalog = LoadCatalog(options.Get("Catalog", Path.Combine(root, "tests/test-suites.json")), root);
        var unknown = names.Where(name => !catalog.ContainsKey(name)).Distinct().Order(PythonStringComparer.Instance).ToArray();
        if (unknown.Length != 0) throw new ReleaseException("Unknown suites: " + string.Join(", ", unknown), 64);
        var arguments = options.All("DotnetArg").Concat(options.Has("NoRestore") ? new[] { "--no-restore" } : []).ToArray();
        var success = true;
        // The engine variants share build outputs and must never run concurrently here.
        foreach (var name in names)
        {
            var result = RunSuite(root, name, catalog[name]!.AsObject(), options.Get("ResultsDirectory", Path.Combine(root, "bin-build/test-results")), options.Get("Dotnet", "dotnet"), arguments, environment, process);
            output.Append(result.StandardOutput);
            errors.Append(result.StandardError);
            success &= result.ExitCode == 0;
        }
        return new CommandResult(success ? 0 : 1, output.ToString(), errors.ToString());
    }
}

internal static partial class ReleaseCommands
{
    private static int TestSuites(Options options)
    {
        var result = DotnetSuites.Run(Root, options);
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        return result.ExitCode;
    }
}

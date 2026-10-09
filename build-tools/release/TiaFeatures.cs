using System.Text.Json.Nodes;
using System.Text;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class TiaFeatures
{
    internal static readonly string[] Releases = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];
    internal static List<(string Project, Dictionary<string, string> Properties)> ProjectCases(string root)
    {
        var cases = new List<(string Project, Dictionary<string, string> Properties)>();
        void Add(string path, Dictionary<string, string>? properties = null) => cases.Add((Path.GetRelativePath(root, path).Replace('\\', '/'), properties ?? new()));
        foreach (var folder in new[] { "src/Adapters", "src/Studio/Openness" })
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, folder), "V*").Order(StringComparer.Ordinal))
                foreach (var path in Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.Ordinal)) Add(path);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src/Engine"), "TiaMcp.Engine.V*.csproj").Order(StringComparer.Ordinal)) Add(path);
        foreach (var name in new[] { "src/PlcWorker/TiaMcp.PlcWorker.csproj", "tests/Engine/TiaMcp.Engine.ApiCompileChecks/TiaMcp.Engine.ApiCompileChecks.csproj" })
            foreach (var release in Releases) Add(Path.Combine(root, name), new() { ["TiaReleaseKey"] = release });
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var source = Repository.ReadSource(path);
            if (source.Contains("<DefineConstants>", StringComparison.Ordinal) || source.Contains("<TiaMajor>", StringComparison.Ordinal)) Add(path);
        }
        var suites = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/test-suites.json")))!;
        foreach (var name in new[] { "offline", "offline-v20" })
        {
            var row = suites[name]!;
            var properties = row["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).Where(a => a.StartsWith("-p:", StringComparison.Ordinal)).Select(a => a[3..].Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            Add(Path.Combine(root, row["project"]!.GetValue<string>()), properties);
        }
        return cases.OrderBy(c => c.Project, StringComparer.Ordinal).ThenBy(c => string.Join("\0", c.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\0" + p.Value)), StringComparer.Ordinal).ToList();
    }
    internal static JsonObject Evaluate(string root, string project, IReadOnlyDictionary<string, string> properties, string dotnet)
    {
        var result = ProcessRunner.Run(dotnet, ["msbuild", Path.Combine(root, project), "-nologo", "-getProperty:DefineConstants,TargetFramework,TargetFrameworks", "-p:Configuration=Release", .. properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"-p:{p.Key}={p.Value}")], root, timeoutMilliseconds: 120000);
        ProcessRunner.RequireSuccess(result, project + " evaluation");
        return JsonNode.Parse(result.StandardOutput)!["Properties"]!.AsObject();
    }
    internal static JsonArray Capture(string root, string dotnet = "dotnet")
    {
        var rows = new JsonArray();
        foreach (var (project, properties) in ProjectCases(root))
        {
            var values = Evaluate(root, project, properties, dotnet);
            var multiple = values["TargetFrameworks"]!.GetValue<string>();
            foreach (var framework in multiple.Length > 0 ? multiple.Split(';') : new[] { values["TargetFramework"]!.GetValue<string>() })
            {
                var evaluated = multiple.Length == 0 ? values : Evaluate(root, project, new Dictionary<string, string>(properties) { ["TargetFramework"] = framework }, dotnet);
                rows.Add(new JsonObject { ["project"] = project, ["properties"] = SourceCheck.Json(properties), ["targetFramework"] = framework, ["defineConstants"] = SourceCheck.Json(evaluated["DefineConstants"]!.GetValue<string>().Split(';', StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal).ToArray()) });
            }
        }
        return rows;
    }
    internal static int Run(string root, Options options)
    {
        void WriteCapture(string path, JsonArray table)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, (PythonJson.Dumps(table, indent: 2) + "\n").Replace("\n", Environment.NewLine, StringComparison.Ordinal), new UTF8Encoding(false));
        }
        var actual = Capture(root, options.Get("Dotnet", "dotnet"));
        if (options.Get("Capture") is { } capture) { WriteCapture(capture, actual); Console.WriteLine($"Captured {actual.Count} project/framework evaluations in {capture}."); return 0; }
        if (options.Get("Output") is { } output) WriteCapture(output, actual);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scripts/checks/tia-feature-expectations.json")))!.AsArray();
        Dictionary<string, JsonNode> Index(JsonArray rows) => rows.ToDictionary(r => r!["project"]!.GetValue<string>() + " " + PythonJson.Dumps(r["properties"], sortKeys: true) + " " + r["targetFramework"]!.GetValue<string>(), r => r!["defineConstants"]!);
        var before = Index(expected); var after = Index(actual);
        var errors = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
            if (!before.ContainsKey(key)) errors.Add(key + ": missing expectation");
            else if (!after.ContainsKey(key)) errors.Add(key + ": missing project evaluation");
            else if (!JsonNode.DeepEquals(before[key], after[key])) errors.Add(key + ": define constants differ");
        return SourceCheck.Report("TIA features", errors, $"Checked {actual.Count} release define evaluations.");
    }
}

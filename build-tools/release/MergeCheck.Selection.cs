using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;
internal static partial class MergeCheck
{

    internal static string[] ChangeSet(string baseRef, Func<string[], CommandResult> git)
    {
        var merge = git(["merge-base", baseRef, "HEAD"]);
        ProcessRunner.RequireSuccess(merge, "Resolve merge base " + baseRef);
        var revision = merge.StandardOutput.Trim();
        if (revision.Length == 0) throw new ReleaseException("Empty merge base: " + baseRef);
        return new[] { new[] { "diff", "--no-renames", "--name-only", "-z", revision + "..HEAD" },
            ["diff", "--no-renames", "--name-only", "-z", "HEAD"], ["ls-files", "--others", "--exclude-standard", "-z"] }
            .SelectMany(args =>
            {
                var result = git(args);
                ProcessRunner.RequireSuccess(result, "Read changed paths");
                return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool Under(string path, string directory) => path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
    private static bool CatalogDocument(string path) => path is "docs/reference/version-tool-catalog.md" or "docs/reference/tool-matrix.md"
        or "docs/reference/real-machine-ledger.md" or "docs/releases/v4.0.0-tool-migration.md";
    internal static bool Documentation(string path) => (Under(path, "docs") || !path.Contains('/') && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        && !CatalogDocument(path);
    private static bool RuleSixPath(string path) => CatalogDocument(path) || new[] { "reference", "manifest", "schemas", "templates", "hooks", "plugin", ".claude-plugin", "third_party", "scripts/ecosystem", "scripts/operations" }
        .Any(directory => Under(path, directory));
    private static bool WidePath(string path) => path is "Directory.Packages.props" or "global.json" or "NuGet.Config" or ".editorconfig" or "Version.props" or "tests/test-suites.json"
        || !path.Contains('/') && (path.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("TiaPortalOpenness", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        || Under(path, "tests/Shared") || Under(path, "build-tools/common")
        || Under(path, "src/Shared") && !path[11..].Contains('/') && path.EndsWith(".props", StringComparison.OrdinalIgnoreCase);

    internal static Evaluation Evaluate(string root, string project, string[] properties, SuiteProcess process, string dotnet)
    {
        var result = process(dotnet, ["msbuild", Path.Combine(root, project), "-nologo", "-nodeReuse:false",
            "-getItem:Compile,EmbeddedResource,None,Content,Page,ApplicationDefinition,Resource,ProjectReference", "-getProperty:MSBuildAllProjects", .. properties], root,
            new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null });
        ProcessRunner.RequireSuccess(result, "Evaluate " + project);
        int start = result.StandardOutput.IndexOf('{');
        if (start < 0) throw new ReleaseException("MSBuild returned no input inventory: " + project);
        using var document = JsonDocument.Parse(result.StandardOutput[start..]);
        var items = document.RootElement.GetProperty("Items");
        string Relative(string path) => Normalize(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.Combine(root, project))!, path))));
        var inputs = new List<string> { project };
        var projects = new List<string>();
        foreach (var item in items.EnumerateObject())
            foreach (var row in item.Value.EnumerateArray())
            {
                var path = Relative(row.GetProperty("FullPath").GetString()!);
                if (item.Name == "ProjectReference") projects.Add(path); else inputs.Add(path);
                if (row.TryGetProperty("DefiningProjectFullPath", out var defining)) inputs.Add(Relative(defining.GetString()!));
            }
        // This property is only a partial import list; an absent import remains unmapped and selects the wide plan.
        inputs.AddRange(document.RootElement.GetProperty("Properties").GetProperty("MSBuildAllProjects").GetString()!
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Relative));
        return new(inputs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), projects.ToArray());
    }

    // Evaluate each project/property pair once per invocation, including recursive references.
    internal static Dictionary<string, HashSet<string>> Closures(JsonObject catalog, IEnumerable<PrepareBuild.Project> table,
        string api, Func<string, string[], Evaluation> evaluate)
    {
        var evaluations = new ConcurrentDictionary<string, Lazy<Evaluation>>(StringComparer.OrdinalIgnoreCase);
        var roots = catalog.Select(pair => (Name: pair.Key, Project: pair.Value!["project"]!.GetValue<string>(),
            Properties: DotnetSuites.BuildArguments(pair.Value!["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>())).ToArray()))
            .Concat(table.Select((row, i) => (Name: "table:" + i, Project: row.Path, Properties: row.Properties.Split(';').Select(p => "-p:" + p).ToArray())))
            .Concat(new[] { (Name: "configuration-page", Project: ConfigurationProject, Properties: Array.Empty<string>()), (Name: "native-diagnostics", Project: DiagnosticsProject, Properties: Array.Empty<string>()) }).ToArray();
        var result = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = 8 }, row =>
        {
            string[] properties = ["-p:Configuration=Release", "-p:TiaPublicApiRoot=" + api, .. row.Properties];
            var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string project)
            {
                if (!visited.Add(project)) return;
                var key = project + "\n" + string.Join('\n', properties);
                var evaluated = evaluations.GetOrAdd(key, _ => new Lazy<Evaluation>(() => evaluate(project, properties))).Value;
                inputs.Add(Normalize(project));
                inputs.UnionWith(evaluated.Inputs);
                foreach (var reference in evaluated.Projects) Visit(reference);
            }
            Visit(row.Project);
            result[row.Name] = inputs;
        });
        return result.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static Dictionary<string, HashSet<string>> SelectionClosures(string[] changes, bool all, JsonObject catalog,
        IEnumerable<PrepareBuild.Project> table, string api, Func<string, string[], Evaluation> evaluate) =>
        all || changes.All(Documentation) || changes.Any(path => WidePath(path) || RuleSixPath(path))
            ? new(StringComparer.Ordinal) : Closures(catalog, table, api, evaluate);

    private static void Reason(Dictionary<string, HashSet<string>> map, string name, string path)
    {
        if (!map.TryGetValue(name, out var paths)) map[name] = paths = new(StringComparer.Ordinal);
        paths.Add(path);
    }

    internal static Plan Select(string[] changes, bool all, JsonObject catalog, IReadOnlyDictionary<string, HashSet<string>> closures,
        Func<string, string, bool> mentions, IEnumerable<string> filePrograms, string dotnet, string python, string api, string output,
        int parallelism = 4, string? transportFallback = null)
    {
        var plan = new Plan { DocsOnly = !all && changes.All(Documentation) };
        void Tool(string name, params string[] args) => plan.Checks.Add(new(name, dotnet, [typeof(Program).Assembly.Location, name, .. args]));
        Tool("check-repository", "-NoBinaries");
        Tool("check-dead-tool-references");
        Tool("generate-tool-counts", "-Check");
        if (plan.DocsOnly) return plan;
        void Suite(string name, string path) { if (catalog.ContainsKey(name)) Reason(plan.Suites, name, path); }
        void Everything(string path)
        {
            plan.Wide = true;
            foreach (var name in catalog.Select(pair => pair.Key)) Suite(name, path);
            Reason(plan.Builds, "eight-release-table", path);
            Reason(plan.Builds, ConfigurationProject, path);
            Reason(plan.Builds, DiagnosticsProject, path);
        }
        if (all) Everything("-All");
        foreach (var path in changes.Where(path => WidePath(path) || RuleSixPath(path))) Everything(path);
        foreach (var path in plan.Wide ? Array.Empty<string>() : changes)
        {
            if (Documentation(path)) continue;
            bool mapped = false;
            foreach (var (name, inputs) in closures.Where(pair => pair.Value.Contains(path)))
            {
                mapped = true;
                if (name.StartsWith("table:", StringComparison.Ordinal))
                {
                    Reason(plan.Builds, "eight-release-table", path);
                    // Studio adapter references do not select PLC source-reader suites.
                    if (inputs.Any(input => Under(input, "src/Adapters") || Under(input, "src/PlcWorker")))
                        foreach (var suite in catalog.Select(pair => pair.Key).Where(IsAdapterSuite)) Suite(suite, path);
                }
                else if (name == "configuration-page") Reason(plan.Builds, ConfigurationProject, path);
                else if (name == "native-diagnostics") Reason(plan.Builds, DiagnosticsProject, path);
                else Suite(name, path);
            }
            bool product = new[] { "src/Adapters", "src/Adapters.Contracts", "src/PlcWorker", "src/Studio/Openness", "build-tools/native-call-weaver", "sdk" }.Any(dir => Under(path, dir));
            if (product)
            {
                mapped = true;
                Reason(plan.Builds, "eight-release-table", path);
                if (!Under(path, "src/Studio/Openness"))
                    foreach (var name in catalog.Select(pair => pair.Key).Where(IsAdapterSuite)) Suite(name, path);
            }
            foreach (var group in catalog.GroupBy(pair => Path.GetDirectoryName(pair.Value!["project"]!.GetValue<string>())!.Replace('\\', '/')))
                if (mentions(Path.GetFileName(path), group.Key))
                {
                    foreach (var suite in group) Suite(suite.Key, path);
                }
            if (Under(path, "scripts") && !Under(path, "scripts/ecosystem") && !Under(path, "scripts/operations")) { mapped = true; Suite("release-tool", path); }
            if (Under(path, "build-tools/release")) { mapped = true; Suite("release-tool", path); }
            if (!mapped || Under(path, "scripts/ecosystem") || Under(path, "scripts/operations")) Everything(path);
        }
        Suite("source-contracts", "non-docs change");
        if (!plan.Wide)
            foreach (var name in plan.Suites.Keys.Where(name => name == "studio-gui" || DerivedSuiteGates.Supersets.GetValueOrDefault(name) == "studio-gui").ToArray())
                if (plan.Suites[name].All(path => Under(path, "src/FoundationHost") || Under(path, "src/Logic"))) plan.Suites.Remove(name);
        foreach (var name in plan.Suites.Keys.ToArray())
            foreach (var companion in DotnetSuites.WithCompanions([name], catalog))
                foreach (var reason in plan.Suites[name].ToArray()) Suite(companion, reason);
        foreach (var (subset, superset) in DerivedSuiteGates.Supersets)
            if (catalog.ContainsKey(subset) && plan.Suites.ContainsKey(superset))
            {
                foreach (var reason in plan.Suites[superset].ToArray()) Suite(subset, reason);
                if (DerivedSuiteGates.FilterContains(catalog[superset]!.AsObject(), catalog[subset]!.AsObject())) plan.Derived[subset] = superset;
            }
        foreach (var (name, reasons) in plan.Suites.Where(pair => !plan.Derived.ContainsKey(pair.Key)))
        {
            bool variant = DotnetSuites.BuildArguments(catalog[name]!["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>())).Any();
            foreach (var path in reasons) Reason(plan.Builds, variant ? "variant:" + name : catalog[name]!["project"]!.GetValue<string>(), path);
        }
        foreach (var name in plan.Suites.Keys.Where(name => name == "foundation-api" || name.StartsWith("plc-exchange-worker-", StringComparison.Ordinal)))
            foreach (var path in plan.Suites[name]) Reason(plan.Builds, "eight-release-table", path);
        foreach (var program in filePrograms.Where(program => plan.Wide || changes.Contains(program, StringComparer.OrdinalIgnoreCase)))
            Reason(plan.Builds, "file:" + program, plan.Wide ? "wide change" : program);
        AddChecks(plan, changes, dotnet, python, api, output);
        AddWorkerChecks(plan, changes, parallelism, dotnet, python, api, output, transportFallback);
        return plan;
    }

    private static bool IsAdapterSuite(string name) => name is "foundation-api" or "adapter-contracts" or "kept-code" || name.StartsWith("plc-exchange-worker-", StringComparison.Ordinal);

    internal static string Describe(Plan plan, IEnumerable<PrepareBuild.Project>? table = null) => string.Join('\n',
        new[] { plan.DocsOnly ? "Docs-only plan (an empty clean change set also uses this plan)." : "Affected build and test plan:" }
            .Concat(plan.Suites.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => "SUITE " + pair.Key + (plan.Derived.TryGetValue(pair.Key, out var source) ? " (derived from " + source + ")" : "") + ": " + string.Join(", ", pair.Value.Order(StringComparer.Ordinal))))
            .Concat(plan.Builds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => "BUILD " + pair.Key + ": " + string.Join(", ", pair.Value.Order(StringComparer.Ordinal))))
            .Concat(plan.Table ? (table ?? []).Select(row => "BUILD " + row.Path + " [" + row.Properties + "]: " + string.Join(", ", plan.Builds["eight-release-table"].Order(StringComparer.Ordinal))) : [])
            .Concat(plan.Checks.Select(job => "CHECK " + job.Name))) + "\n";
}

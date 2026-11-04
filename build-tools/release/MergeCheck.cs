using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static class MergeCheck
{
    internal const string ConfigurationProject = "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj";
    internal const string DiagnosticsProject = "tests/Engine/TiaMcp.Engine.Diagnostics.Tests/TiaMcp.Engine.Diagnostics.Tests.csproj";
    internal sealed record Evaluation(string[] Inputs, string[] Projects);
    internal sealed record Job(string Name, string Executable, string[] Arguments, bool Serial = false);
    internal sealed class Plan
    {
        internal bool DocsOnly { get; set; }
        internal bool Wide { get; set; }
        internal Dictionary<string, HashSet<string>> Suites { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, HashSet<string>> Builds { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Derived { get; } = new(StringComparer.Ordinal);
        internal List<Job> Checks { get; } = [];
        internal bool Table => Builds.ContainsKey("eight-release-table");
    }

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
    internal static bool Documentation(string path) => (Under(path, "docs") || !path.Contains('/') && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        && path is not ("docs/reference/version-tool-catalog.md" or "docs/reference/tool-matrix.md" or "docs/reference/real-machine-ledger.md" or "docs/releases/v4.0.0-tool-migration.md");
    private static bool WidePath(string path) => path is "Directory.Packages.props" or "global.json" or "NuGet.Config" or ".editorconfig" or "Version.props" or "tests/test-suites.json"
        || !path.Contains('/') && (path.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("TiaPortalOpenness", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        || Under(path, "tests/Shared") || Under(path, "build-tools/common")
        || Under(path, "src/Shared") && !path[11..].Contains('/') && path.EndsWith(".props", StringComparison.OrdinalIgnoreCase);

    internal static Evaluation Evaluate(string root, string project, string[] properties, SuiteProcess process, string dotnet)
    {
        var result = process(dotnet, ["msbuild", Path.Combine(root, project), "-nologo", "-nodeReuse:false",
            "-getItem:Compile,EmbeddedResource,None,Content,ProjectReference", "-getProperty:MSBuildAllProjects", .. properties], root,
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

    private static void Reason(Dictionary<string, HashSet<string>> map, string name, string path)
    {
        if (!map.TryGetValue(name, out var paths)) map[name] = paths = new(StringComparer.Ordinal);
        paths.Add(path);
    }

    internal static Plan Select(string[] changes, bool all, JsonObject catalog, IReadOnlyDictionary<string, HashSet<string>> closures,
        Func<string, string, bool> mentions, IEnumerable<string> filePrograms, string dotnet, string python, string api, string output)
    {
        var plan = new Plan { DocsOnly = !all && changes.All(Documentation) };
        void Tool(string name, params string[] args) => plan.Checks.Add(new(name, dotnet, [typeof(Program).Assembly.Location, name, .. args]));
        Tool("check-repository", "-NoBinaries", "-NoAnalyzer");
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
        foreach (var path in changes.Where(WidePath)) Everything(path);
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
                    mapped = true;
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
        return plan;
    }

    private static bool IsAdapterSuite(string name) => name is "foundation-api" or "adapter-contracts" or "kept-code" || name.StartsWith("plc-exchange-worker-", StringComparison.Ordinal);

    internal static string Describe(Plan plan, IEnumerable<PrepareBuild.Project>? table = null) => string.Join('\n',
        new[] { plan.DocsOnly ? "Docs-only plan (an empty clean change set also uses this plan)." : "Affected build and test plan:" }
            .Concat(plan.Suites.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => "SUITE " + pair.Key + (plan.Derived.TryGetValue(pair.Key, out var source) ? " (derived from " + source + ")" : "") + ": " + string.Join(", ", pair.Value.Order(StringComparer.Ordinal))))
            .Concat(plan.Builds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => "BUILD " + pair.Key + ": " + string.Join(", ", pair.Value.Order(StringComparer.Ordinal))))
            .Concat(plan.Table ? (table ?? []).Select(row => "BUILD " + row.Path + " [" + row.Properties + "]: " + string.Join(", ", plan.Builds["eight-release-table"].Order(StringComparer.Ordinal))) : [])
            .Concat(plan.Checks.Select(job => "CHECK " + job.Name))) + "\n";

    private static void AddChecks(Plan plan, string[] changes, string dotnet, string python, string api, string output)
    {
        void Tool(string name, params string[] args) => plan.Checks.Add(new(name, dotnet, [typeof(Program).Assembly.Location, name, .. args]));
        void Py(string name, string script, params string[] args) => plan.Checks.Add(new(name, python, ["scripts/checks/" + script, .. args]));
        foreach (var name in new[] { "generate-catalogs", "generate-tool-usage", "generate-release-migration" }) Tool(name, "-Check");
        foreach (var name in new[] { "check-tools-list", "check-script-tool-calls", "check-adapter-boundary", "check-tia-features" }) Tool(name);
        Tool("check-analyzer-warnings", "-Check", "-DiagnosticsDirectory", Path.Combine(output, "analyzers"));
        Py("contract-verify", "Snapshot-ToolContracts.py", "verify");
        plan.Checks.Add(new("match-plc-name", dotnet, ["run", "scripts/checks/Test-MatchPlcName.cs", "--", "-SourceOnly"]));
        bool Changed(params string[] names) => plan.Wide || changes.Any(path => names.Any(name => path == "scripts/checks/" + name));
        foreach (var script in new[] { "Test-ReleaseApprovalGate.py", "Test-RelocatedBundle.py", "Test-ReleaseSmoke.py", "Compare-NativeCallOrder.py",
            "Test-NativeLifecycle.py", "Test-NativeMcpSession.py", "Test-ResourceDiscovery.py" })
            if (Changed(script)) Py(Path.GetFileNameWithoutExtension(script), script, "--self-test");
        if (Changed("Snapshot-ToolContracts.py", "phase6_groups.py", "snapshot_text_migration.py", "offline_fixtures.py")) Py("contract-self-test", "Snapshot-ToolContracts.py", "self-test");
        if (Changed("Test-V21Ecosystem.py")) Py("ecosystem-self-test", "Test-V21Ecosystem.py", "--self-test", "--output", Path.Combine(output, "ecosystem"));
        if (Changed("Test-CampaignInputs.py", "Test-CampaignInputs.cs"))
        {
            Py("campaign-self-test", "Test-CampaignInputs.py", "--self-test");
            Py("campaign-validator-self-test", "Test-CampaignInputs.py", "--validator-self-test");
        }
        if (plan.Builds.ContainsKey(ConfigurationProject)) plan.Checks.Add(new("configuration-page", dotnet,
            ["run", "--project", ConfigurationProject, "-c", "Release", "--no-build", "--no-restore", "--", Path.Combine(output, "configuration")]));
        if (plan.Wide || changes.Any(path => new[] { "src/Adapters", "src/Adapters.Contracts", "src/PlcWorker" }.Any(dir => Under(path, dir))))
        {
            plan.Checks.Add(new("adapter-inputs", dotnet, ["run", "src/Adapters/build/Test-AdapterInputs.cs", "--", "--source-root", "src", "--public-api-root", api, "--evidence-directory", Path.Combine(output, "adapter-inputs")], true));
            plan.Checks.Add(new("worker-isolation", dotnet, ["run", "src/Adapters/build/Test-WorkerIsolation.cs", "--", "--evidence-directory", Path.Combine(output, "worker-isolation"), "--native-call-weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"], true));
            Reason(plan.Builds, "eight-release-table", "worker-isolation");
        }
        if (plan.Wide || plan.Builds.ContainsKey(DiagnosticsProject) || changes.Any(path => Under(path, "src/Adapters") || Under(path, "build-tools/native-call-weaver")) || Changed("Test-NativeDiagnostics.py"))
        {
            Reason(plan.Builds, DiagnosticsProject, "native-diagnostics");
            Reason(plan.Builds, "build-tools/native-call-weaver/NativeCallWeaver.csproj", "native-diagnostics");
            plan.Checks.Add(new("native-diagnostics", python, ["scripts/checks/Test-NativeDiagnostics.py", "--fixture", "tests/Engine/TiaMcp.Engine.Diagnostics.Tests/bin/Release/net48/TiaMcp.Engine.Diagnostics.Tests.exe",
                "--weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll", "--output", Path.Combine(output, "native-diagnostics")], true));
        }
    }

    internal static XDocument Traversal(IEnumerable<string> projects) => new(new XElement("Project",
        new XElement("ItemGroup", projects.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new XElement("MergeProject", new XAttribute("Include", path)))),
        new[] { "Restore", "Build" }.Select(target => new XElement("Target", new XAttribute("Name", target),
            new XElement("MSBuild", new XAttribute("Projects", "@(MergeProject)"), new XAttribute("Targets", target),
                new XAttribute("BuildInParallel", target == "Build" ? "true" : "false"), new XAttribute("StopOnFirstFailure", "false"))))));

    internal sealed record Timing(string Name, double Seconds, CommandResult Result);
    internal static string Summary(IEnumerable<Timing> timings, double seconds) => string.Join('\n', timings.OrderByDescending(row => row.Seconds)
        .ThenBy(row => row.Name, StringComparer.Ordinal).Select(row => $"{row.Name}: {row.Seconds.ToString("F3", CultureInfo.InvariantCulture)} s"))
        + $"\nTotal: {seconds.ToString("F3", CultureInfo.InvariantCulture)} s\n"
        + string.Join('\n', timings.Where(row => row.Result.ExitCode != 0).Select(row => "FAIL " + row.Name + ": exit " + row.Result.ExitCode + "; " + row.Result.StandardOutput + row.Result.StandardError)) + "\n";

    internal static int Run(string root, Options options, SuiteProcess? process = null, Func<string[], CommandResult>? git = null)
    {
        var total = Stopwatch.StartNew();
        var dotnet = ReleaseCommands.Dotnet;
        process ??= DotnetSuites.RunProcess;
        git ??= args => process("git", args, root, null);
        var changes = ChangeSet(options.Get("Base", "master"), git);
        Console.WriteLine($"Selecting from {changes.Length} changed paths.");
        var api = options.Get("PublicApiRoot") ?? Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT") ?? Path.Combine(root, "sdk");
        var python = options.Get("Python") ?? Environment.GetEnvironmentVariable(ReleaseCommands.DecoderPythonVariable) ?? "python";
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        int parallelism = FastSuites.Positive(options, "MaxParallelism", Math.Max(1, Environment.ProcessorCount - 4));
        var output = Path.Combine(root, "bin-build/merge-check");
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
        CheckAllProfiles.IncludeApprovedSubsets(catalog);
        var context = new PrepareContext(root, "", api, nuget, python, python, output, DateOnly.FromDateTime(DateTime.Today), parallelism);
        var table = PrepareBuild.VersionProjects(context).Select(row => row with { Path = Normalize(Path.GetRelativePath(root, row.Path)) }).ToArray();
        var closures = !options.Has("All") && changes.All(Documentation) ? new Dictionary<string, HashSet<string>>()
            : Closures(catalog, table, api, (project, properties) => Evaluate(root, project, properties, process, dotnet));
        var textReferences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var testDirectories = catalog.Select(pair => Normalize(Path.GetDirectoryName(pair.Value!["project"]!.GetValue<string>())!)).Distinct().ToArray();
        bool Mentions(string name, string directory)
        {
            if (!textReferences.TryGetValue(name, out var paths))
            {
                var result = git(["grep", "-l", "-z", "-F", "-e", name, "--", .. testDirectories, "tests/Shared"]);
                if (result.ExitCode is not (0 or 1)) ProcessRunner.RequireSuccess(result, "Read text references");
                textReferences[name] = paths = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
            }
            return paths.Any(path => Under(path, directory) || Under(path, "tests/Shared"));
        }
        var plan = Select(changes, options.Has("All"), catalog, closures, Mentions,
            !options.Has("All") && changes.All(Documentation) ? [] : FileBasedPrograms.Discover(root), dotnet, python, api, output);
        Console.Write(Describe(plan, table));
        if (options.Has("DryRun")) return 0;
        PrepareBuild.RequireOwnedTree(root, output);
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(Path.Combine(output, "analyzers"));
        var timings = new ConcurrentBag<Timing>();
        timings.Add(new("selection", total.Elapsed.TotalSeconds, new(0, "", "")));
        CommandResult Timed(string name, Func<CommandResult> run)
        {
            var watch = Stopwatch.StartNew();
            CommandResult result;
            try { result = run(); } catch (Exception ex) { result = new(1, "", ex.ToString()); }
            timings.Add(new(name, watch.Elapsed.TotalSeconds, result));
            File.WriteAllText(Path.Combine(output, name.Replace('/', '_').Replace(':', '_') + ".log"), result.StandardOutput + result.StandardError
                + $"\nExit code: {result.ExitCode}; elapsed: {watch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s\n");
            return result;
        }
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = Path.Combine(output, "analyzers"),
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1", ["RestoreConfigFile"] = nuget };
        ReleaseCommands.ConfigureDecoderTestEnvironment(environment, root, options);
        void ConfigureTests(IDictionary<string, string?> env)
        {
            ReleaseCommands.ConfigureDecoderTestEnvironment(env, root, options);
            // Synthetic projects created by checks must not become build-gate inputs.
            env["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
        }
        string[] properties = ["-p:Configuration=Release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaPublicApiRoot=" + api,
            .. nuget == null ? Array.Empty<string>() : ["-p:RestoreConfigFile=" + nuget]];
        CommandResult Exec(string name, string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null) =>
            Timed(name, () => process(exe, args, root, env ?? environment));
        if (!plan.DocsOnly)
        {
            var projects = plan.Builds.Keys.Where(key => key.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plan.Table) projects.UnionWith(PrepareBuild.SharedProjects);
            if (projects.Count != 0)
            {
                var traversal = Path.Combine(output, "build.proj");
                Traversal(projects.Select(project => Path.Combine(root, project))).Save(traversal);
                Exec("restore", dotnet, ["msbuild", traversal, "-t:Restore", "-m:1", "-nodeReuse:false", .. properties]);
                Exec("build", dotnet, ["msbuild", traversal, "-t:Build", "-m:" + parallelism, "-nodeReuse:false", .. properties]);
            }
            if (plan.Table)
            {
                foreach (var row in table) Exec("restore-" + row.Kind + "-" + row.Properties.Split(';')[0].Split('=')[1], dotnet,
                    ["msbuild", Path.Combine(root, row.Path), "-t:Restore", "-m:1", "-nodeReuse:false", .. properties, .. row.Properties.Split(';').Select(p => "-p:" + p)]);
                foreach (var kind in new[] { "Adapter", "Worker", "Studio" })
                {
                    var document = PrepareBuild.BuildTableDocument(table.Where(row => row.Kind == kind).Select(row => row with { Path = Path.Combine(root, row.Path) }));
                    document.Descendants("MSBuild").Single().SetAttributeValue("Targets", "Build");
                    document.Descendants("MSBuild").Single().SetAttributeValue("StopOnFirstFailure", "false");
                    var path = Path.Combine(output, "table-" + kind + ".proj"); document.Save(path);
                    Exec("build-" + kind, dotnet, ["msbuild", path, "-t:Build", "-m:" + parallelism, "-nodeReuse:false", .. properties]);
                }
            }
            var files = plan.Builds.Keys.Where(key => key.StartsWith("file:", StringComparison.Ordinal)).Select(key => key[5..]);
            var groups = FileBasedPrograms.BuildGroups(root, files);
            if (groups.Length != 0) ParallelPipeline.Run(groups.Select((group, index) => ("file-group-" + index, (Func<PipelineResult>)(() =>
            {
                var rows = group.Select(file => Exec("file-" + file.Replace('/', '_'), dotnet, ["build", Path.Combine(root, file), "-c", "Release", "-p:TiaMcpCollectFileBasedPrograms=true", .. properties])).ToArray();
                return new("file-group-" + index, rows.Any(row => row.ExitCode != 0) ? 1 : 0, "", "");
            }))).ToArray(), Path.Combine(output, "file-builds"), parallelism, continueOnFailure: true);
            Timed("decoder-python", () => new(ReleaseCommands.PrepareDecoderPython(Options.Parse(["-Python=" + python, "-Reuse=true"], new HashSet<string>()), root), "", ""));
        }
        CommandResult Check(Job job) => Timed(job.Name, () =>
        {
            var checkEnvironment = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            ConfigureTests(checkEnvironment);
            var result = process(job.Executable, job.Arguments, root, checkEnvironment);
            return CheckResult(job.Name, result, output);
        });
        foreach (var job in plan.Checks.Where(job => job.Serial)) Check(job);
        var additional = plan.Checks.Where(job => !job.Serial).Select(job => (job.Name, (Func<PipelineResult>)(() =>
        {
            var result = Check(job); return new(job.Name, result.ExitCode, result.StandardOutput, result.StandardError);
        }))).ToList();
        var suites = new JsonObject();
        foreach (var name in plan.Suites.Keys.Where(name => !plan.Derived.ContainsKey(name) && name != "foundation-api")) suites[name] = catalog[name]!.DeepClone();
        var suiteOutput = Path.Combine(output, "suites");
        if (plan.Suites.ContainsKey("foundation-api")) additional.Add(("foundation-api", () =>
        {
            var result = Timed("foundation-api", () =>
            {
                var rows = ReleaseFoundationStage.Run(root, catalog, suiteOutput, api, dotnet, nuget, parallelism,
                    ConfigureTests,
                    (opts, registrations, run) => PlainShards.Run(root, Options.Parse([.. opts.Arguments(), "-NoBuild=true"], new HashSet<string>()), registrations,
                        (exe, args, cwd, env) => Timed("foundation-shard-" + env!["TIA_MCP_TEST_SHARD_INDEX"], () => run(exe, args, cwd, env))));
                return new(rows.Values.Any(row => row.ExitCode != 0) ? 1 : 0, string.Join('\n', rows.Values.Select(row => row.StandardOutput)),
                    string.Join('\n', rows.Where(pair => pair.Value.ExitCode != 0).Select(pair => pair.Key + ": " + pair.Value.StandardError)));
            });
            return new("foundation-api", result.ExitCode, result.StandardOutput, result.StandardError);
        }));
        int variantBuild = 0;
        SuiteProcess SuiteRun = (exe, arguments, cwd, env) =>
        {
            var args = arguments.ToArray();
            string name = args[0] == "test"
                ? "suite-" + args.Single(arg => arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal))[16..^4]
                : "variant-build-" + Interlocked.Increment(ref variantBuild) + "-" + Path.GetFileNameWithoutExtension(args[1]);
            env!["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = args[0] == "build" ? Path.Combine(output, "analyzers") : null;
            return Timed(name, () => process(exe, args, cwd, env));
        };
        Timed("suite-stage", () =>
        {
            ReleaseSuiteStage.Run(root, suites, suiteOutput, api, dotnet, nuget, parallelism, SuiteRun,
                ConfigureTests, additional,
                prebuilt: plan.Builds.Keys.Where(key => key.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase));
            return new(0, "", "");
        });
        foreach (var (name, source) in plan.Derived.Where(pair => pair.Value != "foundation-api"))
            Timed("derived-" + name, () => DerivedSuiteGates.Evaluate(name, catalog[name]!.AsObject(), Path.Combine(suiteOutput, source, source + ".trx"), Path.Combine(suiteOutput, name), source));
        Console.Write(Summary(timings, total.Elapsed.TotalSeconds));
        return timings.Any(row => row.Result.ExitCode != 0) ? 1 : 0;
    }

    internal static CommandResult CheckResult(string name, CommandResult result, string output)
    {
        if (result.ExitCode != 0) return result;
        if (name == "configuration-page")
        {
            var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"(?m)^Passed: (\d+)\s*$");
            if (!match.Success || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < 157)
                return result with { ExitCode = 1, StandardError = "Configuration page passed count below 157 or missing." };
        }
        if (name == "native-diagnostics")
        {
            var path = Path.Combine(output, "native-diagnostics/result.json");
            var report = JsonNode.Parse(File.ReadAllText(path))!;
            if (((int?)report["behaviorChecks"] ?? 0) < 36 || ((int?)report["rejectionChecks"] ?? 0) < 5 || (bool?)report["nativeTiaExecuted"] != false)
                return result with { ExitCode = 1, StandardError = "Native diagnostics gate failed." };
        }
        return result;
    }
}

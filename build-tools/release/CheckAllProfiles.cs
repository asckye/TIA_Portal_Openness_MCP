using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record SuiteExecution(CheckBuild Unit, string Executable, IReadOnlyDictionary<string, string?> Environment,
    string Runner = "FastSuites.RunShard", string Approval = "enabled=true;timeoutSeconds=120",
    IReadOnlyDictionary<string, string?>? BaseEnvironment = null);
internal sealed record DerivationPlan(IReadOnlyDictionary<string, string> Mappings, IReadOnlyDictionary<string, string> Fallbacks,
    IReadOnlyDictionary<string, SuiteExecution> Executions);

internal static class CheckAllProfiles
{
    internal static string Parse(Options options) => options.Get("Profile", "merge") switch
    {
        "merge" => "merge", "nightly" => "nightly", _ => throw new ReleaseException("-Profile must be merge or nightly", 64)
    };

    internal static readonly IReadOnlyDictionary<string, string> Derived = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["foundation"] = "foundation-api", ["worker-robustness"] = "foundation-api",
        ["workbench-control-host"] = "foundation-api", ["shared-pipeline"] = "foundation-api", ["shared-lifecycle-plc"] = "foundation-api",
        ["workbench-control"] = "studio-gui-false", ["mcp-panel"] = "studio-gui-false",
        ["workbench-vci-prefill"] = "studio-gui-false", ["crash-evidence"] = "studio-gui-false",
        ["vci-read-only-binding"] = "studio-core-false", ["shared-lifecycle"] = "shared-session",
        ["kept-code-v20"] = "kept-code"
    };

    internal static readonly string[] StudioSuites = ["studio-core-false", "studio-core-true", "studio-gui-false", "studio-gui-true"];

    // These are the only extra settings of the original Studio Isolated jobs. Per-process scratch roots, pipe scopes and
    // shard/timing paths are supplied by the same RunShard/EnvironmentFor implementation for both executions.
    private static readonly string[] StudioEnvironmentDifferences = ["TIA_MCP_TEST_PUBLIC_API_ROOT", "TIA_MCP_RELEASE_TEMP_ROOT",
        "TIA_MCP_OFFLINE_NUGET_CONFIG", "RestoreConfigFile", "PYTHONIOENCODING", "TIA_MCP_CHECK_NO_BUILD",
        "TIA_MCP_PLC_TOOLS_PYTHON", "TIA_MCP_ANALYZER_REPORT_DIRECTORY"];

    internal static DerivationPlan Plan(JsonObject catalog, string api, string nuget, string? companion, string dotnet)
    {
        var executions = catalog.ToDictionary(pair => pair.Key, pair =>
        {
            string? sdk = pair.Key == "foundation-api" || pair.Value!["publicApiRoot"]?.GetValue<bool>() == true ? api : null;
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = sdk };
            if (StudioSuites.Contains(pair.Key))
            {
                environment["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api;
                environment["TIA_MCP_RELEASE_TEMP_ROOT"] = "$TEMP";
                environment["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget; environment["RestoreConfigFile"] = nuget;
                environment["PYTHONIOENCODING"] = "utf-8"; environment["TIA_MCP_CHECK_NO_BUILD"] = "1";
                environment["TIA_MCP_PLC_TOOLS_PYTHON"] = companion; environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
                // The original Isolated jobs never set a bundle root; Studio's layout tests resolve their own temporary roots.
                environment["TIA_MCP_BUNDLE_ROOT"] = null;
            }
            return new SuiteExecution(CheckAllBuild.Suite(pair.Value!.AsObject()), dotnet, environment,
                BaseEnvironment: FastSuites.EnvironmentValues("$ISOLATION", "$PIPE_SCOPE", sdk));
        }, StringComparer.Ordinal);
        return ValidatePlan(catalog, executions);
    }

    internal static DerivationPlan ValidatePlan(JsonObject catalog, IReadOnlyDictionary<string, SuiteExecution> executions)
    {
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal); var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, source) in Derived)
        {
            var reason = Precondition(name, source, catalog, executions);
            if (reason == null) accepted.Add(name, source); else rejected.Add(name, reason);
        }
        return new(accepted, rejected, executions);
    }

    internal static string? Precondition(string name, string source, JsonObject catalog, IReadOnlyDictionary<string, SuiteExecution> executions)
    {
        if (!Derived.TryGetValue(name, out var approved) || approved != source) return "mapping is not approved";
        if (catalog[name] is not JsonObject subset || catalog[source] is not JsonObject superset ||
            !executions.TryGetValue(name, out var target) || !executions.TryGetValue(source, out var parent)) return "missing suite execution";
        bool SameUnit(CheckBuild left, CheckBuild right) => left.Project == right.Project && left.Variant == right.Variant &&
            left.Properties.Order(StringComparer.Ordinal).SequenceEqual(right.Properties.Order(StringComparer.Ordinal));
        if (!SameUnit(target.Unit, CheckAllBuild.Suite(subset)) || !SameUnit(parent.Unit, CheckAllBuild.Suite(superset)) || !SameUnit(target.Unit, parent.Unit))
            return "different project, variant or build unit";
        if (target.Executable != parent.Executable || target.Runner != parent.Runner || target.Runner != "FastSuites.RunShard" || target.Approval != parent.Approval || target.Approval != "enabled=true;timeoutSeconds=120")
            return "different runner or configuration";
        if (target.BaseEnvironment == null || parent.BaseEnvironment == null) return "missing runner environment template";
        try { if (!DerivedSuiteGates.FilterContains(superset, subset)) return "superset filter does not provably contain the subset filter"; }
        catch (ReleaseException ex) { return ex.Message; }
        var allowed = source == "foundation-api" ? new[] { "TIA_MCP_TEST_PUBLIC_API_ROOT" }
            : StudioSuites.Contains(source) ? StudioEnvironmentDifferences : [];
        Dictionary<string, string?> Effective(SuiteExecution execution)
        {
            var values = new Dictionary<string, string?>(execution.BaseEnvironment ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in execution.Environment) values[pair.Key] = pair.Value;
            return values;
        }
        var targetEnvironment = Effective(target); var parentEnvironment = Effective(parent);
        if (source == "foundation-api" && (targetEnvironment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT") != null ||
            string.IsNullOrWhiteSpace(parentEnvironment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT"))))
            return "Foundation derivation requires the reviewed SDK-less to API environment mapping";
        foreach (var key in targetEnvironment.Keys.Union(parentEnvironment.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (targetEnvironment.GetValueOrDefault(key) == parentEnvironment.GetValueOrDefault(key)) continue;
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase) || targetEnvironment.GetValueOrDefault(key) != null)
                return "unapproved environment difference: " + key;
        }
        if (StudioSuites.Contains(source) && (parent.Environment.GetValueOrDefault("TIA_MCP_CHECK_NO_BUILD") != "1" ||
            parent.Environment.GetValueOrDefault("PYTHONIOENCODING") != "utf-8" || parent.Environment.GetValueOrDefault("TIA_MCP_RELEASE_TEMP_ROOT") != "$TEMP" ||
            parent.Environment.GetValueOrDefault("TIA_MCP_OFFLINE_NUGET_CONFIG") != parent.Environment.GetValueOrDefault("RestoreConfigFile")))
            return "unapproved Studio environment configuration";
        // The listed SDK difference is precisely SDK-less original versus the configured API root, never two SDK roots.
        if (target.Environment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT") != parent.Environment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT") &&
            (target.Environment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT") != null || string.IsNullOrWhiteSpace(parent.Environment.GetValueOrDefault("TIA_MCP_TEST_PUBLIC_API_ROOT"))))
            return "unapproved SDK environment difference";
        return null;
    }

    internal static void ApplyEnvironment(SuiteExecution execution, IDictionary<string, string?> environment, bool studio)
    {
        foreach (var pair in execution.Environment) environment[pair.Key] = pair.Value == "$TEMP" ? environment["TEMP"] : pair.Value;
        if (!studio) return;
        var config = Path.Combine(environment["TIA_MCP_DATA_DIRECTORY"]!, "config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=true\ntimeoutSeconds=120\n");
    }

    internal static bool RunsSeparately(string name, string profile, DerivationPlan plan) => !StudioSuites.Contains(name) &&
        (profile == "nightly" || !plan.Mappings.ContainsKey(name));

    // GUI uses Foundation MCP clients after P8-21, and has no direct reference to SessionFactoryLoader, BridgeChannel or
    // RpcDispatcher. Core's true suite exercises those variant-specific types. Keep the GUI session/control integration
    // classes too (including their partial files); UiSettings inspects Core's assembly and StudioBundleLayout checks loading.
    // Pure rendering/layout classes construct windows with fake clients and never dispatch to the changed Core types.
    internal static readonly string[] SharedGuiClasses = ["MainWindowTests", "McpPanelTests", "OfflineDemoTests",
        "ProjectPageInteractionTests", "UnifiedDesktopTests", "VciPanelTests", "WorkbenchControlArtifactTests",
        "WorkbenchControlLoadTests", "WorkbenchControlPipeTests", "WorkbenchControlTests", "WorkbenchMcpPrefillTests",
        "WorkbenchViewModelTests", "UiSettingsTests", "StudioBundleLayoutTests"];

    internal static JsonObject SharedGuiSuite(JsonObject suite)
    {
        var selected = suite.DeepClone().AsObject();
        selected["arguments"]!.AsArray().Add("--filter");
        selected["arguments"]!.AsArray().Add(string.Join("|", SharedGuiClasses.Select(cls => "FullyQualifiedName~TiaOpenness.Gui.Tests." + cls + ".")));
        // Full registration remains unchanged. Merge has a presence gate for every approved class, not a fixed case count.
        selected["minimumPassed"] = 1;
        selected["maximumSkipped"] = 0;
        return selected;
    }

    internal static string[] Profiles(string job, DerivationPlan plan) => CheckAllJobInputs.NightlyOnly(job) || job.EndsWith("-coverage-proof", StringComparison.Ordinal) ? ["nightly"] : plan.Mappings.Keys.Any(name => job.StartsWith(name + "-shard-", StringComparison.Ordinal)
        || job == name + "-sequential") ? ["nightly"] : ["merge", "nightly"];

    internal static void WriteInventory(JsonObject catalog, string output, string profile, DerivationPlan plan)
    {
        var rows = catalog.Select(pair => new
        {
            name = pair.Key, buildProfiles = new[] { "merge", "nightly" },
            processProfiles = plan.Mappings.ContainsKey(pair.Key) ? new[] { "nightly" } : new[] { "merge", "nightly" },
            fullProfiles = plan.Mappings.ContainsKey(pair.Key) || pair.Key == "studio-gui-true" ? new[] { "nightly" } : new[] { "merge", "nightly" },
            mergeDerivedFrom = plan.Mappings.GetValueOrDefault(pair.Key),
            fallbackReason = plan.Fallbacks.GetValueOrDefault(pair.Key),
            mergeClasses = pair.Key == "studio-gui-true" ? SharedGuiClasses : null,
            guiShards = pair.Key.StartsWith("studio-gui-", StringComparison.Ordinal) ? 3 : (int?)null,
            concurrentGuiShards = false
        });
        File.WriteAllText(Path.Combine(output, "profile-inventory.json"), JsonSerializer.Serialize(new { profile, suites = rows },
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    internal static void AddDerived(List<CheckJob> jobs, JsonObject catalog, string output, DerivationPlan plan)
    {
        foreach (var (name, source) in plan.Mappings)
        {
            var suite = catalog[name]!.AsObject();
            jobs.Add(new(name + "-gate", () => DerivedSuiteGates.Evaluate(name, suite, Path.Combine(output, source + ".trx"),
                output, source), [source + "-gate"]) { AllowFailedDependencies = true, Priority = 1 });
        }
    }

    internal static void AddNightlyProofs(List<CheckJob> jobs, JsonObject catalog, string output)
    {
        foreach (var (name, source) in Derived)
        {
            var suite = catalog[name]!.AsObject();
            jobs.Add(new(name + "-coverage-proof", () => DerivedSuiteGates.CompareSameRun(name, suite,
                Path.Combine(output, name + ".trx"), Path.Combine(output, source + ".trx"), Path.Combine(output, "same-run-proofs"), source),
                [name + "-gate", source + "-gate"]) { AllowFailedDependencies = true, Priority = 1 });
        }
    }

    internal static IReadOnlyList<string> SharedGuiErrors(IEnumerable<DerivedSuiteGates.Case> cases, bool requirePassed)
    {
        var rows = cases.ToArray(); var errors = new List<string>();
        foreach (var cls in SharedGuiClasses)
        {
            string full = "TiaOpenness.Gui.Tests." + cls;
            var group = rows.Where(row => row.Class == full).ToArray();
            if (group.Length == 0) errors.Add("Missing selected GUI class: " + full);
            else if (requirePassed && (!group.Any(row => (string?)row.Result.Attribute("outcome") == "Passed") ||
                group.Any(row => (string?)row.Result.Attribute("outcome") != "Passed")))
                errors.Add("Selected GUI class must have >0 passed and zero failed/skipped: " + full);
        }
        return errors;
    }

    internal static void AddStudio(List<CheckJob> jobs, string root, JsonObject catalog, string output, string dotnet, string profile,
        string? manifest, SuiteProcess? process = null, IReadOnlyDictionary<string, double>? history = null, Func<string, SuiteProcess>? processFor = null, string? api = null)
    {
        foreach (var name in StudioSuites)
        {
            var full = catalog[name]!.AsObject();
            var suite = name == "studio-gui-true" && profile == "merge" ? SharedGuiSuite(full) : full;
            bool gui = name.StartsWith("studio-gui-", StringComparison.Ordinal);
            int count = gui ? 3 : 1;
            string directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            var (assignments, costs) = GuiPlan(root, name, count, !ReferenceEquals(suite, full));
            string plan = Path.Combine(directory, "shard-plan.tsv");
            File.WriteAllLines(plan, assignments.Select(pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "\t" + pair.Value));
            File.WriteAllText(Path.Combine(directory, "shard-plan.json"), JsonSerializer.Serialize(new { assignments, costs,
                algorithm = "LPT by measured class duration", concurrent = false, reason = "Shown windows and Keyboard.FocusedElement share the desktop" }) + "\n");
            var unit = CheckAllBuild.Suite(suite);
            var budget = CheckScheduler.JobTimeout(new(name, () => new(0, "", ""), []), history ?? new Dictionary<string, double>());
            long runtimeTicks = 0;
            var paths = new List<string>();
            var names = new List<string>();
            for (int index = 0; index < count; index++)
            {
                int shard = index; string shardDirectory = Path.Combine(directory, "shard-" + index);
                string job = name + "-shard-" + index; names.Add(job); paths.Add(Path.Combine(shardDirectory, name + ".trx"));
                // Preserve the exclusive GUI lane; focus/activation assertions cannot share a desktop concurrently.
                jobs.Add(new(job, () =>
                {
                    var clock = Stopwatch.StartNew();
                    try { return FastSuites.RunShard(root, name, suite, shardDirectory, dotnet, null, unit, shard, count,
                        plan: plan, manifest: manifest, process: processFor?.Invoke(name) ?? process, diagnostics: true, registeredOutput: true); }
                    finally { Interlocked.Add(ref runtimeTicks, clock.Elapsed.Ticks); }
                }, index == 0 ? [] : [names[index - 1]], Exclusive: true)
                    { AllowFailedDependencies = true, SensitiveGroup = unit.Project, EstimatedSeconds = costs[index] + 5,
                        ResultInputs = new("suite", new JsonObject { ["suite"] = name, ["registration"] = suite.DeepClone(),
                            ["project"] = unit.Project, ["variant"] = unit.Variant, ["index"] = index, ["count"] = count,
                            ["api"] = api, ["runner"] = "Studio RunShard", ["profileSelection"] = ReferenceEquals(suite, full) ? "full" : "selected-classes" }.ToJsonString(),
                            shardDirectory, name + ".trx", plan, (int)suite["maximumSkipped"]!) });
            }
            jobs.Add(new(name + "-gate", () =>
            {
                var result = SuiteSharding.Merge(name, suite, paths, output);
                var errors = new List<string>();
                if (result.ExitCode != 0) errors.Add(result.StandardError);
                // Three processes must not gain three times the former unsharded job's runtime allowance.
                if (StudioRuntimeError(TimeSpan.FromTicks(runtimeTicks), budget) is { } timeout) errors.Add(timeout);
                if (name == "studio-gui-true") errors.AddRange(SharedGuiErrors(DerivedSuiteGates.Read(Path.Combine(output, name + ".trx")), requirePassed: profile == "merge"));
                var summaryPath = Path.Combine(output, name + ".json");
                var summary = JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject();
                summary["profile"] = profile; summary["execution"] = ReferenceEquals(suite, full) ? "full" : "selected-classes";
                summary["releaseEligible"] = ReferenceEquals(suite, full);
                summary["runtimeSeconds"] = TimeSpan.FromTicks(runtimeTicks).TotalSeconds; summary["timeoutSeconds"] = budget.TotalSeconds;
                summary["errors"] = ReleaseEvidence.Strings(errors);
                File.WriteAllText(summaryPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                SuiteCosts.Record(name, directory, Path.Combine(root, "bin-build/check-timings"));
                return result with { ExitCode = errors.Count == 0 ? 0 : 1, StandardError = string.Join("\n", errors) };
            }, names.ToArray()) { AllowFailedDependencies = true, Priority = 1 });
            // Preserve the historical release-evidence job names as full-suite aggregate aliases.
            jobs.Add(new(name, () => new(0, name + ": registered gate passed\n", ""), [name + "-gate"]));
        }
    }

    internal static string? StudioRuntimeError(TimeSpan runtime, TimeSpan budget) => runtime >= budget
        ? "Studio shards exceeded the original unsharded runtime budget: " + runtime.TotalSeconds + "s >= " + budget.TotalSeconds + "s" : null;

    internal static (Dictionary<string, int>, double[]) GuiPlan(string root, string name, int count, bool selected = false)
    {
        var path = Path.Combine(root, "bin-build/check-timings", name + ".classes.json");
        Dictionary<string, SuiteCosts.ClassCost> measured;
        try { measured = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, SuiteCosts.ClassCost>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { measured = new(); }
        var costs = measured.Where(pair => !selected || SharedGuiClasses.Any(cls => pair.Key == "TiaOpenness.Gui.Tests." + cls))
            .ToDictionary(pair => "C:" + pair.Key, pair => pair.Value is { Seconds: var seconds } && double.IsFinite(seconds) && seconds >= 0 ? seconds : 1, StringComparer.Ordinal);
        // Estimates only: absent, added or renamed classes never fail a gate. Unknown classes use ShardFramework's
        // deterministic class fallback, so all their cases still execute in exactly one of the three processes.
        if (name.StartsWith("studio-gui-", StringComparison.Ordinal) && !selected)
        {
            costs.TryAdd("C:TiaOpenness.Gui.Tests.WorkbenchFontStyleTests", 76);
            costs.TryAdd("C:TiaOpenness.Gui.Tests.WorkbenchNarrowLayoutTests", 67);
        }
        if (selected) foreach (var cls in SharedGuiClasses) costs.TryAdd("C:TiaOpenness.Gui.Tests." + cls, 1);
        return SuiteCosts.Balance(costs, count);
    }
}

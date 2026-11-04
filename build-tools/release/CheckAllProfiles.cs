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

    internal static readonly IReadOnlyDictionary<string, string> Derived = DerivedSuiteGates.Supersets;

    internal static readonly string[] StudioSuites = ["studio-core", "studio-gui"];

    // Old resource observations remain on disk, but deleted jobs must not size the active packing plan.
    internal static bool RetiredJob(string name) => name is "check-latency" or "suite-output-parity" or "studio-gui-control-load" ||
        name.StartsWith("latency-", StringComparison.Ordinal) ||
        name.StartsWith("stability-", StringComparison.Ordinal) && name.Contains("-http-", StringComparison.Ordinal) ||
        new[] { "offline-v20", "kept-code-v20", "studio-core-true", "studio-core-false", "studio-gui-true", "studio-gui-false" }
            .Any(suite => name == suite || name.StartsWith(suite + "-", StringComparison.Ordinal)) ||
        new[] { "14sp1", "15.1", "16", "17", "18", "19" }.Any(key => name == "responses-v" + key ||
            name.StartsWith("responses-v" + key + "-", StringComparison.Ordinal) || name == "transport-usage-v" + key ||
            name.StartsWith("transport-usage-v" + key + "-", StringComparison.Ordinal));

    // Dropping the shipping variant suffix must preserve its deadlines, packing observations and ordering history.
    internal static void UseShippingHistory<T>(Dictionary<string, T> history, Func<T, T, T>? combine = null)
    {
        foreach (var name in StudioSuites)
        {
            string legacy = name + "-false";
            foreach (var key in history.Keys.Where(key => key == legacy || key.StartsWith(legacy + "-", StringComparison.Ordinal)).ToArray())
            {
                string renamed = name + key[legacy.Length..];
                if (!history.TryGetValue(renamed, out var current)) history[renamed] = history[key];
                else if (combine != null) history[renamed] = combine(current, history[key]);
                history.Remove(key);
            }
        }
    }

    // These are the only extra settings of the original Studio Isolated jobs. Per-process scratch roots, pipe scopes and
    // shard/timing paths are supplied by the same RunShard/EnvironmentFor implementation for both executions.
    private static readonly string[] StudioEnvironmentDifferences = ["TIA_MCP_TEST_PUBLIC_API_ROOT", "TIA_MCP_RELEASE_TEMP_ROOT",
        "TIA_MCP_OFFLINE_NUGET_CONFIG", "RestoreConfigFile", "PYTHONIOENCODING", "TIA_MCP_CHECK_NO_BUILD",
        "TIA_MCP_PLC_TOOLS_PYTHON", "TIA_MCP_ANALYZER_REPORT_DIRECTORY"];

    internal static DerivationPlan Plan(JsonObject catalog, string api, string nuget, string? companion, string dotnet)
    {
        IncludeApprovedSubsets(catalog);
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

    internal static void IncludeApprovedSubsets(JsonObject catalog)
    {
        if (catalog["foundation-api"] is not JsonObject source) return;
        var arguments = source["arguments"]!.AsArray();
        int filter = Enumerable.Range(0, arguments.Count).FirstOrDefault(index => (string?)arguments[index] == "--filter", -1);
        if (filter < 0) return; // Unfiltered already contains every approved subset.
        var terms = ((string)arguments[filter + 1]!).Split('|').ToList();
        foreach (var name in new[] { "worker-robustness", "workbench-control-host", "shared-pipeline", "shared-lifecycle-plc" })
        {
            if (catalog[name] is not JsonObject subset) continue;
            var args = subset["arguments"]!.AsArray();
            int index = Enumerable.Range(0, args.Count).FirstOrDefault(i => (string?)args[i] == "--filter", -1);
            if (index >= 0) terms.AddRange(((string)args[index + 1]!).Split('|'));
        }
        // Expand the superset rather than assume that a subset's future method names cannot contain "Operator".
        arguments[filter + 1] = string.Join('|', terms.Distinct(StringComparer.Ordinal));
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
        (profile == "nightly" && name != "foundation" || !plan.Mappings.ContainsKey(name));

    internal static string[] Profiles(string job, DerivationPlan plan) => plan.Mappings.ContainsKey("foundation") &&
        (job.StartsWith("foundation-shard-", StringComparison.Ordinal) || job is "foundation-sequential" or "foundation-coverage-proof") ? [] :
        CheckAllJobInputs.NightlyOnly(job) || job.EndsWith("-coverage-proof", StringComparison.Ordinal) ? ["nightly"] : plan.Mappings.Keys.Any(name => job.StartsWith(name + "-shard-", StringComparison.Ordinal)
        || job == name + "-sequential") ? ["nightly"] : ["merge", "nightly"];

    internal static void WriteInventory(JsonObject catalog, string output, string profile, DerivationPlan plan)
    {
        var rows = catalog.Select(pair => new
        {
            name = pair.Key, buildProfiles = new[] { "merge", "nightly" },
            processProfiles = plan.Mappings.ContainsKey(pair.Key) ? pair.Key == "foundation" ? [] : new[] { "nightly" } : new[] { "merge", "nightly" },
            fullProfiles = plan.Mappings.ContainsKey(pair.Key) && pair.Key != "foundation" ? new[] { "nightly" } : new[] { "merge", "nightly" },
            mergeDerivedFrom = plan.Mappings.GetValueOrDefault(pair.Key),
            fallbackReason = plan.Fallbacks.GetValueOrDefault(pair.Key),
            guiShards = pair.Key == "studio-gui" ? 3 : (int?)null,
            concurrentGuiShards = false
        });
        File.WriteAllText(Path.Combine(output, "profile-inventory.json"), JsonSerializer.Serialize(new { profile, suites = rows },
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    internal static void AddDerived(List<CheckJob> jobs, JsonObject catalog, string output, DerivationPlan plan, string profile = "merge")
    {
        foreach (var (name, source) in plan.Mappings.Where(pair => profile == "merge" || pair.Key == "foundation"))
        {
            var suite = catalog[name]!.AsObject();
            jobs.Add(new(name + "-gate", () => DerivedSuiteGates.Evaluate(name, suite, Path.Combine(output, source + ".trx"),
                output, source), [source + "-gate"]) { AllowFailedDependencies = true, Priority = 1 });
        }
    }

    internal static void AddNightlyProofs(List<CheckJob> jobs, JsonObject catalog, string output)
    {
        foreach (var (name, source) in Derived.Where(pair => pair.Key != "foundation"))
        {
            var suite = catalog[name]!.AsObject();
            jobs.Add(new(name + "-coverage-proof", () => DerivedSuiteGates.CompareSameRun(name, suite,
                Path.Combine(output, name + ".trx"), Path.Combine(output, source + ".trx"), Path.Combine(output, "same-run-proofs"), source),
                [name + "-gate", source + "-gate"]) { AllowFailedDependencies = true, Priority = 1 });
        }
    }

    internal static void AddStudio(List<CheckJob> jobs, string root, JsonObject catalog, string output, string dotnet, string profile,
        string? manifest, SuiteProcess? process = null, IReadOnlyDictionary<string, double>? history = null, Func<string, SuiteProcess>? processFor = null, string? api = null)
    {
        foreach (var name in StudioSuites)
        {
            var suite = catalog[name]!.AsObject();
            bool gui = name == "studio-gui";
            int count = gui ? 3 : 1;
            string directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            var (assignments, costs) = GuiPlan(root, name, count);
            string plan = Path.Combine(directory, "shard-plan.tsv");
            File.WriteAllLines(plan, assignments.Select(pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "\t" + pair.Value));
            File.WriteAllText(Path.Combine(directory, "shard-plan.json"), JsonSerializer.Serialize(new { assignments, costs,
                algorithm = "LPT by measured class duration", concurrent = false, reason = "Shown windows and Keyboard.FocusedElement share the desktop" }) + "\n");
            var unit = CheckAllBuild.Suite(suite);
            var budget = UnshardedBudget(name, count, history);
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
                            ["api"] = api, ["runner"] = "Studio RunShard", ["profileSelection"] = "full" }.ToJsonString(),
                            shardDirectory, name + ".trx", plan, (int)suite["maximumSkipped"]!) });
            }
            jobs.Add(new(name + "-gate", () =>
            {
                var result = SuiteSharding.Merge(name, suite, paths, output);
                var errors = new List<string>();
                if (result.ExitCode != 0) errors.Add(result.StandardError);
                // Three processes must not gain three times the former unsharded job's runtime allowance.
                if (StudioRuntimeError(TimeSpan.FromTicks(runtimeTicks), budget) is { } timeout) errors.Add(timeout);
                var summaryPath = Path.Combine(output, name + ".json");
                var summary = JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject();
                summary["profile"] = profile; summary["execution"] = "full";
                summary["releaseEligible"] = true;
                summary["runtimeSeconds"] = TimeSpan.FromTicks(runtimeTicks).TotalSeconds; summary["timeoutSeconds"] = budget.TotalSeconds;
                summary["errors"] = ReleaseEvidence.Strings(errors);
                File.WriteAllText(summaryPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                SuiteCosts.Record(name, directory, Path.Combine(root, "bin-build/check-timings"));
                return result with { ExitCode = errors.Count == 0 ? 0 : 1, StandardError = string.Join("\n", errors) };
            }, names.ToArray()) { AllowFailedDependencies = true, Priority = 1 });
            // Release evidence binds the full-suite aggregate alias to its registered gate.
            jobs.Add(new(name, () => new(0, name + ": registered gate passed\n", ""), [name + "-gate"]));
        }
    }

    // The former unsharded job's allowance, computed as before (3x its measured runtime, at least 300 s). Since the job now runs
    // as shards, its runtime is recorded under the shard names; their sum stands in for the unsharded runtime (maintainer
    // decision 2026-10-31), so a fresh history or the aggregate alias never falls back to the bare 300 s floor.
    internal static TimeSpan UnshardedBudget(string name, int count, IReadOnlyDictionary<string, double>? history)
    {
        history ??= new Dictionary<string, double>();
        double shards = Enumerable.Range(0, count).Sum(index => history.GetValueOrDefault(name + "-shard-" + index));
        return CheckScheduler.JobTimeout(new(name, () => new(0, "", ""), []) { EstimatedSeconds = Math.Max(history.GetValueOrDefault(name), shards) },
            new Dictionary<string, double>());
    }

    internal static string? StudioRuntimeError(TimeSpan runtime, TimeSpan budget) => runtime >= budget
        ? "Studio shards exceeded the original unsharded runtime budget: " + runtime.TotalSeconds + "s >= " + budget.TotalSeconds + "s" : null;

    internal static (Dictionary<string, int>, double[]) GuiPlan(string root, string name, int count)
    {
        var path = Path.Combine(root, "bin-build/check-timings", name + ".classes.json");
        if (!File.Exists(path)) path = Path.Combine(root, "bin-build/check-timings", name + "-false.classes.json");
        Dictionary<string, SuiteCosts.ClassCost> measured;
        try { measured = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, SuiteCosts.ClassCost>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { measured = new(); }
        var costs = measured
            .ToDictionary(pair => "C:" + pair.Key, pair => pair.Value is { Seconds: var seconds } && double.IsFinite(seconds) && seconds >= 0 ? seconds : 1, StringComparer.Ordinal);
        // Estimates only: absent, added or renamed classes never fail a gate. Unknown classes use ShardFramework's
        // deterministic class fallback, so all their cases still execute in exactly one of the three processes.
        if (name == "studio-gui")
        {
            costs.TryAdd("C:TiaOpenness.Gui.Tests.WorkbenchFontStyleTests", 76);
            costs.TryAdd("C:TiaOpenness.Gui.Tests.WorkbenchNarrowLayoutTests", 67);
        }
        return SuiteCosts.Balance(costs, count);
    }
}

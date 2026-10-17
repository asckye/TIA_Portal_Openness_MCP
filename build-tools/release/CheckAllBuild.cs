using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckBuild
{
    internal string Project { get; init; }
    internal string Variant { get; init; }
    internal string[] Properties { get; init; }
    internal CheckBuild(string project, string variant, string[] properties)
    {
        Project = project; Properties = properties;
        Variant = variant.Length != 0 ? variant : properties.Length == 0 ? "default" : "v-" + BuildOutputCache.Key([], properties.Order(StringComparer.Ordinal))[..12];
    }
}

internal static class CheckAllBuild
{
    internal static IEnumerable<CheckBuild> WithTestFixtures(IEnumerable<CheckBuild> builds)
    {
        var units = builds.ToList();
        // ReferenceOutputAssembly=false fixtures are compiled transitively, but
        // only traversal roots receive runtime projection receipts. Their tests
        // discover the ordinary fixture paths in a fresh checkout.
        if (units.Any(unit => Path.GetFileNameWithoutExtension(unit.Project) is "TiaMcp.FoundationHost.Tests" or "TiaMcp.WorkerChannel.Tests"))
            units.Add(new("tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj", "", []));
        if (units.Any(unit => Path.GetFileNameWithoutExtension(unit.Project) == "TiaMcp.FoundationHost.Tests"))
        {
            units.Add(new("tests/WorkerChannel/TiaMcp.EngineWorker.FaultFixture/TiaMcp.EngineWorker.FaultFixture.csproj", "", ["-p:TiaMcpEngineWorkerFaultFixture=true"]));
            units.Add(new("tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/TiaMcp.PlcWorker.FaultFixture.csproj", "", ["-p:TiaMcpPlcWorkerSdkFixture=true"]));
        }
        return units.GroupBy(unit => unit.Project + "|" + unit.Variant + "|" + string.Join(";", unit.Properties), StringComparer.Ordinal).Select(group => group.First());
    }

    internal static CheckBuild Suite(JsonObject suite)
    {
        var properties = DotnetSuites.BuildArguments(suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>())).ToArray();
        return new(suite["project"]!.GetValue<string>(), "", properties);
    }

    internal static string[] Arguments(string root, CheckBuild unit) => [.. unit.Properties,
        "-p:DirectoryBuildPropsPath=" + Path.Combine(root, "build-tools/release/CheckAll.Build.props"),
        "-p:DirectoryBuildTargetsPath=" + Path.Combine(root, "build-tools/release/CheckAll.Build.targets"),
        "-p:ImportDirectoryBuildProps=true", "-p:ImportDirectoryBuildTargets=true",
        "-p:CheckAllVariant=" + unit.Variant];

    internal static CommandResult Run(string root, IReadOnlyList<CheckBuild> units, string directory, string dotnet, string? api, string? nuget, int parallelism, bool cold)
    {
        Directory.CreateDirectory(directory);
        var taskAssembly = Path.Combine(Path.GetDirectoryName(typeof(CheckAllBuild).Assembly.Location)!, "TiaMcp.CheckBuild.dll");
        var verifier = Path.Combine(root, "bin-build/check-all/TiaMcp.CheckBuild.dll"); Directory.CreateDirectory(Path.GetDirectoryName(verifier)!);
        if (!File.Exists(verifier) || ReleaseRecords.HashFile(taskAssembly) != ReleaseRecords.HashFile(verifier)) File.Copy(taskAssembly, verifier, true);
        // Use the SDK's executable reference traversal for discovery and Build.
        // Target-time metadata updates must participate in Restore as well.
        var items = new XElement("ItemGroup", units.Distinct().Select(unit => new XElement("CheckProject",
            new XAttribute("Include", Path.Combine(root, unit.Project)), new XElement("AdditionalProperties",
                string.Join(";", Arguments(root, unit).Select(arg => Escape(arg[3..])))))));
        var prerequisites = new XElement("ItemGroup", new[] { "build-tools/native-call-weaver/NativeCallWeaver.csproj", "build-tools/release/check-build/TiaMcp.CheckBuild.csproj" }.Select(project =>
            new XElement("CheckPrerequisite", new XAttribute("Include", Path.Combine(root, project)), new XElement("AdditionalProperties",
                string.Join(";", Arguments(root, new(project, "prerequisite", [])).Select(arg => Escape(arg[3..])))))));
        var nodesPath = Path.Combine(directory, "build-nodes.props");
        var graph = new XElement("Project", items, prerequisites,
            new XElement("Import", new XAttribute("Project", nodesPath), new XAttribute("Condition", "Exists('" + nodesPath + "')")),
            new XElement("UsingTask", new XAttribute("TaskName", "TiaMcp.ReleaseTool.DiscoverCheckBuildProperties"), new XAttribute("AssemblyFile", verifier)),
            new XElement("UsingTask", new XAttribute("TaskName", "TiaMcp.ReleaseTool.AssignCheckBuildNodes"), new XAttribute("AssemblyFile", verifier)),
            new XElement("Target", new XAttribute("Name", "InitializeNodes"),
                new XElement("AssignCheckBuildNodes", new XAttribute("Projects", "@(CheckProject)"),
                    new XElement("Output", new XAttribute("TaskParameter", "Nodes"), new XAttribute("ItemName", "AssignedCheckProject"))),
                new XElement("AssignCheckBuildNodes", new XAttribute("Projects", "@(CheckPrerequisite)"),
                    new XElement("Output", new XAttribute("TaskParameter", "Nodes"), new XAttribute("ItemName", "AssignedCheckPrerequisite"))),
                new XElement("ItemGroup", new XElement("CheckProject", new XAttribute("Remove", "@(CheckProject)")), new XElement("CheckProject", new XAttribute("Include", "@(AssignedCheckProject)")),
                    new XElement("CheckPrerequisite", new XAttribute("Remove", "@(CheckPrerequisite)")), new XElement("CheckPrerequisite", new XAttribute("Include", "@(AssignedCheckPrerequisite)")))),
            new XElement("Target", new XAttribute("Name", "DiscoverProperties"), new XAttribute("DependsOnTargets", "InitializeNodes"),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckProject);@(CheckPrerequisite)"), new XAttribute("Targets", "GetCheckBuildGraph"), new XAttribute("BuildInParallel", "true"),
                    new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"), new XAttribute("ItemName", "DiscoveredCheckNode"))),
                new XElement("DiscoverCheckBuildProperties", new XAttribute("Projects", "@(DiscoveredCheckNode)"), new XAttribute("GraphFile", nodesPath))),
            new XElement("Target", new XAttribute("Name", "Restore"),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckGraphProject)"), new XAttribute("Targets", "Restore"), new XAttribute("BuildInParallel", "true"))),
            new XElement("Target", new XAttribute("Name", "CleanChecks"),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckProject)"), new XAttribute("Targets", "Clean"), new XAttribute("BuildInParallel", "true"))),
            new XElement("Target", new XAttribute("Name", "BuildPrerequisites"),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckPrerequisite)"), new XAttribute("Targets", "StageCheckPrerequisite"))),
            new XElement("Target", new XAttribute("Name", "BuildChecks"), new XAttribute("DependsOnTargets", cold ? "InitializeNodes;CleanChecks;BuildPrerequisites" : "InitializeNodes;BuildPrerequisites"),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckProject)"), new XAttribute("Targets", "Build"), new XAttribute("BuildInParallel", "true")),
                new XElement("MSBuild", new XAttribute("Projects", "@(CheckProject)"), new XAttribute("Targets", "GetCheckRuntime"),
                    new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"), new XAttribute("ItemName", "CheckRuntime"))),
                new XElement("WriteLinesToFile", new XAttribute("File", Path.Combine(directory, "runtime-paths.tsv")), new XAttribute("Overwrite", "true"),
                    new XAttribute("Lines", "@(CheckRuntime->'%(Identity)\t%(Variant)\t%(Source)\t%(Destination)\t%(Assembly)')"))));
        var path = Path.Combine(directory, "build.proj"); new XDocument(graph).Save(path);
        var arguments = new List<string> { "-m:" + parallelism, "-nodeReuse:false", "-v:minimal",
            "-p:Configuration=Release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaSharedAdapterPaths=false" };
        if (api != null) arguments.Add("-p:TiaPublicApiRoot=" + api);
        if (nuget != null) arguments.Add("-p:RestoreConfigFile=" + nuget);
        var environment = new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null,
            ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = Path.Combine(directory, "analyzer-warnings") };
        var results = new List<CommandResult>();
        CommandResult Step(string target, string log)
        {
            var result = ProcessRunner.Run(dotnet, new[] { "msbuild", path, "-t:" + target }.Concat(arguments), root, environment, Path.Combine(directory, log));
            results.Add(result); return result;
        }
        CommandResult Combined(int exitCode) => new(exitCode, string.Concat(results.Select(result => result.StandardOutput)), string.Concat(results.Select(result => result.StandardError)));
        if (Step("DiscoverProperties", "discover.log").ExitCode != 0) return Combined(results[^1].ExitCode);
        // NuGet assets can add implicit transitive ProjectReferences. Reevaluate
        // after Restore until the SDK's complete node/property graph is unchanged.
        // Separate invocations also refresh NuGet imports and node identities;
        // no cached pre-restore ProjectInstance is allowed into compilation.
        bool stable = false;
        for (int round = 1; round <= 4; round++)
        {
            var before = ReleaseRecords.HashFile(nodesPath);
            if (Step("Restore", "restore-" + round + ".log").ExitCode != 0) return Combined(results[^1].ExitCode);
            if (Step("DiscoverProperties", "discover-" + round + ".log").ExitCode != 0) return Combined(results[^1].ExitCode);
            if (before == ReleaseRecords.HashFile(nodesPath)) { stable = true; break; }
        }
        if (!stable) return new(1, Combined(1).StandardOutput, Combined(1).StandardError + "Check build graph did not stabilize after Restore; compilation was not started.\n");
        var build = Step("BuildChecks", "build.log");
        return Combined(build.ExitCode);
    }

    internal static string? TestAssembly(string root, CheckBuild unit, string? manifest, string? suite = null)
    {
        var project = Path.GetFullPath(Path.Combine(root, unit.Project));
        var row = File.Exists(manifest) ? File.ReadLines(manifest).Select(line => line.Split('\t')).FirstOrDefault(row => row.Length == 5 &&
            string.Equals(Path.GetFullPath(row[0]), project, StringComparison.OrdinalIgnoreCase) && row[1] == unit.Variant) : null;
        if (row is { Length: 5 } && !string.IsNullOrWhiteSpace(row[4])) return row[4];
        if (unit.Properties.Length != 0 || unit.Variant != "default" || manifest != null)
            throw new ReleaseException($"Suite {suite ?? unit.Project}: missing runtime manifest row ({project}, {unit.Variant}) in {manifest ?? "(no runtime manifest supplied)"}; refusing to fall back to another build.");
        return null;
    }

    private static string Escape(string value) => value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal);
}

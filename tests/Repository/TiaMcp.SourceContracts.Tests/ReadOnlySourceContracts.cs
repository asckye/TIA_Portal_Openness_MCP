using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ReadOnlySourceContracts
{
    private static readonly string Root = Repository.FindRoot(AppContext.BaseDirectory);
    private static readonly EngineSources Sources = new(Root);
    private static string Read(string path) => Repository.ReadSource(Path.Combine(Root, path));
    private static void Contains(string source, params string[] values) { foreach (var value in values) Assert.Contains(value, source, StringComparison.Ordinal); }
    private static void Excludes(string source, params string[] values) { foreach (var value in values) Assert.DoesNotContain(value, source, StringComparison.Ordinal); }
    private static void Count(string source, string value, int expected) => Assert.Equal(expected, Regex.Matches(source, Regex.Escape(value)).Count);

    [Fact]
    public void DiagnosticMembershipPreservesReadinessAndNoFixRouting()
    {
        var bootstrap = Sources.Member("Bootstrap", tool: false, owner: "SessionTools");
        var bootstrapGroup = Sources.Member("ReadBootstrapOpennessGroup", tool: false, owner: "SessionTools");
        var bootstrapPortal = Sources.Member("ReadBootstrapPortal", tool: false, owner: "SessionTools");
        var doctor = Sources.Member("Doctor", tool: false, owner: "McpServer");
        var doctorGroup = Sources.Member("ReadOpennessGroup", tool: false, owner: "McpServer");
        var selftest = Sources.Member("RunCapabilitySelfTest", tool: false, owner: "DiagnosticsTools");
        var logic = Sources.Member("Run", tool: false, owner: "CapabilitySelfTestLogic");
        Contains(bootstrap, "ReadBootstrapOpennessGroup()", "else if (OpennessReadiness.Ready)", "ReadBootstrapPortal()", "OpennessReadiness.Ready ? ReadBootstrapPortal()");
        Excludes(bootstrap, "Siemens.Openness.", "Siemens.Portal");
        Count(bootstrapGroup, "Siemens.Openness.IsUserInGroupNoFix()", 1);
        var bootstrapSource = Sources.TypeText("SessionTools");
        Assert.Matches(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static bool ReadBootstrapOpennessGroup", bootstrapSource);
        Assert.Matches(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static BootstrapPortal ReadBootstrapPortal", bootstrapSource);
        Contains(bootstrapPortal, "EngineServices.Get<IEngineeringSession>()");
        Contains(doctor, "if (!Runtime.OpennessReadiness.Ready)", "ReadPortalState()", "ReadOpennessGroup(fix: true)", "ReadOpennessGroup(fix: false)");
        Excludes(doctor, "Siemens.Openness.", "Siemens.Portal");
        var tokens = new CSharpLexer(doctor).Scan().Tokens;
        var pairs = MatchingPairs.Find(tokens);
        var cursor = doctor.IndexOf("if (!Runtime.OpennessReadiness.Ready)", StringComparison.Ordinal);
        var branches = new List<string>();
        foreach (var marker in new[] { "if (!Runtime.OpennessReadiness.Ready)", "else if (fix)", "else" })
        {
            Assert.StartsWith(marker, doctor[cursor..]);
            var opening = Enumerable.Range(0, tokens.Count).First(i => tokens[i].OffsetStart >= cursor + marker.Length && tokens[i].Value == "{");
            var closing = pairs[opening];
            branches.Add(doctor[tokens[opening].OffsetStart..tokens[closing].OffsetEnd]);
            cursor = tokens[closing].OffsetEnd;
            while (cursor < doctor.Length && char.IsWhiteSpace(doctor[cursor])) cursor++;
        }
        Contains(branches[0], "Runtime.OpennessReadiness.GroupOk == true");
        Excludes(branches[0], "ReadOpennessGroup(", "ReadPortalState(");
        Contains(branches[1], "await ReadOpennessGroup(fix: true)"); Contains(branches[2], "await ReadOpennessGroup(fix: false)");
        Count(doctor, "ReadOpennessGroup(", 2); Count(doctor, "ReadPortalState()", 1);
        Contains(EngineSources.BlockAfter(doctor, "if (Runtime.OpennessReadiness.Ready)"), "ReadPortalState()");
        Contains(doctorGroup, "HostToolServices.Observe(fix ? \"group.fix\" : \"group\", new JsonObject())"); Excludes(doctorGroup, "Siemens.Openness.");
        var doctorSource = Read("src/Shared/Host/McpServer.Doctor.cs");
        Assert.Matches(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static \(bool Connected, string\? ProjectName\) ReadPortalState", doctorSource);
        Contains(EngineSources.BlockAfter(doctorSource, "private static (bool Connected, string? ProjectName) ReadPortalState()"), "HostToolServices.Observe(\"session.GetState\", new JsonObject())");
        Excludes(doctorSource, "EngineServices.", "Siemens.Portal");
        Assert.Matches(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static Task<bool> ReadOpennessGroup", doctorSource);
        Contains(Read("src/FoundationHost/EngineWorkerClient.cs"), "channel!.CallAsync(\"engine.observe\"");
        Contains(Read("src/Engine/WorkerMode/EngineWorkerHost.cs"), "request.Method == \"engine.observe\"");
        var observations = Sources.Member("Read", tool: false, owner: "HostObservations");
        Contains(EngineSources.BlockAfter(observations, "if (operation == \"group\" || operation == \"group.fix\")"), "Assembly.GetType(\"TiaMcpServer.Siemens.Openness\")", "GetMethod(operation == \"group\" ? \"IsUserInGroupNoFix\" : \"IsUserInGroup\")", ".Invoke(null, null)", "task.GetAwaiter().GetResult()");
        Excludes(observations, "Siemens.Openness.", "global::Siemens.");
        Count(selftest, "Siemens.Openness.IsUserInGroupNoFix", 1);
        Contains(selftest, "CapabilitySelfTestLogic.Run(Siemens.Openness.IsUserInGroupNoFix,", "_session.GetState, _session.ConnectPortal, _session.ValidateAutomationContext, _session.GetProjectTree", "bool connectIfNeeded = false");
        Count(logic, "opennessOk = group();", 1);
        foreach (var text in new[] { selftest, logic }) Assert.DoesNotMatch(@"(?:IsUserInGroup|AddUserToGroupAsync|EnsureOpennessUserGroup)\s*\(", text);
        Assert.Matches(@"catch\s*(?:/\*\s*swallow\(env-probe\):\s*[^*]+\*/\s*)?\{\s*env\.OpennessGroupOk = false;\s*\}", bootstrap);
        Contains(bootstrap, "nextTool = \"EnsureOpennessUserGroup\";");
        Contains(logic, "catch (Exception ex)", "\"fail\", ex.Message", "if (!isConnected && connectIfNeeded)", "isConnected = connect();");
        Contains(Sources.Member("EnsureOpennessUserGroup", tool: false), "await Siemens.Openness.IsUserInGroup()");
        Assert.Matches(@"\A\s*\{\s*return Api\.Global\.Openness\(\)\.IsUserInGroup\(\);\s*\}\s*\z", Sources.Member("IsUserInGroupNoFix", bodyOnly: true));
    }
    [Fact]
    public void DiagnosticStartupKeepsWorkerAvailableAndNonWorkerExitExplicit()
    {
        var source = Sources.Member("Main");
        Contains(source, "OpennessReadiness.Ready", "OpennessReadiness.MarkUnavailable", "if (options.EngineWorker)", "Worker.EngineWorkerHost.Run();", "Engine usage:", "Environment.ExitCode = 64;", "User is not in the required group 'Siemens TIA Openness'. Exiting.", "Environment.ExitCode = 2;", "OpennessReadiness.Guidance(false)");
        Assert.DoesNotMatch(@"Openness\.IsUserInGroup\s*\(", source);
    }
    [Fact]
    public void PromptContainersAreExplicitCompleteAndFatalOnLoadFailure()
    {
        var program = Read("src/FoundationHost/EngineReleaseHost.cs");
        var registration = Sources.TypeText("McpPromptRegistration");
        var pipeline = Read("src/EngineHost/EngineHostPipeline.cs");
        Excludes(Sources.AllText(), "WithPromptsFromAssembly(", "WithToolsFromAssembly failed:");
        Count(program, "pipeline.RegisterHandlers(mcp);", 1); Count(program, "probe.RegisterHandlers(mcp);", 1);
        Contains(pipeline, "McpPromptRegistration.Configure(builder)");
        Excludes(registration, "catch", "GetTypes("); Contains(registration, "builder.WithPrompts(new[] {");
        Contains(program, "WithStdioServerTransport()", "WithHttpTransport(", "WorkerShutdown.StopAll");
        Contains(Sources.Member("Main"), "Environment.ExitCode = 70;", "throw;");
        var containers = new List<string>(); var promptCount = 0;
        foreach (var text in Sources.Sources.Values)
        {
            var source = Regex.Replace(text, @"^\s*//.*$", "", RegexOptions.Multiline);
            var found = Regex.Matches(source, @"\[McpServerPromptType(?:Attribute)?\]\s*public\s+(?:static\s+)?class\s+(\w+)");
            Assert.Equal(Regex.Matches(source, @"\[McpServerPromptType(?:Attribute)?\]").Count, found.Count);
            containers.AddRange(found.Select(m => m.Groups[1].Value));
            promptCount += Regex.Matches(source, @"\[McpServerPrompt\(").Count;
        }
        var registered = Regex.Matches(registration, @"typeof\((\w+)\)").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(containers.Order(), registered.Order()); Assert.Equal(registered.Length, registered.Distinct().Count()); Assert.Equal(30, promptCount);
    }
    [Fact]
    public void PromptRecipesUseV4AdmissionApprovalAndRegisteredToolNames()
    {
        var source = Sources.Sources.Single(p => Path.GetFileName(p.Key) == "McpPrompts.cs").Value;
        Contains(source, "private static string WithV4Rules", "ListPortalProcessProjects", "ConnectPortal with that row's processId, then AttachOpenProject", "schemaVersion 4 envelope", "OUTCOME_UNKNOWN", "D1 native behavior remains current", "L5 is NOT RUN", "Do not save or close a project unless the user explicitly requested that action", "Workbench approval before dispatch", "data/logs/audit", "tia audit verify");
        Excludes(source, "EnsureOpennessUserGroup", "fbBlockJson", "designJson", "ConnectPortal — attach to a running", "meta.success / meta.operationSuccess", "SaveProject — save any pending changes first", "ConnectProject");
        string[] tools = ["ListPortalProcessProjects", "ConnectPortal", "AttachOpenProject", "GetSessionState", "OpenProject", "GetProjectTree", "CloseProject", "SaveProject", "DisconnectPortal", "CreateProject", "SearchHardwareCatalog", "CreateHardwareDevice", "CreateHardwareCatalogDevice", "ConnectDeviceNodesToProfinetSubnet", "GetSoftwareTree", "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments", "BuildAndImportPlcArtifact", "CompilePlcDiagnostics", "EnsureUnifiedHmiConnection", "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreenItem", "ApplyUnifiedHmiScreenDesign", "EnsureUnifiedHmiDynamization", "SetUnifiedHmiRuntimeState", "BuildUnifiedHmiLayoutDesign", "ApplyUnifiedHmiLayout", "BuildClassicHmiScreen", "WriteClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackageFiles", "ImportHmiScreen", "ImportHmiTagTable", "PlanOnlineReadOnlyMonitoring", "ProbePlcMonitorOnlineCapabilities", "ListPlcWatchTables", "GetPlcWatchTableCurrentValuesReadOnly", "RunOfflineReleaseValidationSuite", "BuildReleaseDiagnosticReport", "BuildReleaseRunbook", "GetToolUsage"];
        var names = Directory.EnumerateFiles(Path.Combine(Root, "manifest/contracts/v4/baseline"), "*.json").SelectMany(p => JsonNode.Parse(File.ReadAllText(p))!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())).ToHashSet();
        foreach (var tool in tools) Assert.Contains(tool, names);
    }
    [Fact]
    public void SupplementaryReadsUseExplicitSourcesTypedAccessAndReadOnlyDispatch()
    {
        var files = XDocument.Parse(Read("src/Adapters/build/Adapter.Sources.props")).Descendants("AdapterSource").Select(n => Path.GetFullPath(Path.Combine(Root, "src", n.Attribute("Include")!.Value.Replace("$(AdapterSourceRoot)/", "", StringComparison.Ordinal)))).ToList();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "src/Shared/shared-native"), "*.props").Order(StringComparer.Ordinal))
            foreach (var node in XDocument.Load(path).Descendants("TiaSharedNativePrimitive")) files.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, node.Attribute("Include")!.Value.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.Ordinal))));
        Assert.Equal(files.Count, files.Distinct().Count()); foreach (var path in files) Assert.True(File.Exists(path), path);
        foreach (var folder in new[] { "Native", "Policy" }) foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "src/Adapters", folder), "*.cs", SearchOption.AllDirectories)) Assert.Contains(Path.GetFullPath(path), files);
        var code = string.Join("\n", files.Where(p => Path.GetFileName(p) != "OpennessAdapter.cs").Select(Repository.ReadSource));
        var operations = Regex.Matches(Read("src/PlcWorker/WorkerOperations.cs"), "\"([A-Z][A-Za-z]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var methods = Regex.Matches(code, @"public\s+(?:[\w<>?\[\],]+\s+)+([A-Za-z]+)\s*\(").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Empty(operations.Except(methods));
        var native = Read("src/Adapters/Native/Plc/PlcSupplementaryRead.cs");
        Count(native, "PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey", 2); Count(native, "PlcSupplementaryReadPolicy.ReadSnapshot(", 2);
        Excludes(native, ".ForceTables", "GetType().Get", "GetAttributeInfos", "GetAttribute(", "SetAttribute(", "IEngineeringObject", "NativeCalls.Attribute(", "NativeCalls.SetAttribute(");
        Contains(native, "()=>item.OfSystemLibElement,()=>item.OfSystemLibVersion", "PlcSupplementaryReadPolicy.TechnologyMetadata(NativeCalls.Name(item)");
        Assert.DoesNotMatch(@"item\.OfSystemLib(?:Element|Version)\s*=(?!=)", native);
        Contains(Read("src/PlcWorker/FoundationWorkerDispatcher.cs"), "WorkerOperations.IsReadOnly(name)");
        var readOnly = Regex.Match(Read("src/PlcWorker/WorkerOperations.cs"), @"IsReadOnly\(string name\)\s*=>(.*?);", RegexOptions.Singleline).Groups[1].Value;
        foreach (var name in new[] { "ReadWatchTableNames", "ReadTechnologyObjects", "ReadState", "ReadPortalProcessProjects", "ReadPortalConnectReadiness" }) Contains(readOnly, $"name==\"{name}\"");
    }
    [Theory]
    [InlineData("V14Sp1", false, false)] [InlineData("V15_1", true, false)] [InlineData("V16", true, false)] [InlineData("V17", true, false)]
    [InlineData("V18", true, false)] [InlineData("V19", true, true)] [InlineData("V20", true, true)] [InlineData("V21", true, true)]
    public void SupplementaryReleaseSymbols(string release, bool watch, bool technology)
    {
        var project = Directory.EnumerateFiles(Path.Combine(Root, "src/Adapters", release), "*.csproj").Single();
        var constants = DefineConstants(project).Split(';');
        Assert.Equal(watch, constants.Contains("PLC_WATCH_READ")); Assert.Equal(technology, constants.Contains("PLC_TECH_GROUP_READ"));
    }

    // The same MSBuild evaluation as the release tool's TiaFeatures.Evaluate, without referencing the release tool project:
    // building that reference rewrote the running release tool's output during the release pipeline (locked BuildCommon.dll).
    private static string DefineConstants(string project)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Root };
        foreach (var argument in new[] { "msbuild", project, "-nologo", "-getProperty:DefineConstants", "-p:Configuration=Release" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException(project + " evaluation timed out"); }
        Assert.True(process.ExitCode == 0, project + " evaluation failed: " + errors.Result);
        return output.Trim();
    }
}

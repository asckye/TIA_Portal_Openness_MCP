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
        Contains(Sources.Member("EnsureOpennessUserGroup", tool: false, owner: "ProjectLifecycleSessionTools"), "_service.EnsureGroup()");
        var membership = Sources.Member("EnsureMembership", tool: false, owner: "OpennessGroup");
        Contains(membership, "Api.Global.Openness().IsUserInGroup()", "await Api.Global.Openness().AddUserToGroupAsync()");
        Count(membership, "Api.Global.Openness().IsUserInGroup()", 1);
        Count(membership, "Api.Global.Openness().AddUserToGroupAsync()", 1);
        Assert.Matches(@"\A\s*\{\s*return Api\.Global\.Openness\(\)\.IsUserInGroup\(\);\s*\}\s*\z", Sources.Member("IsUserInGroupNoFix", bodyOnly: true));
    }
    [Fact]
    public void DiagnosticStartupKeepsWorkerAvailableAndNonWorkerExitExplicit()
    {
        // P8-04l: the engine entry point (src/Engine/Program.cs) is deleted; its engine-worker startup branch has no subject.
        // The host entry refuses the retired engine options first and keeps explicit argument and bundle exit codes.
        Assert.False(File.Exists(Path.Combine(Root, "src/Engine/Program.cs")));
        var source = Read("src/FoundationHost/Program.cs");
        Assert.True(source.IndexOf("HostOptions.RejectEngineOptions(args, AppContext.BaseDirectory);", StringComparison.Ordinal) is >= 0 and var reject
            && reject < source.IndexOf("HostOptions.Parse(args, AppContext.BaseDirectory)", StringComparison.Ordinal));
        Contains(source, "return 64;", "return 70;", "return 2;");
        Excludes(source, "EngineWorkerHost", "--engine-worker");
        Assert.DoesNotMatch(@"Openness\.IsUserInGroup\s*\(", source);
    }
    [Fact]
    public void PromptContainersAreExplicitCompleteAndFatalOnLoadFailure()
    {
        var program = Read("src/FoundationHost/EngineReleaseHost.cs");
        var registration = Sources.TypeText("McpPromptRegistration");
        var pipeline = Read("src/EngineHost/EngineHostPipeline.cs");
        Excludes(Sources.AllText(), "WithPromptsFromAssembly(", "WithToolsFromAssembly failed:");
        // P8-stab-3: HTTP registers the same handlers by release key before any session catalog exists.
        Count(program, "pipeline.RegisterHandlers(mcp);", 1); Count(program, "EngineHostPipeline.RegisterHandlers(mcp, options.ReleaseKey);", 1);
        Contains(pipeline, "McpPromptRegistration.Configure(builder)");
        Excludes(registration, "catch", "GetTypes("); Contains(registration, "builder.WithPrompts(new[] {");
        Contains(program, "WithStdioServerTransport()", "WithHttpTransport(", "WorkerShutdown.StopAll");
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
        var entry = Sources.Sources.Single(p => Path.GetFileName(p.Key) == "McpPrompts.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "src/EngineHost/ModelContextProtocol/McpPrompts.cs")), entry.Key);
        var source = entry.Value;
        Contains(source, "private static string WithV4Rules", "ListPortalProcessProjects", "ConnectPortal with that row's processId, then AttachOpenProject", "schemaVersion 4 envelope", "OUTCOME_UNKNOWN", "D1 native behavior remains current", "L5 is NOT RUN", "Do not save or close a project unless the user explicitly requested that action", "Workbench approval before dispatch", "data/logs/audit", "tia audit verify");
        Excludes(source, "EnsureOpennessUserGroup", "fbBlockJson", "designJson", "ConnectPortal — attach to a running", "meta.success / meta.operationSuccess", "SaveProject — save any pending changes first", "ConnectProject");
        string[] tools = ["ListPortalProcessProjects", "ConnectPortal", "AttachOpenProject", "GetSessionState", "OpenProject", "GetProjectTree", "CloseProject", "SaveProject", "DisconnectPortal", "CreateProject", "SearchHardwareCatalog", "CreateHardwareDevice", "CreateHardwareCatalogDevice", "ConnectDeviceNodesToProfinetSubnet", "GetSoftwareTree", "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments", "BuildAndImportPlcArtifact", "CompilePlcDiagnostics", "EnsureUnifiedHmiConnection", "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreenItem", "ApplyUnifiedHmiScreenDesign", "EnsureUnifiedHmiDynamization", "SetUnifiedHmiRuntimeState", "BuildUnifiedHmiLayoutDesign", "ApplyUnifiedHmiLayout", "BuildClassicHmiScreen", "WriteClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackageFiles", "ImportHmiScreen", "ImportHmiTagTable", "PlanOnlineReadOnlyMonitoring", "ProbePlcMonitorOnlineCapabilities", "ListPlcWatchTables", "GetPlcWatchTableCurrentValuesReadOnly", "RunOfflineReleaseValidationSuite", "BuildReleaseDiagnosticReport", "BuildReleaseRunbook", "GetToolUsage"];
        var names = Directory.EnumerateFiles(Path.Combine(Root, "manifest/contracts/v4/baseline"), "*.json").SelectMany(p => JsonNode.Parse(File.ReadAllText(p))!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())).ToHashSet();
        foreach (var tool in tools) Assert.Contains(tool, names);
    }
    [Fact]
    public void OnlineTransitionsResolveThePlcWithoutTheOfflineExportPrecondition()
    {
        // GoOffline acts on an online PLC and needs no configuration; GoOnline and DownloadPlc reach R/H systems. The write
        // resolution runs the offline-export precondition (exactly Offline, standard target only), so none of them may use it.
        var adapter = Read("src/Adapters/Native/Online/OnlineDownloadAdapter.cs");
        Excludes(adapter, "ResolvePlc(softwarePath, true)");
        foreach (var entry in new[] { "public OnlineStateResult GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface, bool trustDeviceCertificate",
            "public HmiMessage GoOffline(string softwarePath)", "public OnlineDownloadResult DownloadToPlc(OnlineRequest request)" })
            Contains(EngineSources.BlockAfter(adapter, entry), "_session.ResolvePlc(softwarePath, false)");
        var download = EngineSources.BlockAfter(adapter, "public OnlineDownloadResult DownloadToPlc(OnlineRequest request)");
        Contains(download, "RequireDownloadOffline(softwarePath, plcSoftware, rhTarget)", "rh.Configuration", "downloadProvider!.Download(downloadConfig, preDelegate, postDelegate, options)");
        Excludes(download, "GetMethods(", ".Invoke(downloadProvider");
        Contains(EngineSources.BlockAfter(adapter, "public HmiMessage GoOffline(string softwarePath)"), "OnlineTransitionPolicy.GoOfflineOutcome(softwarePath, before, after)");
        Excludes(adapter, "AttachOnlineLegitimationHandler(provider.Configuration, password, meta, true)", "AttachOnlineLegitimationHandler(providerConfiguration, password, meta, true)");
        Excludes(Read("src/Adapters/Native/Online/OnlineDataAdapter.cs"), "TlsSelectionToApply(true,");
    }

    // P8-81c (API audit 2026-10-19, session family): each fix keeps its exact shape in the adapter, Studio and host sources.
    [Fact]
    public void SessionAuditFixesKeepTheirExactShape()
    {
        var engine = Read("src/Adapters/Native/Session/PlcFoundationEngine.cs");
        var guards = Read("src/Adapters/Native/Session/PortalSessionGuards.cs");
        var lifecycle = Read("src/Adapters/Native/Session/ProjectLifecycleWorker.cs");
        // F5: the primary-project precheck runs before the native Open/Create.
        foreach (var entry in new[] { "public PlcMutationResult OpenProject(", "public PlcMutationResult CreateProject(" })
        {
            var body = EngineSources.BlockAfter(engine, entry);
            Assert.True(body.IndexOf("RequireNoOpenProject(p, ", StringComparison.Ordinal) is >= 0 and var check
                && check < body.IndexOf("if (!dryRun)", StringComparison.Ordinal), entry + " prechecks open projects before the native call");
        }
        Contains(EngineSources.BlockAfter(guards, "private void RequireNoOpenProject(TiaPortal connection, string operation)"), "connection.Projects.Any()", "connection.LocalSessions.Any()");
        // F2: credentials become a SecureString UmacDelegate (and the V17+ Authentication answer) for the one call; no plain-text echo.
        Contains(guards, "secret = EngineeringCredentialRules.ToSecureString(password!);", "credentials.SetPassword(secret);",
            "internal UmacDelegate? Delegate => secret == null ? null : new UmacDelegate(Fill);", "portal.Authentication += handler;",
            "portal.Authentication -= handler", "e.AuthenticationTypeProvider = AuthenticationTypeProvider.Credentials;", "public void Dispose() => secret?.Dispose();");
        Contains(engine, "connection.Projects.Open(input, login.Delegate)", "using var login = UmacCredentialsFor(umacUserName, umacPassword, umacUserType, \"umacUserName\");");
        Contains(lifecycle, "connection.Projects.Retrieve(file, directory, login.Delegate)", "connection.Projects.RetrieveWithUpgrade(file, directory, login.Delegate)");
        Excludes(guards + engine + lifecycle, "WriteLine(password", "umacPassword +", "+ umacPassword", "password +", "UmacPassword +");
        // F17: confirmations are refused by default, notifications logged, Disposed marks the session lost.
        var confirmation = EngineSources.BlockAfter(guards, "private void OnPortalConfirmation(object? sender, ConfirmationEventArgs e)");
        Contains(confirmation, "PortalDialogPolicy.RefusingChoice(offered)", "e.IsHandled = true;", "[\"answer\"] = refusal ?? \"unanswered\"");
        Excludes(confirmation, "ConfirmationResult.Yes", "ConfirmationResult.Ok", "ConfirmationResult.YesToAll");
        Contains(EngineSources.BlockAfter(guards, "private void OnPortalDisposed(object? sender, EventArgs e)"), "portalLost = true;");
        Contains(engine, "RequirePortalAlive();", "ObservePortal(portal);");
        Contains(lifecycle, "ObservePortal(portal);", "workerStartedPortal = true;");
        // F18: one targeted lookup, never an enumeration of every process for attach.
        Contains(guards, "TiaPortal.GetProcess(processId, ProcessLookupTimeout)");
        Excludes(EngineSources.BlockAfter(engine, "public PlcConnectionResult Attach(int processId)"), "GetProcesses()");
        // F13: SaveAs is refused for a non-Project instead of reporting success.
        Contains(lifecycle, "var single = project as Project ?? throw new AdapterPreconditionException(");
        Excludes(lifecycle, "(project as Project)?.SaveAs(directory);");
        // F14: ShowInEditor only with a user interface.
        Contains(Read("src/Adapters/Native/Engineering/EngineeringServices.cs"), "if (mode != TiaPortalMode.WithUserInterface)");
        // F4 round 2: a failed CloseAndCommit is never mapped to a refusal; its outcome stays unknown.
        Contains(Read("src/Adapters/Native/Session/ProjectMultiuserAdapter.cs"), "catch (Exception error) when (action != \"commit\" && OpennessRefusalMapping.Refusal(error, out _) != null)");
        // F19: project-kind aliases follow PROJECT_BASE / PROJECT_MULTIUSER, never the Safety define.
        foreach (var file in new[] { "src/Adapters/Native/Session/PlcFoundationEngine.cs", "src/Adapters/Native/Session/SessionCandidateAdapter.cs",
            "src/Adapters/Native/Session/SaveCloseAdapter.cs", "src/Adapters/Native/Engineering/EngineeringServices.cs" })
        {
            var source = Read(file);
            Contains(source, "#if PROJECT_BASE\nusing EngineeringProject = Siemens.Engineering.ProjectBase;");
            Excludes(source, "#if PLC_SAFETY\nusing EngineeringProject", "#if PLC_SAFETY\n                localSession", "#if PLC_SAFETY\n            localSession");
        }
        // F15: the exclusive-access text names the running tool.
        foreach (var file in new[] { "src/Shared/shared-native/WorkerSessionCallbacks.cs", "src/Adapters/Native/Hardware/HardwareNetworkPrimitives.cs", "src/Adapters/Native/Hardware/HardwareAddressingSteps.cs" })
        { var source = Read(file); Excludes(source, "MCP: verifying a precise HMI operation"); Contains(source, "ExclusiveAccess(ExclusiveAccessText())"); }
        // F3: the V20/V21 Studio bridge owns an MTA thread; the guard follows the release.
        Contains(Read("src/Studio/Bridge/Program.cs"), "owner.SetApartmentState(System.Threading.ApartmentState.MTA);", "return key == \"20\" || key == \"21\";");
        Contains(Read("src/Adapters/Policy/StudioThreadGuard.cs"), "apartment = PlcLifecyclePolicy.OwnerApartment(releaseKey);");
        // F10-F12: Studio attaches only to an exact process, never starts TIA after a denial, keeps ownership, cleans its source.
        var studio = Read("src/Adapters/Native/Studio/OpennessSession.cs");
        Contains(studio, "if (processes.Count > 1)", "_portal = AttachExactly(processes[0]);", "catch (EngineeringSecurityException denied)",
            "var primary = projects.Where(p => p.IsPrimary).ToList();", "if (_portal != null && _startedPortal && _project != null)", "string failed = DeleteTemporarySource(source);");
        Excludes(studio, "TryAttach()", "_portal.Projects.SafeEnumerate().FirstOrDefault();", "source.GenerateBlocksFromSource();\n            source.Delete();");
    }

    [Fact]
    public void DeviceWalksShareOneEnumerationIncludingTheUngroupedDevicesGroup()
    {
        // devices-and-hardware skill: devices sit in Project.Devices, the nested DeviceGroups and UngroupedDevicesGroup.Devices.
        var walk = Read("src/Adapters/Native/Hardware/ProjectDeviceWalk.cs");
        Contains(walk, "internal static IEnumerable<Located> Walk(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped,",
            "Group(group, Name(group, onError), 0, onError)", "Devices(ungrouped, onError)", "new Located(device, ungroupedName, true)",
            "internal const string UngroupedSegment = \"$ungrouped\";", "root.Concat(ungroupedDevices)");
        // One source file, linked into every assembly that walks devices: adapter (all eight) and Studio (all eight); the engine is deleted (P8-04l).
        Contains(Read("src/Adapters/build/Adapter.Sources.props"), "Adapters/Native/Hardware/ProjectDeviceWalk.cs");
        Contains(Read("src/Studio/Openness/Studio.Common.props"), "Adapters/Native/Hardware/ProjectDeviceWalk.cs");
        foreach (var engineProject in new[] { "src/Engine/TiaMcp.Engine.V20.csproj", "src/Engine/TiaMcp.Engine.V21.csproj" })
            Assert.False(File.Exists(Path.Combine(Root, engineProject)));
        const string Uses = "ProjectDeviceWalk.";
        var location = Read("src/Adapters/Native/Hardware/HardwareObjectLocation.cs");
        Contains(location, "ProjectDeviceWalk.FindDevice(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, devicePath)",
            "ProjectDeviceWalk.Devices(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup)",
            "ProjectDeviceWalk.FindItem(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, deviceItemPath)");
        Excludes(location, "EnumerateGroupDevices", "GetDeviceItemFromDevice");
        var devices = Read("src/Adapters/Native/Hardware/HardwareDevices.cs");
        Contains(EngineSources.BlockAfter(devices, "public List<Device> HardwareReadDevicesRaw(string regexName = \"\")"), "EnumerateAllDevices()");
        Contains(EngineSources.BlockAfter(devices, "private IEnumerable<SoftwareContainer> HardwareEnumerateSoftwareContainers()"), "EnumerateAllDevices()");
        var network = Read("src/Adapters/Native/Hardware/HardwareNetworkLegacy.cs");
        Contains(EngineSources.BlockAfter(network, "public Dictionary<string, object?> HardwareGetProjectTopology()"), "ProjectDeviceWalk.Walk(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup)");
        Contains(EngineSources.BlockAfter(network, "private List<string> HardwareBuildSubnetReadbackLines(string subnetName)"), "EnumerateAllDevices()");
        Excludes(network, "foreach (var device in project.Devices)", "foreach (Device device in project!.Devices)", "Devices placed inside device groups are not enumerated here");
        Contains(Read("src/Adapters/Native/Hardware/HardwareAddressingSteps.cs"), "ProjectDeviceWalk.UngroupedSegment", "EnumerateAllDevices().ToArray()");
        Contains(Read("src/Adapters/Native/Hardware/HardwareAml.cs"), "meta[\"deviceCountBefore\"] = EnumerateAllDevices().Count();");
        Contains(Read("src/Adapters/Native/Session/ProjectAutomationContext.cs"), Uses + "Walk(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup)");
        var services = Read("src/Adapters/Native/Engineering/EngineeringServices.cs");
        Contains(services, Uses + "Devices(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup)", Uses + "UngroupedSegment");
        Excludes(services, "EnumerateGroupDevices(");
        var navigator = Read("src/Adapters/Native/Studio/PlcNavigator.cs");
        Contains(navigator, Uses + "Walk(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, _ => { })");
        Excludes(navigator, "project.Devices.SafeEnumerate()");
        var engine = Read("src/Engine/Siemens/Portal/Portal.Helpers.cs");
        Contains(engine, Uses + "FindDevice(_project.Devices, _project.DeviceGroups, _project.UngroupedDevicesGroup, devicePath)",
            Uses + "Devices(_project.Devices, _project.DeviceGroups, _project.UngroupedDevicesGroup)",
            Uses + "FindItem(_project.Devices, _project.DeviceGroups, _project.UngroupedDevicesGroup, deviceItemPath)");
        Contains(Read("src/Engine/Siemens/Portal/Portal.SessionResolvers.cs"), Uses + "UngroupedSegment");
    }

    [Fact]
    public void DocumentExchangeFindsPlcsInGroupedAndUngroupedDevices()
    {
        // The document export/import PLC discovery walked the root devices and user groups only.
        var discovery = EngineSources.BlockAfter(Read("src/Adapters/Native/Plc/PlcB5DocumentExchange.cs"), "private List<PlcSoftware> AllDocumentPlcs()");
        Contains(discovery, "ProjectDeviceWalk.Walk(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, _ => { })",
            "Collect(located.Device.DeviceItems)");
        Excludes(discovery, "WalkDevices(", "WalkGroups(", "foreach (var device in devices)");
    }

    [Fact]
    public void UnifiedHmiPlcFamilyFallbackFindsGroupedAndUngroupedDevices()
    {
        // The PLC-family fallback looked up the PLC device among the root devices only.
        var fallback = EngineSources.BlockAfter(Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.cs"), "private string TryInferPlcFamilyFromProjectDevices(string plcSoftwarePath)");
        Contains(fallback, "ProjectDeviceWalk.Walk(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, _ => { })",
            "var device = located.Device;");
        Excludes(fallback, "_session.CurrentProject.Devices", "foreach (var device in");
    }

    [Fact]
    public void EnginePlcDiscoveryFindsGroupedAndUngroupedDevices()
    {
        // V20/V21 engine (the default there until P8-04j2): GetAllPlcSoftware walked the root devices and user groups only.
        var discovery = EngineSources.BlockAfter(Read("src/Engine/Siemens/Portal/Portal.Software.cs"), "public List<PlcSoftware> GetAllPlcSoftware()");
        Contains(discovery, "ProjectDeviceWalk.Walk(_project.Devices, _project.DeviceGroups, _project.UngroupedDevicesGroup, _ => { })",
            "Collect(located.Device.DeviceItems)");
        Excludes(discovery, "WalkDevices(", "WalkGroups(");
    }

    [Fact]
    public void EngineExactSoftwareLookupFindsGroupedAndUngroupedDevices()
    {
        // The exact container enumeration and the direct lookup's first level missed UngroupedDevicesGroup.
        var source = Read("src/Engine/Siemens/Portal/Portal.SoftwareLookup.cs");
        var enumeration = EngineSources.BlockAfter(source, "private IEnumerable<SoftwareContainer> EnumerateSoftwareContainersForExactLookup()");
        Contains(enumeration, "foreach (var device in EnumerateAllDevices())", "if (++count > 100000)", "foreach (var sc in Items(device.DeviceItems)) yield return sc;");
        Excludes(enumeration, "new Stack<DeviceUserGroup>(_project.DeviceGroups)", "Devices(_project.Devices)");
        var lookup = EngineSources.BlockAfter(source, "private SoftwareContainer? ResolveSoftwareLookup(string name, bool plcOnly)");
        Contains(lookup, "var ungrouped = _project.UngroupedDevicesGroup;",
            ".Concat(ungrouped == null ? Enumerable.Empty<object>() : ungrouped.Devices.Cast<object>())", "EnumerateSoftwareContainersForExactLookup()");
    }

    [Fact]
    public void VciWorkspaceWalkPutsUngroupedDevicesUnderTheFixedFolder()
    {
        // Decided 2026-10-19: UngroupedDevicesGroup devices under 'UngroupedDevices'; a root device or group owning that folder is
        // refused (PRECONDITION_FAILED); a native VCI refusal of an ungrouped device is a failure and is never walked past.
        var adapter = Read("src/Adapters/Native/Vci/VersionControlAdapter.cs");
        var children = EngineSources.BlockAfter(adapter, "private static List<VcNode> TypedChildren(VcNode node)");
        Contains(children, "var ungrouped = PortVci.UngroupedDevicesGroup(proj);", "if (owner != null) throw VciWorkspaceLayout.UngroupedFolderTaken(owner);",
            "Label = node.Label + \"/\" + VciWorkspaceLayout.UngroupedFolder,", "Structural = true,", "case DeviceSystemGroup system:",
            "UngroupedDevice = node.Structural && o is Device,", "UngroupedRoot = node.UngroupedDevice ? node : node.UngroupedRoot,");
        var connect = EngineSources.BlockAfter(adapter, "public VersionControlResult ConnectProjectToWorkspace(");
        Contains(connect, "if (!node.Structural)", "lines.Add(VciWorkspaceLayout.NativeRefusal(node.Label, Flatten(ex.Message)));",
            "catch (Exception ex) when (!(ex is AdapterPreconditionException))", "lines.Add(VciWorkspaceLayout.NothingMappable(device.Label));");
        Contains(adapter, ": error is AdapterPreconditionException ? \"PRECONDITION_FAILED\"");
        Contains(Read("src/Adapters/Native/Vci/VersionControlFallback.cs"), "if (!node.Structural) targets.Add(",
            "catch (AdapterPreconditionException error) { CandidatePrimitives.Fail(\"precondition\", \"vci-ungrouped-workspace-folder\", error.Message); }");
        Contains(Read("src/Adapters/Native/Vci/LegacyVciPortPrimitives.cs"), "internal static DeviceSystemGroup UngroupedDevicesGroup(Project project) => project.UngroupedDevicesGroup;");
        Contains(Read("src/Adapters/Native/Vci/VersionControlPrimitives.cs"), "public static DeviceSystemGroup UngroupedDevicesGroup(ProjectBase project) => project.UngroupedDevicesGroup;");
        Contains(Read("src/Shared/VersionControlTools.cs"), "fixed workspace folder 'UngroupedDevices'");
    }

    [Fact]
    public void DeviceCreationCreatesOnlyTheRequestedTypeOnce()
    {
        // Decided 2026-10-19: exactly the requested type identifier, version and name; no version stepping, model list or renaming.
        var devices = Read("src/Adapters/Native/Hardware/HardwareDevices.cs");
        Excludes(devices, "\"21.0.0.0\"", "\"V21.0.0.0\"", "\"Device_1\"", "\"Station_1\"", "\"6ES7516-3AN03-0AB0\"", "\"6AV2123-3GB32-0AW0\"",
            "versionVariants", "orderVariants", "TryFormatMlfbWithSpaces", "MakeEngineeringName", "HardwareSearchHardwareCatalog(preferredMlfb");
        Count(devices, "HardwarePrimitives.CreateWithItem(", 1);
        var create = EngineSources.BlockAfter(devices, "private Device HardwareCreateExactDevice(string typeIdentifier, string deviceName, string parameter)");
        Contains(create, "DeviceCreationRules.RequireFreeName(ProjectDeviceWalk.OccupiedNames(", "DeviceCreationRules.SameInventory(before, after)",
            "throw DeviceCreationRules.TiaRefusal(typeIdentifier, deviceName, ShortExceptionMessage(error), HardwareCatalogCandidates(typeIdentifier), parameter, error);");
        foreach (var entry in new[] { "HardwareLegacyAddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = \"\")",
            "HardwareLegacyAddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = \"\")" })
        {
            var probe = EngineSources.BlockAfter(devices, entry);
            Contains(probe, "DeviceCreationRules.ExactCandidate(candidates.Select(c => c.TypeIdentifier), keyword,", "HardwareCreateExactDevice(exact, deviceName, \"keyword\")");
            Excludes(probe, "foreach (var candidate in candidates");
        }
        // Plugging resolves one documented identifier (ModuleInformationProvider.FindModuleTypes) instead of spelling/version variants.
        var modules = Read("src/Adapters/Native/Hardware/HardwareModules.cs");
        Contains(modules, "provider.FindModuleTypes(partial)", "var typeIdentifiers = new List<string> { resolved.TypeIdentifier };");
        Excludes(modules, "BuildPlugTypeIdentifiers", "TryFormatMlfbWithSpaces");
        // The scaffold passes the requested version and the HMI type; there is no empty family default create.
        var scaffold = Read("src/Shared/ProjectLifecycleProjectTools.cs");
        Contains(scaffold, "_service.AddDeviceWithFallback(plcMlfb, plcVersion, plcName, plcFamily)", "_service.AddDeviceWithFallback(hmiMlfb, hmiVersion, hmiName, hmiFamily)",
            "DeviceCreationRules.SpecProblem(plcMlfb, plcVersion, \"plcMlfb\", \"plcVersion\")");
        Excludes(scaffold, "AddDeviceWithFallback(\"\", \"\", hmiName, hmiFamily)", "AddDeviceWithFallback(plcMlfb, \"\", plcName, plcFamily)");
        // Typed refusals from the worker stay typed preconditions for exactly the creation operations.
        Contains(Read("src/FoundationHost/PortedToolContract.cs"), "catch (WorkerOperationException error) when (PortedToolContract.IsDeviceRefusal(operation, error))");
    }

    [Fact]
    public void SupplementaryReadsUseExplicitSourcesTypedAccessAndReadOnlyDispatch()
    {
        var files = XDocument.Parse(Read("src/Adapters/build/Adapter.Sources.props")).Descendants("AdapterSource").Select(n => Path.GetFullPath(Path.Combine(Root, "src", n.Attribute("Include")!.Value.Replace("$(AdapterSourceRoot)/", "", StringComparison.Ordinal)))).ToList();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "src/Shared/shared-native"), "*.props").Order(StringComparer.Ordinal))
            foreach (var node in XDocument.Load(path).Descendants("TiaSharedNativePrimitive")) files.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, node.Attribute("Include")!.Value.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.Ordinal))));
        var simulation = Path.Combine(Root, "src/OnlineChannels/Simulation");
        foreach (var node in XDocument.Load(Path.Combine(simulation, "TiaMcp.PlcSimAdvanced.csproj")).Descendants("Compile"))
            if (node.Attribute("Include") is { } include && !include.Value.Contains("..", StringComparison.Ordinal))
                files.Add(Path.GetFullPath(Path.Combine(simulation, include.Value)));
        foreach (var path in Directory.EnumerateFiles(simulation, "*.cs")) Assert.Contains(Path.GetFullPath(path), files);
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

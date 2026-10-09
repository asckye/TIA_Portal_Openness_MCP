using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed partial class VersionCatalogWiring
{
    private static readonly string ROOT = Repository.FindRoot(AppContext.BaseDirectory);
    private static readonly string SRC = Path.Combine(ROOT, "src/Engine");
    private static readonly string LOGIC = Path.Combine(ROOT, "src/Logic");
    private static readonly EngineSources Sources = new(ROOT);
    private static string Read(string path) => Repository.ReadSource(path);
    private static bool Has(string source, string value) => source.Contains(value, StringComparison.Ordinal);
    private static int Count(string source, string value) => Regex.Matches(source, Regex.Escape(value)).Count;
    private static int Find(string source, string value, int start = 0)
    {
        var index = source.IndexOf(value, start, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing source fragment: " + value); return index;
    }
    private static string[] FindAll(string pattern, string source) => Regex.Matches(source, pattern).Select(m => m.Groups[1].Value).ToArray();
    private static string[] Split(string source, string separator, int limit = int.MaxValue) => source.Split(new[] { separator }, limit == int.MaxValue ? limit : limit + 1, StringSplitOptions.None);

    [Fact]
    public void PolicyWrapsFoundationDispatch()
    {
        var wiring = Sources.Member("WrapTools");
        Assert.DoesNotContain("IsolatedWorkerHost", wiring, StringComparison.Ordinal);
        Assert.Contains("WrapWithSerializedCalls(WrapWithResponseGuard(tools))", wiring, StringComparison.Ordinal);
        Assert.Contains("WrapWithVersionPolicy(Dispatch.OpennessReadinessGuard.Wrap(guarded))", wiring, StringComparison.Ordinal);
        var wrapper = Sources.Member("InvokeAsync", owner: "VersionPolicyTool");
        Assert.True(Find(wrapper, "V4Admission") < Find(wrapper, "return inner.InvokeAsync"));
        Assert.DoesNotContain("VersionCallProblem", wrapper, StringComparison.Ordinal);
        var binding = Sources.Member("BindV4Call");
        Assert.True(Find(binding, "VersionCallProblem") < Find(binding, "ToolInputSchema"));
        var profile = (Sources.Member("GetLiteTools") + Sources.Member("GetAllTools"));
        Assert.Contains("CatalogView.Lite", profile, StringComparison.Ordinal);
        Assert.Contains("CatalogView.All", profile, StringComparison.Ordinal);
        Assert.Contains("includeUnavailable || McpServer.VersionToolProblem(pair.Key).Length == 0", Sources.Member("View", owner: "ToolCatalog"), StringComparison.Ordinal);
        var invoke = Sources.Member("InvokeToolMethod");
        var version = Find(invoke, "VersionCallProblem");
        var binding_check = Find(invoke, "ValidateRuntimeBinding(method)");
        var target = Find(invoke, "object? target = method.IsStatic ? null : EngineServices.Get(method.DeclaringType!);");
        Assert.True(((version < binding_check) && (binding_check < target) && (target < Find(invoke, "method.Invoke(target, call)"))));
        Assert.Contains("AvailableToolMethods(((ToolCatalog)CatalogView).Methods, includeUnavailable)", Sources.Member("AllToolMethods", owner: "McpServer"), StringComparison.Ordinal);
        Assert.Contains("includeUnavailable || VersionToolProblem(kv.Key).Length == 0", Sources.Member("AvailableToolMethods"), StringComparison.Ordinal);
        Assert.Contains("=> CallToolCore(name, arguments);", Sources.Member("CallTool", tool: true), StringComparison.Ordinal);
        var bridge = Sources.Member("CallToolCore");
        var bridgeBinding = Find(bridge, "ToolInvoker.Bind(name, arguments ?? EmptyArguments(), out var call)");
        var refusal = Find(bridge, "if (error != null)");
        var approval = Find(bridge, "ApprovedBridgeCall");
        var dispatch = Find(bridge, "call!.Invoke(ApprovalPreviewDepth.Value > 0)");
        Assert.True(((bridgeBinding < refusal) && (refusal < approval) && (approval < dispatch)));
        Assert.Contains("return AuditBridgeResult(audit, rejected, disabled);", bridge[bridgeBinding..approval], StringComparison.Ordinal);
        Assert.Contains("ToolInvoker.Bind", Sources.Member("PreviewToolCall", tool: true), StringComparison.Ordinal);
        Assert.Contains("versionAvailable", Sources.Member("PreflightToolCall", signature: "string argumentsJson)"), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionExclusionsHaveRealRegisteredNames()
    {
        var policy = Split(Read(Path.Combine(LOGIC, "Siemens/ToolVersionPolicy.cs")), "internal static string ToolProblem", 1)[0];
        var excluded = FindAll("\\[\"([^\"]+)\"\\]\\s*=", policy);
        Assert.Equal(11, (excluded).Count());
        var registered = (FindAll("McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\"", Sources.AllText())).ToHashSet();
        Assert.False((excluded).ToHashSet().Except(registered).Any());
        Assert.Contains("exercise_engine", Read(Path.Combine(ROOT, "scripts/checks/Test-FoundationTransport.py")), StringComparison.Ordinal);
        var stability = Read(Path.Combine(ROOT, "scripts/checks/Test-LocalStability.py"));
        Assert.Contains("if getattr(args, key) is None: setattr(args, key, len(names))", stability, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolProfiles.resx", stability, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyContractRemainsUnbound()
    {
        var source = Sources.TypeText("OpennessReleaseContract");
        Assert.Contains("expectedIdentity == null", source, StringComparison.Ordinal);
        Assert.Contains("EngineeringAssemblyIdentity.RequireMatch", source, StringComparison.Ordinal);
        Assert.Contains("HW.ISoftwareContainer", source, StringComparison.Ordinal);
        Assert.Contains("HW.Features.SoftwareContainer", source, StringComparison.Ordinal);
        Assert.Contains("typeof(string) : typeof(FileInfo)", source, StringComparison.Ordinal);
        Assert.Contains("\"Siemens.Engineering.IEngineeringObject\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Assembly.Load", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveRefusalAndRecoveryPrecedeCleanup()
    {
        var source = new EngineSources(ROOT, "src/Adapters/Native/Plc").Member("MoveBlockToGroup", owner: "PlcOrganisationAdapter", tool: false);
        Assert.True(Find(source, "if (block is OB)") < Find(source, "EnsurePlcBlockGroup"));
        Assert.True(Find(source, "moveVerified = verifyGroup") < Find(source, "finally"));
        Assert.Contains("if (moveVerified)", Split(source, "finally", 1)[1], StringComparison.Ordinal);
        Assert.Contains("Recovery export retained", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseKeysAndExistingEngines()
    {
        var source = Read(Path.Combine(LOGIC, "Siemens/TiaVersionCatalog.cs"));
        var keys = FindAll("(?:Foundation|new TiaVersionDescriptor)\\(\"([0-9.a-z]+)\",", source);
        Assert.Equal(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, keys);
        var runnable = FindAll("new TiaVersionDescriptor\\(\"([^\"]+)\", \"[^\"]+\", \\d+, true,", source);
        Assert.Equal(new[] { "20", "21" }, runnable);
        Assert.Contains("major, true, \"v\" + key, null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardPrecedesResolutionAndNativeHost()
    {
        var source = Sources.Member("Main");
        var guard = Find(source, "TiaVersionCatalog.RequireMatchingEngine(tiaMajorVersion, EngineRouter.CompiledTiaMajorVersion)");
        Assert.True(guard < Find(source, "AssemblyResolve += Engineering.Resolver"));
        Assert.True(guard < Find(source, "Openness.Initialize("));
        Assert.Contains("detected ?? EngineRouter.CompiledTiaMajorVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("assembly load will likely fail", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsRemainReachable()
    {
        var source = Sources.Member("Main");
        Assert.True(Find(source, "CliOptions.IsInformationalCommand(args)") < Find(source, "CliOptions.ParseArgs(args)"));
        Assert.True(Find(source, "string.Equals(args[0], \"doctor\"") < Find(source, "TiaVersionCatalog.RequireRunnable(tiaMajorVersion)"));
        var doctor = Sources.Member("DoctorCli");
        Assert.Contains("ready &= supported;", doctor, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationReusesSelectedVersion()
    {
        var source = Split(Sources.Member("Config", signature: "string[] args"), "string exe =", 1)[0];
        Assert.Contains("Engineering.TiaMajorVersion", source, StringComparison.Ordinal);
        Assert.Contains("RequireMatchingEngine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectTiaMajorVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Opt(args", source, StringComparison.Ordinal);
    }
}

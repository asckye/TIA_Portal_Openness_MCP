using TiaMcpServer;
// Response boundary stand-ins. The catalog and bridge use the real MCP SDK attributes.
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcpServer.Siemens
{
    // Registration-only session boundary; no Siemens SDK types enter the offline suite.
    internal partial interface IEngineeringSession { }
    // Kept only for the linked EngineRegistration and maintenance root-lookup checks.
    // No Portal implementation files are compiled into this suite.
    internal sealed partial class Portal : TiaMcp.Engine.Tests.FakeHmiToolSession, IEngineeringSession
    {
        internal (bool IsConnected, string? Project)? GetState()
            => throw new InvalidOperationException("The layout fixture must not read a session.");
    }
}

namespace TiaMcpServer.ModelContextProtocol
{

    public class ResponseMessage { public string? Message { get; set; } public JsonObject? Meta { get; set; } }
    public class ResponseStringList : ResponseMessage { public IEnumerable<string>? Items { get; set; } }
    // Explicit fixture catalogs load this probe; assembly discovery loads the real DCC tool.
    public static class DcbVersionProbe
    {
        [McpServerTool(Name = "ManageDcbLibraries")]
        public static CallToolResult ProbeVersionedDcb(string action = "read", string[] devicePath = null!,
            string[] itemPath = null!, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
            string filePath = "", bool dryRun = true)
        { ToolBridgeProbes.VersionProbeCalls++; return ToolBridgeProbes.ProbeResult(true); }
    }
    [McpServerToolType]
    public static class ToolBridgeProbes
    {
        internal static int VersionProbeCalls;
        // Application-boundary fixtures, not fabricated Siemens SDK classes.
        [global::ModelContextProtocol.Server.McpServerTool(Name = "ListSafetyActivationTests")]
        public static CallToolResult ProbeVersionedSafety()
        { VersionProbeCalls++; return ProbeResult(true); }
        [global::ModelContextProtocol.Server.McpServerTool]
        public static CallToolResult ProbeResult(bool success) => success
            ? McpServer.V4Result("ProbeResult", new JsonObject { ["summary"] = "probe" })
            : McpServer.V4Reject("ProbeResult", new Error("Fixture rejected the request.", new PreconditionFailedDetails("fixture", null)));
        [global::ModelContextProtocol.Server.McpServerTool]
        public static ResponseMessage ProbeThrow() => throw new InvalidOperationException("probe exception");
        [McpServerTool(Name = "ProbeMcpThrow"), ToolClassification("L2", "Diagnostics", "READ", batchRead: true), Description("[L2][Diagnostics][READ] Offline throwing read fixture.")]
        public static CallToolResult ProbeMcpThrow() => throw new global::ModelContextProtocol.McpException("SECRET-probe-argument", global::ModelContextProtocol.McpErrorCode.InternalError);
        [McpServerTool(Name = "ProbeWriteThrow"), ToolClassification("L2", "Diagnostics", "WRITE", batchWrite: true), Description("[L2][Diagnostics][WRITE] Offline throwing write fixture.")]
        public static CallToolResult ProbeWriteThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L0", "Portal", "SESSION")]
        public static CallToolResult ProbeSessionThrow() => throw new global::ModelContextProtocol.McpException("SECRET-probe-argument", global::ModelContextProtocol.McpErrorCode.InternalError);
        [McpServerTool, ToolClassification("L2", "Hardware", "EXECUTE")]
        public static CallToolResult ProbeExecuteThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L2", "Exports", "FILE")]
        public static CallToolResult ProbeFileThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L1", "PLC-Online", "ONLINE")]
        public static CallToolResult ProbeOnlineThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        public sealed class Cycle { public Cycle Self => this; }
        [global::ModelContextProtocol.Server.McpServerTool]
        public static Cycle ProbeCycle() => new Cycle();
    }
}

namespace TiaMcp.Engine.Tests
{
    internal static class ToolBridgeFixture
    {
        internal static readonly ToolCatalog Catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ToolBridgeProbes), typeof(DcbVersionProbe), typeof(InstanceProbeTools), typeof(HmiInspectionTools), typeof(MigrationReadTools), typeof(RuntimeSettingsTools), typeof(GraphicSelectionTools), typeof(GlobalScriptEditTools), typeof(HardwareNetworkTools), typeof(HardwareSecurityTools), typeof(HardwareServicesTools), typeof(HardwareServicesPortTools) });

        internal static void Configure(bool lite = false)
        {
            McpServer.ConfigureToolBridge(Catalog, () => lite, new HashSet<string>(StringComparer.Ordinal) { "ProbeResult" });
            EngineServices.SetServiceProvider(new ServiceCollection()
                .AddSingleton(new ProbeDependency("root"))
                .AddSingleton(HmiToolFixture.HmiInspection)
                .AddSingleton(HmiToolFixture.MigrationRead)
                .AddSingleton(HmiToolFixture.RuntimeSettings)
                .AddSingleton(HmiToolFixture.GraphicSelection)
                .AddSingleton(HmiToolFixture.GlobalScriptEdit)
                .AddSingleton<TiaMcpServer.Siemens.Services.HardwareNetworkService>()
                .AddSingleton<TiaMcpServer.Siemens.Services.HardwareServicesService>()
                .AddEngine(includeSession: false, Catalog).BuildServiceProvider());
        }
    }

    public sealed class ProbeDependency
    {
        public string Value { get; }
        public ProbeDependency(string value) { Value = value; }
    }

    [McpServerToolType]
    public sealed class InstanceProbeTools
    {
        private readonly ProbeDependency dependency;
        internal int Calls;
        public InstanceProbeTools(ProbeDependency dependency) { this.dependency = dependency; }

        [McpServerTool(Name = "InstanceProbe"), Description("[L0][Meta][READ] Read the injected probe dependency.")]
        public CallToolResult Read(string suffix = "default")
        {
            Calls++;
            return McpServer.V4Result("InstanceProbe", new JsonObject { ["summary"] = dependency.Value + ":" + suffix });
        }
    }

    public class ToolCatalogServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    public sealed class ToolFailureTests
    {
        private static Envelope Envelope(CallToolResult result)
        {
            string text = result.Content.OfType<TextContentBlock>().Single().Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            Assert.DoesNotContain("SECRET", text);
            return envelope;
        }

        [Theory]
        [InlineData("ProbeMcpThrow", "INTERNAL_ERROR", "read-failed", false)]
        [InlineData("ProbeWriteThrow", "OUTCOME_UNKNOWN", "unknown", true)]
        [InlineData("ProbeSessionThrow", "OUTCOME_UNKNOWN", "unknown", true)]
        [InlineData("ProbeExecuteThrow", "OUTCOME_UNKNOWN", "unknown", true)]
        [InlineData("ProbeFileThrow", "OUTCOME_UNKNOWN", "unknown", true)]
        [InlineData("ProbeOnlineThrow", "OUTCOME_UNKNOWN", "unknown", true)]
        public void BridgeExceptionsReturnTypedV4Results(string tool, string code, string outcome, bool reset)
        {
            ToolBridgeFixture.Configure();
            var result = McpServer.CallTool(tool);
            var envelope = Envelope(result);
            var json = McpServer.ResultBody(result)!;
            Assert.Equal(code, (string)json["error"]!["code"]!);
            Assert.Equal(outcome, (string)json["meta"]!["outcome"]!);
            Assert.Equal(reset, envelope.Meta.RequiresSessionReset);
            Assert.Equal(tool, envelope.Meta.Tool);
        }

        [Fact]
        public void AdmissionFailureDoesNotClaimAnIssuedWrite()
        {
            var envelope = Envelope(McpServer.TargetFailure("ProbeWriteThrow", new InvalidOperationException("SECRET"), false));
            Assert.Equal(Outcome.RejectedBeforeOperation, envelope.Meta.Outcome);
            Assert.Equal(Execution.NotStarted, envelope.Meta.Execution);
            Assert.False(envelope.Meta.RequiresSessionReset);
            Assert.IsType<PreconditionFailedDetails>(envelope.Error!.Details);
        }

        [Fact]
        public void InstanceResolutionFailureIsRejectedBeforeTheOperation()
        {
            ToolBridgeFixture.Configure();
            EngineServices.SetServiceProvider(new ServiceCollection().BuildServiceProvider());
            try
            {
                var envelope = Envelope(McpServer.CallTool("InstanceProbe"));
                Assert.Equal(Outcome.RejectedBeforeOperation, envelope.Meta.Outcome);
                Assert.Equal(Execution.NotStarted, envelope.Meta.Execution);
                Assert.False(envelope.Meta.RequiresSessionReset);
                Assert.IsType<PreconditionFailedDetails>(envelope.Error!.Details);
            }
            finally { ToolBridgeFixture.Configure(); }
        }

        [Fact]
        public void ResetRequiredRejectionCarriesItsResetFlag()
        {
            var envelope = Envelope(McpServer.V4Reject("SaveProject", new Error("Reset is required.", new SessionResetRequiredDetails("worker-fault"))));
            Assert.Equal(Outcome.RejectedBeforeOperation, envelope.Meta.Outcome);
            Assert.Equal(Execution.NotStarted, envelope.Meta.Execution);
            Assert.True(envelope.Meta.RequiresSessionReset);
        }

        [Fact]
        public void LegacyOrCyclicReturnValuesCannotBecomeSuccessfulToolResults()
        {
            Assert.ThrowsAny<Exception>(() => McpServer.ToolResult(new ResponseMessage { Message = "old", Meta = new JsonObject { ["success"] = true } }));
            Assert.ThrowsAny<Exception>(() => McpServer.ToolResult(new CallToolResult {
                Content = new[] { new TextContentBlock { Text = "{\"message\":\"old\",\"meta\":{\"success\":true}}" } } }));
            ToolBridgeFixture.Configure();
            var envelope = Envelope(McpServer.CallTool("ProbeCycle"));
            Assert.Equal(Outcome.ReadFailed, envelope.Meta.Outcome);
            Assert.IsType<InternalErrorDetails>(envelope.Error!.Details);
        }

        [Fact]
        public void ReadBatchKeepsTheThrowingTargetsV4Envelope()
        {
            ToolBridgeFixture.Configure();
            var result = McpServer.ReadToolBatch(new[] { new TiaMcp.Logic.V4.Inputs.ToolCall("ProbeMcpThrow", McpServer.EmptyArguments()) });
            var envelope = Envelope(result);
            var items = McpServer.ResultBody(result)!["data"]!["items"]!.AsArray();
            Assert.Single(items);
            var child = V4Json.Deserialize<Envelope>(items[0]!["result"]!.ToJsonString());
            Assert.Equal(Outcome.ReadFailed, child.Meta.Outcome);
            Assert.IsType<InternalErrorDetails>(child.Error!.Details);
        }
    }

    public sealed class ToolCatalogTests
    {
        private sealed class DuplicateProbe
        {
            [McpServerTool(Name = "proberesult")]
            public static void Duplicate() { }
        }

        private sealed class DisposableProbe : IDisposable
        {
            [McpServerTool]
            public void DisposableCall() { }
            public void Dispose() { }
        }

        [Fact]
        public void ExplicitCatalogIncludesStaticAndInstanceToolsInOrdinalNameOrder()
        {
            var methods = ToolBridgeFixture.Catalog.Methods;
            Assert.Contains(methods, entry => entry.Key == "InstanceProbe" && !entry.Value.IsStatic);
            Assert.Contains(methods, entry => entry.Key == "ProbeResult" && entry.Value.IsStatic);
            Assert.DoesNotContain(methods, entry => entry.Key == "ConfigureToolBridge");
            Assert.Equal(methods.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal), methods.Select(entry => entry.Key));
        }

        [Fact]
        public void AssemblyDiscoveryIncludesEveryAttributedTypeOnly()
        {
            Assert.Equal(new ToolCatalog(new[] {
                typeof(ToolBridgeProbes), typeof(InstanceProbeTools), typeof(PlcOfflineTools), typeof(HmiOfflineTools), typeof(HostMetaTools), typeof(ExportTools), typeof(EngineeringDiagnosticsTools), typeof(HmiInspectionTools), typeof(MigrationReadTools),
                typeof(RuntimeSettingsTools), typeof(OnlineDownloadTools), typeof(PlcSimAdvancedTools), typeof(RuntimeChannelTools), typeof(RuntimeTools), typeof(GraphicSelectionTools), typeof(GlobalScriptEditTools), typeof(ToolUsageTools),
                typeof(AddressesTools), typeof(HardwareNetworkTools), typeof(HardwareSecurityTools), typeof(HardwareServicesTools), typeof(HardwareServicesPortTools),
                typeof(HardwareDevicesTools), typeof(ModulesTools), typeof(HardwareManagementTools), typeof(HardwareAmlTools), typeof(EcosystemTools), typeof(EngineeringAuditTools),
                typeof(GitWorkflowTools), typeof(ImportStagingTools), typeof(ImportOrderTools), typeof(OfflineAnalysisTools), typeof(OfflineSuiteTools),
                typeof(PlcBuildTools), typeof(PlcDocumentationTools), typeof(QualityAuditTools), typeof(TemplateTools),
                typeof(V21EcosystemTools), typeof(XmlBuilderTools), typeof(AlarmsTools), typeof(OpcUaTools),
                typeof(SoftwareUnitDeepTools), typeof(SoftwareUnitManagementTools), typeof(TechnologyObjectsTools),
                typeof(ClassicHmiFoldersTools), typeof(MotionProDiagClassicHmiTools),
                typeof(CertificateManagementTools), typeof(ProjectSecurityTools), typeof(SafetyManagementTools), typeof(SecurityDeepTools),
                typeof(UnifiedHmiTools), typeof(UnifiedHmiGroupsTools), typeof(UnifiedScreenItemsTools), typeof(UnifiedUiModelTools),
                typeof(LibraryTools), typeof(SivarcTools), typeof(VersionControlTools),
                typeof(DccTools), typeof(StartdriveTools), typeof(TeamcenterTools),
                typeof(CfcTools), typeof(TestSuiteTools), typeof(V20OptionsTools), typeof(OptionalEngineeringTools), typeof(SpecializedExchangeTools),
                typeof(ReflectionTools), typeof(UnifiedEngineeringTools), typeof(UnifiedEventsTools), typeof(UnifiedObjectServicesTools)
            }).Methods, ToolCatalog.Engine.Methods);
        }

        [Fact]
        public void DuplicateNamesFailCaseInsensitivelyWithBothMethods()
        {
            var error = Assert.Throws<InvalidOperationException>(() => new ToolCatalog(new[] { typeof(ToolBridgeProbes), typeof(DuplicateProbe) }));
            Assert.Contains("proberesult", error.Message);
            Assert.Contains(typeof(ToolBridgeProbes).FullName + ".ProbeResult", error.Message);
            Assert.Contains(typeof(DuplicateProbe).FullName + ".Duplicate", error.Message);
        }

        [Fact]
        public void RegistrationKeepsInstancesSingletonAndOmitsParentSession()
        {
            using var parent = new ServiceCollection().AddSingleton(new ProbeDependency("parent"))
                .AddEngine(includeSession: false, ToolBridgeFixture.Catalog).BuildServiceProvider();
            Assert.Null(parent.GetService<TiaMcpServer.Siemens.Portal>());
            Assert.Null(parent.GetService<TiaMcpServer.Siemens.IEngineeringSession>());
            Assert.Same(parent.GetRequiredService<InstanceProbeTools>(), parent.GetRequiredService<InstanceProbeTools>());
            Assert.Same(ToolBridgeFixture.Catalog, parent.GetRequiredService<ToolCatalog>());
            using var child = new ServiceCollection().AddEngine(includeSession: true, ToolBridgeFixture.Catalog).BuildServiceProvider();
            Assert.Same(child.GetRequiredService<TiaMcpServer.Siemens.Portal>(), child.GetRequiredService<TiaMcpServer.Siemens.Portal>());
            Assert.Same(child.GetRequiredService<TiaMcpServer.Siemens.Portal>(), child.GetRequiredService<TiaMcpServer.Siemens.IEngineeringSession>());
        }

        [Fact]
        public void RegistrationRejectsDisposableSingletonTools()
        {
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
                .AddEngine(false, new ToolCatalog(new[] { typeof(DisposableProbe) })));
        }

        [Fact]
        public void BridgeResolvesInstanceFromRootProviderByCanonicalName()
        {
            ToolBridgeFixture.Configure();
            var instance = (InstanceProbeTools)EngineServices.Get(typeof(InstanceProbeTools));
            var result = McpServer.CallTool("InstanceProbe", McpServer.EmptyArguments());
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("root:default", result.Content.OfType<TextContentBlock>().Single().Text);
            Assert.Equal(1, instance.Calls);
        }

        [Fact]
        public void BridgeReceivesLiteProfileAndAllowlist()
        {
            ToolBridgeFixture.Configure(lite: true);
            try
            {
                Assert.Contains(McpServer.FindTools("ProbeResult").Items!, line => line.Contains("[already listed - call it directly]"));
                Assert.Contains(McpServer.FindTools("InstanceProbe").Items!, line => line.Contains("[call via CallTool]"));
            }
            finally { ToolBridgeFixture.Configure(); }
        }

        private static RequestContext<CallToolRequestParams> Request(IServiceProvider? services)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            {
                Services = services,
                Params = new CallToolRequestParams { Name = "InstanceProbe", Arguments = new Dictionary<string, JsonElement>() },
            };

        [Fact]
        public async Task SdkFactoryPrefersRequestServicesAndPreservesMetadata()
        {
            ToolBridgeFixture.Configure();
            using var requestServices = new ServiceCollection().AddSingleton(new ProbeDependency("request"))
                .AddEngine(false, ToolBridgeFixture.Catalog).BuildServiceProvider();
            var method = typeof(InstanceProbeTools).GetMethod("Read")!;
            var tool = ToolCatalog.CreateTool(method, new McpServerToolCreateOptions { Name = "Alias", Description = "decorated" });
            Assert.Equal("Alias", tool.ProtocolTool.Name);
            Assert.Equal("decorated", tool.ProtocolTool.Description);
            Assert.Equal("default", tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("suffix").GetProperty("default").GetString());
            var result = await tool.InvokeAsync(Request(requestServices));
            Assert.Contains("request:default", result.Content.OfType<TextContentBlock>().Single().Text);
            Assert.Equal(1, requestServices.GetRequiredService<InstanceProbeTools>().Calls);
            Assert.Equal(0, ((InstanceProbeTools)EngineServices.Get(typeof(InstanceProbeTools))).Calls);
        }

        [Fact]
        public async Task SdkFactoryFallsBackToRootServicesWhenRequestHasNoTarget()
        {
            ToolBridgeFixture.Configure();
            var tool = ToolCatalog.CreateTool(typeof(InstanceProbeTools).GetMethod("Read")!);
            using var empty = new ServiceCollection().BuildServiceProvider();
            foreach (var services in new IServiceProvider?[] { null, empty })
            {
                var result = await tool.InvokeAsync(Request(services));
                Assert.Contains("root:default", result.Content.OfType<TextContentBlock>().Single().Text);
            }
            Assert.Equal(2, ((InstanceProbeTools)EngineServices.Get(typeof(InstanceProbeTools))).Calls);
        }

        [Fact]
        public async Task SdkStaticCreationPreservesAttributeNameAndInvocation()
        {
            var method = typeof(ToolBridgeProbes).GetMethod("ProbeResult")!;
            var tool = ToolCatalog.CreateTool(method);
            Assert.Equal(McpServerTool.Create(method).ProtocolTool.InputSchema.GetRawText(), tool.ProtocolTool.InputSchema.GetRawText());
            var request = Request(null);
            request.Params = new CallToolRequestParams { Name = "ProbeResult", Arguments = new Dictionary<string, JsonElement>
                { ["success"] = JsonSerializer.SerializeToElement(true) } };
            Assert.Contains("probe", (await tool.InvokeAsync(request)).Content.OfType<TextContentBlock>().Single().Text);
        }
    }
}

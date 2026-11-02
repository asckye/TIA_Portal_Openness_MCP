extern alias foundationhost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using HostSession = foundationhost::TiaMcp.FoundationHost.HostSession;
using IHostSession = foundationhost::TiaMcp.FoundationHost.IHostSession;
using HostOptions = foundationhost::TiaMcp.FoundationHost.HostOptions;
using HostRegistration = foundationhost::TiaMcp.FoundationHost.HostRegistration;
using OperatorConnection = foundationhost::TiaMcp.FoundationHost.OperatorConnection;

public sealed class PlcCrossReferenceAnalysisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_release_refuses_consent_file_targets_before_approval_or_inner_dispatch(bool approvalEnabled)
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var inner = new ConsentFileTool();
            int approvals = 0;
            var wrapper = new FoundationV4Tool(inner, release, null, () => new TiaOpenness.Shared.ApprovalSettings(approvalEnabled, 1),
                (_, _, _) => { approvals++; throw new InvalidOperationException("Consent must never reach approval."); });
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ConsentServerProxy>()) {
                Params = new CallToolRequestParams { Name = inner.ProtocolTool.Name, Arguments = new Dictionary<string, JsonElement> {
                    ["outputPath"] = JsonSerializer.SerializeToElement("native-cross-references-" + Guid.NewGuid().ToString("N") + ".settings") } } };
            var result = await wrapper.InvokeAsync(request);
            Assert.True(result.IsError);
            Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("human-only-native-cross-reference-setting", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Equal(0, approvals);
            Assert.Equal(0, inner.Calls);
        }
    }

    [Fact]
    public void Human_consent_is_absent_from_control_and_approval_contracts_and_file_tools_cannot_target_it()
    {
        Assert.DoesNotContain(Enum.GetNames<TiaOpenness.Shared.WorkbenchControlOperation>(), n => n.Contains("CrossReference", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames<TiaOpenness.Shared.WorkbenchPage>(), n => n.Contains("Settings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames<TiaOpenness.Shared.WorkbenchPrefillForm>(), n => n.Contains("Settings", StringComparison.OrdinalIgnoreCase));
        foreach (var type in new[] { typeof(TiaOpenness.Shared.WorkbenchControlRequest), typeof(TiaOpenness.Shared.ApprovalSettings) })
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("CrossReference", StringComparison.OrdinalIgnoreCase));
        string name = "native-cross-references-" + Guid.NewGuid().ToString("N") + ".settings";
        string file = Path.Combine(Path.GetTempPath(), "approval.settings.sessions", name);
        foreach (string tool in new[] { "SaveExportContent", "WritePlcSclSourceFile", "PatchPlcBlockDocument", "CallTool" })
        foreach (string path in new[] { file, name, Path.Combine("config", "approval.settings.sessions", name), "NATIVE~1.SET" })
        {
            var arguments = new JsonObject { ["arguments"] = new JsonObject { ["outputPath"] = path } };
            var refusal = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.Validate(tool, arguments));
            Assert.False(refusal.IsArgument);
            Assert.Contains("Human-only", refusal.Message);
        }
        TiaMcp.Logic.V4.CallerInputFiles.Validate("WritePlcSclSourceFile", new JsonObject { ["outputPath"] = "native-cross-references-report.scl" });
    }

    [Fact]
    public void Host_signed_consent_is_locked_against_file_tools_and_cannot_be_replayed_between_sessions()
    {
        string root = NewDirectory();
        string file;
        try
        {
            using (var session = new HostSession("21", "fixture", false, [], [], () => new JsonObject(), () => { }, root))
            {
                file = session.NativeCrossReferenceSessionFile;
                Assert.False((bool)session.Snapshot()["nativeCrossReferencesAllowed"]!);
                session.SetNativeCrossReferences(true, Environment.ProcessId);
                Assert.True((bool)session.Snapshot()["nativeCrossReferencesAllowed"]!);
                Assert.True(TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.Read(file, session.NativeCrossReferenceSessionSecret));
                Assert.False(TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.Read(file, TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.CreateSecret()));
                Assert.DoesNotContain(session.NativeCrossReferenceSessionSecret, session.Snapshot().ToJsonString());
                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(file, "enabled=true\nforged"));
                    Assert.Throws<IOException>(() => File.Delete(file));
                }
                session.SetNativeCrossReferences(false, Environment.ProcessId);
                Assert.False((bool)session.Snapshot()["nativeCrossReferencesAllowed"]!);
                Assert.False(TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.Read(file, session.NativeCrossReferenceSessionSecret));
            }
            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("unverified")]
    [InlineData("spoofed")]
    [InlineData("foreign-owner")]
    public async Task Consent_handshake_requires_the_verified_Workbench_peer_and_correct_session_owner(string peer)
    {
        string root = NewDirectory();
        try
        {
            var options = new HostOptions { ReleaseKey = "21", WorkerExe = Path.Combine(root, "worker.exe"), ApiDirectory = root, BundleRoot = root };
            using var registration = new HostRegistration(options, "fixture-sid", root, _ => { });
            using var session = new ConsentSession(peer == "foreign-owner");
            using var member = registration.Register(session);
            using var stream = new MemoryStream();
            await TiaOpenness.Shared.OperatorProtocol.Write(stream, new TiaOpenness.Shared.OperatorHandshake {
                Action = "native-cross-references", SessionKey = session.SessionKey, NativeCrossReferencesAllowed = true,
                Workbench = new() { Pid = peer == "spoofed" ? Environment.ProcessId + 1 : Environment.ProcessId, Version = "fixture" } }, CancellationToken.None);
            long requestBytes = stream.Length; stream.Position = 0;
            var connection = new OperatorConnection(registration, _ => throw new InvalidOperationException("Must target the existing session."), _ => false);
            await connection.Serve(stream, peer == "unverified" ? null : Environment.ProcessId, CancellationToken.None);
            Assert.Equal(peer == "verified" ? 1 : 0, session.Changes);
            if (peer is "verified" or "foreign-owner")
            {
                stream.Position = requestBytes;
                var result = await TiaOpenness.Shared.OperatorProtocol.Read<TiaOpenness.Shared.OperatorHandshakeResult>(stream, CancellationToken.None);
                Assert.Equal(peer == "verified", result.Accepted);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("attach")]
    [InlineData("create")]
    public void Consent_cannot_be_added_to_an_ordinary_operator_handshake(string action)
    {
        var request = new TiaOpenness.Shared.OperatorHandshake { Action = action,
            SessionKey = action == "attach" ? Guid.NewGuid().ToString("N") : null, NativeCrossReferencesAllowed = true,
            Workbench = new() { Pid = Environment.ProcessId, Version = "fixture" } };
        Assert.Throws<InvalidDataException>(() => TiaOpenness.Shared.OperatorProtocol.Validate(request));
    }

    private static string Xml(string name, string body = "") => "<Document><SW.Blocks.FC><AttributeList><Name>" + name
        + "</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit>" + body
        + "</SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task Oversized_or_empty_bounds_are_refused_before_export(int maximum)
    {
        using var worker = new ExportWorker();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PlcCrossReferenceAnalysis.Run(worker, new JsonObject {
            ["softwarePath"] = "devices/Station/PLC", ["groupPath"] = "", ["recursive"] = true, ["maxBlocks"] = maximum }, CancellationToken.None));
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public void Per_block_rows_keep_callers_callees_global_sites_and_unresolved_calls()
    {
        string directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "Caller.xml"), Xml("Caller", "<CallInfo Name='Callee'/><CallInfo Name='Missing'/><Access Scope='GlobalVariable'><Symbol><Component Name='Motor'/><Component Name='Run'/></Symbol></Access>"));
            File.WriteAllText(Path.Combine(directory, "Callee.xml"), Xml("Callee"));
            File.WriteAllText(Path.Combine(directory, "bad.xml"), "<broken>");
            var result = PlcOfflineReferences.Analyze(directory, "blocks", "", 10, 0, 500);
            var caller = result["rows"]!.AsArray().Single(n => (string?)n!["name"] == "Caller")!;
            var callee = result["rows"]!.AsArray().Single(n => (string?)n!["name"] == "Callee")!;
            Assert.Equal("Callee.xml", (string?)caller["callees"]![0]!["callee"]);
            Assert.Equal("Caller.xml", (string?)callee["callers"]![0]!["caller"]);
            Assert.Equal("\"Motor\".\"Run\"", (string?)caller["referenceSites"]![0]!["symbol"]);
            Assert.Equal(0, (int)caller["referenceSites"]![0]!["networkIndex"]!);
            Assert.Single(result["failures"]!.AsArray());
            Assert.Single(result["unresolvedCalls"]!.AsArray());
            Assert.False((bool)result["coverageComplete"]!);
            Assert.False((bool)result["nativeCrossReferencesQueried"]!);
            Assert.False((bool)result["safeToDelete"]!);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("v4")]
    [InlineData("v5")]
    public void Published_SimaticML_networks_keep_namespaces_network_indices_and_quoted_symbols(string schema)
    {
        string directory = NewDirectory();
        try
        {
            // Synthetic export-format documents, including the real CompileUnit/NetworkSource/FlgNet nesting.
            string caller = $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <Document>
                  <Engineering version="V21" />
                  <SW.Blocks.FC ID="0">
                    <AttributeList><Name>Caller</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
                    <ObjectList>
                      <SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits">
                        <AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage><NetworkSource>
                          <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/{{schema}}">
                            <Parts><Access Scope="LocalVariable" UId="1"><Symbol><Component Name="Local" /></Symbol></Access></Parts><Wires />
                          </FlgNet>
                        </NetworkSource></AttributeList>
                      </SW.Blocks.CompileUnit>
                      <SW.Blocks.CompileUnit ID="2" CompositionName="CompileUnits">
                        <AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage><NetworkSource>
                          <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/{{schema}}">
                            <Parts>
                              <Call UId="21"><CallInfo Name="Callee" BlockType="FC"><Parameter Name="Enable" Section="Input" Type="Bool" /></CallInfo></Call>
                              <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="Motor.Run" /></Symbol></Access>
                              <Access Scope="GlobalConstant" UId="23"><Symbol><Component Name="Motor" /><Component Name="Run" /></Symbol></Access>
                            </Parts><Wires />
                          </FlgNet>
                        </NetworkSource></AttributeList>
                      </SW.Blocks.CompileUnit>
                    </ObjectList>
                  </SW.Blocks.FC>
                </Document>
                """;
            string callerFile = Path.Combine(directory, "Caller.xml"), calleeFile = Path.Combine(directory, "Callee.xml");
            File.WriteAllText(callerFile, caller); File.WriteAllText(calleeFile, Xml("Callee"));
            var report = PlcCrossReferenceAnalysis.Report("devices/Station/PLC", directory, new JsonObject {
                ["Items"] = new JsonArray(new JsonObject { ["Status"] = "exported", ["OutputFile"] = callerFile },
                    new JsonObject { ["Status"] = "exported", ["OutputFile"] = calleeFile }), ["InventoryComplete"] = true });
            Assert.True((bool)report["exportComplete"]!);
            var analysis = report["analysis"]!;
            Assert.Equal(2, (int)analysis["indexedBlocks"]!);
            Assert.Empty(analysis["failures"]!.AsArray());
            Assert.Empty(analysis["unresolvedCalls"]!.AsArray());
            var row = analysis["rows"]!.AsArray().Single(n => (string?)n!["name"] == "Caller")!;
            Assert.Equal("Callee.xml", (string?)Assert.Single(row["callees"]!.AsArray())!["callee"]);
            Assert.Equal(1, (int)row["callees"]![0]!["networkIndex"]!);
            Assert.Equal(new[] { "\"Motor.Run\"", "\"Motor\".\"Run\"" },
                row["referenceSites"]!.AsArray().Select(site => (string?)site!["symbol"]));
            Assert.All(row["referenceSites"]!.AsArray(), site => Assert.Equal(1, (int)site!["networkIndex"]!));
            Assert.False((bool)report["coverageComplete"]!);
            Assert.False((bool)report["nativeCrossReferencesQueried"]!);
            Assert.False((bool)analysis["safeToDelete"]!);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Scope_counts_only_published_exports_and_retains_partial_failures(int successful)
    {
        string directory = NewDirectory();
        try
        {
            var items = new JsonArray();
            for (int i = 0; i < 3; i++)
            {
                string file = Path.Combine(directory, "block" + i + ".xml");
                if (i < successful) File.WriteAllText(file, Xml("Block" + i));
                items.Add(new JsonObject { ["Status"] = i < successful ? "exported" : i == successful ? "failed" : "not-attempted", ["OutputFile"] = file });
            }
            // A failed native export can retain valid XML under a staging directory. It is not a published block.
            string staged = Path.Combine(directory, ".tia-export-failed"); Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "unpublished.xml"), Xml("Unpublished"));
            var result = PlcCrossReferenceAnalysis.Report("devices/Station/PLC", directory, new JsonObject {
                ["Items"] = items, ["GroupPath"] = "Group", ["Recursive"] = true, ["InventoryComplete"] = true, ["RequiresSessionReset"] = true });
            Assert.Equal(successful, (int)result["exportedBlocks"]!);
            Assert.Contains("Covers " + successful + " exported blocks of software devices/Station/PLC", (string)result["scope"]!);
            Assert.Contains("blocks not exported are not analysed", (string)result["scope"]!);
            Assert.False((bool)result["exportComplete"]!);
            Assert.False((bool)result["dataComplete"]!);
            Assert.False((bool)result["nativeCrossReferencesQueried"]!);
            Assert.True((bool)result["requiresSessionReset"]!);
            Assert.Equal(successful != 0, (bool)result["analysisQueried"]!);
            if (successful > 0) Assert.Equal(successful, (int)result["analysis"]!["indexedBlocks"]!);
            var envelope = PlcCrossReferenceAnalysis.Result("21", "partial-query", result);
            Assert.False(envelope.Ok);
            Assert.Equal(successful == 0 ? TiaMcp.Logic.V4.Outcome.Failed : TiaMcp.Logic.V4.Outcome.Partial, envelope.Meta.Outcome);
            Assert.Equal(successful == 0 ? TiaMcp.Logic.V4.ErrorCode.IoFailed : TiaMcp.Logic.V4.ErrorCode.PartialFailure, envelope.Error!.Code);
            Assert.True(envelope.Meta.RequiresSessionReset);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Mixed_indexing_failures_remain_partial_even_when_every_export_was_published()
    {
        string directory = NewDirectory();
        try
        {
            string valid = Path.Combine(directory, "Valid.xml"), invalid = Path.Combine(directory, "Invalid.xml");
            File.WriteAllText(valid, Xml("Valid")); File.WriteAllText(invalid, "<broken>");
            var result = PlcCrossReferenceAnalysis.Report("PLC", directory, new JsonObject {
                ["Items"] = new JsonArray(new JsonObject { ["Status"] = "exported", ["OutputFile"] = valid },
                    new JsonObject { ["Status"] = "exported", ["OutputFile"] = invalid }), ["InventoryComplete"] = true });
            Assert.True((bool)result["exportComplete"]!);
            Assert.True((bool)result["analysisQueried"]!);
            Assert.Equal(1, (int)result["analysis"]!["indexedBlocks"]!);
            Assert.Single(result["analysis"]!["failures"]!.AsArray());
            var envelope = PlcCrossReferenceAnalysis.Result("21", "partial-index", result);
            Assert.False(envelope.Ok);
            Assert.Equal(TiaMcp.Logic.V4.Outcome.Partial, envelope.Meta.Outcome);
            Assert.Equal(TiaMcp.Logic.V4.ErrorCode.PartialFailure, envelope.Error!.Code);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void No_supported_export_is_reported_as_not_queried_with_a_failure()
    {
        string directory = NewDirectory();
        try
        {
            string file = Path.Combine(directory, "unsupported.xml"); File.WriteAllText(file, "<Document/>");
            var result = PlcCrossReferenceAnalysis.Report("PLC", directory, new JsonObject {
                ["Items"] = new JsonArray(new JsonObject { ["Status"] = "exported", ["OutputFile"] = file }), ["InventoryComplete"] = true });
            Assert.False((bool)result["analysisQueried"]!);
            Assert.NotNull(result["analysisFailure"]);
            Assert.Contains("Not queried or partial never means zero references", (string)result["scope"]!);
            var envelope = PlcCrossReferenceAnalysis.Result("21", "unindexed-query", result);
            Assert.False(envelope.Ok);
            Assert.Equal(TiaMcp.Logic.V4.Outcome.Failed, envelope.Meta.Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task One_call_previews_exports_then_analyses_without_native_cross_reference_operations()
    {
        using var worker = new ExportWorker();
        var report = await PlcCrossReferenceAnalysis.Run(worker, new JsonObject {
            ["softwarePath"] = "devices/Station/PLC", ["groupPath"] = "", ["recursive"] = true, ["maxBlocks"] = 2 }, CancellationToken.None);
        Assert.Equal(new[] { "ExportBlocks:preview", "ExportBlocks:apply" }, worker.Calls);
        Assert.True((bool)report["analysisQueried"]!);
        Assert.True((bool)report["exportComplete"]!);
        Assert.False((bool)report["coverageComplete"]!);
        Assert.Equal(2, (int)report["analysis"]!["indexedBlocks"]!);
        Assert.StartsWith(TiaOpenness.Shared.DataLocations.Current.ReportsDirectory, (string)report["exportDirectory"]!);
        var envelope = PlcCrossReferenceAnalysis.Result("21", "completed-query", report);
        Assert.True(envelope.Ok);
        Assert.Equal(TiaMcp.Logic.V4.Completeness.Partial, envelope.Meta.Completeness);
        Assert.Contains(envelope.Meta.Warnings, warning => warning.Code == TiaMcp.Logic.V4.WarningCode.UnverifiedBehavior);
        Assert.Contains(envelope.Meta.Warnings, warning => warning.Code == TiaMcp.Logic.V4.WarningCode.IncompleteData);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Tool_is_available_and_read_only_without_any_consent_parameter(string release)
    {
        using var worker = new ExportWorker();
        Assert.True(FoundationTools.Available(PlcCrossReferenceAnalysis.Definition, release));
        var registeredTools = LegacyHostToolRegistry.Create(worker, release, nativeSessionConfigured: false);
        Assert.Single(registeredTools,
            registered => registered.ProtocolTool.Name == "AnalyzePlcCrossReferences");
        Assert.All(registeredTools, registered => Assert.DoesNotContain(
            registered.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject(),
            property => property.Name is "allowNativeCrossReferences" or "nativeCrossReferencesEnabled" or "nativeCrossReferencesAllowed"
                or "nativeCrossReferenceSessionFile" or "nativeCrossReferenceSessionSecret"));
        var tool = PlcCrossReferenceAnalysis.Create(worker, release);
        Assert.Equal("READ", ToolTaxonomy.OperationOf(tool.ProtocolTool.Name, tool.ProtocolTool.Description).Operation);
        Assert.False(((FoundationTool)tool).IsWrite);
        Assert.Equal(release is "14sp1" or "15.1"
            ? new[] { "softwarePath", "groupPath", "recursive", "maxBlocks" }
            : new[] { "softwarePath", "groupPath", "recursive", "maxBlocks", "unitName" }, tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xref-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private sealed class ExportWorker : IFoundationWorker
    {
        internal readonly List<string> Calls = new();
        private string? directory;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Assert.Equal("ExportBlocks", operation);
            bool preview = (bool)arguments["dryRun"]!;
            Calls.Add(operation + (preview ? ":preview" : ":apply"));
            directory = (string)arguments["exportPath"]!;
            if (!preview) Assert.Equal(new string('a', 64), (string?)arguments["expectedInventoryHash"]);
            var items = new JsonArray();
            foreach (string name in new[] { "A", "B" })
            {
                string file = Path.Combine(directory, name + ".xml");
                if (!preview) File.WriteAllText(file, Xml(name));
                items.Add(new JsonObject { ["ObjectPath"] = name, ["OutputFile"] = file, ["Status"] = preview ? "planned" : "exported",
                    ["XmlContent"] = "native-xml", ["Warnings"] = new JsonArray(), ["Evidence"] = new JsonObject() });
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = "project.ap21", ["SoftwarePath"] = "devices/Station/PLC",
                ["GroupPath"] = "", ["Recursive"] = true, ["InventoryHash"] = new string('a', 64), ["InventoryComplete"] = true, ["RequiresSessionReset"] = false, ["Items"] = items });
        }
        public void Dispose() { if (directory != null) Directory.Delete(directory, true); }
    }
    public class ConsentServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
    private sealed class ConsentFileTool : McpServerTool
    {
        internal int Calls;
        public override Tool ProtocolTool { get; } = new Tool { Name = "SaveExportContent", InputSchema = JsonSerializer.SerializeToElement(new {
            type = "object", properties = new { outputPath = new { type = "string" } }, required = new[] { "outputPath" }, additionalProperties = false }) };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++; throw new InvalidOperationException("Consent must never reach a file tool.");
        }
    }
    private sealed class ConsentSession(bool owned) : IHostSession
    {
        internal int Changes;
        public string SessionKey { get; } = Guid.NewGuid().ToString("N");
        public string Instructions => "fixture";
        public bool OperatorOwned => owned;
        public CancellationToken Closed => CancellationToken.None;
        public IReadOnlyList<McpServerTool> Tools => [];
        public IReadOnlyList<McpServerTool> OperatorTools => [];
        public JsonObject Snapshot() => new() { ["sessionKey"] = SessionKey, ["releaseKey"] = "21" };
        public event Action? StateChanged { add { } remove { } }
        public void BindAi(IMcpServer server) { }
        public IDisposable AttachOperator() => new Membership();
        public void SetNativeCrossReferences(bool enabled, int workbenchPid) { Assert.True(enabled); Changes++; }
        public void Dispose() { }
        private sealed class Membership : IDisposable { public void Dispose() { } }
    }
}

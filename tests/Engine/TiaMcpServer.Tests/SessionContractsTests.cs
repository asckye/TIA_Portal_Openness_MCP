using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class SessionContractsTests : IDisposable
    {
        // Admission probes use the group's declared CLR types. They fail loudly if
        // an invalid request reaches the operation; no session/native fake runs it.
        public static class AdmissionProbes
        {
            internal static int Calls;
            [McpServerTool(Name = "GetObjectIdentifier"), System.ComponentModel.Description("[L2][Project][READ]")]
            public static CallToolResult Read(string[]? devicePath = null, string[]? itemPath = null)
                => Entered();
            [McpServerTool(Name = "ShowObjectInEditor"), System.ComponentModel.Description("[L2][Project][WRITE]")]
            public static CallToolResult Show(string[]? devicePath = null, string[]? itemPath = null, bool dryRun = true)
                => Entered();
            [McpServerTool(Name = "RunToolTransaction"), System.ComponentModel.Description("[L2][Project][WRITE]")]
            public static CallToolResult Transaction(ToolCall[] calls, string text, bool dryRun = true)
                => Entered();
            private static CallToolResult Entered() { Calls++; throw new InvalidOperationException("Admission reached the operation."); }
        }

        public SessionContractsTests()
        {
            AdmissionProbes.Calls = 0;
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(AdmissionProbes) }),
                () => false, new HashSet<string>());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.Equal(result.StructuredContent!.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
            Assert.Equal(!V4Json.Deserialize<Envelope>(text).Ok, result.IsError);
            return result.StructuredContent.AsObject();
        }

        [Theory]
        [InlineData("GetObjectIdentifier", "{\"devicePathJson\":\"[]\"}")]
        [InlineData("GetObjectIdentifier", "{\"devicePath\":\"[]\"}")]
        [InlineData("GetObjectIdentifier", "{\"itemPath\":\"[]\"}")]
        [InlineData("GetObjectIdentifier", "{\"itemPath\":[null]}")]
        [InlineData("GetObjectIdentifier", "{\"devicePath\":null}")]
        [InlineData("GetObjectIdentifier", "{\"devicePath\":[1]}")]
        [InlineData("ShowObjectInEditor", "{\"itemPathJson\":\"[]\"}")]
        [InlineData("ShowObjectInEditor", "{\"devicePath\":\"[]\"}")]
        [InlineData("ShowObjectInEditor", "{\"itemPath\":{}}")]
        [InlineData("RunToolTransaction", "{\"callsJson\":\"[]\",\"text\":\"fixture\"}")]
        [InlineData("RunToolTransaction", "{\"calls\":\"[]\",\"text\":\"fixture\"}")]
        [InlineData("RunToolTransaction", "{\"calls\":[{\"name\":\"GetSessionState\",\"arguments\":\"{}\"}],\"text\":\"fixture\"}")]
        [InlineData("RunToolTransaction", "{\"calls\":[{\"Name\":\"GetSessionState\",\"arguments\":{}}],\"text\":\"fixture\"}")]
        public async Task TypedRejectionsUseOneBoundaryBeforeOperation(string name, string json)
        {
            var method = McpServer.AllToolMethods()[name];
            var direct = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), name, method);
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>()) {
                Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
            };
            var results = new[] { await direct.InvokeAsync(request), McpServer.CallTool(name, Args(json)),
                McpServer.ReadToolBatch(new[] { new ToolCall(name, Args(json)) }),
                McpServer.PreviewToolBatch(new[] { new ToolCall(name, Args(json)) }, "Fixture") };
            foreach (var result in results)
            {
                var body = Body(result);
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
            Assert.Equal(0, AdmissionProbes.Calls);
        }

        [Theory]
        [InlineData("ConnectIsolatedPortal", "{\"success\":false,\"verified\":false}", true, true, "unknown", "unknown", "unknown")]
        [InlineData("SaveProject", "{}", true, true, "unknown", "unknown", "unknown")]
        [InlineData("CloseProject", "{\"success\":true}", true, true, "succeeded", "completed", "complete")]
        [InlineData("GetSessionState", "{\"success\":true}", false, false, "succeeded", "read-only", "complete")]
        [InlineData("GetPortalInfo", "{\"success\":true,\"projectError\":\"unavailable\"}", false, false, "succeeded", "read-only", "partial")]
        [InlineData("GetPortalInfo", "{}", false, false, "read-failed", "read-only", "none")]
        [InlineData("GetObjectIdentifier", "{\"success\":false,\"status\":\"InvalidState\",\"tool\":\"GetObjectIdentifier\"}", false, false, "rejected-before-operation", "not-started", "none")]
        [InlineData("RestartOpennessWorker", "{\"success\":false,\"enabled\":false}", true, false, "rejected-before-operation", "not-started", "none")]
        [InlineData("EnsureOpennessUserGroup", "{\"success\":false}", true, false, "failed", "completed", "none")]
        [InlineData("RunToolTransaction", "{\"success\":false,\"mayHaveChanged\":true,\"rollbackCompleted\":true}", true, false, "failed", "completed", "none")]
        public void EnvelopeUsesEvidence(string name, string metadata, bool writes, bool current, string outcome, string execution, string completeness)
        {
            var body = Body(SessionToolContract.Map(name, new ResponseMessage { Message = "Success is only prose", Meta = JsonNode.Parse(metadata)!.AsObject() }, writes, current));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal(current ? "current" : "not-applicable", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Equal("Success is only prose", (string?)body["data"]!["summary"]);
        }

        [Fact]
        public void ReadinessIsDiagnosticDataAndDoesNotDisappear()
        {
            var body = Body(SessionToolContract.Map("InitializeEnvironment", new {
                Ready = false, RecommendedNextTool = "EnsureOpennessUserGroup",
                Environment = new { OpennessGroupOk = false }, Portal = new { Connected = false },
                Meta = new JsonObject { ["success"] = true }
            }, false, false));
            Assert.True((bool)body["ok"]!);
            Assert.False((bool)body["data"]!["ready"]!);
            Assert.Equal("EnsureOpennessUserGroup", (string?)body["data"]!["recommendedNextTool"]);
            Assert.False((bool)body["data"]!["environment"]!["opennessGroupOk"]!);
        }

        [Fact]
        public void TransactionUnknownRetainsPriorResultsAndWinsOverRollback()
        {
            var child = Body(SessionToolContract.Failure("SetUnifiedObjectProperties", new Exception("private"), true, false));
            var body = Body(SessionToolContract.Map("RunToolTransaction", new ResponseMessage {
                Meta = new JsonObject { ["success"] = false, ["rollbackCompleted"] = true,
                    ["calls"] = new JsonArray(new JsonObject { ["name"] = "SetUnifiedObjectProperties" }, new JsonObject { ["name"] = "CreatePlcTypeGroup" }),
                    ["results"] = new JsonArray(
                    new JsonObject { ["name"] = "SetUnifiedObjectProperties", ["result"] = child }) }
            }, true, false));
            Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
            Assert.Equal(child.ToJsonString(), body["data"]!["items"]![0]!["result"]!.ToJsonString());
            Assert.Equal("NOT_EXECUTED", (string?)body["data"]!["items"]![1]!["result"]!["error"]!["code"]);
            Assert.Equal("not-started", (string?)body["data"]!["items"]![1]!["result"]!["meta"]!["execution"]);
        }

        [Fact]
        public void ReportFailureRetainsCompletedFilesAndTheDomainSummary()
        {
            var body = Body(SessionToolContract.Map("GenerateAcceptanceReport", new {
                Summary = "Environment is not ready", Message = "Report generated with failures",
                MarkdownPath = "report.md", JsonPath = "report.json", Meta = new { Success = false }
            }, true, false));
            Assert.Equal("failed", (string?)body["meta"]!["outcome"]);
            Assert.Equal("completed", (string?)body["meta"]!["execution"]);
            Assert.Equal("Environment is not ready", (string?)body["data"]!["summary"]);
            Assert.Equal("Report generated with failures", (string?)body["data"]!["operationSummary"]);
            Assert.Equal("report.md", (string?)body["data"]!["markdownPath"]);
            Assert.Equal("report.json", (string?)body["data"]!["jsonPath"]);
        }

        [Fact]
        public void AllGroupRegistrationsUseGeneratedNamesAndConcreteInputs()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
            Assert.NotNull(directory);
            var stems = new[] { "SessionTools", "ProjectSessionTools", "DiagnosticsTools", "EngineeringDiagnosticsTools", "McpServer.Doctor", "McpServer.Maintenance", "McpServer.Worker" };
            var source = string.Join("\n", stems.Select(stem => File.ReadAllText(Path.Combine(directory!.FullName, "src/Engine/ModelContextProtocol/Tools", stem + ".cs"))));
            var names = Regex.Matches(source, "McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(31, names.Length);
            Assert.Equal(31, names.Distinct().Count());
            var generated = ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey).ToDictionary(row => (string)row!["name"]!);
            foreach (var name in names)
            {
                Assert.Equal(name, (string?)generated[name]["currentName"]);
                Assert.Equal(4, (int)generated[name]["envelopeVersion"]!);
            }
            Assert.Contains("string[]? devicePath = null", source);
            Assert.Contains("string[]? itemPath = null", source);
            Assert.Contains("ToolCall[] calls", source);
            Assert.Contains("_session.GetState()", source);
            Assert.Contains("_session.ReadPortalInfo(includeProcesses,includeSessions,includeProducts)", source);
        }
    }
}

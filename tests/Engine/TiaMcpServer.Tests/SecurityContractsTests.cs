using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.Siemens.Services
{
    internal static class SecurityServiceFixture
    {
        internal static int Calls;
        internal static object[] Arguments = Array.Empty<object>();
        internal static ResponseMessage Response = new ResponseMessage();
        internal static ResponseMessage Invoke(params object[] arguments)
        { Calls++; Arguments = arguments; return Response; }
    }
    internal sealed class CertificateManagementService
    {
        public ResponseMessage ManagePlcCertificate(string devicePathJson, string itemPathJson, string action, string certificateId="", string filePath="", string usage="", string propertiesJson="{}", string assignment="", bool dryRun=true, string assignmentItemPathJson="", string subjectAlternativeNamesJson="[]", string password="") => SecurityServiceFixture.Invoke(devicePathJson, itemPathJson, action, certificateId, filePath, usage, propertiesJson, assignment, dryRun, assignmentItemPathJson, subjectAlternativeNamesJson, password);
    }
    internal sealed class ProjectSecurityService
    {
        public ResponseMessage ReadProjectUserManagement(string category="users", string name="", string devicePathJson="[]", string itemPathJson="[]", int offset=0, int limit=100) => SecurityServiceFixture.Invoke(category, name, devicePathJson, itemPathJson, offset, limit);
        public ResponseMessage ManageProjectUserManagement(string action, string name, string password="", string roleName="", string rightName="", string comment="", string group="", string devicePathJson="[]", string itemPathJson="[]", bool confirmChange=false, bool dryRun=true) => SecurityServiceFixture.Invoke(action, name, password, roleName, rightName, comment, group, devicePathJson, itemPathJson, confirmChange, dryRun);
        public ResponseMessage ReadProjectProtection() => SecurityServiceFixture.Invoke();
        public ResponseMessage ManageMultiuserSession(string action="read", string serverName="", string projectName="", string protocol="Https", string host="", int port=0, string commitComment="", int offset=0, int limit=100, bool confirmChange=false, bool dryRun=true) => SecurityServiceFixture.Invoke(action, serverName, projectName, protocol, host, port, commitComment, offset, limit, confirmChange, dryRun);
        public ResponseMessage CompareLibraries(string leftLibraryName="", string rightLibraryName="", bool includeIdentical=false, int maxDepth=8, int offset=0, int limit=100) => SecurityServiceFixture.Invoke(leftLibraryName, rightLibraryName, includeIdentical, maxDepth, offset, limit);
        public ResponseMessage CompareProjects(string kind, string softwarePath="", string devicePathJson="[]", string itemPathJson="[]", string targetProjectName="", string targetSoftwarePath="", string targetDevicePathJson="[]", string targetItemPathJson="[]", string targetLibraryName="", bool includeIdentical=false, int maxDepth=8, int offset=0, int limit=100) => SecurityServiceFixture.Invoke(kind, softwarePath, devicePathJson, itemPathJson, targetProjectName, targetSoftwarePath, targetDevicePathJson, targetItemPathJson, targetLibraryName, includeIdentical, maxDepth, offset, limit);
        public ResponseMessage ReadProjectSettings(string folderPath="", string customIdentityKey="", int offset=0, int limit=100) => SecurityServiceFixture.Invoke(folderPath, customIdentityKey, offset, limit);
    }
    internal sealed class SafetyManagementService
    {
        public ResponseMessage ManagePlcSafety(string softwarePath, string action="read", string runtimeGroup="", string propertiesJson="{}", bool dryRun=true, string password="", bool confirmSafetyChange=false, string mainSafetyBlockPath="", string mainSafetyInstanceDbPath="") => SecurityServiceFixture.Invoke(softwarePath, action, runtimeGroup, propertiesJson, dryRun, password, confirmSafetyChange, mainSafetyBlockPath, mainSafetyInstanceDbPath);
        public ResponseMessage ManageSafetyGlobalSettings(string action="read",string propertiesJson="{}",bool dryRun=true) => SecurityServiceFixture.Invoke(action, propertiesJson, dryRun);
        public ResponseMessage ReadSafetyBlockSignatures(string softwarePath, string blockPath="", bool includeProgramSignatures=true, int offset=0, int limit=100) => SecurityServiceFixture.Invoke(softwarePath, blockPath, includeProgramSignatures, offset, limit);
        public ResponseMessage ExportSafetyPrintout(string softwarePath, string filePath, string printer="MicrosoftPrintToPdf", string option="All", string documentLayout="", bool dryRun=true) => SecurityServiceFixture.Invoke(softwarePath, filePath, printer, option, documentLayout, dryRun);
    }
    internal sealed class SafetyValidationService
    {
        public ResponseMessage ReadSafetyActivationTests(string groupPathJson="[]", string name="", bool includeSafetyFunctions=false, bool includeConditions=false, int offset=0, int limit=100) => SecurityServiceFixture.Invoke(groupPathJson, name, includeSafetyFunctions, includeConditions, offset, limit);
        public ResponseMessage ManageSafetyActivationTest(string name, string action="read", string groupPathJson="[]", string evaluationDeviceName="", string sourceName="", string masterCopyPath="", string libraryName="", string newValue="", string filePath="", string exportOptions="", string documentInfoOptions="", string importOptions="", bool confirmDelete=false, bool dryRun=true) => SecurityServiceFixture.Invoke(name, action, groupPathJson, evaluationDeviceName, sourceName, masterCopyPath, libraryName, newValue, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun);
        public ResponseMessage ManageSafetyActivationTestGroup(string action="read", string groupPathJson="[]", string name="", string masterCopyPath="", string libraryName="", string newName="", bool confirmDelete=false, bool dryRun=true) => SecurityServiceFixture.Invoke(action, groupPathJson, name, masterCopyPath, libraryName, newName, confirmDelete, dryRun);
        public ResponseMessage ManageSafetyFunction(string activationTest, string action="read", string groupPathJson="[]", string name="", string sourceName="", string propertiesJson="{}", string filePath="", string exportOptions="", string documentInfoOptions="", string importOptions="", bool confirmDelete=false, bool dryRun=true) => SecurityServiceFixture.Invoke(activationTest, action, groupPathJson, name, sourceName, propertiesJson, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun);
        public ResponseMessage ManageSafetyFunctionCondition(string activationTest, string safetyFunction, string action="read", string groupPathJson="[]", int index=-1, string deviceName="", string signalUsage="", string signalName="", string propertiesJson="{}", bool dryRun=true) => SecurityServiceFixture.Invoke(activationTest, safetyFunction, action, groupPathJson, index, deviceName, signalUsage, signalName, propertiesJson, dryRun);
    }
    internal sealed class SecurityDeepService
    {
        public ResponseMessage ManageSyslogServers(string scope="project", string action="read", string name="", string propertiesJson="{}", string attributesJson="{}", string devicePathJson="[]", string itemPathJson="[]", string serverAddress="", int serverPort=0, bool confirmDelete=false, bool dryRun=true) => SecurityServiceFixture.Invoke(scope, action, name, propertiesJson, attributesJson, devicePathJson, itemPathJson, serverAddress, serverPort, confirmDelete, dryRun);
        public ResponseMessage ManagePasswordPolicy(string action="read", string target="", string propertiesJson="{}", bool confirmChange=false, bool dryRun=true) => SecurityServiceFixture.Invoke(action, target, propertiesJson, confirmChange, dryRun);
        public ResponseMessage ManageUmcUsers(string kind="user", string action="read", string name="", string newName="", string roleName="", string serverUserName="", string serverPassword="", bool confirmChange=false, bool dryRun=true, int offset=0, int limit=100) => SecurityServiceFixture.Invoke(kind, action, name, newName, roleName, serverUserName, serverPassword, confirmChange, dryRun, offset, limit);
    }
}

namespace TiaMcpServer.Tests
{
    public sealed class SecurityContractsTests : IDisposable
    {
        private readonly ToolCatalog catalog;
        private static readonly string[] Names = {
            "ManagePlcCertificate", "GetProjectUserManagement", "ManageProjectUserManagement", "GetProjectProtection",
            "ManageMultiuserSession", "CompareLibraries", "CompareProjects", "GetProjectSettings", "ManagePlcSafety",
            "ManageSafetyGlobalSettings", "GetSafetyBlockSignatures", "ExportSafetyPrintout", "ListSafetyActivationTests",
            "ManageSafetyActivationTest", "ManageSafetyActivationTestGroup", "ManageSafetyFunction", "ManageSafetyFunctionCondition",
            "ManageSyslogServers", "ManagePasswordPolicy", "ManageUmcUsers"
        };

        public SecurityContractsTests()
        {
            SecurityServiceFixture.Calls = 0;
            SecurityServiceFixture.Response = new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["operationSuccess"] = true } };
            catalog = new ToolCatalog(new[] { typeof(CertificateManagementTools), typeof(ProjectSecurityTools), typeof(SafetyManagementTools), typeof(SecurityDeepTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection()
                .AddSingleton<CertificateManagementService>().AddSingleton<ProjectSecurityService>().AddSingleton<SafetyManagementService>()
                .AddSingleton<SafetyValidationService>().AddSingleton<SecurityDeepService>().AddEngine(false, catalog).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            var body = JsonNode.Parse(((TextContentBlock)Assert.Single(result.Content)).Text)!.AsObject();
            Assert.True(JsonNode.DeepEquals(body, result.StructuredContent));
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            return body;
        }
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };

        [Fact]
        public void RegistrationsUseTheReviewedNamesAndConcreteTypes()
        {
            var expected = Names.Where(n => !Siemens.ToolVersionPolicy.V21Only.ContainsKey(n));
            Assert.Equal(expected.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            foreach (var pair in catalog.Methods)
            {
                Assert.Equal(typeof(CallToolResult), pair.Value.ReturnType);
                foreach (var parameter in pair.Value.GetParameters())
                {
                    Assert.False(parameter.Name!.EndsWith("Json", StringComparison.Ordinal));
                    if (parameter.Name == "properties" || parameter.Name == "attributes") Assert.Equal(typeof(AttributeMap<Scalar>), parameter.ParameterType);
                    if (parameter.Name == "subjectAlternativeNames") Assert.Equal(typeof(SubjectAlternativeName[]), parameter.ParameterType);
                    if (parameter.Name.EndsWith("ItemPath", StringComparison.Ordinal) || parameter.Name == "itemPath" || parameter.Name == "devicePath" || parameter.Name == "groupPath" || parameter.Name == "targetDevicePath")
                        Assert.Equal(typeof(string[]), parameter.ParameterType);
                }
            }
        }

        [Theory]
        [InlineData("GetProjectUserManagement", "{\"devicePath\":\"[]\"}")]
        [InlineData("GetProjectUserManagement", "{\"devicePath\":null}")]
        [InlineData("GetProjectUserManagement", "{\"devicePath\":[1]}")]
        [InlineData("GetProjectUserManagement", "{\"devicePathJson\":\"[]\"}")]
        [InlineData("GetProjectUserManagement", "{\"category\":\"users\",\"Category\":\"roles\"}")]
        [InlineData("ManagePasswordPolicy", "{\"properties\":\"{}\"}")]
        [InlineData("ManagePasswordPolicy", "{\"properties\":null}")]
        [InlineData("ManagePasswordPolicy", "{\"properties\":{\"MinimumLength\":{}}}")]
        [InlineData("ManagePasswordPolicy", "{\"Properties\":{}}")]
        [InlineData("GetProjectSettings", "{\"limit\":1.5}")]
        [InlineData("GetProjectSettings", "{\"limit\":2147483648}")]
        public async Task DirectBridgeAndBatchRejectMalformedInputsBeforeDispatch(string name, string json)
        {
            var method = catalog.Methods.Single(p => p.Key == name).Value;
            var direct = await McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), name, method).InvokeAsync(Request(name, json));
            var bridge = McpServer.CallTool(name, Args(json));
            var call = new[] { new ToolCall(name, Args(json)) };
            var batch = name.StartsWith("Get", StringComparison.Ordinal) ? McpServer.ReadToolBatch(call) : McpServer.PreviewToolBatch(call, "Fixture");
            foreach (var result in new[] { direct, bridge, batch })
            {
                var body = Body(result);
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
            Assert.Equal(0, SecurityServiceFixture.Calls);
        }

        [Fact]
        public async Task DuplicateMapKeysAreRejectedAtEveryWireBoundary()
        {
            string arguments = "{\"properties\":{\"MinimumLength\":1,\"MinimumLength\":2}}";
            var target = catalog.Methods.Single(p => p.Key == "ManagePasswordPolicy").Value;
            var bridge = typeof(McpServer).GetMethods().Single(m => m.Name == "CallTool" && m.ReturnType == typeof(CallToolResult));
            var batch = typeof(McpServer).GetMethod("PreviewToolBatch")!;
            foreach (var test in new[] {
                ("ManagePasswordPolicy", target, arguments),
                ("CallTool", bridge, "{\"name\":\"ManagePasswordPolicy\",\"arguments\":" + arguments + "}"),
                ("PreviewToolBatch", batch, "{\"operations\":[{\"name\":\"ManagePasswordPolicy\",\"arguments\":" + arguments + "}],\"expectedProject\":\"Fixture\"}") })
            {
                var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(test.Item2), test.Item1, test.Item2);
                var body = Body(await tool.InvokeAsync(Request(test.Item1, test.Item3)));
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
            Assert.Equal(0, SecurityServiceFixture.Calls);
        }

        [Fact]
        public void OmittedCollectionsKeepLegacyDefaultsAndExactSegmentsAreNotSplit()
        {
            Body(McpServer.CallTool("GetProjectUserManagement", Args("{}")));
            Assert.Equal("[]", SecurityServiceFixture.Arguments[2]);
            Assert.Equal("[]", SecurityServiceFixture.Arguments[3]);
            string json = "{\"devicePath\":[\"Group/with,delimiters\",\"PLC\\\\exact\"],\"itemPath\":[]}";
            Body(McpServer.CallTool("GetProjectUserManagement", Args(json)));
            Assert.Equal(JsonNode.Parse(json)!["devicePath"]!.ToJsonString(), SecurityServiceFixture.Arguments[2]);
            Body(McpServer.CallTool("ManagePasswordPolicy", Args("{}")));
            Assert.Equal("{}", SecurityServiceFixture.Arguments[2]);
        }

        [Theory]
        [InlineData("{\"operationSuccess\":true}", true, "succeeded", "read-only", "complete")]
        [InlineData("{\"operationSuccess\":true,\"dataComplete\":false}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":true,\"nested\":{\"valuesError\":\"private failure\"}}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":false,\"status\":\"InvalidState\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"operationSuccess\":false,\"status\":\"NotSupportedOnVersion\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"operationSuccess\":false}", true, "read-failed", "read-only", "unknown")]
        [InlineData("{\"operationSuccess\":false,\"mayHaveChanged\":false}", false, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"operationSuccess\":false,\"mayHaveChanged\":true,\"applied\":[\"Name\"]}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"operationSuccess\":true,\"mayHaveChanged\":true,\"requiresExplicitRebind\":true}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"operationSuccess\":true,\"mayHaveChanged\":true,\"verifiedAbsent\":false}", false, "failed", "completed", "complete")]
        [InlineData("{\"operationSuccess\":false,\"mayHaveWrittenFiles\":true,\"mayHaveChanged\":false,\"file\":{\"sha256\":\"evidence\"}}", false, "partial", "partial", "complete")]
        public void EnvelopesPreserveTheNativeVerdictAndSideEffectEvidence(string json, bool readOnly, string outcome, string execution, string completeness)
        {
            var body = Body(SecurityToolContract.MapResult("ManagePlcSafety", new ResponseMessage { Meta = JsonNode.Parse(json)!.AsObject() }, readOnly, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            if (json.Contains("applied")) Assert.Single(body["data"]!["applied"]!.AsArray());
            if (json.Contains("sha256")) Assert.Equal("evidence", (string?)body["data"]!["file"]!["sha256"]);
        }

        [Fact]
        public void SecretsAndRawNativeDiagnosticsAreRemovedRecursively()
        {
            var response = new ResponseMessage { Message = "accepted fixture-secret", Meta = JsonNode.Parse("{\"operationSuccess\":true,\"passwordProvided\":true,\"nested\":{\"password\":\"fixture-secret\",\"privateKey\":\"key-material\",\"rawData\":\"certificate-material\",\"name\":\"fixture-secret\",\"items\":[\"fixture-secret\"],\"valuesError\":\"native-secret\",\"readFailures\":{\"Name\":\"native-secret\"}},\"error\":\"stack-secret\",\"messageData\":{\"text\":\"native-secret\"}}")!.AsObject() };
            var body = Body(SecurityToolContract.MapResult("ManageUmcUsers", response, true, false, "fixture-secret"));
            foreach (string secret in new[] { "fixture-secret", "key-material", "certificate-material", "native-secret", "stack-secret" }) Assert.DoesNotContain(secret, body.ToJsonString());
            Assert.True((bool)body["data"]!["passwordProvided"]!);
            var escaped = Body(SecurityToolContract.Invoke("ManageUmcUsers", false, false, () => throw new InvalidOperationException("fixture-secret")));
            Assert.Equal("unknown", (string?)escaped["meta"]!["outcome"]);
            Assert.DoesNotContain("fixture-secret", escaped.ToJsonString());
        }

        [Fact]
        public void DirectBridgeAndReadBatchReturnTheSameTargetEnvelope()
        {
            var direct = Body(new ProjectSecurityTools(new ProjectSecurityService()).GetProjectProtection());
            var bridge = Body(McpServer.CallTool("GetProjectProtection", Args("{}")));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("GetProjectProtection", Args("{}")) }));
            var child = batch["data"]!["items"]![0]!["result"]!.AsObject();
            foreach (var body in new[] { direct, bridge, child })
            {
                Assert.Equal("GetProjectProtection", (string?)body["meta"]!["tool"]);
                body["meta"]!.AsObject().Remove("timestamp"); body["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridge));
            Assert.True(JsonNode.DeepEquals(direct, child));
        }

        [Fact]
        public void MigratedExamplesUseTheSharedSchemas()
        {
            foreach (var pair in catalog.Methods)
            {
                var row = TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey, engineSource: true).Single(r => (string?)r!["currentName"] == pair.Key)!;
                Assert.Equal(4, (int)row["envelopeVersion"]!);
                var schema = McpServer.ToolInputSchema(pair.Key, pair.Value);
                Assert.Null(McpServer.ValidateV4Arguments(pair.Value, JsonSerializer.SerializeToElement(row["arguments"]), schema));
                Assert.DoesNotContain("\"default\":null", schema.GetRawText());
            }
            Assert.Equal(0, SecurityServiceFixture.Calls);
        }

        [Fact]
        public void SafetyValidationNamesKeepTheirVersionGateAndGeneratedContract()
        {
            // The offline assembly already has a registration probe for the V21
            // listing name. Full-engine snapshots inspect the real tool signatures.
            foreach (string name in Names.Where(Siemens.ToolVersionPolicy.V21Only.ContainsKey))
            {
                Assert.NotEmpty(Siemens.ToolVersionPolicy.ToolProblem("20", name));
                Assert.Empty(Siemens.ToolVersionPolicy.ToolProblem("21", name));
                var row = TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries("21").Single(r => (string?)r!["currentName"] == name)!;
                Assert.Equal(4, (int)row["envelopeVersion"]!);
                Assert.DoesNotContain(TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries("20"), r => (string?)r!["currentName"] == name);
            }
        }

        [Fact]
        public void PreviewAndComparisonRetainTheirDifferentDataSemantics()
        {
            var preview = Body(SecurityToolContract.MapResult("ManagePlcSafety", new ResponseMessage {
                Meta = JsonNode.Parse("{\"operationSuccess\":true,\"dryRun\":true,\"action\":\"updateSettings\",\"mayHaveChanged\":false}")!.AsObject() }, true, true));
            Assert.False((bool)preview["data"]!["plan"]!["executed"]!);
            Assert.Equal("read-only", (string?)preview["meta"]!["execution"]);
            var comparison = Body(SecurityToolContract.MapResult("CompareProjects", new ResponseMessage {
                Message = "Comparison read.", Meta = JsonNode.Parse("{\"operationSuccess\":true,\"summary\":{\"LeftMissing\":2},\"expectedCount\":12,\"offset\":0,\"limit\":10,\"truncated\":true}")!.AsObject() }, true, false));
            Assert.Equal(2, (int)comparison["data"]!["summary"]!["LeftMissing"]!);
            Assert.Equal(12, (int)comparison["meta"]!["paging"]!["total"]!);
            Assert.Equal("partial", (string?)comparison["meta"]!["completeness"]);
        }

        [Theory]
        [InlineData("null", "INVALID_ARGUMENT")]
        [InlineData("\"[]\"", "INVALID_ARGUMENT")]
        [InlineData("[\"server.example\"]", "INVALID_ARGUMENT")]
        [InlineData("[null]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"dns\",\"value\":\"host\"}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\"}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\",\"value\":1}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\",\"value\":\" \"}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\",\"value\":\"host\",\"extra\":true}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\",\"value\":\"host\",\"value\":\"other\"}]", "INVALID_ARGUMENT")]
        [InlineData("[{\"type\":\"Dns\",\"value\":\"host\"},{\"type\":\"Dns\",\"value\":\"HOST\"}]", "INVALID_ARGUMENT")]
        public async Task SubjectAlternativeNamesUseTheSameContractAtEveryEntry(string names, string code)
        {
            string arguments = "{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"action\":\"create\",\"usage\":\"Tls\",\"subjectAlternativeNames\":" + names + "}";
            var target = catalog.Methods.Single(p => p.Key == "ManagePlcCertificate").Value;
            var bridge = typeof(McpServer).GetMethods().Single(m => m.Name == "CallTool" && m.ReturnType == typeof(CallToolResult));
            foreach (var test in new[] {
                ("ManagePlcCertificate", target, arguments),
                ("CallTool", bridge, "{\"name\":\"ManagePlcCertificate\",\"arguments\":" + arguments + "}"),
                ("PreviewToolBatch", typeof(McpServer).GetMethod("PreviewToolBatch")!, "{\"operations\":[{\"name\":\"ManagePlcCertificate\",\"arguments\":" + arguments + "}],\"expectedProject\":\"Fixture\"}") })
            {
                var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(test.Item2), test.Item1, test.Item2);
                var body = Body(await tool.InvokeAsync(Request(test.Item1, test.Item3)));
                Assert.Equal(code, (string?)body["error"]!["code"]);
                Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            }
            Assert.Equal(0, SecurityServiceFixture.Calls);
        }

        [Fact]
        public async Task SubjectAlternativeNameLimitsAreRetainedAtEveryEntry()
        {
            await SubjectAlternativeNamesUseTheSameContractAtEveryEntry(V4Json.Serialize(Enumerable.Range(0, 65).Select(i => new { type = "Dns", value = "host" + i })), "LIMIT_EXCEEDED");
            await SubjectAlternativeNamesUseTheSameContractAtEveryEntry(V4Json.Serialize(new[] { new { type = "Dns", value = new string('a', 256) } }), "LIMIT_EXCEEDED");
        }

        [Fact]
        public void SubjectAlternativeNamesPreserveParserRulesAndNativeValues()
        {
            string json = "[{\"type\":\"Dns\",\"value\":\"Host.Example\"},{\"type\":\"Email\",\"value\":\"owner@example.test\"},{\"type\":\"IP\",\"value\":\"192.0.2.1\"},{\"type\":\"Uri\",\"value\":\"urn:example:test\"},{\"type\":\"Uri\",\"value\":\"Host.Example\"}]";
            var names = DomainValidation.Read<SubjectAlternativeName[]>(json);
            var legacy = Siemens.SecurityDeepLogic.ParseSubjectAlternativeNames(json);
            Assert.Equal(legacy.Select(n => (n.Type, n.Value)), names.Select(n => (n.Type, n.Value)));
            Body(new CertificateManagementTools(new CertificateManagementService()).ManagePlcCertificate(new[] { "PLC" }, new[] { "CPU" }, "create", subjectAlternativeNames: names));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse((string)SecurityServiceFixture.Arguments[10])));
            Assert.Equal("", SecurityServiceFixture.Arguments[9]);
            Body(new CertificateManagementTools(new CertificateManagementService()).ManagePlcCertificate(new[] { "PLC" }, new[] { "CPU" }, "list", assignmentItemPath: Array.Empty<string>()));
            Assert.Equal("[]", SecurityServiceFixture.Arguments[9]);
            Assert.Equal("[]", SecurityServiceFixture.Arguments[10]);
            Assert.Equal(64, DomainValidation.Read<SubjectAlternativeName[]>(V4Json.Serialize(Enumerable.Range(0, 64).Select(i => new { type = "Dns", value = i + new string('a', 253) }))).Length);
            var schema = McpServer.ToolInputSchema("ManagePlcCertificate", catalog.Methods.Single(p => p.Key == "ManagePlcCertificate").Value)
                .GetProperty("properties").GetProperty("subjectAlternativeNames");
            Assert.Equal(64, schema.GetProperty("maxItems").GetInt32());
            Assert.False(schema.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(255, schema.GetProperty("items").GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32());
        }

        [Fact]
        public void TraceSignalsPreserveOmissionEmptyAndOrderedValuesAtTheServiceBoundary()
        {
            var properties = V4Json.Deserialize<AttributeMap<Scalar>>("{\"isTraced\":true,\"pretriggerTime\":5}");
            Assert.Null(JsonNode.Parse(SecurityToolContract.TraceProperties(properties, null))!["signals"]);
            Assert.Empty(JsonNode.Parse(SecurityToolContract.TraceProperties(properties, Array.Empty<string>()))!["signals"]!.AsArray());
            string[] signals = { "PLC/Tag,exact", "PLC/Tag,exact", "Second\\Signal" };
            string combined = SecurityToolContract.TraceProperties(properties, signals);
            var legacy = Siemens.SafetyValidationLogic.ValidateSafetyFunctionRequest("setTrace", "Function", "", combined, "", "", "", "", false, true);
            Assert.Equal(signals, legacy.Properties["signals"]!.AsArray().Select(s => (string)s!));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(combined)!["isTraced"], JsonValue.Create(true)));
            Assert.False(properties.ContainsKey("signals"));
        }
    }
}

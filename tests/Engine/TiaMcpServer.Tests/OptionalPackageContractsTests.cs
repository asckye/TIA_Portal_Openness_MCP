using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class OptionalPackageContractsTests : IDisposable
    {
        private readonly CfcService cfc = new CfcService();
        private readonly TestSuiteService suite = new TestSuiteService();
        private readonly V20OptionsService options = new V20OptionsService();
        private readonly OptionalEngineeringService optional = new OptionalEngineeringService();
        private readonly SpecializedExchangeService exchange = new SpecializedExchangeService();
        private readonly ToolCatalog catalog;
        private readonly Dictionary<string, McpServerTool> tools;
        public OptionalPackageContractsTests()
        {
            catalog = new ToolCatalog(new[] { typeof(CfcTools), typeof(TestSuiteTools), typeof(V20OptionsTools), typeof(OptionalEngineeringTools), typeof(SpecializedExchangeTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new CfcTools(cfc)).AddSingleton(new TestSuiteTools(suite))
                .AddSingleton(new V20OptionsTools(options)).AddSingleton(new OptionalEngineeringTools(optional)).AddSingleton(new SpecializedExchangeTools(exchange)).AddSingleton<Portal>().AddSingleton<SessionTools>().BuildServiceProvider());
            tools = catalog.Methods.ToDictionary(p => p.Key, p => (McpServerTool)new VersionPolicyTool(McpServer.CreateTool(p.Key, p.Value)));
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private int Calls => cfc.Calls + suite.Calls + options.Calls + optional.Calls + exchange.Calls;
        private static ToolArguments Arguments(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        private static JsonObject Body(CallToolResult result)
        {
            string text = ((TextContentBlock)Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            V4Json.Deserialize<Envelope>(text);
            var body = result.StructuredContent!.AsObject();
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            return body;
        }
        private static void Rejected(CallToolResult result, string code = "INVALID_ARGUMENT")
        {
            var body = Body(result);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            Assert.Equal(code, (string?)body["error"]!["code"]);
        }
        [Fact]
        public void CatalogUsesReviewedNamesAndConcreteTypes()
        {
            Assert.Equal(14, catalog.Methods.Count);
            Assert.True(tools.ContainsKey("ListTestSuiteCases"));
            Assert.True(tools.ContainsKey("ListSivarcRules"));
            Assert.True(tools.ContainsKey("ManageSivarcRule"));
            Assert.False(tools.ContainsKey("ReadTestSuiteCases"));
            Assert.False(tools.ContainsKey("ReadSiVArcRules"));
            Assert.False(tools.ContainsKey("ManageSiVArcRule"));
            foreach (var entry in catalog.Methods)
            {
                Assert.Equal(typeof(CallToolResult), entry.Value.ReturnType);
                foreach (var p in entry.Value.GetParameters())
                {
                    Assert.False(p.Name!.EndsWith("Json", StringComparison.Ordinal));
                    Assert.NotEqual(typeof(JsonElement), p.ParameterType);
                    if (p.Name == "scope") Assert.Equal(typeof(TestScope[]), p.ParameterType);
                    if (p.Name == "objectPath" || p.Name == "collectionPath") Assert.Equal(typeof(PropertyStep[]), p.ParameterType);
                    if (p.Name == "properties") Assert.Equal(typeof(AttributeMap<Scalar>), p.ParameterType);
                    if (new[] { "devicePath", "itemPath", "modifiedDevicePath", "modifiedItemPath", "files", "names", "chartNames" }.Contains(p.Name))
                        Assert.Equal(typeof(string[]), p.ParameterType);
                }
            }
        }
        [Theory]
        [InlineData("ExchangeCfcCharts", "{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/a.zip\",\"chartNames\":\"[]\"}")]
        [InlineData("ExchangeCfcCharts", "{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/a.zip\",\"chartNames\":[1]}")]
        [InlineData("RunTestSuiteCase", "{\"category\":\"styleGuide\",\"names\":null}")]
        [InlineData("RunTestSuiteCase", "{\"category\":\"styleGuide\",\"names\":[null]}")]
        [InlineData("ManageTestSuiteCase", "{\"category\":\"styleGuide\",\"name\":\"R\",\"scope\":[{\"Kind\":\"project\"}]}")]
        [InlineData("ManageTestSuiteCase", "{\"category\":\"styleGuide\",\"name\":\"R\",\"scope\":[{\"kind\":\"project\",\"name\":\"wrong\"}]}")]
        [InlineData("ManageTestSuiteCase", "{\"category\":\"styleGuide\",\"name\":\"R\",\"scope\":[{\"kind\":\"project\",\"kind\":\"project\"}]}")]
        [InlineData("ManageTestSuiteCase", "{\"category\":\"styleGuide\",\"name\":\"R\",\"scope\":\"[]\"}")]
        [InlineData("ListSivarcRules", "{\"category\":\"screens\",\"objectPath\":[{\"property\":\"Rules\",\"index\":1.5}]}")]
        [InlineData("ListSivarcRules", "{\"category\":\"screens\",\"objectPath\":[{\"property\":\"Rules\",\"extra\":0}]}")]
        [InlineData("ListSivarcRules", "{\"category\":\"screens\",\"objectPath\":[{\"property\":\"Rules\",\"name\":\"A\",\"index\":0}]}")]
        [InlineData("ListTestSuiteCases", "{\"category\":\"styleGuide\",\"CATEGORY\":\"system\"}")]
        [InlineData("ListTestSuiteCases", "{\"category\":\"styleGuide\",\"offset\":2147483648}")]
        [InlineData("ImportSinumerikAlarmTexts", "{\"devicePath\":\"[]\",\"files\":[]}")]
        [InlineData("ManageSinumerikArchive", "{\"action\":\"archive\",\"filePath\":\"C:/a.dsf\",\"modifiedItemPath\":[true]}")]
        public async Task SharedAdmissionRejectsDirectBridgeAndBatchInputs(string name, string json)
        {
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            var bridge = new VersionPolicyTool(McpServer.CreateTool("CallTool", typeof(McpServer).GetMethod("CallTool", new[] { typeof(string), typeof(ToolArguments) })!));
            Rejected(await bridge.InvokeAsync(Request("CallTool", "{\"name\":\"" + name + "\",\"arguments\":" + json + "}")));
            var batch = new VersionPolicyTool(McpServer.CreateTool("PreviewToolBatch", typeof(McpServer).GetMethod("PreviewToolBatch")!));
            Rejected(await batch.InvokeAsync(Request("PreviewToolBatch", "{\"operations\":[{\"name\":\"" + name + "\",\"arguments\":" + json + "}],\"expectedProject\":\"fixture\"}")));
            Assert.Equal(0, Calls);
        }
        [Theory]
        [InlineData("\"[]\"", "{}")]
        [InlineData("[\"Tables\",\"Rules\"]", "{}")]
        [InlineData("null", "{}")]
        [InlineData("[null]", "{}")]
        [InlineData("[{\"Property\":\"Tables\"}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"extra\":0}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"property\":\"Rules\"}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"name\":\"A\",\"index\":0}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"index\":1.5}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"index\":2147483648}]", "{}")]
        [InlineData("[{\"property\":\"Tables\",\"name\":\" \"}]", "{}")]
        [InlineData("[]", "\"{}\"")]
        [InlineData("[]", "null")]
        [InlineData("[]", "{\"Comment\":{\"nested\":1}}")]
        [InlineData("[]", "{\"Comment\":[1]}")]
        [InlineData("[]", "{\"Comment\":\"a\",\"Comment\":\"b\"}")]
        public Task SivarcTypedInputsRejectThroughEveryEntry(string path, string properties)
            => SharedAdmissionRejectsDirectBridgeAndBatchInputs("ManageSivarcRule",
                "{\"category\":\"screens\",\"name\":\"Rule\",\"action\":\"create\",\"collectionPath\":" + path + ",\"properties\":" + properties + "}");

        [Fact]
        public async Task SivarcNamedNestedPathAndExactScalarsSurviveEveryEntry()
        {
            const string path = """[{"property":"Tables","name":"Exact/Table,Name"},{"property":"RuleGroups","name":"Nested\\Folder"},{"property":"Rules"}]""";
            const string properties = """{"Comment":"Case/and,separators","Enabled":true,"LoopCount":3,"Condition":null}""";
            string json = "{\"category\":\"screens\",\"name\":\"ExactRule\",\"action\":\"create\",\"collectionPath\":" + path + ",\"properties\":" + properties + "}";
            var direct = Body(await tools["ManageSivarcRule"].InvokeAsync(Request("ManageSivarcRule", json)));
            Assert.True((bool)direct["ok"]!);
            Assert.Equal("ManageSivarcRule", (string?)direct["meta"]!["tool"]);
            Assert.Equal("read-only", (string?)direct["meta"]!["execution"]);
            Assert.True((bool)Body(McpServer.CallTool("ManageSivarcRule", Arguments(json)))["ok"]!);
            var batch = Body(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageSivarcRule", Arguments(json)) }, "Fixture"));
            Assert.True((bool)batch["ok"]!);
            Assert.Equal(3, Calls);
            Assert.Equal("ExactRule", optional.Arguments[2]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(path), JsonNode.Parse(optional.Arguments[1])));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(properties), JsonNode.Parse(optional.Arguments[4])));
            Assert.True((bool)Body(McpServer.CallTool("ManageSivarcRule", Arguments("{\"category\":\"screens\",\"name\":\"Rule\",\"action\":\"create\",\"collectionPath\":[]}")))["ok"]!);
            Assert.Equal("{}", optional.Arguments[4]);
        }

        [Fact]
        public async Task SivarcLegacyNavigationBudgetsAndSelectionRemainEnforced()
        {
            var paths = new[] {
                new[] { new PropertyStep("pArEnT") },
                new[] { new PropertyStep("Rules", index: 0) },
                new[] { new PropertyStep("Rules/Other") },
                Enumerable.Repeat(new PropertyStep("Rules"), 25).ToArray(),
                new[] { new PropertyStep("Rules", name: new string('a', 32768)) }
            };
            foreach (var path in paths)
            {
                string json = "{\"category\":\"screens\",\"name\":\"Rule\",\"action\":\"create\",\"collectionPath\":" + V4Json.Serialize(path) + "}";
                Assert.Throws<ArgumentException>(() => EngineeringObjectAddress.Parse(V4Json.Serialize(path)));
                Rejected(new OptionalEngineeringTools(optional).ManageSivarcRule("screens", path, "Rule", "create"));
                bool limit = path.Length > 24 || V4Json.Serialize(path).Length > 32768;
                string code = limit ? "LIMIT_EXCEEDED" : "INVALID_ARGUMENT";
                Rejected(await tools["ManageSivarcRule"].InvokeAsync(Request("ManageSivarcRule", json)), code);
                Rejected(McpServer.CallTool("ManageSivarcRule", Arguments(json)), code);
                var batch = Body(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageSivarcRule", Arguments(json)) }, "Fixture"));
                var result = limit ? batch : batch["data"]!["items"]![0]!["result"]!;
                Assert.Equal(code, (string?)result["error"]!["code"]);
                Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            }
            Assert.Equal(0, Calls);
        }

        [Theory]
        [InlineData("{\"success\":true,\"dryRun\":true}", true, "succeeded", "read-only")]
        [InlineData("{\"success\":true,\"mayHaveChanged\":true,\"verifiedAbsent\":true,\"verifiedOnFreshNavigation\":true}", false, "succeeded", "completed")]
        [InlineData("{\"success\":false,\"operationNotStarted\":true,\"rejectionCode\":\"INVALID_ARGUMENT\"}", false, "rejected-before-operation", "not-started")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true}", false, "unknown", "unknown")]
        public void SivarcEnvelopeKeepsPreviewDeletionAndUncertainWriteEvidence(string meta, bool dryRun, string outcome, string execution)
        {
            optional.Result = JsonNode.Parse(meta)!.AsObject();
            var body = Body(new OptionalEngineeringTools(optional).ManageSivarcRule("screens", Array.Empty<PropertyStep>(), "Rule", "delete", dryRun: dryRun));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            if (optional.Result.ContainsKey("verifiedAbsent"))
            {
                Assert.True((bool)body["data"]!["verifiedAbsent"]!);
                Assert.True((bool)body["data"]!["verifiedOnFreshNavigation"]!);
            }
        }
        [Fact]
        public void OmittedCollectionsUseEmptyDefaultsAndSegmentsRemainExact()
        {
            var input = new JsonObject { ["softwarePath"] = "PLC", ["action"] = "export", ["filePath"] = Path.Combine(Path.GetTempPath(), "tia-cfc-" + Guid.NewGuid().ToString("N") + ".xml.zip") };
            Body(McpServer.CallTool("ExchangeCfcCharts", Arguments(input.ToJsonString())));
            Assert.Equal("[]", cfc.Arguments.Last());
            var path = new[] { "Group/with,separator", "Exact\\Name" };
            Body(new V20OptionsTools(options).ManageSinumerikArchive("archive", "C:/a.dsf", devicePath: path));
            Assert.Equal(V4Json.Serialize(path), options.Arguments[2]);
            Rejected(McpServer.CallTool("ExchangeCfcCharts", Arguments("{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/a.zip\",\"chartNames\":null}")));
            Assert.Equal(2, Calls);
        }
        [Fact]
        public void ScopeAndPropertyPathsRetainLegacyParserLimits()
        {
            foreach (string json in new[] { "[{\"kind\":\"project\"}]", "[{\"kind\":\"blocks\",\"softwarePath\":\"PLC\",\"groupPath\":\"Group\"}]", "[{\"kind\":\"deviceGroup\",\"name\":\"Group\"}]" })
            {
                var scopes = DomainValidation.Contract<TestScope[]>().Read(json, "scope");
                Assert.Null(scopes.Error);
                Assert.Equal(TestSuiteLogic.ParseScopeEntries(json).Select(s => s.Kind), TestSuiteLogic.ParseScopeEntries(V4Json.Serialize(scopes.Value)).Select(s => s.Kind));
            }
            Rejected(new OptionalEngineeringTools(optional).ListSivarcRules("screens", new[] { new PropertyStep("Parent") }));
            Rejected(new OptionalEngineeringTools(optional).ListSivarcRules("screens", new[] { new PropertyStep("Rules", index: 0) }));
            Assert.Equal(0, Calls);
        }
        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true,\"beforeError\":\"private diagnostic\"}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":false,\"operationNotStarted\":true}", true, "read-failed", "read-only", "unknown")]
        [InlineData("{\"success\":false,\"tool\":\"fixture\",\"status\":\"InvalidState\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"nativeResult\":false,\"mayHaveChanged\":true}", true, "failed", "completed", "complete")]
        [InlineData("{\"success\":false,\"nativeResult\":false,\"mayHaveWrittenFiles\":true,\"file\":{\"bytes\":42}}", true, "partial", "partial", "partial")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":true,\"imported\":[{\"name\":\"A\"}]}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"expectedPresenceVerified\":false,\"imported\":[{\"name\":\"A\"}]}", true, "partial", "partial", "partial")]
        public void EnvelopePreservesVerdictsAndEvidence(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(OptionalPackageContract.Map("ExchangeTestSuiteCase", new ResponseMessage { Message = "success", Meta = JsonNode.Parse(json)!.AsObject() }, writes));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.DoesNotContain("private diagnostic", body.ToJsonString());
        }
        [Theory]
        [InlineData("Success", true, "succeeded")]
        [InlineData("Information", true, "succeeded")]
        [InlineData("Warning", false, "failed")]
        [InlineData("Error", false, "failed")]
        [InlineData("FutureState", true, "unknown")]
        [InlineData(null, true, "unknown")]
        public void NativeTestStateCannotBeReplacedByApiCompletion(string? state, bool passed, string outcome)
        {
            var meta = new JsonObject { ["success"] = true, ["operationSuccess"] = passed, ["testPassed"] = passed, ["nativeState"] = state, ["result"] = new JsonObject { ["errorCount"] = 3 } };
            var body = Body(OptionalPackageContract.Map("RunTestSuiteCase", new ResponseMessage { Meta = meta }, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(3, (int)body["data"]!["result"]!["errorCount"]!);
        }
        [Fact]
        public void PagingCompletenessAndCredentialRedactionRemainExplicit()
        {
            var meta = JsonNode.Parse("{\"success\":true,\"dataComplete\":false,\"expectedCount\":3,\"records\":[{},{}],\"before\":{\"protected\":true,\"passwordHash\":\"private\"}}")!.AsObject();
            var body = Body(OptionalPackageContract.Map("ListSivarcRules", new ResponseMessage { Meta = meta }, false, 0, 2));
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal(2, (int)body["meta"]!["paging"]!["nextOffset"]!);
            Assert.Equal(2, body["data"]!["items"]!.AsArray().Count);
            Assert.True((bool)body["data"]!["before"]!["protected"]!);
            Assert.DoesNotContain("private", body.ToJsonString());
        }
    }
}

// Only the application service boundary is replaced; registered tools and admission are production code.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class CfcService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]", bool skipChartPreflight = false)
        {
            Calls++; Arguments = new[] { softwarePath, action, filePath, modelVersion, chartNamesJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true, string modelVersion = "V2.0", bool skipChartPreflight = false)
        {
            Calls++; Arguments = new[] { softwarePath, chartName, action, currentPassword, newHashedPassword, modelVersion };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class TestSuiteService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ReadTestSuiteCases(string category, string name = "", int offset = 0, int limit = 100, string kind = "case")
        {
            Calls++; Arguments = new[] { category, name, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ExchangeTestSuiteCase(string category, string action, string name, string filePath = "", string importOptions = "None", string loadOptions = "", bool dryRun = true, string kind = "case")
        {
            Calls++; Arguments = new[] { category, action, name, filePath, importOptions, loadOptions, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage RunTestSuiteCase(string category, string name = "", bool confirmExternalExecution = false, bool dryRun = true, string namesJson = "[]", bool runAll = false, string kind = "case")
        {
            Calls++; Arguments = new[] { category, name, namesJson, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageTestSuiteCase(string category, string name, string action = "read", string kind = "case", string newName = "", string softwarePath = "", string instanceName = "", string executionMode = "", string opcUaServerAddress = "", string serverInterfaceType = "", string interfaceFolderPath = "", string updateOptions = "", string scopeJson = "[]", string targetName = "", string masterCopyPath = "", string libraryName = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { category, name, action, kind, newName, softwarePath, instanceName, executionMode, opcUaServerAddress, serverInterfaceType, interfaceFolderPath, updateOptions, scopeJson, targetName, masterCopyPath, libraryName };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class V20OptionsService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ManageSinumerikArchive(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]", string modifiedDevicePathJson = "[]", string modifiedItemPathJson = "[]", string mode = "HardwareAndAllProgramBlocks", string comment = "", string author = "", string password = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { action, filePath, devicePathJson, itemPathJson, modifiedDevicePathJson, modifiedItemPathJson, mode, comment, author, password };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ImportSinumerikAlarmTexts(string devicePathJson, string filesJson, bool dryRun = true)
        {
            Calls++; Arguments = new[] { devicePathJson, filesJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSinumerikSafetyMode(string devicePathJson, string action = "read", string mode = "", bool dryRun = true, bool confirmSafetyChange = false)
        {
            Calls++; Arguments = new[] { devicePathJson, action, mode };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage InitializeSimotionScripting(bool dryRun = true)
        {
            Calls++; Arguments = Array.Empty<string>();
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ExportScadaData(string filePath, string softwarePath = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { filePath, softwarePath };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class OptionalEngineeringService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        internal JsonObject Result = new JsonObject { ["success"] = true };
        public ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
        {
            Calls++; Arguments = new[] { category, objectPathJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
        {
            Calls++; Arguments = new[] { category, collectionPathJson, name, action, propertiesJson };
            return new ResponseMessage { Message = "fixture", Meta = Result };
        }
    }
    internal sealed class SpecializedExchangeService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ExchangePlcSupervisions(string softwarePath,string action,string filePath,string importOptions="None",bool dryRun=true)
        {
            Calls++; Arguments = new[] { softwarePath, action, filePath, importOptions };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
}

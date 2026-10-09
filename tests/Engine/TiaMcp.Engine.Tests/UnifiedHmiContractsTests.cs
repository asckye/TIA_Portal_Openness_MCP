using TiaMcpServer;
using System;
using System.IO;
using System.Text.RegularExpressions;
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
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class UnifiedHmiContractsTests : IDisposable
    {
        private readonly UnifiedUiModelTools ui = new UnifiedUiModelTools(new UnifiedUiModelService());
        private readonly ToolCatalog catalog;
        private static readonly Type[] Groups = { typeof(UnifiedHmiGroupsTools), typeof(UnifiedScreenItemsTools), typeof(UnifiedUiModelTools) };
        public UnifiedHmiContractsTests()
        {
            UnifiedContractProbe.Calls = 0;
            UnifiedContractProbe.Json = null;
            catalog = new ToolCatalog(Groups.Concat(new[] { typeof(McpServer) }));
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(ui)
                .AddSingleton(new UnifiedHmiGroupsTools(new UnifiedHmiGroupsService()))
                .AddSingleton(new UnifiedScreenItemsTools(new UnifiedScreenItemsService()))
                .AddSingleton<TiaMcpServer.Siemens.Portal>().AddSingleton<SessionTools>().BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }
        private static void Rejected(CallToolResult result, string code = "INVALID_ARGUMENT")
        {
            var body = Body(result);
            Assert.Equal(code, (string?)body["error"]!["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };

        [Fact]
        public void GroupHasOneRegistrationPerNameAndNoLegacyAliases()
        {
            var methods = catalog.Methods.Where(p => Groups.Contains(p.Value.DeclaringType!)).ToDictionary(p => p.Key, p => p.Value);
            string[] expected = {
                "SetUnifiedHmiRuntimeState", "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag",
                "EnsureUnifiedHmiConnection", "EnsureUnifiedHmiScreenItem", "GetUnifiedHmiTexts", "ApplyUnifiedHmiScreenDesign",
                "BuildUnifiedHmiThemeDesign", "BuildUnifiedHmiLayoutDesign", "ApplyUnifiedHmiTheme", "ApplyUnifiedHmiLayout",
                "BindUnifiedHmiButtonPressedTag", "ListUnifiedHmiApiTypes", "EnsureUnifiedHmiButtonEventHandler",
                "DescribeUnifiedHmiButtonEventScript", "SetUnifiedHmiButtonEventScriptCode", "BuildUnifiedHmiButtonActionScript",
                "RunHmiActionScriptRecipeSafetySelfTest", "EnsureUnifiedHmiButtonAction", "EnsureUnifiedHmiDynamization",
                "BindUnifiedHmiTagDynamization", "ManageUnifiedHmiGroup", "DescribeUnifiedScreenItemType", "ManageUnifiedScreenItem",
                "GetUnifiedObjectEvents", "ManageUnifiedObjectParts", "ManageUnifiedDynamization", "ManageUnifiedScreenLayout",
                "ManageUnifiedListEntries", "GetUnifiedAlarmCommon", "GetUnifiedAuditSettings"
            };
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var source = File.ReadAllText(Path.Combine(dir!.FullName, "src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs"));
            var hostSource = File.ReadAllText(Path.Combine(dir!.FullName, "src/Shared/HmiOfflineTools.cs"));
            var coreNames = Regex.Matches(source + hostSource, "McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).Where(n => !new[] { "AnalyzeGlobalLibraryPackage", "PlanGlobalLibraryTemplateReuse", "AnalyzeHmiTemplateReference", "AnalyzeUnifiedHmiTemplateLayout" }.Contains(n));
            Assert.Equal(expected.OrderBy(n => n), methods.Keys.Concat(coreNames).OrderBy(n => n));
            Assert.Contains("UnifiedScreenSpec design", source);
            Assert.Contains("UnifiedThemeSpec theme", source);
            Assert.Contains("UnifiedLayoutSpec layout", source);
            foreach (var method in methods.Values)
            {
                Assert.Equal(typeof(CallToolResult), method.ReturnType);
                Assert.DoesNotContain(method.GetParameters(), p => p.Name!.EndsWith("Json", StringComparison.Ordinal) || p.ParameterType == typeof(JsonElement));
            }
            Assert.Equal(typeof(PropertyStep[]), methods["GetUnifiedObjectEvents"].GetParameters()[1].ParameterType);
            Assert.Equal(typeof(DynamizationMapping[]), methods["ManageUnifiedDynamization"].GetParameters()[6].ParameterType);
            Assert.Equal(typeof(CompositeAttributeMap), methods["ManageUnifiedScreenItem"].GetParameters()[5].ParameterType);
        }

        [Theory]
        [InlineData("null", "INVALID_ARGUMENT")]
        [InlineData("\"{}\"", "INVALID_ARGUMENT")]
        [InlineData("{\"Font\":{\"Size\":[]}}", "LIMIT_EXCEEDED")]
        [InlineData("{\"Font\":{\"Size\":{\"Value\":14}}}", "LIMIT_EXCEEDED")]
        public async Task ScreenItemShapeIsRejectedThroughDirectBridgeAndBatch(string properties, string code)
        {
            var json = "{\"softwarePath\":\"HMI\",\"screenPath\":\"Main\",\"action\":\"update\",\"itemName\":\"Caption\",\"properties\":" + properties + "}";
            var method = catalog.Methods.Single(p => p.Key == "ManageUnifiedScreenItem").Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), "ManageUnifiedScreenItem", method);
            Rejected(await tool.InvokeAsync(Request("ManageUnifiedScreenItem", json)), code);
            Rejected(McpServer.CallTool("ManageUnifiedScreenItem", Args(json)), code);
            Rejected(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageUnifiedScreenItem", Args(json)) }, "Fixture"), code);
            Assert.Equal(0, UnifiedContractProbe.Calls);
        }

        [Theory]
        [InlineData("{\"Unknown\":1}")]
        [InlineData("{\"Name\":\"Renamed\"}")]
        [InlineData("{\"ReadOnly\":1}")]
        [InlineData("{\"Font\":{\"ReadOnly\":1}}")]
        [InlineData("{\"Parent\":{\"Size\":14}}")]
        [InlineData("{\"Items\":{\"Size\":14}}")]
        [InlineData("{\"Text\":{\"en-US\":14}}")]
        public async Task ScreenItemDomainRulesRejectBeforeWritesThroughAllPaths(string properties)
        {
            var json = "{\"softwarePath\":\"HMI\",\"screenPath\":\"Main\",\"action\":\"update\",\"itemName\":\"Caption\",\"properties\":" + properties + "}";
            var method = catalog.Methods.Single(p => p.Key == "ManageUnifiedScreenItem").Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), "ManageUnifiedScreenItem", method);
            Rejected(await tool.InvokeAsync(Request("ManageUnifiedScreenItem", json)));
            Rejected(McpServer.CallTool("ManageUnifiedScreenItem", Args(json)));
            var batch = Body(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageUnifiedScreenItem", Args(json)) }, "Fixture"));
            var target = batch["data"]!["items"]![0]!["result"]!;
            Assert.Equal("INVALID_ARGUMENT", (string?)target["error"]!["code"]);
            Assert.Equal("not-started", (string?)target["meta"]!["execution"]);
            Assert.Equal(0, UnifiedContractProbe.Calls);
        }

        [Fact]
        public async Task ScreenItemPartsAndLanguagesSurviveAllPathsAndOmissionStaysEmpty()
        {
            const string properties = """{"Width":80,"Font":{"Size":14},"Text":{"en-US":"","de-DE":"Start"}}""";
            var json = "{\"softwarePath\":\"HMI\",\"screenPath\":\"Main\",\"action\":\"update\",\"itemName\":\"Caption\",\"properties\":" + properties + "}";
            var method = catalog.Methods.Single(p => p.Key == "ManageUnifiedScreenItem").Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), "ManageUnifiedScreenItem", method);
            Assert.True((bool)Body(await tool.InvokeAsync(Request("ManageUnifiedScreenItem", json)))["ok"]!);
            Assert.True((bool)Body(McpServer.CallTool("ManageUnifiedScreenItem", Args(json)))["ok"]!);
            Assert.True((bool)Body(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageUnifiedScreenItem", Args(json)) }, "Fixture"))["ok"]!);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(properties), JsonNode.Parse(UnifiedContractProbe.Json!)));
            Assert.Equal(3, UnifiedContractProbe.Calls);
            Assert.True((bool)Body(McpServer.CallTool("ManageUnifiedScreenItem", Args("{\"softwarePath\":\"HMI\",\"screenPath\":\"Main\",\"itemName\":\"Caption\"}")))["ok"]!);
            Assert.Equal("{}", UnifiedContractProbe.Json);
            // Resolving host rules must not evaluate any native getters.
            Assert.Equal(0, ScreenItemContractFixture.GetterCalls);
        }

        [Fact]
        public async Task ScreenItemAggregateLeafBudgetIsEnforcedBeforeServices()
        {
            var first = new JsonObject();
            var second = new JsonObject();
            for (int i = 0; i < 51; i++) (i < 26 ? first : second)["P" + i] = i;
            await ScreenItemShapeIsRejectedThroughDirectBridgeAndBatch(new JsonObject { ["First"] = first, ["Second"] = second }.ToJsonString(), "LIMIT_EXCEEDED");
        }

        [Theory]
        [InlineData("GetUnifiedObjectEvents", "{\"softwarePath\":\"HMI\",\"objectPath\":\"[]\"}")]
        [InlineData("GetUnifiedObjectEvents", "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"Property\":\"Screens\"}]}")]
        [InlineData("GetUnifiedObjectEvents", "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"property\":\"Screens\",\"name\":\"A\",\"index\":0}]}")]
        [InlineData("ManageUnifiedObjectParts", "{\"softwarePath\":\"HMI\",\"objectPath\":[],\"properties\":null}")]
        [InlineData("ManageUnifiedObjectParts", "{\"softwarePath\":\"HMI\",\"objectPath\":[],\"properties\":{\"Size\":[]}}")]
        [InlineData("ManageUnifiedDynamization", "{\"softwarePath\":\"HMI\",\"objectPath\":[],\"propertyName\":\"Visible\",\"mappingEntries\":[{\"kind\":\"Other\",\"properties\":{}}]}")]
        [InlineData("ManageUnifiedDynamization", "{\"softwarePath\":\"HMI\",\"objectPath\":[],\"propertyName\":\"Visible\",\"mappingEntries\":[{\"kind\":\"Simple\",\"From\":1}]}")]
        public async Task DirectAndCallToolRejectMalformedTypedInputsBeforeServices(string name, string json)
        {
            var method = catalog.Methods.Single(p => p.Key == name).Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), name, method);
            Rejected(await tool.InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Args(json)));
            Assert.Equal(0, UnifiedContractProbe.Calls);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"[]\"")]
        [InlineData("[{\"property\":\"Screens\",\"extra\":1}]")]
        [InlineData("[{\"property\":\"Screens\",\"index\":1.5}]")]
        public void ReadBatchRejectsTheSameTypedPathBeforeDispatch(string path)
        {
            var args = Args("{\"softwarePath\":\"HMI\",\"objectPath\":" + path + "}");
            Rejected(McpServer.ReadToolBatch(new[] { new ToolCall("GetUnifiedObjectEvents", args) }));
            Assert.Equal(0, UnifiedContractProbe.Calls);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("{\"Size\":{\"nested\":1}}")]
        public void PreviewBatchRejectsTypedMapsBeforeDispatch(string properties)
        {
            var args = Args("{\"softwarePath\":\"HMI\",\"objectPath\":[],\"properties\":" + properties + "}");
            Rejected(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageUnifiedObjectParts", args) }, "Fixture"));
            Assert.Equal(0, UnifiedContractProbe.Calls);
        }

        [Fact]
        public void SharedBoundaryKeepsOmissionDistinctFromExplicitNull()
        {
            var result = Body(McpServer.CallTool("ManageUnifiedObjectParts", Args("{\"softwarePath\":\"HMI\",\"objectPath\":[]}")));
            Assert.True((bool)result["ok"]!);
            Assert.Equal("{}", UnifiedContractProbe.Json);
            Assert.Equal(1, UnifiedContractProbe.Calls);
            Rejected(McpServer.CallTool("ManageUnifiedObjectParts", Args("{\"softwarePath\":\"HMI\",\"objectPath\":[],\"properties\":null}")));
            Assert.Equal(1, UnifiedContractProbe.Calls);
        }

        [Fact]
        public void DirectBridgeAndReadBatchKeepTargetEnvelopeAndEvidence()
        {
            var direct = Body(ui.ReadUnifiedObjectEventsV4("HMI", Array.Empty<PropertyStep>()));
            var bridge = Body(McpServer.CallTool("GetUnifiedObjectEvents", Args("{\"softwarePath\":\"HMI\",\"objectPath\":[]}")));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("GetUnifiedObjectEvents", Args("{\"softwarePath\":\"HMI\",\"objectPath\":[]}")) }));
            foreach (var body in new[] { direct, bridge, batch["data"]!["items"]![0]!["result"]!.AsObject() })
            {
                Assert.Equal("GetUnifiedObjectEvents", (string?)body["meta"]!["tool"]);
                Assert.Equal(direct["data"]!.ToJsonString(), body["data"]!.ToJsonString());
                Assert.Equal("succeeded", (string?)body["meta"]!["outcome"]);
            }
        }

        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true}", true, "succeeded", "completed", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", false, "succeeded", "read-only", "partial")]
        [InlineData("{}", false, "read-failed", "read-only", "none")]
        [InlineData("{}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"verified\":false}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"bindingVerified\":false}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"ok\":true,\"setMeta\":{\"success\":true,\"setAsync\":false}}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"syntaxErrorCount\":1}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"status\":\"InvalidState\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":true,\"steps\":[{\"ok\":true},{\"ok\":false}]}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"ok\":true,\"setMeta\":{\"success\":false}}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"itemResults\":[{\"success\":true},{\"success\":false,\"mayHaveChanged\":false}]}", true, "partial", "partial", "partial")]
        [InlineData("{\"success\":false,\"itemResults\":[{\"success\":true},{\"success\":false,\"mayHaveChanged\":true}]}", true, "unknown", "unknown", "unknown")]
        public void EnvelopeVerdictsFollowEvidenceInsteadOfMessageText(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(UnifiedHmiContract.Map("ApplyUnifiedHmiScreenDesign", new ResponseMessage { Message = "Success in text is not a verdict", Meta = JsonNode.Parse(json)!.AsObject() }, writes, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
            Assert.NotNull(body["data"]!["evidence"]);
        }

        [Fact]
        public void ControlDtosPreserveBuilderFieldsAndNarrowKinds()
        {
            const string json = """{"screen":{"name":"Main","width":800,"height":480,"properties":{"BackColor":"0xFFFFFFFF"}},"items":[{"type":"Text","name":"Caption","left":12,"top":24,"width":80,"height":30,"text":"","culture":"en-US","textProperty":"ToolTipText","font":{"Size":14},"content":{"Alignment":"Left"},"padding":{"Left":2},"properties":{"Visible":true}},{"type":"Rectangle","name":"Background"}]}""";
            var design = V4Json.Deserialize<UnifiedScreenSpec>(json);
            UnifiedContractProbe.Json = design.ToBuilderInput().ToJsonString();
            Assert.Equal(2, JsonNode.Parse(UnifiedContractProbe.Json)!["items"]!.AsArray().Count);
            Assert.Equal(14, (int)JsonNode.Parse(UnifiedContractProbe.Json)!["items"]![0]!["font"]!["Size"]!);
            Assert.Equal("ToolTipText", (string?)JsonNode.Parse(UnifiedContractProbe.Json)!["items"]![0]!["textProperty"]);
            Assert.Equal("", (string?)JsonNode.Parse(UnifiedContractProbe.Json!)!["items"]![0]!["text"]);
            Assert.Equal(800, (int)JsonNode.Parse(UnifiedContractProbe.Json!)!["screen"]!["Width"]!);
            var layout = V4Json.Deserialize<UnifiedLayoutSpec>("""{"items":[{"type":"Text","name":"Caption","text":"Ready","font":{"Size":14}}]}""");
            var expected = HmiUnifiedThemeLayoutBuilder.BuildLayoutDesign(layout.ToBuilderInput());
            var result = Body(UnifiedHmiContract.Map("BuildUnifiedHmiLayoutDesign", new ResponseJsonReport { Ok = true, Data = expected }, false, false));
            foreach (var field in expected) Assert.True(JsonNode.DeepEquals(field.Value, result["data"]![field.Key]), field.Key);
            Assert.IsType<UnifiedTextItem>(design.Items[0]);
            Assert.IsType<UnifiedRectangleItem>(design.Items[1]);
        }

        [Fact]
        public void MappingAdapterPreservesOrderedKindsAndNativeAttributeNames()
        {
            var mappings = DomainValidation.Contract<DynamizationMapping[]>().Read("""[{"kind":"Simple","properties":{"From":1,"To":2}},{"kind":"Range","properties":{"From":3}}]""", "mappingEntries").Value!;
            var adapted = JsonNode.Parse(UnifiedHmiContract.MappingEntries(mappings))!.AsArray();
            Assert.Equal("Simple", (string?)adapted[0]!["kind"]);
            Assert.Equal(2, (int)adapted[0]!["To"]!);
            Assert.Equal("Range", (string?)adapted[1]!["kind"]);
            Assert.Null(adapted[0]!["properties"]);
        }

        [Fact]
        public void PagingAndSessionFaultEvidenceAreRetained()
        {
            var response = new ResponseMessage { Meta = JsonNode.Parse("""{"success":true,"dataComplete":false,"records":[{"name":"A"}],"expectedCount":3,"nextOffset":1}""")!.AsObject() };
            var page = Body(UnifiedHmiContract.Map("GetUnifiedAuditSettings", response, false, false, 0, 1));
            Assert.Equal(1, (int)page["meta"]!["paging"]!["nextOffset"]!);
            Assert.Equal("A", (string?)page["data"]!["items"]![0]!["name"]);
            response.Meta = JsonNode.Parse("""{"status":"HmiReadSessionBlocked","lastFailure":{"phase":"readback"}}""")!.AsObject();
            var blocked = Body(UnifiedHmiContract.Map("GetUnifiedAuditSettings", response, false, false));
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)blocked["error"]!["code"]);
            Assert.True((bool)blocked["meta"]!["requiresSessionReset"]!);
            Assert.Equal("readback", (string?)blocked["data"]!["evidence"]!["lastFailure"]!["phase"]);
        }

        [Fact]
        public void ReadFaultsKeepResetRequirementAndApplyFailuresKeepEveryItem()
        {
            var fault = Body(UnifiedHmiContract.Map("GetUnifiedHmiTexts", new ResponseMessage
            { Meta = JsonNode.Parse("""{"success":false,"connectionUnavailable":true,"error":"Exception\n   at Native.Read()"}""")!.AsObject() }, false, false));
            Assert.Equal("read-failed", (string?)fault["meta"]!["outcome"]);
            Assert.True((bool)fault["meta"]!["requiresSessionReset"]!);
            Assert.DoesNotContain("Native.Read()", fault.ToJsonString());
            var apply = Body(UnifiedHmiContract.Map("ApplyUnifiedHmiScreenDesign", new ResponseMessage
            { Meta = JsonNode.Parse("""{"success":false,"failed":["screen write","item rejected"],"itemResults":[{"success":true},{"success":false,"mayHaveChanged":false}]}""")!.AsObject() }, true, true));
            Assert.Equal("unknown", (string?)apply["meta"]!["outcome"]);
            Assert.Equal(2, apply["data"]!["items"]!.AsArray().Count);
        }
    }
}

// Service doubles record the exact legacy inputs. No Siemens objects are created.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ScreenItemContractFixture
    {
        internal static int GetterCalls;
        public double Width { get { GetterCalls++; throw new InvalidOperationException(); } set { } }
        public int ReadOnly => throw new InvalidOperationException();
        public string Name { get; set; } = "";
        public FontPart Font => throw new InvalidOperationException();
        public FontPart Parent => throw new InvalidOperationException();
        public FontPart[] Items => throw new InvalidOperationException();
        public MultilingualText Text => throw new InvalidOperationException();
        public sealed class FontPart { public double Size { get; set; } public int ReadOnly => 0; }
        public sealed class MultilingualText { }
    }
    internal static class UnifiedContractProbe
    {
        internal static int Calls;
        internal static string? Json;
        internal static ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
    }
    internal sealed class UnifiedHmiGroupsService
    {
        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true) {  return UnifiedContractProbe.Next(); }
    }
    internal sealed class UnifiedScreenItemsService
    {
        public ResponseMessage DescribeUnifiedScreenItemType(string itemType="", int depth=2) {  return UnifiedContractProbe.Next(); }
        public ResponseMessage ManageUnifiedScreenItem(string softwarePath, string screenPath, string action="read", string itemName="", string itemType="", string propertiesJson="{}", int depth=2, bool confirmDelete=false, int offset=0, int limit=100, bool dryRun=true, string containedType="")
        {
            var invalid = UnifiedScreenItemsTools.ValidateProperties(typeof(ScreenItemContractFixture), V4Json.Deserialize<CompositeAttributeMap>(propertiesJson));
            if (invalid != null) return new ResponseMessage { Meta = new JsonObject { ["v4ArgumentError"] = JsonNode.Parse(V4Json.Serialize(invalid)) } };
            UnifiedContractProbe.Json = propertiesJson;
            return UnifiedContractProbe.Next();
        }
    }
    internal sealed class UnifiedUiModelService
    {
        public ResponseMessage ReadUnifiedObjectEvents(string softwarePath,string objectPathJson,int offset=0,int limit=100) { UnifiedContractProbe.Json = objectPathJson; return UnifiedContractProbe.Next(); }
        public ResponseMessage ManageUnifiedObjectParts(string softwarePath, string objectPathJson, string action="read", string collectionProperty="", string partName="", int partIndex=-1, string partKind="", string propertiesJson="{}", bool confirmDelete=false, bool dryRun=true) { UnifiedContractProbe.Json = objectPathJson; UnifiedContractProbe.Json = propertiesJson; return UnifiedContractProbe.Next(); }
        public ResponseMessage ManageUnifiedDynamization(string softwarePath, string objectPathJson, string propertyName, string action="read", string dynamizationKind="", string propertiesJson="{}", string mappingEntriesJson="[]", bool confirmDelete=false, bool dryRun=true) { UnifiedContractProbe.Json = objectPathJson; UnifiedContractProbe.Json = propertiesJson; UnifiedContractProbe.Json = mappingEntriesJson; return UnifiedContractProbe.Next(); }
        public ResponseMessage ManageUnifiedScreenLayout(string softwarePath, string objectPathJson, string action="read", string name="", string propertiesJson="{}", bool confirmDelete=false, bool dryRun=true) { UnifiedContractProbe.Json = objectPathJson; UnifiedContractProbe.Json = propertiesJson; return UnifiedContractProbe.Next(); }
        public ResponseMessage ManageUnifiedListEntries(string softwarePath, string category, string listName, string action="read", string entryKey="", string entryJson="{}", bool confirmDelete=false, bool dryRun=true) { UnifiedContractProbe.Json = entryJson; return UnifiedContractProbe.Next(); }
        public ResponseMessage ReadUnifiedAlarmCommon(string softwarePath,string category,string name="",int offset=0,int limit=100) {  return UnifiedContractProbe.Next(); }
        public ResponseMessage ReadUnifiedAuditSettings(string softwarePath,string category,string name="",int offset=0,int limit=100) {  return UnifiedContractProbe.Next(); }
    }
}

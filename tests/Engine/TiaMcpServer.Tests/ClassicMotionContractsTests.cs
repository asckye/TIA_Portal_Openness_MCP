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

namespace TiaMcpServer.Tests
{
    public sealed class ClassicMotionContractsTests : IDisposable
    {
        private readonly ClassicHmiFoldersService folders = new ClassicHmiFoldersService();
        private readonly MotionProDiagClassicHmiService motion = new MotionProDiagClassicHmiService();
        private readonly ToolCatalog catalog;
        private readonly MotionProDiagClassicHmiTools tools;
        private static readonly string[] Names = {
            "GetClassicHmiScreenTree", "ManageClassicHmiScreenObject", "ManageClassicHmiFolder", "ManageClassicHmiGraphic",
            "GetMotionAxisConfiguration", "ManageMotionAxis", "ManagePlcSupervision", "ListClassicHmiScripts", "ManageClassicHmiScript",
            "ManageClassicHmiCycle", "ManageClassicHmiTextGraphicList", "GetClassicHmiGlobalization", "ListClassicHmiFaceplates",
            "ExportPlcProDiagInfo", "ExchangeMotionCamData", "ConfigureMotionHardwareConnection"
        };

        public ClassicMotionContractsTests()
        {
            tools = new MotionProDiagClassicHmiTools(motion);
            catalog = new ToolCatalog(new[] { typeof(ClassicHmiFoldersTools), typeof(MotionProDiagClassicHmiTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new ClassicHmiFoldersTools(folders)).AddSingleton(tools).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            return result.StructuredContent!.AsObject();
        }
        private static void Rejected(CallToolResult result, string code = "INVALID_ARGUMENT")
        {
            var body = Body(result);
            Assert.Equal(code, (string?)body["error"]!["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }
        private async Task<CallToolResult> Direct(string name, string json)
        {
            var method = name == "CallTool" || name == "PreviewToolBatch" ? typeof(McpServer).GetMethods().Single(m => m.Name == name && m.GetCustomAttribute<McpServerToolAttribute>() != null)
                : catalog.Methods.Single(p => p.Key == name).Value;
            var tool = McpServer.WithSchemaHints(ToolCatalog.CreateTool(method), name, method);
            return await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } });
        }

        [Fact]
        public void OnlyReviewedNamesAndConcreteInputsAreRegistered()
        {
            Assert.Equal(Names.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            foreach (var entry in catalog.Methods)
            {
                Assert.Equal(typeof(CallToolResult), entry.Value.ReturnType);
                foreach (var p in entry.Value.GetParameters())
                {
                    Assert.False(p.Name!.EndsWith("Json", StringComparison.Ordinal));
                    if (p.Name == "target") Assert.Equal(typeof(MotionTarget), p.ParameterType);
                    if (p.Name == "properties" || p.Name == "attributes") Assert.Equal(typeof(AttributeMap<Scalar>), p.ParameterType);
                }
            }
            foreach (string name in new[] { "ReadClassicHmiScreenTree", "ReadMotionAxisConfiguration", "ReadClassicHmiScripts", "ReadClassicHmiGlobalization", "ReadClassicHmiFaceplates" })
                Rejected(McpServer.CallTool(name, Args("{}")), "TOOL_NOT_FOUND");
        }

        [Theory]
        [InlineData("\"{}\"")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"Address\":1}")]
        [InlineData("{\"address\":1,\"extra\":true}")]
        [InlineData("{\"address\":1,\"address\":2}")]
        [InlineData("{\"address\":1.5}")]
        [InlineData("{\"address\":2147483648}")]
        [InlineData("{\"address\":1,\"plcTagPath\":\"Tag\"}")]
        [InlineData("{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"secondItemPath\":[\"Other\"],\"channelIndex\":0}")]
        [InlineData("{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"channelType\":\"Input\"}")]
        [InlineData("{\"address\":1,\"connectOption\":\"Default\"}")]
        public async Task InvalidTargetIsRejectedByDirectBridgeAndBatch(string target)
        {
            var json = "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"connect\",\"aspect\":\"actor\",\"target\":" + target + "}";
            Rejected(await Direct("ManageMotionAxis", json));
            Rejected(await Direct("CallTool", "{\"name\":\"ManageMotionAxis\",\"arguments\":" + json + "}"));
            Rejected(await Direct("PreviewToolBatch", "{\"operations\":[{\"name\":\"ManageMotionAxis\",\"arguments\":" + json + "}],\"expectedProject\":\"Fixture\"}"));
            Assert.Equal(0, folders.Calls + motion.Calls);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("[]")]
        [InlineData("{\"Name\":{},\"Other\":1}")]
        [InlineData("{\"Name\":1,\"Name\":2}")]
        public async Task InvalidScalarMapIsRejectedByAllInvocationPaths(string attributes)
        {
            var json = "{\"softwarePath\":\"HMI\",\"action\":\"setAttributes\",\"attributes\":" + attributes + "}";
            Rejected(await Direct("ManageClassicHmiCycle", json));
            Rejected(await Direct("CallTool", "{\"name\":\"ManageClassicHmiCycle\",\"arguments\":" + json + "}"));
            Rejected(await Direct("PreviewToolBatch", "{\"operations\":[{\"name\":\"ManageClassicHmiCycle\",\"arguments\":" + json + "}],\"expectedProject\":\"Fixture\"}"));
            Assert.Equal(0, motion.Calls);
        }

        [Theory]
        [InlineData("{\"devicePath\":[\"PLC/exact\"],\"itemPath\":[\"CPU\"]}")]
        [InlineData("{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"secondItemPath\":[\"Other\"],\"connectOption\":\"Default\"}")]
        [InlineData("{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"channelIndex\":0}")]
        [InlineData("{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"channelType\":\"Input\",\"channelIoType\":\"Digital\",\"channelNumber\":0}")]
        [InlineData("{\"dbMemberPath\":\"DB.Member\"}")]
        [InlineData("{\"plcTagPath\":\"Tag\"}")]
        [InlineData("{\"inputBitAddress\":0,\"outputBitAddress\":8}")]
        [InlineData("{\"address\":8}")]
        public void TargetBranchesPreserveTheLegacyOverloadSelection(string json)
        {
            var target = DomainValidation.Contract<MotionTarget>().Read(json, "target");
            Assert.Null(target.Error);
            string serialized = ClassicMotionToolContract.Json(target.Value!);
            Assert.Equal(target.Value!.Mode, Siemens.MotionProDiagClassicHmiLogic.ParseConnectionTarget(serialized).Mode);
        }

        [Theory]
        [InlineData("targetJson", "\"{}\"")]
        [InlineData("propertiesJson", "\"{}\"")]
        [InlineData("properties", "null")]
        public async Task LegacyArgumentsAndExplicitNullAreNotOmittedDefaults(string parameter, string value)
        {
            string json = "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"read\",\"" + parameter + "\":" + value + "}";
            Rejected(await Direct("ManageMotionAxis", json));
            Rejected(McpServer.CallTool("ManageMotionAxis", Args(json)));
            Rejected(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageMotionAxis", Args(json)) }, "Fixture"));
            Assert.Equal(0, motion.Calls);
        }

        [Fact]
        public async Task TargetBudgetIsPreservedAcrossAllInvocationPaths()
        {
            string target = "{\"dbMemberPath\":\"" + new string('x', 16384) + "\"}";
            string json = "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"connect\",\"target\":" + target + "}";
            Rejected(await Direct("ManageMotionAxis", json), "LIMIT_EXCEEDED");
            Rejected(McpServer.CallTool("ManageMotionAxis", Args(json)), "LIMIT_EXCEEDED");
            Rejected(McpServer.PreviewToolBatch(new[] { new ToolCall("ManageMotionAxis", Args(json)) }, "Fixture"), "LIMIT_EXCEEDED");
            Assert.Equal(0, motion.Calls);
        }

        [Fact]
        public async Task OmittedTargetUsesTheDeclaredDefaultAndReadsHaveTheSameEnvelope()
        {
            Body(McpServer.CallTool("ManageMotionAxis", Args("{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"read\"}")));
            Assert.Equal("{}", motion.LastTarget);
            const string json = "{\"softwarePath\":\"HMI\"}";
            var direct = Body(await Direct("ListClassicHmiScripts", json));
            var bridge = Body(McpServer.CallTool("ListClassicHmiScripts", Args(json)));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("ListClassicHmiScripts", Args(json)) }));
            foreach (var body in new[] { direct, bridge, batch["data"]!["items"]![0]!["result"]!.AsObject() })
            {
                Assert.Equal("ListClassicHmiScripts", (string?)body["meta"]!["tool"]);
                Assert.Equal("succeeded", (string?)body["meta"]!["outcome"]);
                Assert.Equal(direct["data"]!.ToJsonString(), body["data"]!.ToJsonString());
            }
        }

        [Theory]
        [InlineData("{\"operationSuccess\":true}", true, "succeeded", "read-only", "complete")]
        [InlineData("{\"operationSuccess\":true,\"dataComplete\":false}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":true,\"typedError\":\"private native detail\"}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":true,\"foldersTruncated\":true}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":true,\"screenCount\":501,\"screens\":[]}", true, "succeeded", "read-only", "partial")]
        [InlineData("{\"operationSuccess\":false}", true, "read-failed", "read-only", "unknown")]
        [InlineData("{}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"operationSuccess\":false,\"mayHaveChanged\":true,\"after\":{\"Name\":\"Axis\"}}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"mappingVerified\":false,\"mayHaveChanged\":true}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"mayHaveWrittenFiles\":true,\"file\":{\"sha256\":\"evidence\"}}", false, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"v4FailureCode\":\"UNSUPPORTED_CAPABILITY\"}", false, "rejected-before-operation", "not-started", "none")]
        public void F3MappingUsesEvidenceAndNeverPromotesUnknown(string json, bool readOnly, string outcome, string execution, string completeness)
        {
            var body = Body(ClassicMotionToolContract.Map("ManageMotionAxis", new ResponseMessage { Message = "Success is only message text", Meta = JsonNode.Parse(json)!.AsObject() }, readOnly, true));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
            Assert.DoesNotContain("private native detail", body.ToJsonString());
            if (json.Contains("sha256")) Assert.Equal("evidence", (string?)body["data"]!["file"]!["sha256"]);
            if (json.Contains("after")) Assert.Equal("Axis", (string?)body["data"]!["after"]!["Name"]);
        }

        [Fact]
        public void NativeCapabilityRefusalIsRetainedBeforeAndAfterAWrite()
        {
            foreach (bool issued in new[] { false, true })
            {
                var meta = new JsonObject();
                Assert.Throws<NotSupportedException>(() => ClassicMotionToolContract.Capture(meta, data => {
                    data["mayHaveChanged"] = issued;
                    throw new NotSupportedException("private native message");
                }));
                ResponseMeta.Failed(meta);
                var body = Body(ClassicMotionToolContract.Map("ManageMotionAxis", new ResponseMessage { Meta = meta }, false, true));
                Assert.Equal(issued ? "OUTCOME_UNKNOWN" : "UNSUPPORTED_CAPABILITY", (string?)body["error"]!["code"]);
            }
        }

        [Fact]
        public void PagingRemainsSeparateFromObservationCompleteness()
        {
            var body = Body(ClassicMotionToolContract.Map("ListClassicHmiScripts", new ResponseMessage {
                Meta = JsonNode.Parse("{\"success\":true,\"offset\":2,\"limit\":2,\"total\":5,\"nextOffset\":4,\"dataComplete\":false,\"records\":[{},{}]}")!.AsObject()
            }, true, false));
            Assert.Equal(4, (int)body["meta"]!["paging"]!["nextOffset"]!);
            Assert.False((bool)body["meta"]!["paging"]!["complete"]!);
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal(2, body["data"]!["records"]!.AsArray().Count);
        }

        [Fact]
        public void GroupExamplesAndOperationExamplesUseTheSharedSchemas()
        {
            foreach (var entry in catalog.Methods.Where(p => string.IsNullOrEmpty(Siemens.ToolVersionPolicy.CallProblem(McpServer.ReleaseKey, p.Key, _ => null))))
            {
                var body = Body(new ToolUsageTools().GetToolUsage(toolName: entry.Key));
                Assert.True((bool)body["ok"]!);
                var data = body["data"]!;
                var schema = JsonSerializer.SerializeToElement(data["inputSchema"]);
                var arguments = JsonSerializer.SerializeToElement(data["example"]!["request"]!["params"]!["arguments"]);
                Assert.Null(McpServer.ValidateV4Arguments(entry.Value, arguments, schema));
                foreach (var operation in data["operations"]!.AsArray())
                {
                    var selected = Body(new ToolUsageTools().GetToolUsage(toolName: entry.Key, operation: (string)operation!["operation"]!));
                    Assert.True((bool)selected["ok"]!);
                    var sample = JsonSerializer.SerializeToElement(selected["data"]!["example"]!["request"]!["params"]!["arguments"]);
                    Assert.Null(McpServer.ValidateV4Arguments(entry.Value, sample, schema));
                }
            }
        }
    }
}

// Service doubles stop at the application boundary; no Siemens API is loaded.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ClassicHmiFoldersService
    {
        internal int Calls;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadClassicHmiScreenTree(string softwarePath, string kind="all", string folderPath="", int maxDepth=4) => Next();
        public ResponseMessage ManageClassicHmiScreenObject(string softwarePath, string objectKind, string objectPath="", string action="read", string filePath="", string importOptions="None", bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ManageClassicHmiFolder(string softwarePath, string folderKind, string folderPath="", string action="read", string newName="", bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ManageClassicHmiGraphic(string softwarePath, string action="list", string name="", string filePath="", string importOptions="None", bool confirmDelete=false, bool dryRun=true) => Next();
    }
    internal sealed class MotionProDiagClassicHmiService
    {
        internal int Calls;
        internal string? LastTarget;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadMotionAxisConfiguration(string softwarePath, string objectPath, bool includeParameters=false, int offset=0, int limit=100) => Next();
        public ResponseMessage ManageMotionAxis(string softwarePath, string objectPath, string action, string aspect="", string name="", string targetJson="{}", string propertiesJson="{}", int sensorIndex=0, bool confirmDelete=false, bool dryRun=true) { LastTarget = targetJson; return Next(); }
        public ResponseMessage ManagePlcSupervision(string softwarePath, string action, string blockPath="", string providerKind="supervision", string compositionName="", string entryName="", string typeName="", string filePath="", string attributesJson="{}", int offset=0, int limit=100, bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ReadClassicHmiScripts(string softwarePath,string folderPath="",int offset=0,int limit=100) => Next();
        public ResponseMessage ManageClassicHmiScript(string softwarePath, string scriptPath, string action, string filePath="", string importOptions="None", string attributesJson="{}", bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ManageClassicHmiCycle(string softwarePath, string action, string cycleName="", string filePath="", string importOptions="None", string attributesJson="{}", int offset=0, int limit=100, bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ManageClassicHmiTextGraphicList(string softwarePath, string listKind, string action, string listName="", string compositionName="", string entryName="", string typeName="", string filePath="", string importOptions="None", string attributesJson="{}", int offset=0, int limit=100, bool confirmDelete=false, bool dryRun=true) => Next();
        public ResponseMessage ReadClassicHmiGlobalization(string softwarePath,int offset=0,int limit=100) => Next();
        public ResponseMessage ReadClassicHmiFaceplates(string kind="faceplate", string libraryName="", string folderPath="", int offset=0, int limit=100) => Next();
        public ResponseMessage ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName="", string unitKind="unit", bool dryRun=true) => Next();
        public ResponseMessage ExchangeMotionCamData(string softwarePath, string objectPath, string action, string filePath, string format="", string separator="", int pointCount=0, bool dryRun=true) => Next();
        public ResponseMessage ConfigureMotionHardwareConnection(string softwarePath, string objectPath, string interfaceKind, string action, int inputBitAddress=0, int outputBitAddress=0, string connectOption="Default", int sensorIndex=0, bool dryRun=true) => Next();
    }
}

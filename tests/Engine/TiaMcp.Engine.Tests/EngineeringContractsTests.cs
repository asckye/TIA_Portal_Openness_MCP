using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseImportBatch : ResponseMessage
    { public IEnumerable<string>? Imported { get; set; } public IEnumerable<ImportFailure>? Failed { get; set; } }
    public class TechnologyObjectInfo
    { public string? Name { get; set; } public string? OfSystemLibElement { get; set; } public string? OfSystemLibVersion { get; set; } public string? TypeHint { get; set; } public string? Folder { get; set; } }
    public class ResponseTechnologyObjectList : ResponseMessage
    { public bool? Ok { get; set; } public string? SoftwarePath { get; set; } public int? Count { get; set; } public TechnologyObjectInfo[]? Items { get; set; } }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class EngineeringContractsTests : IDisposable
    {
        [Fact]
        public void Alarm_instance_export_rejects_the_wrong_extension_before_the_service()
        {
            var result = Body(new AlarmsTools(alarms).ExportAlarmInstanceTextsV4("PLC", Path.GetFullPath("AlarmTexts.xml")));
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
            Assert.Equal("exportPath", (string?)result["error"]?["details"]?["parameter"]);
            Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
            Assert.Equal(0, Calls);
        }

        [Theory, InlineData(false), InlineData(true)]
        public void Native_export_exception_distinguishes_an_absent_target_from_a_partial_file(bool wroteFile)
        {
            string directory = Path.Combine(Path.GetTempPath(), "tia-alarm-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "AlarmTexts.xlsx");
            try
            {
                using var native = InvocationJournal.BeginNativeCallScope();
                var result = Body(EngineeringToolContract.Export("ExportAlarmInstanceTexts", path, () => {
                    InvocationJournal.NativeCallStarted();
                    if (wroteFile) File.WriteAllText(path, "partial native file");
                    CallerInputFiles.RecordExportFailure(new IOException("Export failed: native format refused at C:\\private\\secret.xlsx"));
                    return new ResponseMessage { Message = "Export failed", Meta = new JsonObject { ["success"] = false } };
                }));
                Assert.Equal(wroteFile ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)result["error"]?["code"]);
                Assert.Equal(wroteFile ? "unknown" : "failed", (string?)result["meta"]?["outcome"]);
                Assert.Equal(wroteFile, (bool?)result["meta"]?["requiresSessionReset"]);
                Assert.Equal(wroteFile, (bool?)result["data"]?["mayHaveChanged"]);
                Assert.Contains("native format refused", (string?)result["error"]?["message"]);
                Assert.DoesNotContain("private", result.ToJsonString());
            }
            finally { Directory.Delete(directory, true); }
        }
        private readonly AlarmsService alarms = new AlarmsService();
        private readonly OpcUaService opc = new OpcUaService();
        private readonly SoftwareUnitDeepService units = new SoftwareUnitDeepService();
        private readonly SoftwareUnitManagementService access = new SoftwareUnitManagementService();
        private readonly TechnologyObjectsService technology = new TechnologyObjectsService();
        private readonly ToolCatalog catalog;
        private readonly Dictionary<string, McpServerTool> tools;
        public EngineeringContractsTests()
        {
            catalog = new ToolCatalog(new[] { typeof(AlarmsTools), typeof(OpcUaTools), typeof(SoftwareUnitDeepTools), typeof(SoftwareUnitManagementTools), typeof(TechnologyObjectsTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection()
                .AddSingleton(new AlarmsTools(alarms)).AddSingleton(new OpcUaTools(opc))
                .AddSingleton(new SoftwareUnitDeepTools(units)).AddSingleton(new SoftwareUnitManagementTools(access))
                .AddSingleton(new TechnologyObjectsTools(technology)).BuildServiceProvider());
            tools = catalog.Methods.ToDictionary(p => p.Key, p => (McpServerTool)new VersionPolicyTool(McpServer.CreateTool(p.Key, p.Value)));
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private int Calls => alarms.Calls + opc.Calls + units.Calls + access.Calls + technology.Calls;
        private static ToolArguments Arguments(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        private static JsonObject Body(CallToolResult result)
        {
            string text = ((TextContentBlock)Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var body = result.StructuredContent!.AsObject();
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.Equal(!(bool)body["ok"]!, result.IsError);
            V4Json.Deserialize<Envelope>(text);
            return body;
        }
        private static void Rejected(CallToolResult result)
        {
            var body = Body(result);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        }
        [Fact]
        public void NamesAndParameterTypesMatchTheReviewedCatalog()
        {
            string[] names = {
                "ExportAlarmClasses", "ImportAlarmClasses", "ExportAlarmTextLists", "ImportAlarmTextLists", "ExportAlarmInstanceTexts",
                "ExchangePlcAlarmTextLists", "ImportPlcAlarmInstanceTexts", "ManagePlcAlarmTextList", "GetPlcOpcUaConfiguration",
                "ManageOpcUaInterface", "SetOpcUaInterfaceEnabled", "ExportOpcUaInterface", "ImportOpcUaInterface", "GenerateOpcUaModelledInterface",
                "GetOpcUaAccessControl", "ManageOpcUaAccessControl", "ListPlcSoftwareUnits", "ManagePlcDocuments", "GetPlcChecksums",
                "GetPlcObjectFingerprints", "ManagePlcBlockWriteProtection", "ManageProjectCompilationSettings", "ManagePlcSoftwareUnit",
                "SetPlcUnitObjectAccess", "ListTechnologyObjects", "ExportTechnologyObject", "ExportTechnologyObjectsToDirectory",
                "ImportTechnologyObject", "ImportTechnologyObjectsFromDirectory", "GetTechnologyObjectTree", "ManageTechnologyObject"
            };
            Assert.Equal(names.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            foreach (var method in catalog.Methods.Select(p => p.Value))
            {
                Assert.Equal(typeof(CallToolResult), method.ReturnType);
                foreach (var p in method.GetParameters())
                {
                    Assert.False(p.Name!.EndsWith("Json", StringComparison.Ordinal));
                    if (p.Name == "properties") Assert.Equal(typeof(AttributeMap<Scalar>), p.ParameterType);
                    if (p.Name == "comments") Assert.Equal(typeof(AttributeMap<string>), p.ParameterType);
                    if (p.Name == "accessLevels") Assert.Equal(typeof(AttributeMap<int>), p.ParameterType);
                    if (p.Name == "value") Assert.Equal(typeof(NativeValue), p.ParameterType);
                    if (p.Name == "cultures" || p.Name == "textListNames") Assert.Equal(typeof(string[]), p.ParameterType);
                }
            }
        }
        [Theory]
        [InlineData("ExchangePlcAlarmTextLists", "{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/a.xlsx\",\"cultures\":\"[]\"}")]
        [InlineData("ExchangePlcAlarmTextLists", "{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/a.xlsx\",\"textListNames\":null}")]
        [InlineData("ImportPlcAlarmInstanceTexts", "{\"softwarePath\":\"PLC\",\"filePath\":\"C:/a.xlsx\",\"cultures\":[null]}")]
        [InlineData("ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"update\",\"properties\":{\"Author\":{}}}")]
        [InlineData("ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"update\",\"comments\":{\"en-US\":1}}")]
        [InlineData("ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"update\",\"comments\":null}")]
        [InlineData("ManageProjectCompilationSettings", "{\"properties\":\"{}\"}")]
        [InlineData("ManageOpcUaAccessControl", "{\"softwarePath\":\"PLC\",\"action\":\"setRestriction\",\"properties\":{\"SessionRequired\":[]}}")]
        [InlineData("ManageTechnologyObject", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"setParameter\",\"valueJson\":\"1\"}")]
        [InlineData("GetTechnologyObjectTree", "{\"softwarePath\":\"PLC\",\"maxDepth\":1.5}")]
        public async Task TypedRejectionsRunThroughDirectBridgeAndBatch(string name, string json)
        {
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Arguments(json)));
            var calls = new[] { new ToolCall(name, Arguments(json)) };
            var batch = name == "GetTechnologyObjectTree" ? McpServer.ReadToolBatch(calls) : McpServer.PreviewToolBatch(calls, "fixture");
            Rejected(batch);
            Assert.Equal(0, Calls);
        }
        [Theory]
        [InlineData("1.5")]
        [InlineData("2147483648")]
        [InlineData("\"4\"")]
        public async Task OpcUaAccessLevelsRejectNonInt32Values(string level)
        {
            const string name = "GenerateOpcUaModelledInterface";
            string json = "{\"softwarePath\":\"PLC\",\"interfaceName\":\"M\",\"namespaceUri\":\"urn:m\",\"outputPath\":\"C:/m.xml\",\"accessLevels\":{\"Inputs\":" + level + "}}";
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Arguments(json)));
            Assert.Equal(0, Calls);
        }
        [Theory]
        [InlineData("[]")]
        [InlineData("{}")]
        public async Task NativeTechnologyValuesRemainScalarAtTheActionBoundary(string value)
        {
            const string name = "ManageTechnologyObject";
            string json = "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"setParameter\",\"value\":" + value + "}";
            Rejected(await tools[name].InvokeAsync(Request(name, json)));
            Rejected(McpServer.CallTool(name, Arguments(json)));
            Assert.Equal(0, Calls);
        }
        [Fact]
        public void OptionalCollectionsAreOmittedWithoutAdvertisingNullDefaults()
        {
            const string name = "ExchangePlcAlarmTextLists";
            var schema = tools[name].ProtocolTool.InputSchema.GetRawText();
            Assert.DoesNotContain("\"default\":null", schema);
            var args = new JsonObject { ["softwarePath"] = "PLC", ["action"] = "export", ["filePath"] = Path.Combine(Path.GetTempPath(), "tia-optional-" + Guid.NewGuid().ToString("N") + ".xlsx") };
            Assert.True((bool)Body(McpServer.CallTool(name, Arguments(args.ToJsonString())))["ok"]!);
            Assert.Equal(1, Calls);
        }
        [Fact]
        public void GroupExamplesFitTheSharedSchemas()
        {
            foreach (var entry in TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey, engineSource: true))
            {
                string name = (string)entry!["currentName"]!;
                if (!catalog.Methods.ToDictionary(p => p.Key, p => p.Value).TryGetValue(name, out var method)) continue;
                Assert.Equal(4, (int)entry["envelopeVersion"]!);
                Assert.Null(McpServer.ValidateV4Arguments(method, JsonSerializer.SerializeToElement(entry["arguments"]), tools[name].ProtocolTool.InputSchema));
            }
            Assert.Equal(0, Calls);
        }
        [Fact]
        public async Task DirectBridgeAndReadBatchPreserveTheTargetEnvelope()
        {
            const string name = "ListPlcSoftwareUnits";
            const string json = "{\"softwarePath\":\"PLC\"}";
            var direct = Body(await tools[name].InvokeAsync(Request(name, json)));
            var bridge = Body(McpServer.CallTool(name, Arguments(json)));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall(name, Arguments(json)) }));
            var child = batch["data"]!["items"]![0]!["result"]!.AsObject();
            foreach (var body in new[] { direct, bridge, child })
            {
                Assert.Equal(name, (string?)body["meta"]!["tool"]);
                Assert.True((bool)body["ok"]!);
                body["meta"]!.AsObject().Remove("timestamp"); body["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridge));
            Assert.True(JsonNode.DeepEquals(direct, child));
            Assert.Equal(3, Calls);
        }
        [Theory]
        [InlineData(true, "read-only")]
        [InlineData(false, "completed")]
        public void AlarmFileExportsUseTheActualExecutionMode(bool dryRun, string execution)
        {
            var result = new AlarmsTools(alarms).ExchangePlcAlarmTextListsXlsxV4("PLC", "export", "C:/a.xlsx", dryRun: dryRun);
            Assert.Equal(execution, (string?)Body(result)["meta"]!["execution"]);
        }
        [Fact]
        public void UnknownBatchWritesKeepEarlierSuccessfulIdentities()
        {
            var body = Body(EngineeringToolContract.Map("ImportTechnologyObjectsFromDirectory", new ResponseImportBatch {
                Imported = new[] { "Earlier" }, Failed = new[] { new ImportFailure { Path = "Later", Error = "private diagnostic" } }
            }, true));
            Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
            Assert.Equal("Earlier", (string?)body["data"]!["imported"]![0]);
            Assert.Equal("Later", (string?)body["data"]!["failed"]![0]!["path"]);
            Assert.DoesNotContain("private diagnostic", body.ToJsonString());
        }
        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true}", true, "succeeded", "completed", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"tree\":{\"groupsTruncated\":true}}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"unitGroup\":{\"name\":null,\"nameNote\":\"V21 only\"}}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"tree\":{\"objectCount\":201,\"technologicalObjects\":[]}}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"parameters\":[],\"parameterCount\":501}", false, "succeeded", "read-only", "partial")]
        [InlineData("{}", false, "read-failed", "read-only", "unknown")]
        [InlineData("{}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"state\":\"Unknown\"}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"operationSuccess\":true,\"nativeState\":null,\"mayHaveChanged\":true}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"state\":\"Warning\"}", true, "succeeded", "completed", "complete")]
        [InlineData("{\"nativeState\":\"Error\",\"mayHaveChanged\":true}", true, "failed", "completed", "complete")]
        [InlineData("{\"success\":false,\"result\":{\"state\":\"Failure\",\"imported\":[\"A\"]}}", true, "partial", "partial", "partial")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"v4Rejection\":\"NOT_FOUND\"}", false, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"status\":\"InvalidState\",\"tool\":\"ManagePlcSoftwareUnit\",\"operationSuccess\":false}", true, "rejected-before-operation", "not-started", "none")]
        public void EnvelopeMappingUsesEvidenceRatherThanMessages(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(EngineeringToolContract.Map("ManagePlcSoftwareUnit", new ResponseMessage { Message = "success", Meta = JsonNode.Parse(json)!.AsObject() }, writes));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
        }
    }
}

// Only application services are substituted. The registered tools and shared dispatch run unchanged.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class AlarmsService
    {
        internal int Calls;
        public ResponseMessage ExportAlarmClasses(
            string softwarePath,
            string exportPath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ImportAlarmClasses(
            string softwarePath,
            string importPath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ExportAlarmTextLists(
            string softwarePath,
            string exportPath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ImportAlarmTextLists(
            string softwarePath,
            string importPath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ExportAlarmInstanceTexts(
            string softwarePath,
            string exportPath,
            bool includeInfoText = true,
            bool includeAdditionalTexts = true,
            bool includeAlarmClass = true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ExchangePlcAlarmTextListsXlsx(
            string softwarePath,
            string action,
            string filePath,
            string unitName="",
            string unitKind="unit",
            string textListNamesJson="[]",
            string culturesJson="[]",
            string importOption="None",
            bool confirmImport=false,
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ImportPlcAlarmInstanceTexts(
            string softwarePath,
            string filePath,
            string culturesJson,
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManagePlcAlarmTextList(
            string softwarePath,
            string action="read",
            string name="",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="",
            bool confirmDelete=false,
            int offset=0,
            int limit=100,
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
    internal sealed class OpcUaService
    {
        internal int Calls;
        public ResponseJsonReport GetOpcUaConfig(
            string softwarePath) { Calls++; return new ResponseJsonReport(); }
        public ResponseMessage ManageOpcUaInterface(
            string softwarePath,
            string interfaceName,
            string action = "read",
            string interfaceType = "ServerInterface",
            bool dryRun = true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage SetOpcUaInterfaceEnabled(
            string softwarePath,
            string interfaceName,
            bool enabled,
            string interfaceType = "ServerInterface") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ExportOpcUaInterface(
            string softwarePath,
            string interfaceName,
            string exportPath,
            string interfaceType = "ServerInterface") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ImportOpcUaInterface(
            string softwarePath,
            string importPath,
            string interfaceType = "ServerInterface") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public JsonObject GenerateOpcUaModelledInterface(string softwarePath, string interfaceName, string namespaceUri, string outputPath,
            string unitName = "", bool keepFolderStructure = false, bool keepEmptyDataBlocks = false, string accessLevelsJson = "{}", bool dryRun = true) { Calls++; return new JsonObject(); }
        public ResponseMessage ReadOpcUaAccessControl(
            string softwarePath,
            string section="roles",
            int offset=0,
            int limit=100) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManageOpcUaAccessControl(
            string softwarePath,
            string action,
            string roleName="",
            string definedInNamespace="",
            string projectRole="",
            string namespaceUri="",
            string permission="",
            bool enabled=false,
            string propertiesJson="{}",
            bool confirmChange=false,
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
    internal sealed class SoftwareUnitDeepService
    {
        internal int Calls;
        public ResponseMessage ReadPlcSoftwareUnits(
            string softwarePath,
            string unitName="",
            string unitKind="all",
            bool includeContents=true,
            int offset=0,
            int limit=50) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManagePlcDocuments(
            string softwarePath,
            string action,
            string objectKind="document",
            string name="",
            string unitName="",
            string unitKind="unit",
            string groupPath="",
            string directoryPath="",
            string importOption="Override",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="",
            string typePath="",
            string version="",
            string updatePathsMode="",
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadPlcChecksums(string softwarePath) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadPlcObjectFingerprints(
            string softwarePath,
            string objectKind,
            string objectPath,
            string unitName="",
            string unitKind="unit") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManagePlcBlockWriteProtection(
            string softwarePath,
            string blockPath,
            string action,
            string password="",
            string newPassword="",
            bool confirmProtectionChange=false,
            bool dryRun=true,
            string unitName="",
            string unitKind="unit") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManageProjectCompilationSettings(string action="read", string propertiesJson="{}", bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManagePlcSoftwareUnit(
            string softwarePath,
            string action,
            string name="",
            string relatedUnit="",
            string relationType="",
            string propertiesJson="{}",
            bool dryRun=true,
            string unitKind="unit",
            string commentsJson="{}",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="") { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
    internal sealed class SoftwareUnitManagementService
    {
        internal int Calls;
        public ResponseMessage SetPlcUnitObjectAccess(
            string softwarePath,
            string unitName,
            string objectKind,
            string objectPath,
            string access,
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
    internal sealed class TechnologyObjectsService
    {
        internal int Calls;
        public List<JsonObject> GetTechnologyObjects(
            string softwarePath) { Calls++; return new List<JsonObject>(); }
        public ResponseMessage ExportTechnologyObject(
            string softwarePath,
            string toName,
            string exportPath,
            bool dryRun = false) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true, ["executed"] = !dryRun } }; }
        public ResponseImportBatch ExportTechnologyObjectsToDirectory(
            string softwarePath,
            string exportDir,
            string regexName = "") { Calls++; return new ResponseImportBatch(); }
        public void ImportTechnologyObject(
            string softwarePath,
            string folderPath,
            string importPath) { Calls++;  }
        public ResponseImportBatch ImportTechnologyObjectsFromDirectory(
            string softwarePath,
            string folderPath,
            string dir,
            string regexName = "",
            bool overwrite = true) { Calls++; return new ResponseImportBatch(); }
        public ResponseMessage ReadTechnologyObjectTree(
            string softwarePath,
            string groupPath="",
            bool includeParameters=false,
            bool includeMotionView=false,
            int maxDepth=4) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ManageTechnologyObject(
            string softwarePath,
            string objectPath,
            string action,
            string typeIdentifier="",
            string version="",
            string parameter="",
            string valueJson="null",
            bool dryRun=true) { Calls++; return new ResponseMessage { Meta = new JsonObject { ["success"] = true } }; }
    }
}

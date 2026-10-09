using TiaMcpServer;
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
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseGlobalLibraryProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? LibraryType { get; set; }
        public IEnumerable<string>? Members { get; set; }
        public IEnumerable<string>? MasterCopies { get; set; }
        public IEnumerable<string>? Types { get; set; }
        public IEnumerable<string>? Folders { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public JsonObject? Raw { get; set; }
    }

    public class ResponseGlobalLibraryImport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? MasterCopyName { get; set; }
        public string? HmiSoftwarePath { get; set; }
        public string? ScreenName { get; set; }
        public string? ImportedItemName { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public IEnumerable<string>? ReadbackItems { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public JsonObject? Raw { get; set; }
    }

}

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class LibraryService
    {
        internal int Calls;
        internal string? LastArguments;
        public ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(string libraryPath, string masterCopyName, string hmiSoftwarePath, string screenName, string importedItemName = "", int left = 0, int top = 0)
        {
            Calls++;
            return new ResponseGlobalLibraryImport { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseGlobalLibraryProbe ProbeGlobalLibrary(string libraryPath, int maxItems = 500)
        {
            Calls++;
            return new ResponseGlobalLibraryProbe { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false, string archiveName="", string archiveMode="Compressed", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ImportLibraryTypeDocuments(string filePath, string folderPath="", string libraryName="", string importOptions="None", string typePath="", string createOptions="None", string targetSoftwarePath="", string targetGroupKind="", string targetGroupPath="", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageLibraryMasterCopy(string sourcePath, string action, string libraryName="", string destinationLibraryName="", string destinationPath="", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage CreateLibraryMasterCopy(string sourceKind, string sourcePath, string softwarePath="", string folderPath="", string libraryName="", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageLibraryTypeVersion(string typePath, string version, string action, string libraryName="", string newVersion="", string dependenciesMode="", string author="", string comment="", string targetSoftwarePath="", bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage CompareLibraryObjects(string kind, string leftPath, string rightPath, string leftLibraryName="", string rightLibraryName="", string leftVersion="", string rightVersion="", bool includeIdentical=false, int offset=0, int limit=100)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage SynchronizeLibrary(string action, string selectionJson, string libraryName="", string targetLibraryName="", string scopeSoftwarePathsJson="[]", string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode="DoNotDelete", string structureConflictResolutionMode="RetainStructure", string harmonizeOptionsJson="[\"HarmonizeNames\",\"HarmonizePaths\"]", string cleanUpMode="PreserveDefaultVersionOfUnusedTypes", bool confirmChange=false, bool dryRun=true)
        {
            Calls++;
            LastArguments = selectionJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage CheckLibraryUpdates(string libraryName="", string updateCheckMode="ReportOutOfDateOnly", int maxItems=2000)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageLibraryType(string typePath, string action, string libraryName="", string propertiesJson="{}", string targetLibraryName="", string scopeSoftwarePathsJson="[]", string deleteUnusedVersionsMode="DoNotDelete", string structureConflictResolutionMode="RetainStructure", string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault", bool confirmDelete=false, bool dryRun=true)
        {
            Calls++;
            LastArguments = propertiesJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ReadLibraryType(string libraryName="", string typePath="", string guid="", int offset=0, int limit=100)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ReadLibraryOverview(string libraryName="", bool includeTypes=true, bool includeMasterCopies=true, int maxDepth=6, int maxItems=500)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class SivarcService
    {
        internal string? LastReferences;
        internal int Calls;
        internal string? LastArguments;
        public ResponseMessage GenerateSiVArc(string hmiDeviceName, string plcSoftwarePathsJson, string generationOptions, bool dryRun=true, string additionalHmiDeviceNamesJson="[]")
        {
            Calls++;
            LastArguments = plcSoftwarePathsJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage UpgradeSivarcDefinitions(string softwarePath, bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSivarcScreenLayout(string softwarePath, string screenName, string action, string filePath, bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ResolveSivarcExpression(string softwarePath, string blockPath, string devicePathJson, string itemPathJson, string libraryItemKind, string libraryItemPath, string expression, string libraryName="", int maxResults=500)
        {
            Calls++;
            LastArguments = devicePathJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSivarcBlockDefinition(string softwarePath, string blockPath, string kind, string name="", string action="read", string propertiesJson="{}", string textsJson="{}", bool confirmDelete=false, bool dryRun=true)
        {
            Calls++;
            LastArguments = propertiesJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ReadSivarcBlockDefinitions(string softwarePath, string blockPath, bool includeBlockParameters=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSivarcTableRule(string category, string tablePath, string rulePath="", string kind="rule", string action="read", string propertiesJson="{}", string referencesJson="{}", string deviceSelectionJson="{}", string deviceNamesJson="[]", string libraryName="", string masterCopyPath="", string createOption="Replace", bool confirmDelete=false, bool dryRun=true)
        {
            Calls++;
            LastArguments = propertiesJson;
            LastReferences = referencesJson;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSivarcRuleContainer(string category, string kind, string path, string action="read", string libraryName="", string typePath="", string typeVersion="", bool confirmDelete=false, bool dryRun=true)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ReadSivarcRuleTree(string category, string folderPath="", string tablePath="", bool includeRules=true, int maxDepth=4, int offset=0, int limit=200)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class VersionControlService
    {
        internal int Calls;
        internal string? LastArguments;
        public ResponseStringList ConnectProjectToWorkspace(string workspaceName = "", bool dryRun = true, string deviceFilter = "", int maxObjects = 3000, bool walkTrace = false)
        {
            Calls++;
            return new ResponseStringList { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseStringList SyncVersionControlWorkspace(string direction = "ProjectToWorkspace", string workspaceName = "", bool dryRun = true, bool changedOnly = true)
        {
            Calls++;
            return new ResponseStringList { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseStringList GetVersionControlStatus(string workspaceName = "", bool changedOnly = true)
        {
            Calls++;
            return new ResponseStringList { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage CreateVersionControlWorkspace(string workspaceName, string folderPath)
        {
            Calls++;
            return new ResponseMessage { Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseStringList GetVersionControlWorkspaces()
        {
            Calls++;
            return new ResponseStringList { Meta = new JsonObject { ["success"] = true } };
        }
    }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class LibrarySivarcVersionControlContractsTests : IDisposable
    {
        private static readonly string[] Names = { "AnalyzeUnifiedHmiTemplateLayout", "AnalyzeHmiTemplateReference", "PlanGlobalLibraryTemplateReuse", "AnalyzeGlobalLibraryPackage", "ImportMasterCopyFromGlobalLibrary", "ProbeGlobalLibrary", "ManageLibraryFolder", "ManageGlobalLibrary", "ImportLibraryTypeDocuments", "ManageLibraryMasterCopy", "CreateLibraryMasterCopy", "ManageLibraryTypeVersion", "CompareLibraryObjects", "SynchronizeLibrary", "CheckLibraryUpdates", "ManageLibraryType", "GetLibraryType", "GetLibraryOverview", "GenerateSivarc", "UpgradeSivarcDefinitions", "ManageSivarcScreenLayout", "ResolveSivarcExpression", "ManageSivarcBlockDefinition", "ListSivarcBlockDefinitions", "ManageSivarcTableRule", "ManageSivarcRuleContainer", "GetSivarcRuleTree", "ConnectProjectToWorkspace", "SynchronizeVersionControlWorkspace", "GetVersionControlStatus", "CreateVersionControlWorkspace", "ListVersionControlWorkspaces" };
        private readonly LibraryService library = new LibraryService();
        private readonly SivarcService sivarc = new SivarcService();
        private readonly VersionControlService vci = new VersionControlService();
        private readonly LibraryTools libraryTools;
        private readonly SivarcTools sivarcTools;
        private readonly ToolCatalog catalog;
        public LibrarySivarcVersionControlContractsTests()
        {
            libraryTools = new LibraryTools(library);
            sivarcTools = new SivarcTools(sivarc);
            catalog = new ToolCatalog(new[] { typeof(LibraryTools), typeof(SivarcTools), typeof(VersionControlTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(libraryTools).AddSingleton(sivarcTools)
                .AddSingleton(new VersionControlTools(vci)).BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static JsonObject Body(CallToolResult result)
        {
            var text = ((TextContentBlock)Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }
        private static void Rejected(CallToolResult result)
        {
            var body = Body(result);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }
        [Fact]
        public void OneRegistrationPerV4NameAndConcreteTypedInputs()
        {
            Assert.Equal(Names.OrderBy(n => n), catalog.Methods.Select(p => p.Key).OrderBy(n => n));
            foreach (var method in catalog.Methods.Select(p => p.Value))
            {
                Assert.Equal(typeof(CallToolResult), method.ReturnType);
                Assert.DoesNotContain(method.GetParameters(), p => p.Name!.EndsWith("Json", StringComparison.Ordinal));
            }
            Assert.Equal(typeof(LibrarySelection[]), catalog.Methods.Single(p => p.Key == "SynchronizeLibrary").Value.GetParameters().Single(p => p.Name == "selection").ParameterType);
            Assert.Equal(typeof(Dictionary<string, SivarcReference?>), catalog.Methods.Single(p => p.Key == "ManageSivarcTableRule").Value.GetParameters().Single(p => p.Name == "references").ParameterType);
            Assert.Equal(typeof(TemplateIntent), catalog.Methods.Single(p => p.Key == "PlanGlobalLibraryTemplateReuse").Value.GetParameters().Single(p => p.Name == "templateIntent").ParameterType);
        }
        [Fact]
        public void DirectBusinessValidationPreservesCaseRangeAndSelection()
        {
            Rejected(libraryTools.GetLibraryOverviewV4(maxDepth: 17));
            Rejected(libraryTools.SynchronizeLibraryV4("cleanUp", Array.Empty<LibrarySelection>()));
            Rejected(libraryTools.ManageLibraryTypeV4("T", "Update"));
            Rejected(sivarcTools.GetSivarcRuleTreeV4("Screens"));
            Rejected(sivarcTools.ManageSivarcTableRuleV4("screens", "Table", "Rule", action: "update",
                properties: V4Json.Deserialize<AttributeMap<Scalar>>("{\"Condition\":\"" + new string('x', 501) + "\"}")));
            Rejected(sivarcTools.GenerateSivarcV4("HMI", new[] { "PLC", "PLC" }, "None"));
            Assert.Equal(0, library.Calls + sivarc.Calls + vci.Calls);
        }
        [Fact]
        public void OptionalCollectionsPreserveServiceDefaultsAndReferenceNull()
        {
            Body(libraryTools.SynchronizeLibraryV4("cleanUp", DomainValidation.Read<LibrarySelection[]>("[{\"folder\":\"\"}]")));
            Assert.Equal("[{\"folder\":\"\"}]", library.LastArguments);
            var references = DomainValidation.Read<Dictionary<string, SivarcReference?>>("{\"ProgramBlock\":null}");
            Body(sivarcTools.ManageSivarcTableRuleV4("screens", "Table", "Rule", action: "update", references: references));
            Assert.Equal("{}", sivarc.LastArguments);
            Assert.Equal("{\"ProgramBlock\":null}", sivarc.LastReferences);
        }
        public static IEnumerable<object[]> InvalidInputs()
        {
            yield return new object[] { "ResolveSivarcExpression", "{\"softwarePath\":\"PLC\",\"blockPath\":\"FB\",\"devicePath\":\"[]\",\"itemPath\":[],\"libraryItemKind\":\"masterCopy\",\"libraryItemPath\":\"M\",\"expression\":\"x\"}" };
            foreach (var selection in new[] { "null", "\"[]\"", "[]", "[{\"Folder\":\"\"}]", "[{\"folder\":\"F\",\"type\":\"T\"}]" })
                yield return new object[] { "SynchronizeLibrary", "{\"action\":\"cleanUp\",\"selection\":" + selection + "}" };
            yield return new object[] { "PlanGlobalLibraryTemplateReuse", "{\"libraryPath\":\"missing\",\"templateIntent\":{\"extra\":1}}" };
            yield return new object[] { "ManageSivarcTableRule", "{\"category\":\"screens\",\"tablePath\":\"T\",\"rulePath\":\"R\",\"references\":{\"ProgramBlock\":{\"kind\":\"plcBlock\",\"path\":\"B\"}}}" };
            yield return new object[] { "ManageSivarcTableRule", "{\"category\":\"screens\",\"tablePath\":\"T\",\"rulePath\":\"R\",\"deviceSelection\":{\"PLC\":1}}" };
            yield return new object[] { "ManageLibraryType", "{\"typePath\":\"T\",\"action\":\"update\",\"properties\":{\"Name\":{}}}" };
        }
        [Fact]
        public void DuplicateNestedFieldsAreRefusedByTheArgumentsCarrier()
        {
            const string json = "{\"selection\":[{\"folder\":\"F\",\"folder\":\"G\"}]}";
            Assert.ThrowsAny<Exception>(() => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json)));
            Assert.NotNull(DomainValidation.Contract<LibrarySelection[]>().Read("[{\"folder\":\"F\",\"folder\":\"G\"}]", "selection").Error);
        }
        [Theory]
        [MemberData(nameof(InvalidInputs))]
        public async Task DirectSdkCallToolAndBatchRejectBeforeService(string name, string json)
        {
            var arguments = JsonSerializer.Deserialize<JsonElement>(json);
            var direct = new VersionPolicyTool(McpServer.CreateTool(name, catalog.Methods.Single(p => p.Key == name).Value));
            var directResult = await direct.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>()) {
                Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
            });
            Rejected(directResult);
            Rejected(McpServer.CallTool(name, new ToolArguments(arguments)));
            var calls = new[] { new ToolCall(name, new ToolArguments(arguments)) };
            Rejected(name == "ResolveSivarcExpression" || name == "PlanGlobalLibraryTemplateReuse"
                ? McpServer.ReadToolBatch(calls) : McpServer.PreviewToolBatch(calls, "fixture"));
            Assert.Equal(0, library.Calls + sivarc.Calls + vci.Calls);
        }
        [Fact]
        public void EmptyListsAreArraysAndSuccessfulDirectBridgeBatchResultsMatch()
        {
            var empty = Body(LibraryToolContract.Map("ListVersionControlWorkspaces", new ResponseStringList { Meta = new JsonObject { ["success"] = false } }, false));
            Assert.Empty(empty["data"]!["items"]!.AsArray());
            var args = new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"category\":\"screens\"}"));
            var direct = Body(sivarcTools.GetSivarcRuleTreeV4("screens"));
            var bridge = Body(McpServer.CallTool("GetSivarcRuleTree", args));
            var batch = Body(McpServer.ReadToolBatch(new[] { new ToolCall("GetSivarcRuleTree", args) }));
            var nested = batch["data"]!["items"]![0]!["result"]!.AsObject();
            foreach (var result in new[] { direct, bridge, nested })
            {
                Assert.Equal("GetSivarcRuleTree", (string?)result["meta"]!["tool"]);
                result["meta"]!.AsObject().Remove("timestamp"); result["meta"]!.AsObject().Remove("requestId");
            }
            Assert.True(JsonNode.DeepEquals(direct, bridge));
            Assert.True(JsonNode.DeepEquals(direct, nested));
        }
        [Fact]
        public void SelectionAndScopeBudgetsRemainBounded()
        {
            var selection = "[" + string.Join(",", Enumerable.Range(0, 201).Select(i => "{\"type\":\"T" + i + "\"}")) + "]";
            Assert.Equal(ErrorCode.LimitExceeded, DomainValidation.Contract<LibrarySelection[]>().Read(selection, "selection").Error!.Code);
            Rejected(libraryTools.ManageLibraryTypeV4("T", "updateProject", scopeSoftwarePaths: Enumerable.Range(0, 33).Select(i => "PLC" + i).ToArray()));
            Assert.Equal(0, library.Calls);
        }
        [Fact]
        public void GroupExamplesUseTheAdvertisedSchemas()
        {
            foreach (var entry in catalog.Methods)
            {
                if (!TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey).Any(row => (string?)row!["currentName"] == entry.Key)) continue;
                var body = Body(new ToolUsageTools().GetToolUsage(toolName: entry.Key));
                Assert.True((bool)body["ok"]!, entry.Key);
                var data = body["data"]!;
                Assert.Null(McpServer.ValidateV4Arguments(entry.Value,
                    JsonSerializer.SerializeToElement(data["example"]!["request"]!["params"]!["arguments"]),
                    JsonSerializer.SerializeToElement(data["inputSchema"])));
            }
            Assert.Equal(0, library.Calls + sivarc.Calls + vci.Calls);
        }
        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":true,\"groupsTruncated\":true}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"synchronized\":2,\"failed\":1}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"result\":{\"state\":\"Unrecognized\"}}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"generationPassed\":false}", true, "failed", "completed", "complete")]
        [InlineData("{\"success\":false,\"transferResultState\":\"Failure\",\"createdType\":{\"name\":\"T\"}}", true, "unknown", "unknown", "unknown")]
        public void EnvelopePreservesKnownPartialAndUnknownEvidence(string json, bool writes, string outcome, string execution, string completeness)
        {
            var body = Body(LibraryToolContract.Map("fixture", new ResponseMessage { Meta = JsonNode.Parse(json)!.AsObject() }, writes));
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.NotNull(body["data"]!["evidence"]);
        }
    }
}

using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcImportCandidateTools
    {
        private readonly IEngineeringSession session;
        public PlcImportCandidateTools(IEngineeringSession session) { this.session = session; }
        private CallToolResult Run(string tool, PlcImportRequest request, string mode, bool confirm, string hash, string project)
        {
            var result = McpResult.From(PlcImportCandidateService.For(session).Run(tool, request, mode, confirm, hash, project));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }

        [BehaviorCandidate("ImportPlcBlock", "P6-IMPORT", typeof(PlcImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcBlock(string softwarePath, string groupPath, string importPath, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcBlock", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = importPath, BlockGroupPath = groupPath, MaxItems = 1,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcType", "P6-IMPORT", typeof(PlcTypeImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcType(string softwarePath, string groupPath, string importPath, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcType", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = importPath, TypeGroupPath = groupPath, MaxItems = 1,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcTagTable", "P6-IMPORT", typeof(PlcTagImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcTagTable(string softwarePath, string folderPath, string importPath, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcTagTable", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = importPath, TagFolderPath = folderPath, MaxItems = 1,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcBlocksFromDirectory", "P6-IMPORT", typeof(PlcBlocksImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcBlocksFromDirectory(string softwarePath, string groupPath, string dir, string[] importOrder, string regexName = "", int maxItems = 128, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcBlocksFromDirectory", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = dir, BlockGroupPath = groupPath, ImportOrder = importOrder, RegexName = regexName, MaxItems = maxItems,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcTagTablesFromDirectory", "P6-IMPORT", typeof(PlcTagsImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string[] importOrder, string regexName = "", int maxItems = 128, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcTagTablesFromDirectory", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = dir, TagFolderPath = folderPath, ImportOrder = importOrder, RegexName = regexName, MaxItems = maxItems,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcProgramFromDirectory", "P6-IMPORT", typeof(PlcProgramImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcProgramFromDirectory(string softwarePath, string sourceDir, string[] importOrder, string typeGroupPath = "", string tagFolderPath = "", string technologyFolderPath = "", string blockGroupPath = "", string regexName = "", int maxItems = 128, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcProgramFromDirectory", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = sourceDir, ImportOrder = importOrder, TypeGroupPath = typeGroupPath, TagFolderPath = tagFolderPath, TechnologyFolderPath = technologyFolderPath, BlockGroupPath = blockGroupPath, RegexName = regexName, MaxItems = maxItems,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcBlockDocuments", "P6-IMPORT", typeof(PlcDocumentImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcBlockDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcBlockDocuments", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = importPath, BlockGroupPath = groupPath, FileNameWithoutExtension = fileNameWithoutExtension, MaxItems = 1,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ImportPlcBlocksDocuments", "P6-IMPORT", typeof(PlcDocumentsImportContract))]
        [Description("[PLC-Software][WRITE] Reviewed same-release PLC import. Default preview returns original file hashes, exact groups, complete inventory, overwrite capability and plan. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. Caller orders directory inputs. Original bytes remain locked; one import per item, content readback, stop on first failure, no rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ImportPlcBlocksDocuments(string softwarePath, string groupPath, string importPath, string[] importOrder, string regexName = "", int maxItems = 16, bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcBlocksDocuments", new PlcImportRequest { SoftwarePath = softwarePath, InputPath = importPath, BlockGroupPath = groupPath, ImportOrder = importOrder, RegexName = regexName, MaxItems = maxItems,
                Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter }, mode, confirm, expectedPlanHash, expectedProjectFile);
    }
}

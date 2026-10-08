using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class NativeExchangeTools
    {
        private readonly IEngineeringSession _session;
        private readonly NativeExchangeService _domain;

        public NativeExchangeTools(NativeExchangeService domain, IEngineeringSession session)
        {
            _domain = domain;
            _session = session;
        }

        public ResponseMessage ManageProjectLanguage(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
        => _session.ManageProjectLanguage(action,culture,dryRun);

        public ResponseMessage CreatePlcInstanceDb(
            string softwarePath,
            [Description("fbPath: 'Group/Name' of the function block the instance DB is created for.")] string fbPath,
            string name,
            string groupPath="",
            [Description("autoNumber: true lets TIA assign the block number.")] bool autoNumber=true,
            [Description("number: block number when autoNumber is false.")] int number=0,
            bool dryRun=true)
        => _domain.CreatePlcInstanceDb(softwarePath,fbPath,name,groupPath,autoNumber,number,dryRun);

        public ResponseMessage GeneratePlcSourceFromBlocks(
            string softwarePath,
            [Description("blockPathsJson: JSON array of block paths.")] string blockPathsJson,
            string filePath,
            bool dryRun=true)
        => _domain.GeneratePlcSourceFromBlocks(softwarePath,blockPathsJson,filePath,dryRun);

        public ResponseMessage GeneratePlcLoadableFile(
            string softwarePath,
            [Description("objectPathsJson: JSON array of object paths.")] string objectPathsJson,
            [Description("objectKind: blocks | units.")] string objectKind,
            [Description("targetOption: Openness loadable-file target option name ('' = default).")] string targetOption,
            string filePath,
            bool dryRun=true)
        => _domain.GeneratePlcLoadableFile(softwarePath,objectPathsJson,objectKind,targetOption,filePath,dryRun);

        public ResponseMessage RetrieveProjectArchive(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            string destinationDirectory,
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            bool dryRun=true)
        => _session.RetrieveProjectArchive(archivePath,destinationDirectory,upgrade,dryRun);

        public ResponseMessage ExportProjectTexts(
            string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
        => _session.ExportProjectTexts(filePath,sourceCulture,targetCulture,dryRun);

        public ResponseMessage ImportProjectTexts(
            string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
        => _session.ImportProjectTexts(filePath,updateSourceLanguage,dryRun);

        public ResponseMessage ManagePlcTagDefinition(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tablePath: 'Group/Table' path of the tag table, e.g. 'Default tag table'.")] string tablePath,
            [Description("name: exact tag or constant name.")] string name,
            [Description("kind: tag | constant.")] string kind,
            [Description("action: read | create | update | delete.")] string action,
            [Description("dataType: for create / update - PLC data type, e.g. Bool, Int, Real.")] string dataType="",
            [Description("addressOrValue: for create / update - logical address of a tag (e.g. %M0.0, %I0.0) or the value of a constant.")] string addressOrValue="",
            [Description("propertiesJson: JSON object of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; Comment as text or per culture).")] string propertiesJson="{}",
            [Description("dryRun: true (default) previews; false executes (project must be offline).")] bool dryRun=true)
        => _domain.ManagePlcTagDefinition(softwarePath,tablePath,name,kind,action,dataType,addressOrValue,propertiesJson,dryRun);

        [McpServerTool(Name = "ManageProjectLanguage"), Description("[L2][Project][WRITE] Read/activate/deactivate/setEditing/setReference project language by exact culture. Default preview. Editing/reference must be active, cannot deactivate either; readback verified; no save/compile/download. Returns a V4 envelope; inspect outcome, execution and completeness.")]
        public CallToolResult ManageProjectLanguageV4(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
        {
            return PlcExchangeContract.Run("ManageProjectLanguage", () => ManageProjectLanguage(action, culture, dryRun), write: !dryRun && action != "read", current: false);
        }

        [McpServerTool(Name = "CreatePlcInstanceDb"), Description("[L2][PLC-Software][WRITE] Create a native instance DB from an exact FB and destination block group. Default preview; requires offline PLC when executing. No save/compile/download." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult CreatePlcInstanceDbV4(
            string softwarePath,
            [Description("fbPath: 'Group/Name' of the function block the instance DB is created for.")] string fbPath,
            string name,
            string groupPath="",
            [Description("autoNumber: true lets TIA assign the block number.")] bool autoNumber=true,
            [Description("number: block number when autoNumber is false.")] int number=0,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("CreatePlcInstanceDb", () => CreatePlcInstanceDb(softwarePath, fbPath, name, groupPath, autoNumber, number, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "GeneratePlcSourceFromBlocks"), Description("[L2][PLC-Software][FILE] Generate native source from 1..500 exact block paths (array). New absolute output file only; SHA-256 returned. Unsupported block languages rejected; default preview." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult GeneratePlcSourceFromBlocksV4(
            string softwarePath,
            [Description("blockPaths: Array of block paths.")] string[] blockPaths,
            string filePath,
            bool dryRun=true)
        {
            var validated = PlcExchangeContract.Paths.Validate(blockPaths, "blockPaths");
            if (validated.Error != null) return PlcExchangeContract.Reject("GeneratePlcSourceFromBlocks", validated.Error);
            return PlcExchangeContract.Run("GeneratePlcSourceFromBlocks", () => GeneratePlcSourceFromBlocks(softwarePath, V4Json.Serialize(validated.Value), filePath, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "GeneratePlcLoadableFile"), Description("[L2][PLC-Software][FILE] Native GenerateLoadable for exact blocks or units with explicit TargetOption enum name. New absolute output file; default preview. Does not download or assert compiled content completeness." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult GeneratePlcLoadableFileV4(
            string softwarePath,
            [Description("objectPaths: Array of object paths.")] string[] objectPaths,
            [Description("objectKind: blocks | units.")] string objectKind,
            [Description("targetOption: Openness loadable-file target option name ('' = default).")] string targetOption,
            string filePath,
            bool dryRun=true)
        {
            var validated = PlcExchangeContract.Paths.Validate(objectPaths, "objectPaths");
            if (validated.Error != null) return PlcExchangeContract.Reject("GeneratePlcLoadableFile", validated.Error);
            return PlcExchangeContract.Run("GeneratePlcLoadableFile", () => GeneratePlcLoadableFile(softwarePath, V4Json.Serialize(validated.Value), objectKind, targetOption, filePath, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "RetrieveProjectArchive"), Description("[L2][Project][WRITE] Retrieve an archive into a new absolute directory and bind returned project. Requires connected Portal with no open project/session; never closes existing projects. upgrade=false, dryRun=true." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult RetrieveProjectArchiveV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            [Description("destinationDirectory: absolute path of a new directory on the TIA machine; existing directories are refused (no merge or overwrite).")] string destinationDirectory,
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("dryRun: true validates the archive and destination without retrieving, creating files or opening a project; false retrieves and binds the returned project.")] bool dryRun=true)
        {
            return PlcExchangeContract.Run("RetrieveProjectArchive", () => RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ExportProjectTexts"), Description("[L2][Project][FILE] Native project text export for explicit source/target cultures to a new absolute file. Returns API completion and file hash; default preview." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ExportProjectTextsV4(
            string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ExportProjectTexts", () => ExportProjectTexts(filePath, sourceCulture, targetCulture, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ImportProjectTexts"), Description("[L2][Project][WRITE] Native project text import. updateSourceLanguage explicitly controls updating SOURCE language, not a generic overwrite switch. Default preview; returns native diagnostics; no save/compile/download." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ImportProjectTextsV4(
            string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ImportProjectTexts", () => ImportProjectTexts(filePath, updateSourceLanguage, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ManagePlcTagDefinition"), Description("[L2][PLC-Software][WRITE] Read/create/update/delete an exact tag or constant. properties accepts scalar SDK attributes; Comment is text in the editing language. Default preview, execution requires Offline. No runtime write, save, compile or download. V4 envelope; PLC path policy remains current pending native acceptance.")]
        public CallToolResult ManagePlcTagDefinitionV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tablePath: 'Group/Table' path of the tag table, e.g. 'Default tag table'.")] string tablePath,
            [Description("name: exact tag or constant name.")] string name,
            [Description("kind: tag | constant.")] string kind,
            [Description("action: read | create | update | delete.")] string action,
            [Description("dataType: for create / update - PLC data type, e.g. Bool, Int, Real.")] string dataType="",
            [Description("addressOrValue: for create / update - logical address of a tag (e.g. %M0.0, %I0.0) or the value of a constant.")] string addressOrValue="",
            [Description("properties: Scalar attribute map of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; constants: Value; Comment: text).")] AttributeMap<Scalar>? properties=null,
            [Description("dryRun: true (default) previews; false executes (project must be offline).")] bool dryRun=true)
        {
            Func<ResponseMessage> invoke = () => ManagePlcTagDefinition(softwarePath, tablePath, name, kind, action, dataType, addressOrValue, dryRun: dryRun);
            if (properties != null)
            {
                var validated = PlcExchangeContract.TagProperties(kind).Validate(properties, "properties");
                if (validated.Error != null) return PlcExchangeContract.Reject("ManagePlcTagDefinition", validated.Error);
                invoke = () => ManagePlcTagDefinition(softwarePath, tablePath, name, kind, action, dataType, addressOrValue, V4Json.Serialize(validated.Value), dryRun);
            }
            return PlcExchangeContract.Run("ManagePlcTagDefinition", invoke, write: !dryRun && action != "read", current: true);
        }

    }
}

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



        [McpServerTool(Name = "ManageProjectLanguage"), Description("[L2][Project][WRITE] Read/activate/deactivate/setEditing/setReference project language by exact culture. Default preview. Editing/reference must be active, cannot deactivate either; readback verified; no save/compile/download. Returns a V4 envelope; inspect outcome, execution and completeness.")]
        public CallToolResult ManageProjectLanguageV4(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
        {
            return PlcExchangeContract.Run("ManageProjectLanguage", () => ManageProjectLanguage(action, culture, dryRun), write: !dryRun && action != "read", current: false);
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
            [Description("filePath: new absolute XLSX file in an existing directory.")] string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ExportProjectTexts", () => ExportProjectTexts(filePath, sourceCulture, targetCulture, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ImportProjectTexts"), Description("[L2][Project][WRITE] Native project text import. updateSourceLanguage explicitly controls updating SOURCE language, not a generic overwrite switch. Default preview; returns native diagnostics; no save/compile/download." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ImportProjectTextsV4(
            [Description("filePath: existing absolute XLSX file exported by ExportProjectTexts.")] string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ImportProjectTexts", () => ImportProjectTexts(filePath, updateSourceLanguage, dryRun), write: !dryRun, current: true);
        }



    }
}

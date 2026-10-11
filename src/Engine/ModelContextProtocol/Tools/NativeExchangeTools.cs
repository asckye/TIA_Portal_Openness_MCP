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

    }
}

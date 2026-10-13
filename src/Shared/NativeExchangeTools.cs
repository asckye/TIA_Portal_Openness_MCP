using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol;

[McpServerToolType]
internal sealed class NativeExchangeTools
{
    private readonly OnlineDownloadService _onlineDownload;
    public NativeExchangeTools(OnlineDownloadService onlineDownload) => _onlineDownload = onlineDownload;
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

        public ResponseMessage GeneratePlcLoadableFile(
            string softwarePath,
            [Description("objectPathsJson: JSON array of object paths.")] string objectPathsJson,
            [Description("objectKind: blocks | units.")] string objectKind,
            [Description("targetOption: Openness loadable-file target option name ('' = default).")] string targetOption,
            string filePath,
            bool dryRun=true)
        => _onlineDownload.GeneratePlcLoadableFile(softwarePath,objectPathsJson,objectKind,targetOption,filePath,dryRun);
}

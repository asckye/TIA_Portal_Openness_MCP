using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcProDiagTools
    {
        private readonly PlcB5PortService _service;
        public PlcProDiagTools(PlcB5PortService service) => _service = service;
        [McpServerTool(Name="ManagePlcSupervision"), Description("[L2][PLC-Software][WRITE] ProDiag native object access on an exact PLC or block: read provider metadata (attributes, advertised compositions), readComposition/createEntry/deleteEntry through the official IEngineeringObject composition API (typeName must be advertised by GetCreationInfos), setAttributes on the provider, exportSettings/importSettings (.dat) via SupervisionSettingsProvider. Openness exposes no typed supervision list; XLSX bulk exchange is ExchangePlcSupervisions. Offline PLC for real writes; deletes need confirmDelete=true. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcSupervision(
            string softwarePath,
            [Description("read | readComposition | createEntry | deleteEntry | setAttributes | exportSettings | importSettings. ")] string action,
            string blockPath="",
            [Description("providerKind: supervision | settings.")] string providerKind="supervision",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManagePlcSupervision", dryRun || action == "read" || action == "readComposition", action != "read" && action != "readComposition",
                () => _service.ManagePlcSupervision(softwarePath,action,blockPath,providerKind,compositionName,entryName,typeName,filePath,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="ExportPlcProDiagInfo"), Description("[L2][PLC-Software][READ] Native CodeBlock.ExportProDIAGInfo: writes the alarm messages of one exact ProDiag FB (blockPath under BlockGroup or a unit's BlockGroup) as CSV files into an existing directoryPath on the TIA Portal machine and hashes the new files. Refused before the call unless the block is a ProDiag FB and consistent (compile first). Default dryRun=true; no project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportPlcProDiagInfo(
            string softwarePath,
            string blockPath,
            [Description("directoryPath: folder on the TIA machine that receives the files.")] string directoryPath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ExportPlcProDiagInfo", dryRun, true,
                () => _service.ExportPlcProDiagInfo(softwarePath,blockPath,directoryPath,unitName,unitKind,dryRun));
    }
}

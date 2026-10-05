using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SoftwareUnitManagementTools
    {
        private readonly SoftwareUnitManagementService _units;

        public SoftwareUnitManagementTools(SoftwareUnitManagementService service) => _units = service;

        [McpServerTool(Name="SetPlcUnitObjectAccess"), Description("[L2][PLC-Software][WRITE] Publish/unpublish a block or PLC type inside an exact software unit using official Access attribute. access=Published/Unpublished. objectPath includes nested groups relative to the unit's block/type root; OB publication is refused by native API. dryRun=true default; writes require Offline and readback. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SetPlcUnitObjectAccessV4(
            string softwarePath,
            string unitName,
            [Description("block | type. Object family inside the exact software unit.")] string objectKind,
            string objectPath,
            [Description("Published | Unpublished. Native software-unit object publication state.")] string access,
            bool dryRun=true)
            => EngineeringToolContract.Run("SetPlcUnitObjectAccess", !dryRun, () => SetPlcUnitObjectAccess(softwarePath, unitName, objectKind, objectPath, access, dryRun));

        public ResponseMessage SetPlcUnitObjectAccess(
            string softwarePath,
            string unitName,
            string objectKind,
            string objectPath,
            string access,
            bool dryRun=true)
            => _units.SetPlcUnitObjectAccess(softwarePath,unitName,objectKind,objectPath,access,dryRun);
    }
}

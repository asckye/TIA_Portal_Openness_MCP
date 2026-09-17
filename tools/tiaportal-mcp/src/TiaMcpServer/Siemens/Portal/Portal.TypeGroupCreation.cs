using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Types;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public JsonObject CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            PlcTypeGroupCreation.Parse(groupPath);
            if (IsProjectNull() || _portal == null)
                throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _portal.ExclusiveAccess("MCP: create PLC type groups");
                var plc = ResolveSoftwareContainerUncached(softwarePath)?.Software as PlcSoftware
                    ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                var result = PlcTypeGroupCreation.Execute<PlcTypeGroup>(plc.TypeGroup, groupPath, dryRun,
                    g => g.Groups, g => g.Name, (g, name) => g.Groups.Create(name));
                result["softwarePath"] = softwarePath;
                result["resolvedSoftwareName"] = plc.Name;
                return result;
            }
        }
    }
}

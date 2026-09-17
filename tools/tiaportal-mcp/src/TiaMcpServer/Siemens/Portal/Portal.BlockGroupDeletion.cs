using System;
using System.Linq;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.Online;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private readonly object _blockGroupDeleteGate = new object();
        public JsonObject DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            EmptyPlcGroupDeletion.Parse(groupPath);
            if (IsProjectNull() || _portal==null) throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _portal.ExclusiveAccess("MCP: delete one empty PLC user block group");
                // Destructive operations use the shared exact container resolver, never fuzzy PLC selection.
                var sc=ResolveSoftwareContainerUncached(softwarePath);
                var plc=sc?.Software as PlcSoftware ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                PlcBlockUserGroup? Find(string[] parts)
                {
                    PlcBlockGroup current=plc.BlockGroup;
                    foreach(var name in parts)
                    {
                        var matches=current.Groups.Where(g=>string.Equals(g.Name,name,StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                        if(matches.Count==0) return null;
                        if(matches.Count!=1) throw new PortalException(PortalErrorCode.InvalidParams,"Ambiguous user group: " + name);
                        current=matches[0];
                    }
                    return current as PlcBlockUserGroup ?? throw new PortalException(PortalErrorCode.InvalidParams,"Only user block groups can be deleted.");
                }
                var result=EmptyPlcGroupDeletion.Execute(groupPath,dryRun,Find,g=>g.Blocks.Count,g=>g.Groups.Count,
                    ()=>ResolvePlcService<OnlineProvider>(softwarePath,plc)?.State.ToString() ?? "Unknown",
                    g=>g.Delete());
                result["softwarePath"]=softwarePath;
                result["resolvedSoftwareName"]=plc.Name;
                result["success"]=true;
                return result;
            }
        }
    }
}

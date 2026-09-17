using System;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW;
using Siemens.Engineering.Online;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => RunHmiStepTool("ManagePlcUserGroup", meta => {
                var shape = family switch {
                    "blocks" => ("BlockGroup", "Blocks"), "types" => ("TypeGroup", "Types"),
                    "tags" => ("TagTableGroup", "TagTables"), "technology" => ("TechnologicalObjectGroup", "TechnologicalObjects"),
                    _ => throw new ArgumentException("family must be blocks, types, tags or technology.") };
                EngineeringGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var plc = ResolveSoftwareContainerUncached(softwarePath)?.Software as PlcSoftware
                    ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                meta["softwarePath"] = softwarePath; meta["resolvedSoftwareName"] = plc.Name;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun && ResolvePlcService<OnlineProvider>(softwarePath, plc)?.State.ToString() != "Offline")
                    throw new PortalException(PortalErrorCode.InvalidState, "Confirmed Offline state is required for group editing.");
                if (!dryRun) meta["mayHaveChanged"] = true;
                meta["result"] = EngineeringGroupOperations.Manage(EngineeringGroupOperations.Get(plc, shape.Item1), groupPath, action, newName, dryRun, shape.Item2);
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });

        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedHmiGroup", meta => {
                var shape = family switch { "screens" => ("ScreenGroups", "Screens"), "tags" => ("TagTableGroups", "TagTables"),
                    _ => throw new ArgumentException("family must be screens or tags. Unified exposes no ScriptGroups collection in the supported PublicAPI.") };
                EngineeringGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Only WinCC Unified is supported.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = !dryRun;
                meta["result"] = EngineeringGroupOperations.Manage(hmi, groupPath, action, newName, dryRun, shape.Item2, shape.Item1);
                return dryRun ? "Unified group preview; nothing changed." : "Unified group operation completed. Project not saved.";
            });
    }
}

using System;
using System.Linq;
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
                    "watchTables" => ("WatchAndForceTableGroup", "WatchTables"), "externalSources" => ("ExternalSourceGroup", "ExternalSources"),
                    _ => throw new ArgumentException("family must be blocks, types, tags, technology, watchTables or externalSources.") };
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
                if (action != "deleteEmpty" || dryRun) { try { meta["group"] = UserGroupRow(EngineeringGroupOperations.Group(EngineeringGroupOperations.Get(plc, shape.Item1), action == "rename" && !dryRun ? string.Join("/", EngineeringGroupOperations.Parts(groupPath).Take(EngineeringGroupOperations.Parts(groupPath).Length - 1).Append(newName)) : groupPath)); } catch (Exception ex) { meta["groupError"] = ex.GetBaseException().Message; } }
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });
        // 2.7.35: typed user-group row for every STEP 7 user group class (Name is writable, Delete exists on each).
        private static JsonObject UserGroupRow(object group) => group switch
        {
            global::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup b => new JsonObject { ["name"] = b.Name, ["groupClass"] = b.GetType().Name, ["blocks"] = b.Blocks.Count, ["groups"] = b.Groups.Count },
            global::Siemens.Engineering.SW.Types.PlcTypeUserGroup ty => new JsonObject { ["name"] = ty.Name, ["groupClass"] = ty.GetType().Name, ["types"] = ty.Types.Count, ["groups"] = ty.Groups.Count },
            global::Siemens.Engineering.SW.Tags.PlcTagTableUserGroup tg => new JsonObject { ["name"] = tg.Name, ["groupClass"] = tg.GetType().Name, ["tagTables"] = tg.TagTables.Count, ["groups"] = tg.Groups.Count },
            global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup w => new JsonObject { ["name"] = w.Name, ["groupClass"] = w.GetType().Name, ["watchTables"] = w.WatchTables.Count, ["forceTables"] = w.ForceTables.Count, ["groups"] = w.Groups.Count },
            global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup e => new JsonObject { ["name"] = e.Name, ["groupClass"] = e.GetType().Name, ["externalSources"] = e.ExternalSources.Count, ["groups"] = e.Groups.Count },
            _ => new JsonObject { ["name"] = EngineeringGroupOperations.Get(group, "Name").ToString(), ["groupClass"] = group.GetType().Name }
        };

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

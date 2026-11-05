using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "",
                bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcUserGroup", meta => {

                // Emptiness covers every documented object composition of the group (API audit 2026-10-19 F3, F19):
                // PlcTypeGroup.Documents (V20/V21) and PlcWatchAndForceTableGroup.ForceTables.
                var shape = family switch {
                    "blocks" => ("BlockGroup", "Blocks"),
#if PLC_DOCUMENT_EXPORT
                    "types" => ("TypeGroup", "Types,Documents"),
#else
                    "types" => ("TypeGroup", "Types"),
#endif
                    "tags" => ("TagTableGroup", "TagTables"), "technology" => ("TechnologicalObjectGroup", "TechnologicalObjects"),
                    "watchTables" => ("WatchAndForceTableGroup", "WatchTables,ForceTables"), "externalSources" => ("ExternalSourceGroup", "ExternalSources"),
                    _ => throw new ArgumentException("family must be blocks, types, tags, technology, watchTables or externalSources.") };
                PlcGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcOrganisationPrimitives.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                meta["softwarePath"] = softwarePath; meta["resolvedSoftwareName"] = PlcOrganisationPrimitives.Name(plc);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun && PlcOrganisationPrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString() != "Offline")
                    throw new PlcSoftwareException("InvalidState", "Confirmed Offline state is required for group editing.");
                if (!dryRun) meta["mayHaveChanged"] = true;
                meta["result"] = PlcGroupOperations.Manage(PlcGroupOperations.Get(plc, shape.Item1), groupPath, action, newName, dryRun, shape.Item2);
                if (action != "deleteEmpty" || dryRun) {
                    try {
                        meta["group"] =
                            UserGroupRow(PlcGroupOperations
                                .Group(PlcGroupOperations.Get(plc, shape.Item1), action == "rename" && !dryRun ? string
                                    .Join("/", PlcGroupOperations.Parts(groupPath).Take(PlcGroupOperations.Parts(groupPath).Length - 1).Append(newName))
                                    : groupPath));
                    } catch (Exception ex) { meta["groupError"] = ex.GetBaseException().Message; }
                }
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });

    }
}

using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using static TiaMcp.Adapters.Native.Hmi.UnifiedHmiAccess;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class UnifiedHmiGroupsAdapter
    {
        private readonly HmiSession _session;

        public UnifiedHmiGroupsAdapter(HmiSession session) => _session = session;

        public HmiMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedHmiGroup", meta => {
                var shape = family switch { "screens" => ("ScreenGroups", "Screens"), "tags" => ("TagTableGroups", "TagTables"),
                    _ => throw new ArgumentException("family must be screens or tags. Unified exposes no ScriptGroups collection in the supported PublicAPI.") };
                PlcGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var hmi = _session.ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Only WinCC Unified is supported.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = !dryRun;
                meta["result"] = PlcGroupOperations.Manage(hmi, groupPath, action, newName, dryRun, shape.Item2, shape.Item1);
                return dryRun ? "Unified group preview; nothing changed." : "Unified group operation completed. Project not saved.";
            });
    }
}

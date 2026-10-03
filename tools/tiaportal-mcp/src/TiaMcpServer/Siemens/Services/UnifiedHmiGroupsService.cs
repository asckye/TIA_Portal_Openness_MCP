using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW;
using Siemens.Engineering.Online;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedHmiGroupsService
    {
        private readonly IEngineeringSession _session;

        public UnifiedHmiGroupsService(IEngineeringSession session) => _session = session;

        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedHmiGroup", meta => {
                var shape = family switch { "screens" => ("ScreenGroups", "Screens"), "tags" => ("TagTableGroups", "TagTables"),
                    _ => throw new ArgumentException("family must be screens or tags. Unified exposes no ScriptGroups collection in the supported PublicAPI.") };
                EngineeringGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var hmi = _session.ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Only WinCC Unified is supported.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = !dryRun;
                meta["result"] = EngineeringGroupOperations.Manage(hmi, groupPath, action, newName, dryRun, shape.Item2, shape.Item1);
                return dryRun ? "Unified group preview; nothing changed." : "Unified group operation completed. Project not saved.";
            });
    }
}

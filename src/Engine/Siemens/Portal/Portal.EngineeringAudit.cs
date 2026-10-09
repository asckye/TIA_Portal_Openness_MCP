using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static bool RecoverableAuditError(Exception ex)
        {
            for (var cause = ex; cause != null; cause = cause.InnerException)
                if (cause is NonRecoverableException) return false;
            return true;
        }
        private static void ValidateUnitKind(string kind)
        { if (kind != "unit" && kind != "safety") throw new ArgumentException("unitKind must be unit or safety."); }

        private static IEnumerable<(string Name, string Kind, PlcUnitBase? Unit)> PlcScopes(PlcSoftware plc)
        {
            yield return ("", "root", null);
            var provider = InvocationJournal.Native("PlcUnitProvider.GetService", () => plc.GetService<PlcUnitProvider>());
            if (provider == null) yield break; // a PLC without unit support still has its root scope
            foreach (PlcUnit unit in EngineeringGroupOperations.Items(provider.UnitGroup.Units)) yield return (unit.Name, "unit", unit);
            foreach (PlcSafetyUnit unit in EngineeringGroupOperations.Items(provider.UnitGroup.SafetyUnits)) yield return (unit.Name, "safety", unit);
        }
        private static IEnumerable<(string Path, object Value)> ScopedObjects(object group, string collection, string prefix = "", int depth = 0)
        {
            if (depth > 64) throw new InvalidOperationException("Group depth exceeds 64; result is incomplete.");
            foreach (var value in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(group, collection)))
                yield return (prefix + EngineeringGroupOperations.Get(value, "Name"), value);
            foreach (var sub in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(group, "Groups")))
                foreach (var item in ScopedObjects(sub, collection, prefix + EngineeringGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
            var systemGroups = group.GetType().GetProperty("SystemBlockGroups");
            if (systemGroups != null && collection == "Blocks")
                foreach (var sub in EngineeringGroupOperations.Items(systemGroups.GetValue(group)!))
                    foreach (var item in ScopedObjects(sub, collection, prefix + EngineeringGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
        }
        private List<(string Path, bool? Consistent)> ReadPlcConsistency(string softwarePath)
        {
            var plc = GetPlcSoftware(softwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "PLC not found: " + softwarePath + AvailablePlcPathsSuffix());
            return InvocationJournal.Native("PLC.consistency.rootAndUnits", () => {
                var rows = new List<(string Path, bool? Consistent)>();
                foreach (var scope in PlcScopes(plc))
                {
                    string prefix = scope.Kind + ":" + scope.Name + "/";
                    foreach (var item in ScopedObjects(BlockRootOf(plc, scope.Unit), "Blocks"))
                        rows.Add((prefix + item.Path, ((PlcBlock)item.Value).IsConsistent));
                    foreach (var item in ScopedObjects(TypeRootOf(plc, scope.Unit), "Types"))
                        rows.Add((prefix + "Types/" + item.Path, ((PlcType)item.Value).IsConsistent));
                }
                return rows;
            });
        }


    }
}

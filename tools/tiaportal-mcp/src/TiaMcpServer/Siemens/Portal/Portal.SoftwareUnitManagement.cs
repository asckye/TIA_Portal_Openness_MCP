using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage SetPlcUnitObjectAccess(string softwarePath, string unitName, string objectKind, string objectPath, string access, bool dryRun = true)
            => RunHmiStepTool("SetPlcUnitObjectAccess", meta => {
                var parts = EngineeringGroupOperations.Parts(objectPath);
                if (objectKind != "block" && objectKind != "type") throw new ArgumentException("objectKind must be block or type.");
                var desired = (UnitAccessType)EngineeringScalarProperties.ConvertValue(JsonValue.Create(access), typeof(UnitAccessType))!;
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
                var plc = ExactPlcForEngineering(softwarePath, !dryRun);
                PlcUnitProvider provider = RequireUnitProvider(plc);
                PlcUnitSystemGroup unitGroup = provider.UnitGroup;
                // Safety unit blocks are publishable too (official "Publishing blocks under the SafetyUnit"); fall back to it by exact name.
                PlcUnitBase unit = (PlcUnitBase?)unitGroup.Units.Find(unitName) ?? unitGroup.SafetyUnits.Find(unitName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact software / safety unit not found: " + unitName);
                var root = objectKind == "block" ? (object)unit.BlockGroup : unit.TypeGroup;
                var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
                var target = EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, objectKind == "block" ? "Blocks" : "Types"), parts.Last()) as global::Siemens.Engineering.IEngineeringObject
                    ?? throw new InvalidOperationException("Exact object under unit not found.");
                var before = target.GetAttribute("Access");
                meta["before"] = before.ToString(); meta["requestedAccess"] = desired.ToString(); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; target.SetAttribute("Access", desired); var actual = target.GetAttribute("Access"); meta["after"] = actual.ToString(); if (!desired.Equals(actual)) throw new InvalidOperationException("Unit Access readback differs."); }
                return dryRun ? "Unit object publication preview." : "Unit object Access updated and read back; no save/compile/download.";
            });
    }
}

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
                var provider = plc.GetService<PlcUnitProvider>() ?? throw new NotSupportedException("Software units unavailable.");
                var unit = EngineeringGroupOperations.Find(provider.UnitGroup.Units, unitName) as PlcUnit ?? throw new InvalidOperationException("Exact software unit not found.");
                var root = objectKind == "block" ? (object)unit.BlockGroup : unit.TypeGroup;
                var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
                var target = EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, objectKind == "block" ? "Blocks" : "Types"), parts.Last()) as global::Siemens.Engineering.IEngineeringObject
                    ?? throw new InvalidOperationException("Exact object under unit not found.");
                var before = target.GetAttribute("Access");
                meta["before"] = before.ToString(); meta["requestedAccess"] = desired.ToString(); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; target.SetAttribute("Access", desired); var actual = target.GetAttribute("Access"); meta["after"] = actual.ToString(); if (!desired.Equals(actual)) throw new InvalidOperationException("Unit Access readback differs."); }
                return dryRun ? "Unit object publication preview." : "Unit object Access updated and read back; no save/compile/download.";
            });

        public ResponseMessage ManagePlcSoftwareUnit(string softwarePath, string action, string name = "", string relatedUnit = "",
            string relationType = "", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManagePlcSoftwareUnit", meta => {
                if (!new[] { "list", "read", "create", "delete", "update", "createRelation", "deleteRelation" }.Contains(action)) throw new ArgumentException("Unsupported software-unit action.");
                bool writing = action != "list" && action != "read" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var provider = plc.GetService<PlcUnitProvider>() ?? throw new NotSupportedException("PlcUnitProvider unavailable for this PLC.");
                var units = provider.UnitGroup.Units;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    meta["units"] = new JsonArray(units.Select(x => (JsonNode)EngineeringScalarProperties.Read(x)).ToArray());
                    return "Software units listed.";
                }
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact unit name required.");
                var unit = EngineeringGroupOperations.Find(units, name) as PlcUnit;
                if (action == "create")
                {
                    if (unit != null) throw new InvalidOperationException("Unit already exists.");
                    if (writing) { meta["mayHaveChanged"] = true; unit = units.Create(name); meta["after"] = EngineeringScalarProperties.Read(unit); }
                }
                else
                {
                    if (unit == null) throw new InvalidOperationException("Unit not found: " + name);
                    meta["before"] = EngineeringScalarProperties.Read(unit);
                    if (action == "read")
                    {
                        meta["relations"] = new JsonArray(unit.Relations.Select(r => (JsonNode)new JsonObject { ["relatedObject"] = r.RelatedObject, ["relationType"] = r.RelationType.ToString() }).ToArray());
                    }
                    else if (action == "update")
                    {
                        var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                        if (changes.ContainsKey("Name")) throw new ArgumentException("Unit rename requires a separate reference-impact review; Name updates refused.");
                        var prepared = EngineeringScalarProperties.Prepare(unit.GetType(), changes);
                        if (writing) { EngineeringScalarProperties.Apply(unit, prepared, meta); meta["after"] = EngineeringScalarProperties.Read(unit); }
                    }
                    else if (action == "delete")
                    {
                        meta["dependencyImpact"] = "Deletes the unit and contained engineering objects; references are not analyzed.";
                        if (writing) { meta["mayHaveChanged"] = true; unit.Delete(); if (units.Find(name) != null) throw new InvalidOperationException("Unit remains after Delete."); meta["verifiedAbsent"] = true; }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(relatedUnit)) throw new ArgumentException("relatedUnit is required.");
                        var existing = unit.Relations.Find(relatedUnit);
                        if (action == "createRelation")
                        {
                            if (existing != null) throw new InvalidOperationException("Relation already exists.");
                            var kind = (UnitRelationType)EngineeringScalarProperties.ConvertValue(JsonValue.Create(relationType), typeof(UnitRelationType))!;
                            if (writing) { meta["mayHaveChanged"] = true; unit.Relations.Create(relatedUnit, kind); if (unit.Relations.Find(relatedUnit) == null) throw new InvalidOperationException("Relation absent after Create."); }
                        }
                        else
                        {
                            if (existing == null) throw new InvalidOperationException("Relation not found.");
                            if (writing) { meta["mayHaveChanged"] = true; existing.Delete(); if (unit.Relations.Find(relatedUnit) != null) throw new InvalidOperationException("Relation remains after Delete."); }
                        }
                    }
                }
                return writing ? "Native software unit operation completed; project not saved." : "Software unit read/preview completed; no changes.";
            });
    }
}

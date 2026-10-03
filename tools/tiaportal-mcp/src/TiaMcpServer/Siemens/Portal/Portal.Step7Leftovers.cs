using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Phase 4 sub-batch 2 (2.7.35): Step7 leftovers. Official pages: "Generating blocks from source" / "Generating
    // block/UDT from external source file in specific user group" / "Generate source from block" / "Adding external sources
    // in units", "Querying the system group for system blocks" / "Enumerating system subgroups", "Tag table" (user / system
    // constants), "Export/Import of Plc Alarm TextLists" (PlcAlarmTextListProvider, PLC or unit), watch / force table
    // entries (PlcTableCommentEntry base) and "Exporting ProDiag alarm message" (CodeBlock.ExportProDIAGInfo). Everything is
    // the official V20/V21 API.
    public partial class Portal
    {
        // ---- external sources ----------------------------------------------------------------------------------------------------------
        private static PlcExternalSourceSystemGroup ExternalSourceRootOf(PlcSoftware plc, PlcUnitBase? unit) => unit == null ? plc.ExternalSourceGroup : unit.ExternalSourceGroup;
        private static JsonObject ExternalSourceGroupRow(PlcExternalSourceGroup group)
        {
            PlcExternalSourceComposition sources = group.ExternalSources; PlcExternalSourceUserGroupComposition groups = group.Groups;
            return new JsonObject
            {
                ["name"] = group.Name, ["groupClass"] = group.GetType().Name,
                ["externalSources"] = new JsonArray(EngineeringGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => (JsonNode)s.Name).ToArray()),
                ["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<PlcExternalSourceUserGroup>().Select(g => (JsonNode)g.Name).ToArray())
            };
        }
        private static JsonArray GeneratedRows(IList<IEngineeringObject>? generated)
            => generated == null ? new JsonArray() : new JsonArray(generated.Select(o => (JsonNode)new JsonObject { ["name"] = o.GetAttribute("Name")?.ToString(), ["objectClass"] = o.GetType().Name }).ToArray());

        public ResponseMessage ManagePlcExternalSources(string softwarePath, string action, string name = "", string unitName = "", string unitKind = "unit", string groupPath = "",
            string filePath = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", string generateOption = "None", string targetKind = "", string targetGroupPath = "",
            string newName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManagePlcExternalSources", meta => {
                bool writing = Step7LeftoversLogic.ValidateExternalSourceRequest(action, name, unitName, unitKind, filePath, libraryName, masterCopyPath, copyMode, generateOption, targetKind, targetGroupPath, newName, confirmDelete, dryRun);
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var unit = OptionalUnit(plc, unitName, unitKind);
                PlcExternalSourceSystemGroup root = ExternalSourceRootOf(plc, unit);
                PlcExternalSourceGroup group = (PlcExternalSourceGroup)EngineeringGroupOperations.Group(root, groupPath);
                PlcExternalSourceComposition sources = group.ExternalSources; PlcExternalSourceUserGroupComposition groups = group.Groups;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["container"] = new JsonObject { ["unit"] = unit?.Name, ["group"] = group.Name, ["groupPath"] = groupPath };
                if (action == "list") { meta["group"] = ExternalSourceGroupRow(group); meta["apiCallSuccess"] = true; return "External source group listed; no changes."; }
                if (action == "createGroup")
                {
                    if (groups.Find(newName) != null) throw new InvalidOperationException("User group already exists: " + newName);
                    if (!writing) return "External source group creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcExternalSourceUserGroup created = groups.Create(newName); meta["apiCallSuccess"] = true;
                    if (groups.Find(newName) == null) throw new InvalidOperationException("Group not found by readback.");
                    meta["after"] = ExternalSourceGroupRow(created);
                    return "External source user group created and read back; project not saved.";
                }
                if (action == "renameGroup" || action == "deleteGroup")
                {
                    PlcExternalSourceUserGroup target = groups.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact external source user group not found: " + name);
                    meta["before"] = ExternalSourceGroupRow(target);
                    if (action == "renameGroup")
                    {
                        if (groups.Find(newName) != null) throw new InvalidOperationException("A user group named " + newName + " already exists.");
#if TIA_V20
                        throw new NotSupportedException("PlcExternalSourceUserGroup.Name is read-only in the V20 PublicAPI (rename is V21+).");
#else
                        if (!writing) return "Group rename preview; no changes.";
                        meta["mayHaveChanged"] = true; target.Name = newName; meta["apiCallSuccess"] = true;
                        if (target.Name != newName || groups.Find(newName) == null) throw new InvalidOperationException("Group name readback differs.");
#endif
                        meta["after"] = ExternalSourceGroupRow(target);
                        return "External source user group renamed and read back; project not saved.";
                    }
                    if (EngineeringGroupOperations.Items(target.ExternalSources).Any() || EngineeringGroupOperations.Items(target.Groups).Any()) throw new InvalidOperationException("Group is not empty; delete its sources / subgroups first (no recursive deletion).");
                    if (!writing) return "Group deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; target.Delete(); meta["apiCallSuccess"] = true;
                    if (EngineeringGroupOperations.Items(((PlcExternalSourceGroup)EngineeringGroupOperations.Group(ExternalSourceRootOf(plc, unit), groupPath)).Groups).Cast<PlcExternalSourceUserGroup>().Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Group remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "External source user group deleted and absence verified; project not saved.";
                }
                PlcExternalSource? source = sources.Find(name);
                if (action == "createFromFile" || action == "createFromMasterCopy")
                {
                    if (source != null) throw new InvalidOperationException("External source already exists: " + name);
                    MasterCopy? copy = null; MasterCopyMode? mode = null;
                    if (action == "createFromFile")
                    {
                        var file = new FileInfo(filePath);
                        if (!file.Exists || file.Length == 0) throw new FileNotFoundException("Source file missing or empty on the TIA Portal machine: " + filePath, filePath);
                        meta["sourceFile"] = SoftwareUnitDeepLogic.FileRow(file);
                    }
                    else
                    {
                        copy = ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = EngineeringScalarProperties.Read(copy);
                        mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                        meta["copyMode"] = mode?.ToString();
                    }
                    if (!writing) return "External source creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcExternalSource created = copy == null ? sources.CreateFromFile(name, filePath) : mode == null ? sources.CreateFrom(copy) : sources.CreateFrom(copy, mode.Value);
                    meta["apiCallSuccess"] = true;
                    if (sources.Find(created.Name) == null) throw new InvalidOperationException("External source not found by readback.");
                    meta["after"] = new JsonObject { ["name"] = created.Name };
                    return "External source created and read back; project not saved.";
                }
                if (source == null) throw new PortalException(PortalErrorCode.NotFound, "Exact external source not found: " + name + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => s.Name)) + ").");
                meta["source"] = new JsonObject { ["name"] = source.Name, ["scope"] = "PlcExternalSource exposes only Name in the PublicAPI; the file content stays on the TIA Portal machine." };
                if (action == "read") return "External source read; no changes.";
                if (action == "delete")
                {
                    if (!writing) return "External source deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; source.Delete(); meta["apiCallSuccess"] = true;
                    if (sources.Find(name) != null) throw new InvalidOperationException("External source remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "External source deleted and absence verified; project not saved.";
                }
                // generateBlocks: existing blocks are overwritten natively; on error the project is reset to the state before the call.
                var option = (GenerateBlockOption)EngineeringScalarProperties.ConvertValue(JsonValue.Create(generateOption), typeof(GenerateBlockOption))!;
                PlcBlockUserGroup? blockTarget = null; PlcTypeUserGroup? typeTarget = null;
                if (!string.IsNullOrEmpty(targetKind))
                {
                    if (targetKind == "block") blockTarget = EngineeringGroupOperations.Group(BlockRootOf(plc, unit), targetGroupPath) as PlcBlockUserGroup ?? throw new PortalException(PortalErrorCode.NotFound, "targetGroupPath must name a block user group (not the root).");
                    else typeTarget = EngineeringGroupOperations.Group(TypeRootOf(plc, unit), targetGroupPath) as PlcTypeUserGroup ?? throw new PortalException(PortalErrorCode.NotFound, "targetGroupPath must name a type user group (not the root).");
                    meta["target"] = new JsonObject { ["kind"] = targetKind, ["group"] = blockTarget?.Name ?? typeTarget?.Name };
                }
                meta["generateOption"] = option.ToString(); meta["warning"] = "Existing blocks / types with the same names are overwritten by the native generation.";
                if (!writing) return "Block generation preview; no changes.";
                meta["mayHaveChanged"] = true;
                IList<IEngineeringObject> generated = blockTarget != null ? source.GenerateBlocksFromSource(blockTarget, option) : typeTarget != null ? source.GenerateBlocksFromSource(typeTarget, option) : source.GenerateBlocksFromSource(option);
                meta["apiCallSuccess"] = true; meta["generated"] = GeneratedRows(generated); meta["generatedCount"] = generated?.Count ?? 0;
                return "Blocks / types generated from the external source; project not saved / compiled.";
            });

        // ---- system block / type groups ---------------------------------------------------------------------------------------------------
        private static JsonObject SystemBlockGroupRow(PlcSystemBlockGroup group, bool includeBlocks, int depth, int maxDepth)
        {
            PlcBlockComposition blocks = group.Blocks; PlcSystemBlockGroupComposition groups = group.Groups;
            var row = new JsonObject { ["name"] = group.Name, ["blockCount"] = blocks.Count, ["groupCount"] = groups.Count };
            if (includeBlocks) row["blocks"] = new JsonArray(EngineeringGroupOperations.Items(blocks).Cast<PlcBlock>().Take(200).Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["number"] = b.Number, ["blockClass"] = b.GetType().Name, ["programmingLanguage"] = b.ProgrammingLanguage.ToString() }).ToArray());
            if (depth < maxDepth) row["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<PlcSystemBlockGroup>().Select(g => (JsonNode)SystemBlockGroupRow(g, includeBlocks, depth + 1, maxDepth)).ToArray());
            else row["groupsTruncated"] = groups.Count > 0;
            return row;
        }
        public ResponseMessage ReadPlcSystemGroups(string softwarePath, string unitName = "", string unitKind = "unit", bool includeBlocks = true, int maxDepth = 4)
            => RunHmiStepTool("ReadPlcSystemGroups", meta => {
                Step7LeftoversLogic.ValidateSystemGroupRequest(unitName, unitKind, maxDepth);
                var plc = ExactPlcForEngineering(softwarePath, false);
                var unit = OptionalUnit(plc, unitName, unitKind);
                PlcBlockSystemGroup blockRoot = unit == null ? plc.BlockGroup : unit.BlockGroup; PlcTypeSystemGroup typeRoot = unit == null ? plc.TypeGroup : unit.TypeGroup;
                PlcSystemBlockGroupComposition systemBlockGroups = blockRoot.SystemBlockGroups; PlcSystemTypeGroupComposition systemTypeGroups = typeRoot.SystemTypeGroups;
                meta["unit"] = unit?.Name;
                meta["systemBlockGroups"] = new JsonArray(EngineeringGroupOperations.Items(systemBlockGroups).Cast<PlcSystemBlockGroup>().Select(g => (JsonNode)SystemBlockGroupRow(g, includeBlocks, 1, maxDepth)).ToArray());
                meta["systemTypeGroups"] = new JsonArray(EngineeringGroupOperations.Items(systemTypeGroups).Cast<PlcSystemTypeGroup>().Select(g => { PlcTypeComposition types = g.Types; return (JsonNode)new JsonObject { ["name"] = g.Name, ["typeCount"] = types.Count, ["types"] = new JsonArray(EngineeringGroupOperations.Items(types).Cast<PlcType>().Take(200).Select(t => (JsonNode)t.Name).ToArray()) }; }).ToArray());
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcBlockSystemGroup.SystemBlockGroups (PlcSystemBlockGroup Name / Blocks / Groups, recursive to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types); first 200 objects per group. No modification.";
                return "System block / type groups read; no modification.";
            });


    }
}

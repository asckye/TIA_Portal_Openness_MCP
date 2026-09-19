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

        // ---- tag table constants ----------------------------------------------------------------------------------------------------------
        private static JsonObject ConstantRow(PlcConstant constant, string kind) => new JsonObject { ["name"] = constant.Name, ["dataTypeName"] = constant.DataTypeName, ["value"] = constant.Value, ["kind"] = kind, ["constantClass"] = constant.GetType().Name };
        public ResponseMessage ReadPlcTagTableConstants(string softwarePath, string tablePath, string kind = "all", string unitName = "", string unitKind = "unit", int offset = 0, int limit = 200)
            => RunHmiStepTool("ReadPlcTagTableConstants", meta => {
                Step7LeftoversLogic.ValidateConstantRequest(tablePath, kind, unitName, unitKind, offset, limit);
                var plc = ExactPlcForEngineering(softwarePath, false);
                var unit = OptionalUnit(plc, unitName, unitKind);
                PlcTagTableSystemGroup root = unit == null ? plc.TagTableGroup : unit.TagTableGroup;
                var table = (PlcTagTable)ExactObjectUnder(root, tablePath, "TagTables", "PLC tag table");
                PlcUserConstantComposition userConstants = table.UserConstants; PlcSystemConstantComposition systemConstants = table.SystemConstants;
                var rows = new List<JsonNode>();
                if (kind != "system") rows.AddRange(EngineeringGroupOperations.Items(userConstants).Cast<PlcUserConstant>().Select(c => (JsonNode)ConstantRow(c, "user")));
                if (kind != "user") rows.AddRange(EngineeringGroupOperations.Items(systemConstants).Cast<PlcSystemConstant>().Select(c => (JsonNode)ConstantRow(c, "system")));
                meta["table"] = new JsonObject { ["name"] = table.Name, ["unit"] = unit?.Name, ["isDefault"] = table.IsDefault, ["userConstantCount"] = userConstants.Count, ["systemConstantCount"] = systemConstants.Count };
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcConstant Name / DataTypeName / Value of the table's UserConstants and SystemConstants; user constants are edited with ManagePlcTag kind=constant. No modification.";
                return "Tag table constants read; no modification.";
            });

        // ---- alarm text lists XLSX ----------------------------------------------------------------------------------------------------------
        public ResponseMessage ExchangePlcAlarmTextListsXlsx(string softwarePath, string action, string filePath, string unitName = "", string unitKind = "unit",
            string textListNamesJson = "[]", string culturesJson = "[]", string importOption = "None", bool confirmImport = false, bool dryRun = true)
            => RunHmiStepTool("ExchangePlcAlarmTextListsXlsx", meta => {
                var request = Step7LeftoversLogic.ValidateXlsxRequest(action, filePath, unitName, unitKind, textListNamesJson, culturesJson, importOption, confirmImport, dryRun);
                bool writing = request.Writing;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var unit = OptionalUnit(plc, unitName, unitKind);
                PlcAlarmTextListProvider provider = (unit == null ? plc.GetService<PlcAlarmTextListProvider>() : unit.GetService<PlcAlarmTextListProvider>())
                    ?? throw new NotSupportedException("PlcAlarmTextListProvider unavailable on this " + (unit == null ? "PLC" : "unit") + ".");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["owner"] = unit?.Name ?? plc.Name;
                if (action == "export")
                {
                    var file = NativeFileOutput.Plan(filePath);
                    Language[] languages = Array.Empty<Language>();
                    if (request.Cultures.Length > 0)
                    {
                        LanguageComposition available = _project!.LanguageSettings.Languages;
                        languages = request.Cultures.Select(c => available.Find(CultureInfo.GetCultureInfo(c)) ?? throw new PortalException(PortalErrorCode.NotFound, "Project language not found: " + c + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(available).Cast<Language>().Select(l => l.Culture?.Name)) + ").")).ToArray();
                        meta["languages"] = new JsonArray(languages.Select(l => (JsonNode)l.Culture?.Name).ToArray()); meta["textLists"] = new JsonArray(request.TextLists.Select(t => (JsonNode)t).ToArray());
                    }
                    if (dryRun) return "Text list XLSX export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true;
                    TextListXlsxResult result = languages.Length == 0 ? provider.ExportToXlsx(file) : provider.ExportToXlsx(file, request.TextLists, languages);
                    meta["apiCallSuccess"] = true; meta["nativeState"] = result?.State.ToString(); meta["logFile"] = result?.LogFilePath?.FullName;
                    if (result?.State == TextListXlsxResultState.Error) throw new PortalException(PortalErrorCode.ExportFailed, "ExportToXlsx reported Error (see logFile " + result.LogFilePath?.FullName + ").");
                    meta["file"] = NativeFileOutput.Verify(file);
                    return "Alarm text lists exported to XLSX and hashed; no project change.";
                }
                var source = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = SoftwareUnitDeepLogic.FileRow(source);
                var option = (ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportOptions))!;
                meta["importOption"] = option.ToString();
                if (!writing) return "Text list XLSX import preview; no changes.";
                meta["mayHaveChanged"] = true;
                TextListXlsxResult imported = provider.ImportFromXlsx(source, option);
                meta["apiCallSuccess"] = true; meta["nativeState"] = imported?.State.ToString(); meta["logFile"] = imported?.LogFilePath?.FullName;
                if (imported?.State == TextListXlsxResultState.Error) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromXlsx reported Error (see logFile " + imported.LogFilePath?.FullName + ").");
                return "Alarm text lists imported from XLSX (native state attached); project not saved / compiled.";
            });

        // ---- watch / force table entries ------------------------------------------------------------------------------------------------------
        private static JsonObject TableEntryRow(PlcTableCommentEntry entry, int index)
        {
            var row = new JsonObject { ["index"] = index, ["entryClass"] = entry.GetType().Name };
            switch (entry)
            {
                case PlcWatchTableEntry w:
                    row["kind"] = "watch"; row["name"] = w.Name; row["address"] = w.Address; row["displayFormat"] = w.DisplayFormat.ToString(); row["monitorTrigger"] = w.MonitorTrigger.ToString();
                    row["modifyTrigger"] = w.ModifyTrigger.ToString(); row["modifyValue"] = w.ModifyValue; row["modifyIntention"] = w.ModifyIntention; break;
                case PlcForceTableEntry f:
                    row["kind"] = "force"; row["name"] = f.Name; row["address"] = f.Address; row["displayFormat"] = f.DisplayFormat.ToString(); row["monitorTrigger"] = f.MonitorTrigger.ToString();
                    row["forceValue"] = f.ForceValue; row["forceIntention"] = f.ForceIntention; break;
                default: row["kind"] = "comment"; break;
            }
            return row;
        }
        public ResponseMessage ManagePlcTableEntries(string softwarePath, string tableKind, string tablePath, string action = "read", int entryIndex = -1, bool confirmDelete = false, bool dryRun = true, int offset = 0, int limit = 200)
            => RunHmiStepTool("ManagePlcTableEntries", meta => {
                bool writing = Step7LeftoversLogic.ValidateTableEntryRequest(tableKind, tablePath, action, entryIndex, confirmDelete, dryRun, offset, limit);
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                PlcWatchAndForceTableGroup root = plc.WatchAndForceTableGroup;
                PlcTableCommentEntryComposition entries; string tableName; bool consistent;
                if (tableKind == "watch") { var table = (PlcWatchTable)ExactObjectUnder(root, tablePath, "WatchTables", "watch table"); entries = table.Entries; tableName = table.Name; consistent = table.IsConsistent; }
                else { var table = (PlcForceTable)ExactObjectUnder(root, tablePath, "ForceTables", "force table"); entries = table.Entries; tableName = table.Name; consistent = table.IsConsistent; }
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["table"] = new JsonObject { ["name"] = tableName, ["kind"] = tableKind, ["isConsistent"] = consistent, ["entryCount"] = entries.Count };
                var all = EngineeringGroupOperations.Items(entries).Cast<PlcTableCommentEntry>().ToArray();
                if (action == "read")
                {
                    Page(all.Select((e, i) => (JsonNode)TableEntryRow(e, i)).ToArray(), offset, limit, meta);
                    meta["apiCallSuccess"] = true;
                    meta["scope"] = "PlcWatchTableEntry / PlcForceTableEntry scalars and comment rows (PlcTableCommentEntry) in native order; values are configuration, not online data (ReadPlcWatchTableCurrentValuesReadOnly).";
                    return "Table entries read; no modification.";
                }
                if (action == "createComment")
                {
                    if (!writing) return "Comment entry creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcTableCommentEntry created = entries.Create(); meta["apiCallSuccess"] = true;
                    // 2.7.35 real project: the composition proxy used for Create still answers the old Count; count on a fresh navigation.
                    int after = EngineeringGroupOperations.Items(((PlcWatchTable)ExactObjectUnder(root, tablePath, "WatchTables", "watch table")).Entries).Count(); meta["entryCountAfter"] = after;
                    if (after != all.Length + 1) throw new InvalidOperationException("Entry count did not increase by one after Create (fresh readback).");
                    meta["after"] = TableEntryRow(created, after - 1);
                    return "Comment entry appended to the watch table and counted back; project not saved.";
                }
                if (entryIndex >= all.Length) throw new PortalException(PortalErrorCode.NotFound, "entryIndex " + entryIndex + " is outside 0.." + (all.Length - 1) + ".");
                meta["before"] = TableEntryRow(all[entryIndex], entryIndex);
                if (!writing) return "Entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                all[entryIndex].Delete(); meta["apiCallSuccess"] = true;
                int remaining = EngineeringGroupOperations.Items(((PlcWatchTable)ExactObjectUnder(root, tablePath, "WatchTables", "watch table")).Entries).Count(); meta["entryCountAfter"] = remaining;
                if (remaining != all.Length - 1) throw new InvalidOperationException("Entry count did not decrease by one after Delete.");
                return "Watch table entry deleted and counted back; project not saved.";
            });

        // ---- ProDiag CSV export -------------------------------------------------------------------------------------------------------------------
        public ResponseMessage ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName = "", string unitKind = "unit", bool dryRun = true)
            => RunHmiStepTool("ExportPlcProDiagInfo", meta => {
                Step7LeftoversLogic.ValidateProDiagRequest(blockPath, directoryPath, unitName, unitKind);
                var plc = ExactPlcForEngineering(softwarePath, false);
                var unit = OptionalUnit(plc, unitName, unitKind);
                var block = (PlcBlock)ExactObjectUnder(BlockRootOf(plc, unit), blockPath, "Blocks", "block");
                CodeBlock code = block as CodeBlock ?? throw new ArgumentException("blockPath must name a code block (FB); " + block.GetType().Name + " has no ExportProDIAGInfo.");
                meta["block"] = new JsonObject { ["name"] = code.Name, ["blockClass"] = code.GetType().Name, ["programmingLanguage"] = code.ProgrammingLanguage.ToString(), ["isConsistent"] = code.IsConsistent, ["unit"] = unit?.Name };
                var refusal = Step7LeftoversLogic.ProDiagRefusal(code.ProgrammingLanguage.ToString(), code.IsConsistent);
                if (refusal != null) throw new PortalException(PortalErrorCode.InvalidState, refusal);
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists) throw new DirectoryNotFoundException("directoryPath must already exist: " + directoryPath);
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "ProDiag CSV export preview; no files written.";
                var before = SoftwareUnitDeepLogic.SnapshotDirectory(directory.FullName);
                meta["mayHaveWrittenFiles"] = true;
                code.ExportProDIAGInfo(directory); meta["apiCallSuccess"] = true;
                meta["newFiles"] = SoftwareUnitDeepLogic.NewFilesSince(directory.FullName, before);
                return "ProDiag alarm messages exported as CSV (new files hashed); no project change.";
            });
    }
}

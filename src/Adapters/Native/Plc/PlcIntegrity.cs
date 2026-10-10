using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if PLC_SAFETY_UNITS
using Siemens.Engineering.SW.Alarm.TextLists;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#if !PLC_SAFETY_UNITS
using PlcUnitBase = Siemens.Engineering.SW.Units.PlcUnit;
#endif
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
#if PLC_UNITS
        // TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md): after PlcUnitRelationComposition.Create
        // the same composition proxy answers Find(relatedObject) with null,
        // and after PlcUnitRelation.Delete it throws EngineeringObjectDisposedException - readback must re-navigate from the unit group.
        private bool RelationExists(PlcUnitSystemGroup group, string unitKind, string unitName, string relatedUnit)
        {
            PlcUnitRelationComposition fresh = PlcUnitScope.Exact(group, unitKind, unitName).Relations;
            return PlcGroupOperations.Items(fresh).Cast<PlcUnitRelation>().Any(r => string.Equals(r.RelatedObject, relatedUnit, StringComparison.OrdinalIgnoreCase));
        }
        // ---- rows ------------------------------------------------------------------------------------------------------------------
        private static Dictionary<string, object?> RelationRow(PlcUnitRelation relation) => new Dictionary<string, object?> { ["relatedObject"] = relation.RelatedObject, ["relationType"] = relation.RelationType.ToString() };
        private Dictionary<string, object?> UnitRow(PlcUnitBase unit, string kind, bool includeContents)
        {
            var row = new Dictionary<string, object?> { ["name"] = unit.Name, ["kind"] = kind, ["unitClass"] = unit.GetType().Name, ["author"] = unit.Author, ["namespacePreset"] = NamespacePreset(unit) };
#if !PLC_SAFETY_UNITS
            row["namespacePresetCapability"] = "requiresV18";
#endif
            Safe(row, "comment", () => ReadMultilingualEvidence(unit.Comment));
            Safe(row, "relations", () => { PlcUnitRelationComposition relations = unit.Relations; return PlcGroupOperations.Items(relations).Cast<PlcUnitRelation>().Select(r => (object?)RelationRow(r)).ToArray(); });
            if (!includeContents) return row;
            Safe(row, "blockGroup", () => { PlcBlockSystemGroup blocks = unit.BlockGroup; return new Dictionary<string, object?> { ["name"] = blocks.Name, ["blocks"] = Names(blocks.Blocks), ["groups"] = Names(blocks.Groups), ["systemBlockGroups"] = Names(blocks.SystemBlockGroups) }; });
            Safe(row, "typeGroup", () => { PlcTypeSystemGroup types = unit.TypeGroup;
#if PLC_DOCUMENT_EXPORT
                PlcDocumentComposition documents = types.Documents;
                return new Dictionary<string, object?> { ["name"] = types.Name, ["types"] = Names(types.Types), ["groups"] = Names(types.Groups), ["documents"] = Names(documents) };
#else
                return new Dictionary<string, object?> { ["name"] = types.Name, ["types"] = Names(types.Types), ["groups"] = Names(types.Groups), ["documents"] = Array.Empty<object?>(), ["documentsCapability"] = "requiresV20" };
#endif
            });
            Safe(row, "tagTableGroup", () => { PlcTagTableSystemGroup tables = unit.TagTableGroup; return new Dictionary<string, object?> { ["name"] = tables.Name, ["tagTables"] = Names(tables.TagTables), ["groups"] = Names(tables.Groups) }; });
            Safe(row, "externalSourceGroup", () => { PlcExternalSourceSystemGroup sources = unit.ExternalSourceGroup; return new Dictionary<string, object?> { ["name"] = sources.Name, ["externalSources"] = Names(sources.ExternalSources), ["groups"] = Names(sources.Groups) }; });
#if PLC_SAFETY_UNITS
            Safe(row, "alarmTextLists", () => { PlcAlarmTextlistGroup lists = unit.PlcAlarmTextlistGroup; return new Dictionary<string, object?> { ["system"] = Names(lists.PlcAlarmSystemTextlists), ["user"] = Names(lists.PlcAlarmUserTextlists) }; });
#else
            row["alarmTextListsCapability"] = "requiresV18";
#endif
            return row;
        }
        // ---- ReadPlcSoftwareUnits ----------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadPlcSoftwareUnits(string softwarePath, string unitName = "", string unitKind = "all", bool includeContents = true, int offset = 0, int limit = 50)
            => _session.RunHmiStepTool("ListPlcSoftwareUnits", meta => {
                meta["mayHaveChanged"] = false;
                PlcIntegrityPolicy.RequireOneOf(unitKind, PlcIntegrityPolicy.UnitListKinds, "unitKind");
                HardwareAddressingPolicy.ValidatePage(offset, limit);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                PlcUnitProvider provider = PlcUnitScope.RequireProvider(plc);
                PlcUnitSystemGroup group = provider.UnitGroup;
                PlcUnitComposition units = group.Units;
#if PLC_SAFETY_UNITS
                PlcSafetyUnitComposition safetyUnits = group.SafetyUnits;
#endif
                var groupRow = new Dictionary<string, object?> { ["unitCount"] = units.Count,
#if PLC_SAFETY_UNITS
                    ["safetyUnitCount"] = safetyUnits.Count
#else
                    ["safetyUnitCount"] = 0
#endif
                };
#if !PLC_UNIT_GROUP_NAME
                groupRow["name"] = null; groupRow["nameNote"] = "PlcUnitSystemGroup.Name exists in the V21 PublicAPI only.";
#else
                groupRow["name"] = group.Name;
#endif
                meta["unitGroup"] = groupRow;
                var rows = new List<object?>();
                if (unitKind != "safety") rows.AddRange(PlcGroupOperations.Items(units).Cast<PlcUnit>().Where(u => string.IsNullOrEmpty(unitName) || string.Equals(u.Name, unitName, StringComparison.OrdinalIgnoreCase)).Select(u => (object?)UnitRow(u, "unit", includeContents)));
#if PLC_SAFETY_UNITS
                if (unitKind != "unit") rows.AddRange(PlcGroupOperations.Items(safetyUnits).Cast<PlcSafetyUnit>().Where(u => string.IsNullOrEmpty(unitName) || string.Equals(u.Name, unitName, StringComparison.OrdinalIgnoreCase)).Select(u => (object?)UnitRow(u, "safety", includeContents)));
#else

#endif
                if (!string.IsNullOrEmpty(unitName) && rows.Count == 0) throw new PlcSoftwareException("NotFound", "Exact unit not found: " + unitName);
                Page(rows.ToArray(), offset, limit, meta);
#if !PLC_DOCUMENT_EXPORT
                meta["dataComplete"] = false;
                meta["versionLimitations"] = "Named value type documents require V20; safety units and NamespacePreset require V18.";
#endif
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcUnitBase scalars (Name, Author, NamespacePreset), Comment per culture, relations, and with includeContents the names in BlockGroup / TypeGroup (types + named value type documents) / TagTableGroup / ExternalSourceGroup / PlcAlarmTextlistGroup (first 200 each). No modification.";
                return "Software units read; no modification.";
            });

        // ---- ManagePlcSoftwareUnit (safety units, master copies, per-culture comments) --------------------------------------------
        public HardwareAddressingReply ManagePlcSoftwareUnit(string softwarePath, string action, string name, string relatedUnit,
            string relationType, Dictionary<string, HardwareScalar> properties, bool dryRun, string unitKind, PlcTagComment[] comments,
            string libraryName, string masterCopyPath, string copyMode, string validationError)
            => _session.RunHmiStepTool("ManagePlcSoftwareUnit", meta => {
                meta["mayHaveChanged"] = false;
                if (validationError.Length != 0) throw new ArgumentException(validationError);
                bool writing = action != "list" && action != "read" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                PlcUnitProvider provider = PlcUnitScope.RequireProvider(plc);
                PlcUnitSystemGroup group = provider.UnitGroup;
                PlcUnitComposition units = group.Units;
#if !PLC_DOCUMENT_EXPORT
                meta["dataComplete"] = false;
                meta["versionLimitations"] = "Named value type documents require V20; safety units and NamespacePreset require V18.";
#endif
#if PLC_SAFETY_UNITS
                PlcSafetyUnitComposition safetyUnits = group.SafetyUnits;
#endif
                meta["action"] = action; meta["unitKind"] = unitKind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    meta["units"] = PlcGroupOperations.Items(units).Cast<PlcUnit>().Select(u => (object?)UnitRow(u, "unit", false)).ToArray();
#if PLC_SAFETY_UNITS
                    meta["safetyUnits"] = PlcGroupOperations.Items(safetyUnits).Cast<PlcSafetyUnit>().Select(u => (object?)UnitRow(u, "safety", false)).ToArray();
#else
                    meta["safetyUnits"] = Array.Empty<object?>();
#endif
                    return "Software units listed (standard and safety).";
                }
                if (action == "create" || action == "createFromMasterCopy")
                {
                    if (units.Find(name) != null) throw new InvalidOperationException("Unit already exists: " + name);
                    MasterCopy? source = null; MasterCopyMode? mode = null;
                    if (action == "createFromMasterCopy")
                    {
                        source = _session.ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = HardwareScalarEvidence.Read(source);
                        mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = copyMode }, typeof(MasterCopyMode))!;
                        meta["copyMode"] = mode?.ToString();
                        if (!string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)) meta["note"] = "The created unit keeps the master copy name (" + source.Name + "); name is only the expected result used for the existence check.";
                    }
                    if (!writing) return "Unit creation preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    PlcUnit created;
#if PLC_DOCUMENT_EXPORT
                    created = source == null ? units.Create(name) : mode == null ? units.CreateFrom(source) : units.CreateFrom(source, mode.Value);
#else
                    created = source == null ? units.Create(name) : units.CreateFrom(source);
#endif
                    meta["apiCallSuccess"] = true;
                    if (units.Find(created.Name) == null) throw new InvalidOperationException("Created unit not found by readback.");
                    meta["after"] = UnitRow(created, "unit", false);
                    return "Software unit created and verified by readback; project not saved.";
                }
                PlcUnitBase unit = PlcUnitScope.Exact(group, unitKind, name);
                meta["before"] = UnitRow(unit, unitKind, false);
                if (action == "read") { meta["before"] = UnitRow(unit, unitKind, true); return "Software unit read; no changes."; }
                if (action == "update")
                {
                    var prepared = HardwareAddressingPolicy.Prepare(unit.GetType(), properties);
                    foreach (var (culture, _) in comments.Select(c => (c.Culture!, c.Text)))
                    {
                        MultilingualTextItem? item = PlcGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, culture, StringComparison.OrdinalIgnoreCase));
                        if (item == null) throw new PlcSoftwareException("NotFound", "Comment culture not active in the project: " + culture + " (active: " + string.Join(", ", PlcGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().Select(i => i.Language?.Culture?.Name)) + ").");
                    }
                    if (!writing) return "Unit update preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    if (prepared.Count > 0) HardwareScalarEvidence.Apply(unit, prepared, meta);
                    foreach (var (culture, text) in comments.Select(c => (c.Culture!, c.Text)))
                    {
                        MultilingualTextItem item = PlcGroupOperations.Items(unit.Comment.Items).Cast<MultilingualTextItem>().First(i => string.Equals(i.Language?.Culture?.Name, culture, StringComparison.OrdinalIgnoreCase));
                        item.Text = text;
                        if (item.Text != text) throw new InvalidOperationException("Comment readback differs for culture " + culture + ".");
                    }
                    meta["apiCallSuccess"] = true; meta["after"] = UnitRow(unit, unitKind, false);
                    return "Software unit scalars / comments written and read back; project not saved.";
                }
                if (action == "delete")
                {
                    meta["dependencyImpact"] = "Deletes the unit and contained engineering objects; references are not analyzed.";
                    if (!writing) return "Unit deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    ((PlcUnit)unit).Delete(); meta["apiCallSuccess"] = true;
                    if (units.Find(name) != null) throw new InvalidOperationException("Unit remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "Software unit deleted and absence verified; project not saved.";
                }
                PlcUnitRelationComposition relations = unit.Relations;
                PlcUnitRelation? existing = relations.Find(relatedUnit);
                meta["relationBefore"] = existing == null ? null : RelationRow(existing);
                if (action == "createRelation")
                {
                    if (existing != null) throw new InvalidOperationException("Relation already exists to " + relatedUnit + " (" + existing.RelationType + ").");
                    var kind = (UnitRelationType)HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = relationType }, typeof(UnitRelationType))!;
                    if (!writing) return "Relation creation preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    PlcUnitRelation created = relations.Create(relatedUnit, kind); meta["apiCallSuccess"] = true;
                    meta["relationAfter"] = RelationRow(created);
                    if (!RelationExists(group, unitKind, name, relatedUnit)) throw new InvalidOperationException("Relation absent after Create (fresh readback from the unit group).");
                    return "Unit relation created and verified by readback; project not saved.";
                }
                if (existing == null) throw new PlcSoftwareException("NotFound", "Relation not found to " + relatedUnit + ".");
                if (!writing) return "Relation deletion preview; no changes.";
                meta["mayHaveChanged"] = true; MutationStarted = true;
                existing.Delete(); meta["apiCallSuccess"] = true;
                if (RelationExists(group, unitKind, name, relatedUnit)) throw new InvalidOperationException("Relation remains after Delete (fresh readback from the unit group).");
                meta["verifiedAbsent"] = true;
                return "Unit relation deleted and absence verified; project not saved.";
            });

#endif
#if PLC_INTEGRITY_PROVIDERS
        // ---- ReadPlcChecksums ---------------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadPlcChecksums(string softwarePath)
            => _session.RunHmiStepTool("GetPlcChecksums", meta => {
                meta["mayHaveChanged"] = false;
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                PlcChecksumProvider? provider = plc.GetService<PlcChecksumProvider>();
                meta["supported"] = provider != null;
                if (provider == null) { meta["software"] = null; meta["textLists"] = null; return "PlcChecksumProvider unavailable: this PLC does not support checksum calculation (GetService returned null)."; }
                meta["software"] = provider.Software; 
#if PLC_DOCUMENT_EXPORT
                meta["textLists"] = provider.TextLists;
#else
                meta["textLists"] = null;
                meta["textListsCapability"] = "requiresV20";
                meta["dataComplete"] = false;
#endif
 meta["apiCallSuccess"] = true;
                meta["note"] = "Both attributes are read-only and null until the program is compiled.";
                return provider.Software == null ? "Checksums read: Software is null (program not compiled)." : "Software and text list checksums read; no modification.";
            });

        // ---- ReadPlcObjectFingerprints ------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadPlcObjectFingerprints(string softwarePath, string objectKind, string objectPath, string unitName = "", string unitKind = "unit")
            => _session.RunHmiStepTool("GetPlcObjectFingerprints", meta => {
                meta["mayHaveChanged"] = false;
                PlcIntegrityPolicy.ValidateFingerprintRequest(objectKind, objectPath, unitName, unitKind);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var target = objectKind == "block" ? _session.ExactObjectUnder(_session.BlockRootOf(plc, unit), objectPath, "Blocks", "block") : _session.ExactObjectUnder(_session.TypeRootOf(plc, unit), objectPath, "Types", "type");
                meta["objectKind"] = objectKind; meta["objectPath"] = objectPath;
#if PLC_UNITS
                meta["unit"] = ((PlcUnitBase?)unit)?.Name;
#else
                meta["unit"] = null;
#endif
                meta["objectClass"] = target.GetType().Name;
                FingerprintProvider provider = (target as IEngineeringServiceProvider)?.GetService<FingerprintProvider>() ?? throw new NotSupportedException("FingerprintProvider unavailable on this object (available for blocks and UDTs, not tag tables).");
                IList<Fingerprint> fingerprints;
                try { fingerprints = provider.GetFingerprints(); }
                catch (EngineeringTargetInvocationException ex) { throw new PlcSoftwareException("InvalidState", "GetFingerprints refused (the object must be consistent; compile first): " + ex.Message, null, ex); }
                meta["records"] = fingerprints.Select(f => (object?)new Dictionary<string, object?> { ["id"] = f.Id.ToString(), ["value"] = f.Value }).ToArray();
                meta["count"] = fingerprints.Count; meta["apiCallSuccess"] = true;
                meta["scope"] = "Every Fingerprint returned natively (Id / Value); fingerprints consider user input only, never compilation results. Online CPU fingerprints: GetPlcBlockFingerprints.";
                return "Offline object fingerprints read; no modification.";
            });

        // ---- ManagePlcBlockWriteProtection (V21) ----------------------------------------------------------------------------------------
        public HardwareAddressingReply ManagePlcBlockWriteProtection(string softwarePath, string blockPath, string action, string password = "", string newPassword = "",
            bool confirmProtectionChange = false, bool dryRun = true, string unitName = "", string unitKind = "unit")
            => _session.RunHmiStepTool("ManagePlcBlockWriteProtection", meta => {
                meta["mayHaveChanged"] = false;
                bool writing = PlcIntegrityPolicy.ValidateWriteProtectionRequest(action, password, newPassword, confirmProtectionChange, dryRun);
                if (!string.IsNullOrEmpty(unitName)) PlcIntegrityPolicy.RequireOneOf(unitKind, PlcIntegrityPolicy.UnitKinds, "unitKind");
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var block = (PlcBlock)_session.ExactObjectUnder(_session.BlockRootOf(plc, unit), blockPath, "Blocks", "block");
                meta["action"] = action; meta["blockPath"] = blockPath;
#if PLC_UNITS
                meta["unit"] = ((PlcUnitBase?)unit)?.Name;
#else
                meta["unit"] = null;
#endif
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password); meta["newPasswordProvided"] = !string.IsNullOrEmpty(newPassword);
#if !PLC_BLOCK_WRITE_PROTECTION
                throw new NotSupportedException("PlcBlockWriteProtectionProvider exists in the V21 PublicAPI only; V20 offers know-how protection (ManagePlcBlockProtection) but no block write protection.");
#else
                PlcBlockWriteProtectionProvider provider = block.GetService<PlcBlockWriteProtectionProvider>() ?? throw new NotSupportedException("PlcBlockWriteProtectionProvider unavailable on this block.");
                Dictionary<string, object?> State() => new Dictionary<string, object?> { ["name"] = block.Name, ["isDefined"] = provider.IsDefined, ["isProtected"] = provider.IsProtected, ["isKnowHowProtected"] = block.IsKnowHowProtected };
                meta["before"] = State();
                if (action == "read") return "Block write-protection state read.";
                var refusal = PlcIntegrityPolicy.WriteProtectionStateRefusal(action, provider.IsDefined, provider.IsProtected);
                if (refusal != null) throw new InvalidOperationException(refusal);
                var invalid = provider.GetInvalidPasswordCharacters()?.ToArray() ?? Array.Empty<char>();
                if ((action == "define" && PlcProtectionPolicy.ContainsInvalidPasswordCharacter(password, invalid)) || (action == "change" && PlcProtectionPolicy.ContainsInvalidPasswordCharacter(newPassword, invalid)))
                    throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Write-protection preview; no changes.";
                meta["mayHaveChanged"] = true; MutationStarted = true;
                using (var secure = PlcProtectionPolicy.ToSecureString(password))
                using (var next = action == "change" ? PlcProtectionPolicy.ToSecureString(newPassword) : null)
                {
                    switch (action)
                    {
                        case "define": provider.Define(secure); break;
                        case "protect": provider.Protect(secure); break;
                        case "unprotect": provider.Unprotect(secure); break;
                        case "change": provider.Change(secure, next!); break;
                        default: provider.Change(secure, null); break;   // remove: official "Change(pwd, null)" drops password and protection
                    }
                }
                meta["apiCallSuccess"] = true; meta["after"] = State();
                bool expectedDefined = action != "remove", expectedProtected = action == "protect" || (action == "change" && provider.IsProtected);
                if (action == "remove" && (provider.IsDefined || provider.IsProtected)) throw new InvalidOperationException("Write protection remains after Change(pwd, null).");
                if (action != "remove" && action != "change" && (provider.IsDefined != expectedDefined || (action != "define" && provider.IsProtected != expectedProtected))) throw new InvalidOperationException("Write-protection readback differs from the requested state.");
                return "Block write protection changed and verified by IsDefined / IsProtected readback; no save/compile/download.";
#endif
            });

        // ---- ManageProjectCompilationSettings ------------------------------------------------------------------------------------------
#endif
#if PLC_UNITS
        public HardwareAddressingReply SetPlcUnitObjectAccess(string softwarePath, string unitName, string objectKind, string objectPath, string access, bool dryRun = true)
            => _session.RunHmiStepTool("SetPlcUnitObjectAccess", meta => {
                meta["mayHaveChanged"] = false;
                var parts = PlcGroupOperations.Parts(objectPath);
                if (objectKind != "block" && objectKind != "type") throw new ArgumentException("objectKind must be block or type.");
                var desired = (UnitAccessType)HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = access }, typeof(UnitAccessType))!;
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                PlcUnitProvider provider = PlcUnitScope.RequireProvider(plc);
                PlcUnitSystemGroup unitGroup = provider.UnitGroup;
                // Safety unit blocks are publishable too (official "Publishing blocks under the SafetyUnit"); fall back to it by exact name.
                PlcUnitBase unit = (PlcUnitBase?)unitGroup.Units.Find(unitName) ?? PlcUnitScope.FindSafety(unitGroup, unitName) ?? throw new PlcSoftwareException("NotFound", "Exact software / safety unit not found: " + unitName);
                var root = objectKind == "block" ? (object)unit.BlockGroup : unit.TypeGroup;
                var group = PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
                var target = PlcGroupOperations.Find(PlcGroupOperations.Get(group, objectKind == "block" ? "Blocks" : "Types"), parts.Last()) as global::Siemens.Engineering.IEngineeringObject
                    ?? throw new InvalidOperationException("Exact object under unit not found.");
                var before = target.GetAttribute("Access");
                meta["before"] = before.ToString(); meta["requestedAccess"] = desired.ToString(); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; MutationStarted = true; target.SetAttribute("Access", desired); var actual = target.GetAttribute("Access"); meta["after"] = actual.ToString(); if (!desired.Equals(actual)) throw new InvalidOperationException("Unit Access readback differs."); }
                return dryRun ? "Unit object publication preview." : "Unit object Access updated and read back; no save/compile/download.";
            });
#endif
#if PLC_UNITS
        private static object? NamespacePreset(PlcUnitBase unit)
        {
#if PLC_SAFETY_UNITS
            return unit.NamespacePreset;
#else
            return null;
#endif
        }
#endif
        public static object? ReadMultilingualEvidence(MultilingualText? text)
        {
            if (text == null) return null;
            var items = new Dictionary<string, object?>();
            foreach (var item in PlcGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"] = item.Text;
            return items;
        }
        private static void Safe(Dictionary<string, object?> row, string key, Func<object?> read)
        {
            try { row[key] = read(); }
            catch (Exception ex) { row[key] = null; row[key + "Error"] = ex.GetBaseException().Message; }
        }
        private static object?[] Names(object composition) => PlcGroupOperations.Items(composition).Select(x => (object?)PlcGroupOperations.Get(x, "Name").ToString()).ToArray();
        private static object?[] Names(object composition, int max) => PlcGroupOperations.Items(composition).Take(max).Select(x => (object?)PlcGroupOperations.Get(x, "Name").ToString()).ToArray();
    }
}

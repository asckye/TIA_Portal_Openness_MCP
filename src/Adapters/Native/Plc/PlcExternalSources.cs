using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static PlcExternalSourceSystemGroup ExternalSourceRootOf(PlcSoftware plc, object? unit) => unit == null ? Documents.ExternalSourceGroup(plc) : (PlcExternalSourceSystemGroup)PlcGroupOperations.Get(unit!, "ExternalSourceGroup");

        private static Dictionary<string, object?> ExternalSourceGroupRow(PlcExternalSourceGroup group)
        {
            PlcExternalSourceComposition sources = Documents.Sources(group); PlcExternalSourceUserGroupComposition groups = Documents.SourceGroups(group);
            return new Dictionary<string, object?>
            {
                ["name"] = Documents.Name(group), ["groupClass"] = group.GetType().Name,
                ["externalSources"] = new List<object?>(PlcGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => (object?)Documents.Name(s)).ToArray()),
                ["groups"] = new List<object?>(PlcGroupOperations.Items(groups).Cast<PlcExternalSourceUserGroup>().Select(g => (object?)Documents.Name(g)).ToArray())
            };
        }

        private static List<object?> GeneratedRows(IList<IEngineeringObject>? generated)
            => generated == null ? new List<object?>() : new List<object?>(generated.Select(o => (object?)new Dictionary<string, object?> { ["name"] = Documents.Attribute(o, "Name")?.ToString(), ["objectClass"] = o.GetType().Name }).ToArray());

        public HardwareAddressingReply ManagePlcExternalSources(string softwarePath, string action, string name = "", string unitName = "", string unitKind = "unit", string groupPath = "",
            string filePath = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", string generateOption = "None", string targetKind = "", string targetGroupPath = "",
            string newName = "", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcExternalSources", meta => {
                bool writing = PlcSourceRules.ValidateExternalSourceRequest(action, name, unitName, unitKind, filePath, libraryName, masterCopyPath, copyMode, generateOption, targetKind, targetGroupPath, newName, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcExternalSourceSystemGroup root = ExternalSourceRootOf(plc, unit);
                PlcExternalSourceGroup group = (PlcExternalSourceGroup)PlcGroupOperations.Group(root, groupPath);
                PlcExternalSourceComposition sources = Documents.Sources(group); PlcExternalSourceUserGroupComposition groups = Documents.SourceGroups(group);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["container"] = new Dictionary<string, object?> { ["unit"] = unit == null ? null : PlcGroupOperations.Get(unit, "Name"), ["group"] = Documents.Name(group), ["groupPath"] = groupPath };
                if (action == "list") { meta["group"] = ExternalSourceGroupRow(group); meta["apiCallSuccess"] = true; return "External source group listed; no changes."; }
                if (action == "createGroup")
                {
                    if (Documents.Find(groups, newName) != null) throw new InvalidOperationException("User group already exists: " + newName);
                    if (!writing) return "External source group creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcExternalSourceUserGroup created = Documents.Create(groups, newName); meta["apiCallSuccess"] = true;
                    if (Documents.Find(groups, newName) == null) throw new InvalidOperationException("Group not found by readback.");
                    meta["after"] = ExternalSourceGroupRow(created);
                    return "External source user group created and read back; project not saved.";
                }
                if (action == "renameGroup" || action == "deleteGroup")
                {
                    PlcExternalSourceUserGroup target = Documents.Find(groups, name) ?? throw new PlcSoftwareException("NotFound", "Exact external source user group not found: " + name);
                    meta["before"] = ExternalSourceGroupRow(target);
                    if (action == "renameGroup")
                    {
                        if (Documents.Find(groups, newName) != null) throw new InvalidOperationException("A user group named " + newName + " already exists.");
#if !PLC_EXTERNAL_SOURCE_GROUP_RENAME
                        throw new NotSupportedException("PlcExternalSourceUserGroup.Name is read-only in the V20 PublicAPI (rename is V21+).");
#else
                        if (!writing) return "Group rename preview; no changes.";
                        meta["mayHaveChanged"] = true; Documents.SetName(target, newName); meta["apiCallSuccess"] = true;
                        if (Documents.Name(target) != newName || Documents.Find(groups, newName) == null) throw new InvalidOperationException("Group name readback differs.");
#endif
                        meta["after"] = ExternalSourceGroupRow(target);
                        return "External source user group renamed and read back; project not saved.";
                    }
                    if (PlcGroupOperations.Items(Documents.Sources(target)).Any() || PlcGroupOperations.Items(Documents.SourceGroups(target)).Any()) throw new InvalidOperationException("Group is not empty; delete its sources / subgroups first (no recursive deletion).");
                    if (!writing) return "Group deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; Documents.Delete(target); meta["apiCallSuccess"] = true;
                    if (PlcGroupOperations.Items(Documents.SourceGroups((PlcExternalSourceGroup)PlcGroupOperations.Group(ExternalSourceRootOf(plc, unit), groupPath))).Cast<PlcExternalSourceUserGroup>().Any(g => string.Equals(Documents.Name(g), name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Group remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "External source user group deleted and absence verified; project not saved.";
                }
                PlcExternalSource? source = Documents.Find(sources, name);
                if (action == "createFromFile" || action == "createFromMasterCopy")
                {
                    if (source != null) throw new InvalidOperationException("External source already exists: " + name);
                    MasterCopy? copy = null; MasterCopyMode? mode = null;
                    if (action == "createFromFile")
                    {
                        var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
                        if (!file.Exists || file.Length == 0) throw new FileNotFoundException("Source file missing or empty on the TIA Portal machine: " + filePath, filePath);
                        meta["sourceFile"] = PlcSourceFileOutput.Row(file);
                    }
                    else
                    {
                        copy = _session.ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = HardwareScalarEvidence.Read(copy);
                        mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)Enum.Parse(typeof(MasterCopyMode), copyMode, false);
                        meta["copyMode"] = mode?.ToString();
                    }
                    if (!writing) return "External source creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcExternalSource created = copy == null ? Documents.CreateFromFile(sources, name, filePath) : mode == null ? Documents.CreateFrom(sources, copy) :
#if PLC_DOCUMENT_EXPORT
                        Documents.CreateFrom(sources, copy, mode.Value);
#else
                        throw new NotSupportedException("MasterCopyMode overload requires V20 or later.");
#endif
                    meta["apiCallSuccess"] = true;
                    if (Documents.Find(sources, Documents.Name(created)) == null) throw new InvalidOperationException("External source not found by readback.");
                    meta["after"] = new Dictionary<string, object?> { ["name"] = Documents.Name(created) };
                    return "External source created and read back; project not saved.";
                }
                if (source == null) throw new PlcSoftwareException("NotFound", "Exact external source not found: " + name + " (available: " + string.Join(", ", PlcGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => Documents.Name(s))) + ").");
                meta["source"] = new Dictionary<string, object?> { ["name"] = Documents.Name(source), ["scope"] = "PlcExternalSource exposes only Name in the PublicAPI; the file content stays on the TIA Portal machine." };
                if (action == "read") return "External source read; no changes.";
                if (action == "delete")
                {
                    if (!writing) return "External source deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; Documents.Delete(source); meta["apiCallSuccess"] = true;
                    if (Documents.Find(sources, name) != null) throw new InvalidOperationException("External source remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "External source deleted and absence verified; project not saved.";
                }
                // generateBlocks: existing blocks are overwritten natively; on error the project is reset to the state before the call.
#if PLC_SOURCE_RESULTS
                var option = (GenerateBlockOption)Enum.Parse(typeof(GenerateBlockOption), generateOption, false);
                PlcBlockUserGroup? blockTarget = null; PlcTypeUserGroup? typeTarget = null;
                if (!string.IsNullOrEmpty(targetKind))
                {
                    if (targetKind == "block") blockTarget = PlcGroupOperations.Group(_session.BlockRootOf(plc, unit), targetGroupPath) as PlcBlockUserGroup ?? throw new PlcSoftwareException("NotFound", "targetGroupPath must name a block user group (not the root).");
                    else typeTarget = PlcGroupOperations.Group(_session.TypeRootOf(plc, unit), targetGroupPath) as PlcTypeUserGroup ?? throw new PlcSoftwareException("NotFound", "targetGroupPath must name a type user group (not the root).");
                    meta["target"] = new Dictionary<string, object?> { ["kind"] = targetKind, ["group"] = Documents.Name(blockTarget) ?? Documents.Name(typeTarget) };
                }
#else
                var option = generateOption;
#endif
                meta["generateOption"] = option.ToString(); meta["warning"] = "Existing blocks / types with the same names are overwritten by the native generation.";
                if (!writing) return "Block generation preview; no changes.";
                meta["mayHaveChanged"] = true;
#if PLC_SOURCE_TARGET
                IList<IEngineeringObject> generated = blockTarget != null ? Documents.Generate(source, blockTarget, option) : typeTarget != null ? Documents.Generate(source, typeTarget, option) : Documents.Generate(source, option);
#elif PLC_SOURCE_RESULTS
                IList<IEngineeringObject> generated = Documents.Generate(source, option);
#else
                Documents.Generate(source);
                IList<IEngineeringObject>? generated = null;
#endif
                meta["apiCallSuccess"] = true; meta["generated"] = GeneratedRows(generated); meta["generatedCount"] = generated?.Count ?? 0;
                return "Blocks / types generated from the external source; project not saved / compiled.";
            });
    }
}

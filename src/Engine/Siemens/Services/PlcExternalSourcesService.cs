using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System.Globalization;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.WatchAndForceTables;

#if TIA_SHARED_ADAPTER_PATHS
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
#else
using Documents = TiaMcpServer.Siemens.LocalDocuments.PlcDocumentPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcExternalSourcesService
    {
        private readonly IEngineeringSession _session;

        public PlcExternalSourcesService(IEngineeringSession session) => _session = session;

        private bool IsProjectNull()
        {
#if TIA_SHARED_ADAPTER_PATHS
            if (_session.IsProjectNull()) return true;
            return TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).Documents.CurrentProject == null;
#else
            return _session.IsProjectNull();
#endif
        }

        public List<string>? GetPlcExternalSources(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (Documents.Software(softwareContainer) is not PlcSoftware plcSoftware) return null;

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null)
            {
                PlcExchangeContract.Observe("complete", JsonValue.Create(false));
                return new List<string>();
            }

            var names = new List<string>();
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            return names;
        }

        /// <summary>
        /// Removes a PLC external source by name (e.g. <c>Ramp.scl</c> or <c>Ramp</c>) so a subsequent
        /// <see cref="ImportPlcExternalSource"/> can recreate it. Returns true if deleted or if no matching source exists.
        /// </summary>
        public void DeletePlcExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "DeletePlcExternalSource: project is null");

            if (string.IsNullOrWhiteSpace(externalSourceName))
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcExternalSource: externalSourceName is empty");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (Documents.Software(softwareContainer) is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"DeletePlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null)
                throw new PortalException(PortalErrorCode.OpennessError, "DeletePlcExternalSource: ExternalSources collection not available");

            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!ExternalSourceNameMatches(name!, externalSourceName)) continue;

                try
                {
                    var del = item.GetType().GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (del != null)
                    {
                        PlcExchangeContract.StartWrite("external-source-delete");
                        del.Invoke(item, null);
                        PlcExchangeContract.ConfirmWrite();
                        return;
                    }

                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: no parameterless Delete() on {item.GetType().Name}");
                }
                catch (PortalException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: {ex.Message}", null, ex);
                }
            }

            // source not present = idempotent no-op success
        }

        public void ImportPlcExternalSource(string softwarePath, string groupPath, string filePath)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "ImportPlcExternalSource: project is null");
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (Documents.Software(softwareContainer) is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var group = TryGetExternalSourceGroupByPath(plcSoftware, groupPath);
            if (group == null)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: ExternalSourceGroup not found (groupPath='{groupPath}')");

            var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
            if (!fi.Exists) throw new PortalException(PortalErrorCode.InvalidParams, "External source file not found: " + filePath);
            var target = group as global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup;
            if (target == null) throw new PortalException(PortalErrorCode.OpennessError, "Expected a public PLC external-source group.");
            try
            {
                // Official V20/V21 signature: source name, then the full source file path.
                PlcExchangeContract.StartWrite("external-source-import");
                var source = Documents.CreateFromFile(Documents.Sources(target), fi.Name, fi.FullName);
                if (source == null) throw new InvalidOperationException("CreateFromFile returned no source.");
                PlcExchangeContract.ConfirmWrite();
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    "External source import outcome is unknown. Inspect the source collection before another write; no alternative import was attempted.", inner: ex);
            }
        }

        private static bool ExternalSourceNameMatches(string actualName, string requested)
        {
            if (string.IsNullOrWhiteSpace(actualName)) return false;
            if (string.Equals(actualName, requested, StringComparison.OrdinalIgnoreCase)) return true;
            var req = (requested ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(req)) return false;
            var reqNoExt = Path.GetFileNameWithoutExtension(req);
            var actNoExt = Path.GetFileNameWithoutExtension(actualName);
            if (string.Equals(actNoExt, reqNoExt, StringComparison.OrdinalIgnoreCase)) return true;
            if (req.IndexOf('.') < 0 &&
                string.Equals(actualName, req + ".scl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public void GenerateBlocksFromExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "GenerateBlocksFromExternalSource: project is null");
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (Documents.Software(softwareContainer) is not PlcSoftware plcSoftware) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null) throw new PortalException(PortalErrorCode.OpennessError, "GenerateBlocksFromExternalSource: ExternalSources collection not available");

            object? src = null;
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ExternalSourceNameMatches(name!, externalSourceName))
                {
                    src = item;
                    break;
                }
            }
            if (src == null) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: external source not found: {externalSourceName}");

            var source = src as global::Siemens.Engineering.SW.ExternalSources.PlcExternalSource;
            if (source == null) throw new PortalException(PortalErrorCode.OpennessError, "Expected a public PLC external source.");
            try
            {
                PlcExchangeContract.StartWrite("external-source-generation");
                Documents.Generate(source);
                PlcExchangeContract.ConfirmWrite();
                PlcExchangeContract.Observe("nativeResult", null);
                PlcExchangeContract.Observe("observation", new JsonObject { ["source"] = "native-call-returned", ["contentVerified"] = false });
                PlcExchangeContract.Observe("contentVerified", null);
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    "Block generation outcome is unknown. Inspect generated blocks before another write; no alternative generation was attempted.", inner: ex);
            }
        }

        private static IEnumerable<object?>? TryGetExternalSourcesCollection(PlcSoftware plcSoftware)
        {
            try
            {
                var group = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware)
                           ?? plcSoftware.GetType().GetProperty("ExternalSources")?.GetValue(plcSoftware);
                if (group == null) return null;

                var sources = group.GetType().GetProperty("ExternalSources")?.GetValue(group) ?? group;
                return sources as IEnumerable<object?>;
            }
            catch
            { /* swallow(probe-optional): Unavailable external-source reflection members are reported as an unresolved collection or group. */
                return null;
            }
        }

        private static object? TryGetExternalSourceGroupByPath(PlcSoftware plcSoftware, string groupPath)
        {
            try
            {
                var root = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware);
                if (root == null) return null;

                if (string.IsNullOrWhiteSpace(groupPath) || groupPath == "/")
                {
                    return root;
                }

                var segments = groupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                object current = root;
                foreach (var seg in segments)
                {
                    var groups = current.GetType().GetProperty("Groups")?.GetValue(current) as IEnumerable;
                    if (groups == null) return null;

                    object? next = null;
                    foreach (var g in groups)
                    {
                        if (g == null) continue;
                        var name = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString();
                        if (string.Equals(name, seg, StringComparison.OrdinalIgnoreCase))
                        {
                            next = g;
                            break;
                        }
                    }
                    if (next == null) return null;
                    current = next;
                }

                return current;
            }
            catch
            { /* swallow(probe-optional): Unavailable external-source reflection members are reported as an unresolved collection or group. */
                return null;
            }
        }

        // ---- external sources ----------------------------------------------------------------------------------------------------------
        private static PlcExternalSourceSystemGroup ExternalSourceRootOf(PlcSoftware plc, PlcUnitBase? unit) => unit == null ? Documents.ExternalSourceGroup(plc) : Documents.ExternalSourceGroup(unit);

        private static JsonObject ExternalSourceGroupRow(PlcExternalSourceGroup group)
        {
            PlcExternalSourceComposition sources = Documents.Sources(group); PlcExternalSourceUserGroupComposition groups = Documents.SourceGroups(group);
            return new JsonObject
            {
                ["name"] = Documents.Name(group), ["groupClass"] = group.GetType().Name,
                ["externalSources"] = new JsonArray(EngineeringGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => (JsonNode)Documents.Name(s)).ToArray()),
                ["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<PlcExternalSourceUserGroup>().Select(g => (JsonNode)Documents.Name(g)).ToArray())
            };
        }

        private static JsonArray GeneratedRows(IList<IEngineeringObject>? generated)
            => generated == null ? new JsonArray() : new JsonArray(generated.Select(o => (JsonNode)new JsonObject { ["name"] = Documents.Attribute(o, "Name")?.ToString(), ["objectClass"] = o.GetType().Name }).ToArray());

        public ResponseMessage ManagePlcExternalSources(string softwarePath, string action, string name = "", string unitName = "", string unitKind = "unit", string groupPath = "",
            string filePath = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", string generateOption = "None", string targetKind = "", string targetGroupPath = "",
            string newName = "", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcExternalSources", meta => {
                bool writing = ExternalSourceRules.ValidateExternalSourceRequest(action, name, unitName, unitKind, filePath, libraryName, masterCopyPath, copyMode, generateOption, targetKind, targetGroupPath, newName, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcExternalSourceSystemGroup root = ExternalSourceRootOf(plc, unit);
                PlcExternalSourceGroup group = (PlcExternalSourceGroup)EngineeringGroupOperations.Group(root, groupPath);
                PlcExternalSourceComposition sources = Documents.Sources(group); PlcExternalSourceUserGroupComposition groups = Documents.SourceGroups(group);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["container"] = new JsonObject { ["unit"] = Documents.Name(unit), ["group"] = Documents.Name(group), ["groupPath"] = groupPath };
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
                    PlcExternalSourceUserGroup target = Documents.Find(groups, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact external source user group not found: " + name);
                    meta["before"] = ExternalSourceGroupRow(target);
                    if (action == "renameGroup")
                    {
                        if (Documents.Find(groups, newName) != null) throw new InvalidOperationException("A user group named " + newName + " already exists.");
#if TIA_V20
                        throw new NotSupportedException("PlcExternalSourceUserGroup.Name is read-only in the V20 PublicAPI (rename is V21+).");
#else
                        if (!writing) return "Group rename preview; no changes.";
                        meta["mayHaveChanged"] = true; Documents.SetName(target, newName); meta["apiCallSuccess"] = true;
                        if (Documents.Name(target) != newName || Documents.Find(groups, newName) == null) throw new InvalidOperationException("Group name readback differs.");
#endif
                        meta["after"] = ExternalSourceGroupRow(target);
                        return "External source user group renamed and read back; project not saved.";
                    }
                    if (EngineeringGroupOperations.Items(Documents.Sources(target)).Any() || EngineeringGroupOperations.Items(Documents.SourceGroups(target)).Any()) throw new InvalidOperationException("Group is not empty; delete its sources / subgroups first (no recursive deletion).");
                    if (!writing) return "Group deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; Documents.Delete(target); meta["apiCallSuccess"] = true;
                    if (EngineeringGroupOperations.Items(Documents.SourceGroups((PlcExternalSourceGroup)EngineeringGroupOperations.Group(ExternalSourceRootOf(plc, unit), groupPath))).Cast<PlcExternalSourceUserGroup>().Any(g => string.Equals(Documents.Name(g), name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Group remains after Delete.");
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
                        meta["sourceFile"] = SoftwareUnitDeepLogic.FileRow(file);
                    }
                    else
                    {
                        copy = _session.ExactMasterCopy(libraryName, masterCopyPath); meta["masterCopy"] = EngineeringScalarProperties.Read(copy);
                        mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                        meta["copyMode"] = mode?.ToString();
                    }
                    if (!writing) return "External source creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    PlcExternalSource created = copy == null ? Documents.CreateFromFile(sources, name, filePath) : mode == null ? Documents.CreateFrom(sources, copy) : Documents.CreateFrom(sources, copy, mode.Value);
                    meta["apiCallSuccess"] = true;
                    if (Documents.Find(sources, Documents.Name(created)) == null) throw new InvalidOperationException("External source not found by readback.");
                    meta["after"] = new JsonObject { ["name"] = Documents.Name(created) };
                    return "External source created and read back; project not saved.";
                }
                if (source == null) throw new PortalException(PortalErrorCode.NotFound, "Exact external source not found: " + name + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(sources).Cast<PlcExternalSource>().Select(s => Documents.Name(s))) + ").");
                meta["source"] = new JsonObject { ["name"] = Documents.Name(source), ["scope"] = "PlcExternalSource exposes only Name in the PublicAPI; the file content stays on the TIA Portal machine." };
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
                var option = (GenerateBlockOption)EngineeringScalarProperties.ConvertValue(JsonValue.Create(generateOption), typeof(GenerateBlockOption))!;
                PlcBlockUserGroup? blockTarget = null; PlcTypeUserGroup? typeTarget = null;
                if (!string.IsNullOrEmpty(targetKind))
                {
                    if (targetKind == "block") blockTarget = EngineeringGroupOperations.Group(_session.BlockRootOf(plc, unit), targetGroupPath) as PlcBlockUserGroup ?? throw new PortalException(PortalErrorCode.NotFound, "targetGroupPath must name a block user group (not the root).");
                    else typeTarget = EngineeringGroupOperations.Group(_session.TypeRootOf(plc, unit), targetGroupPath) as PlcTypeUserGroup ?? throw new PortalException(PortalErrorCode.NotFound, "targetGroupPath must name a type user group (not the root).");
                    meta["target"] = new JsonObject { ["kind"] = targetKind, ["group"] = Documents.Name(blockTarget) ?? Documents.Name(typeTarget) };
                }
                meta["generateOption"] = option.ToString(); meta["warning"] = "Existing blocks / types with the same names are overwritten by the native generation.";
                if (!writing) return "Block generation preview; no changes.";
                meta["mayHaveChanged"] = true;
                IList<IEngineeringObject> generated = blockTarget != null ? Documents.Generate(source, blockTarget, option) : typeTarget != null ? Documents.Generate(source, typeTarget, option) : Documents.Generate(source, option);
                meta["apiCallSuccess"] = true; meta["generated"] = GeneratedRows(generated); meta["generatedCount"] = generated?.Count ?? 0;
                return "Blocks / types generated from the external source; project not saved / compiled.";
            });

        // ---- system block / type groups ---------------------------------------------------------------------------------------------------
        private static JsonObject SystemBlockGroupRow(PlcSystemBlockGroup group, bool includeBlocks, int depth, int maxDepth)
        {
            PlcBlockComposition blocks = Documents.Blocks(group); PlcSystemBlockGroupComposition groups = Documents.SystemBlockGroups(group);
            var row = new JsonObject { ["name"] = Documents.Name(group), ["blockCount"] = Documents.Count(blocks), ["groupCount"] = Documents.Count(groups) };
            if (includeBlocks) row["blocks"] = new JsonArray(EngineeringGroupOperations.Items(blocks).Cast<PlcBlock>().Take(200).Select(b => (JsonNode)new JsonObject { ["name"] = Documents.Name(b), ["number"] = Documents.Number(b), ["blockClass"] = b.GetType().Name, ["programmingLanguage"] = Documents.Language(b).ToString() }).ToArray());
            if (depth < maxDepth) row["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<PlcSystemBlockGroup>().Select(g => (JsonNode)SystemBlockGroupRow(g, includeBlocks, depth + 1, maxDepth)).ToArray());
            else row["groupsTruncated"] = Documents.Count(groups) > 0;
            return row;
        }

        public ResponseMessage ReadPlcSystemGroups(string softwarePath, string unitName = "", string unitKind = "unit", bool includeBlocks = true, int maxDepth = 4)
            => _session.RunHmiStepTool("ListPlcSystemGroups", meta => {
                ExternalSourceRules.ValidateSystemGroupRequest(unitName, unitKind, maxDepth);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcBlockSystemGroup blockRoot = unit == null ? Documents.BlockGroup(plc) : Documents.BlockGroup(unit); PlcTypeSystemGroup typeRoot = unit == null ? Documents.TypeGroup(plc) : Documents.TypeGroup(unit);
                PlcSystemBlockGroupComposition systemBlockGroups = Documents.SystemBlockGroups(blockRoot); PlcSystemTypeGroupComposition systemTypeGroups = Documents.SystemTypeGroups(typeRoot);
                meta["unit"] = Documents.Name(unit);
                meta["systemBlockGroups"] = new JsonArray(EngineeringGroupOperations.Items(systemBlockGroups).Cast<PlcSystemBlockGroup>().Select(g => (JsonNode)SystemBlockGroupRow(g, includeBlocks, 1, maxDepth)).ToArray());
                meta["systemTypeGroups"] = new JsonArray(EngineeringGroupOperations.Items(systemTypeGroups).Cast<PlcSystemTypeGroup>().Select(g => { PlcTypeComposition types = Documents.Types(g); return (JsonNode)new JsonObject { ["name"] = Documents.Name(g), ["typeCount"] = Documents.Count(types), ["types"] = new JsonArray(EngineeringGroupOperations.Items(types).Cast<PlcType>().Take(200).Select(t => (JsonNode)Documents.Name(t)).ToArray()) }; }).ToArray());
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcBlockSystemGroup.SystemBlockGroups (PlcSystemBlockGroup Name / Blocks / Groups, recursive to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types); first 200 objects per group. No modification.";
                return "System block / type groups read; no modification.";
            });
    }
}

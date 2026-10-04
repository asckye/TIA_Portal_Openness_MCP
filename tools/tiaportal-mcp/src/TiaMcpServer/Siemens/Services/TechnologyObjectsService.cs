using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

#if TIA_SHARED_ADAPTER_PATHS
using Native = TiaMcp.PlcFoundation.WatchTechnologyPrimitives;
#else
using Native = TiaMcpServer.Siemens.LocalWatchTechnology.WatchTechnologyPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class TechnologyObjectsService
    {
        private readonly IEngineeringSession _session;

        public TechnologyObjectsService(IEngineeringSession session) => _session = session;

        #region software - TechnologyObjects

        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath)
        {
            // Keep the existing public entry point and its overwrite behavior.
            _session.ImportTechnologyObject(softwarePath, folderPath, importPath, true, new List<string>());
        }

        public ResponseImportBatch ImportTechnologyObjectsFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                var selected = Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal).ToList();
                foreach (var file in selected)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    // Preserve native returned identities, including those read before an enumeration failure.
                    try { _session.ImportTechnologyObject(softwarePath, folderPath, file, overwrite, imported); }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = pex.Message + " Batch stopped; later files were not attempted." });
                        break;
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        // ── Technology Objects (TO) ──────────────────────────────────────────

        // Native observation: root-only enumeration omitted TOs in TechnologicalInstanceDBUserGroup folders.
        // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
        // Typed walk: TechnologicalInstanceDBGroup.TechnologicalObjects + Groups (recursive); folder = "" for the root.
        private static List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB To, string Folder)> EnumerateTechnologyObjectsRecursive(PlcSoftware plc)
        {
            var list = new List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB, string)>();
            void Walk(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup group, string folder)
            {
                foreach (var to in EngineeringGroupOperations.Items(Native.Objects(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>())
                    list.Add((to, folder));
                foreach (var sub in EngineeringGroupOperations.Items(Native.Groups(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup>())
                    Walk(sub, folder.Length == 0 ? Native.Name(sub) : folder + "/" + Native.Name(sub));
            }
            Walk(Native.TechnologyGroup(plc), "");
            return list;
        }

        // Exact name, or folder path + name ("MCP_TO/MCP_PID"); a bare name that exists in several folders is ambiguous.
        private static global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB? FindTechnologyObjectRecursive(PlcSoftware plc, string toName, out string? error)
        {
            error = null;
            var all = EnumerateTechnologyObjectsRecursive(plc);
            var text = (toName ?? "").Trim().Replace('\\', '/').Trim('/');
            var slash = text.LastIndexOf('/');
            var folder = slash < 0 ? null : text.Substring(0, slash);
            var name = slash < 0 ? text : text.Substring(slash + 1);
            var hits = all.Where(x => string.Equals(Native.Name(x.To), name, StringComparison.OrdinalIgnoreCase)
                && (folder == null || string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 1) return hits[0].To;
            if (hits.Count > 1) { error = $"'{name}' exists in {hits.Count} folders ({string.Join(", ", hits.Select(h => h.Folder.Length == 0 ? "(root)" : h.Folder))}); give folder/name."; return null; }
            error = $"Technology object '{toName}' not found. Available: {string.Join(", ", all.Select(x => x.Folder.Length == 0 ? Native.Name(x.To) : x.Folder + "/" + Native.Name(x.To)).Take(30))}";
            return null;
        }

        private static object? ResolveTechnologyObjectCollection(PlcSoftware plc)
        {
            var group = TryGetPropertyValue(plc,
                "TechnologicalObjectGroup", "TechnologyObjectGroup",
                "TechnologicalObjects", "TechnologyObjects");
            if (group == null) return null;

            // If we landed on a group container, drill into the collection
            var col = TryGetPropertyValue(group,
                "TechnologicalObjects", "TechnologyObjects", "Instances", "Objects");
            return col ?? group; // group itself might already be enumerable
        }

        public List<JsonObject> GetTechnologyObjects(string softwarePath)
        {
            var result = new List<JsonObject>();
            // 未连接工程、路径不存在或枚举失败必须报错；空列表只表示已解析的 PLC 确实没有 TO。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetTechnologyObjects: no project is open. Call Connect + OpenProject "
                    + "(or AttachToOpenProject) first.");
            }

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetTechnologyObjects: PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                foreach (var (item, folder) in EnumerateTechnologyObjectsRecursive(plc))
                {
                    var obj = new JsonObject();
                    foreach (var prop in new[] { "Name", "OfSystemLibElement", "OfSystemLibVersion" })
                    {
                        var val = TryGetPropertyValue(item, prop);
                        if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                    }
                    // Try to get a "type" hint from class name as fallback
                    if (!obj.ContainsKey("OfSystemLibElement"))
                        obj["TypeHint"] = JsonValue.Create(item.GetType().Name);
                    obj["Folder"] = JsonValue.Create(folder);
                    result.Add(obj);
                }
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "GetTechnologyObjects failed for {SoftwarePath}", softwarePath);
                // 枚举失败时不得返回看似完整的部分列表。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTechnologyObjects failed halfway through '{softwarePath}': {ex.Message}. "
                    + "The list would have been INCOMPLETE, so it is not returned.", null, ex);
            }
            return result;
        }

        public ResponseMessage ExportTechnologyObject(string softwarePath, string toName, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

            try
            {
                var to = FindTechnologyObjectRecursive(plc, toName, out var lookupError);
                if (to == null)
                    return new ResponseMessage { Message = $"Technology object '{toName}' not found in '{softwarePath}': {lookupError}" };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                _session.TryExportEngineeringObject(to, exportPath, out var err);
                if (err != null)
                    return new ResponseMessage { Message = $"Export error: {err}" };

                return new ResponseMessage
                {
                    Message = $"Technology object '{toName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["toName"] = toName }
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ExportTechnologyObject failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseImportBatch ExportTechnologyObjectsToDirectory(
            string softwarePath, string exportDir, string regexName = "")
        {
            var exported = new List<string>();
            var failed = new List<ImportFailure>();

            if (IsProjectNull())
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = "No project open." });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found." + _session.AvailablePlcPathsSuffix() });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }

            try
            {
                Directory.CreateDirectory(exportDir);
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);

                foreach (var (item, folder) in EnumerateTechnologyObjectsRecursive(plc))
                {
                    var name = Native.Name(item) ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (regex != null && !regex.IsMatch(name)) continue;

                    // TOs of a user folder land in a sub directory of the same name (ImportTechnologyObjectsFromDirectory takes folderPath)
                    var directory = folder.Length == 0 ? exportDir : Path.Combine(exportDir, folder.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, name + ".xml");
                    _session.TryExportEngineeringObject(item, path, out var err);
                    var label = folder.Length == 0 ? name : folder + "/" + name;
                    if (err == null) exported.Add(label);
                    else failed.Add(new ImportFailure { Path = label, Error = err });
                }
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = exportDir, Error = ex.ToString() });
            }

            return new ResponseImportBatch { Imported = exported, Failed = failed };
        }

        #endregion

        public ResponseMessage ManageTechnologyObject(string softwarePath, string objectPath, string action, string typeIdentifier = "",
            string version = "", string parameter = "", string valueJson = "null", bool dryRun = true)
            => _session.RunHmiStepTool("ManageTechnologyObject", meta => {
                if (!new[] { "read", "create", "delete", "setParameter" }.Contains(action)) throw new ArgumentException("action must be read/create/delete/setParameter.");
                var parts = EngineeringGroupOperations.Parts(objectPath);
                bool writing = action != "read" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var group = EngineeringGroupOperations.Group(Native.TechnologyGroup(plc), string.Join("/", parts.Take(parts.Length - 1)));
                var collection = EngineeringGroupOperations.Get(group, "TechnologicalObjects");
                var target = EngineeringGroupOperations.Find(collection, parts.Last());
                meta["objectPath"] = objectPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "create")
                {
                    if (target != null) throw new InvalidOperationException("Technology object already exists.");
                    if (string.IsNullOrWhiteSpace(typeIdentifier)) throw new ArgumentException("Official technology type identifier required.");
                    // Validate the technology version before parsing. Native observation: 1515F FW V2.9 uses TO V6.0.
                    // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                    if (!Version.TryParse(version, out var apiVersion) || apiVersion.Major < 1)
                        throw new ArgumentException("version must be the technology object version as 'major.minor', e.g. \"6.0\" (TIA V17 / FW 2.9), \"7.0\" (FW 3.0), \"8.0\" (FW 3.1); got '" + version + "'. TIA rejects a version the CPU firmware does not support.");
                    meta["typeIdentifier"] = typeIdentifier; meta["version"] = version;
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true;
                        // Typed TechnologicalInstanceDBComposition.Create(name, typeIdentifier, version) with Find readback.
                        if (collection is TechnologicalInstanceDBComposition typedCollection)
                        {
                            TechnologicalInstanceDB created = Native.Create(typedCollection, parts.Last(), typeIdentifier, apiVersion);
                            if (Native.Find(typedCollection, parts.Last()) == null) throw new InvalidOperationException("Technology object not found by readback after Create.");
                            target = created; meta["after"] = _session.TechnologyObjectRow(created);
                        }
                        else
                        {
                            target = EngineeringGroupOperations.Call(collection, "Create", new[] { typeof(string), typeof(string), typeof(Version) }, parts.Last(), typeIdentifier, apiVersion);
                            meta["after"] = EngineeringScalarProperties.Read(target);
                        }
                    }
                }
                else
                {
                    if (target == null) throw new InvalidOperationException("Exact technology object not found.");
                    if (action == "delete")
                    {
                        meta["before"] = EngineeringScalarProperties.Read(target);
                        if (writing)
                        {
                            meta["mayHaveChanged"] = true;
                            EngineeringGroupOperations.Call(target, "Delete", Type.EmptyTypes);
                            if (EngineeringGroupOperations.Find(collection, parts.Last()) != null) throw new InvalidOperationException("Object remains after Delete.");
                            meta["verifiedAbsent"] = true;
                        }
                    }
                    else
                    {
                        var parameters = EngineeringGroupOperations.Get(target, "Parameters");
                        TechnologicalParameterComposition? typedParameters = parameters as TechnologicalParameterComposition;
                        if (target is TechnologicalInstanceDB typedObject) meta["object"] = _session.TechnologyObjectRow(typedObject);
                        if (string.IsNullOrEmpty(parameter))
                        {
                            if (action == "setParameter") throw new ArgumentException("Exact parameter name required.");
                            var all = EngineeringGroupOperations.Items(parameters).Select(x => x is TechnologicalParameter tp ? _session.ParameterRow(tp) : EngineeringScalarProperties.Read(x)).ToArray();
                            meta["parameters"] = new JsonArray(all.Cast<JsonNode>().ToArray());
                            meta["actualCount"] = all.Length; meta["dataComplete"] = all.All(x => x["dataComplete"]?.GetValue<bool>() ?? !x.ContainsKey("valueError"));
                        }
                        else
                        {
                            // Typed TechnologicalParameterComposition.Find(name) with the reflective lookup as fallback.
                            var p = (typedParameters != null ? Native.Find(typedParameters, parameter) : null) ?? EngineeringGroupOperations.Find(parameters, parameter) ?? throw new InvalidOperationException("Parameter not found: " + parameter);
                            meta["before"] = p is TechnologicalParameter typedBefore ? _session.ParameterRow(typedBefore) : EngineeringScalarProperties.Read(p);
                            if (action == "setParameter")
                            {
                                var edits = EngineeringScalarProperties.Prepare(p.GetType(), new JsonObject { ["Value"] = JsonNode.Parse(valueJson) });
                                var existingValue = p.GetType().GetProperty("Value")!.GetValue(p);
                                if (existingValue != null && edits[0].Property.PropertyType == typeof(object))
                                    edits[0] = (edits[0].Property, EngineeringScalarProperties.ConvertValue(JsonNode.Parse(valueJson), existingValue.GetType()));
                                meta["requestedValue"] = JsonNode.Parse(valueJson);
                                if (writing) { EngineeringScalarProperties.Apply(p, edits, meta); meta["after"] = p is TechnologicalParameter typedAfter ? _session.ParameterRow(typedAfter) : EngineeringScalarProperties.Read(p); }
                            }
                        }
                    }
                }
                return writing ? "Native technology object operation completed; no save/compile/download." : "Technology object read/preview completed; no changes.";
            });

        // ---- ReadTechnologyObjectTree ---------------------------------------------------------------------------------------------------
        private JsonObject TechnologyGroupRow(TechnologicalInstanceDBGroup group, bool includeParameters, int depth, int maxDepth)
        {
            TechnologicalInstanceDBComposition objects = Native.Objects(group); TechnologicalInstanceDBUserGroupComposition groups = Native.Groups(group);
            var row = new JsonObject { ["name"] = Native.Name(group), ["groupClass"] = group.GetType().Name, ["objectCount"] = Native.Count(objects), ["groupCount"] = Native.Count(groups) };
            row["technologicalObjects"] = new JsonArray(EngineeringGroupOperations.Items(objects).Cast<TechnologicalInstanceDB>().Take(200).Select(db =>
            {
                var o = _session.TechnologyObjectRow(db);
                if (includeParameters) Safe(o, "parameters", () => { TechnologicalParameterComposition parameters = Native.Parameters(db); return new JsonArray(EngineeringGroupOperations.Items(parameters).Cast<TechnologicalParameter>().Take(500).Select(p => (JsonNode)_session.ParameterRow(p)).ToArray()); });
                return (JsonNode)o;
            }).ToArray());
            if (depth < maxDepth) row["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Cast<TechnologicalInstanceDBUserGroup>().Select(g => (JsonNode)TechnologyGroupRow(g, includeParameters, depth + 1, maxDepth)).ToArray());
            else row["groupsTruncated"] = Native.Count(groups) > 0;
            return row;
        }
        public ResponseMessage ReadTechnologyObjectTree(string softwarePath, string groupPath = "", bool includeParameters = false, bool includeMotionView = false, int maxDepth = 4)
            => _session.RunHmiStepTool("ReadTechnologyObjectTree", meta => {
                if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                TechnologicalInstanceDBGroup root = (TechnologicalInstanceDBGroup)EngineeringGroupOperations.Group(Native.TechnologyGroup(plc), groupPath);
                meta["groupPath"] = groupPath; meta["tree"] = TechnologyGroupRow(root, includeParameters, 1, maxDepth);
                if (includeMotionView)
                {
                    var views = new JsonObject();
                    foreach (TechnologicalInstanceDB db in EngineeringGroupOperations.Items(Native.Objects(root)).Cast<TechnologicalInstanceDB>().Take(50)) Safe(views, Native.Name(db), () => _session.TypedMotionView(db));
                    meta["motionViews"] = views;
                }
                meta["apiCallSuccess"] = true;
                meta["scope"] = "TechnologicalInstanceDBGroup Name / TechnologicalObjects (TechnologicalInstanceDB Name, Number, OfSystemLibElement, OfSystemLibVersion, IsConsistent, parameter count) / Groups recursive to maxDepth; includeParameters adds TechnologicalParameter Name / Value (first 500 per object); includeMotionView adds the typed hardware interfaces, master values and mappings of the root group's objects (first 50). No modification.";
                return "Technology object tree read; no modification.";
            });
#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.WatchTechnologySurface? _watchTechnology;
        private bool IsProjectNull()
        {
            // Keep the engine's binding check before borrowing its current handle.
            if (_session.IsProjectNull()) return true;
            return (_watchTechnology ?? (_watchTechnology =
                TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).WatchTechnology)).CurrentProject == null;
        }
#else
        private bool IsProjectNull() => _session.IsProjectNull();
#endif
    }
}

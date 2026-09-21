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

namespace TiaMcpServer.Siemens
{
    // Partial: software. Family file split out of Portal.Software.cs (2.8.0); behavior unchanged.
    public partial class Portal
    {
        #region software - TechnologyObjects

        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");

            try
            {
                // 2.7.48: typed - the reflective lookup asked for "TechnologyObjectGroup" (the property is TechnologicalObjectGroup) and
                // always fell through to the PLC ("TechnologyObjects collection not found", real project).
                var group = (global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup)EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup, folderPath ?? "");
                var col = group.TechnologicalObjects;
                if (TryImportEngineeringObjectIntoCollection(col, importPath, out _, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportTechnologyObject failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
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

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try { ImportTechnologyObject(softwarePath, folderPath, file); imported.Add(name); }
                    catch (PortalException pex) { failed.Add(new ImportFailure { Path = file, Error = pex.Message }); }
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

        // 2.7.49 (real machine): TOs inside a user folder (TechnologicalInstanceDBUserGroup, e.g. imported with folderPath) were invisible
        // to GetTechnologyObjects / ExportTechnologyObject / ExportTechnologyObjectsToDirectory, which only read the root composition.
        // Typed walk: TechnologicalInstanceDBGroup.TechnologicalObjects + Groups (recursive); folder = "" for the root.
        private static List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB To, string Folder)> EnumerateTechnologyObjectsRecursive(PlcSoftware plc)
        {
            var list = new List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB, string)>();
            void Walk(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup group, string folder)
            {
                foreach (var to in EngineeringGroupOperations.Items(group.TechnologicalObjects).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>())
                    list.Add((to, folder));
                foreach (var sub in EngineeringGroupOperations.Items(group.Groups).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup>())
                    Walk(sub, folder.Length == 0 ? sub.Name : folder + "/" + sub.Name);
            }
            Walk(plc.TechnologicalObjectGroup, "");
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
            var hits = all.Where(x => string.Equals(x.To.Name, name, StringComparison.OrdinalIgnoreCase)
                && (folder == null || string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 1) return hits[0].To;
            if (hits.Count > 1) { error = $"'{name}' exists in {hits.Count} folders ({string.Join(", ", hits.Select(h => h.Folder.Length == 0 ? "(root)" : h.Folder))}); give folder/name."; return null; }
            error = $"Technology object '{toName}' not found. Available: {string.Join(", ", all.Select(x => x.Folder.Length == 0 ? x.To.Name : x.Folder + "/" + x.To.Name).Take(30))}";
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
            // 这三条原来都返回空列表，工具层于是报「在 'XXX' 里找到 0 个技术对象」——
            // 「没连项目」「路径写错」「枚举炸了」全被说成了「这个 PLC 没有技术对象」。
            // 空列表只有一个合法含义：**解析到了这个 PLC，它确实没有 TO**。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetTechnologyObjects: no project is open. Call Connect + OpenProject "
                    + "(or AttachToOpenProject) first.");
            }

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetTechnologyObjects: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
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
                _logger?.LogError(ex, "GetTechnologyObjects failed for {SoftwarePath}", softwarePath);
                // 原来吞掉异常返回已收集的部分 —— 「少了几个 TO」比「一个都没有」更难发现，
                // 因为它看起来完全正常。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTechnologyObjects failed halfway through '{softwarePath}': {ex.Message}. "
                    + "The list would have been INCOMPLETE, so it is not returned.", null, ex);
            }
            return result;
        }

        public ResponseMessage ExportTechnologyObject(string softwarePath, string toName, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var to = FindTechnologyObjectRecursive(plc, toName, out var lookupError);
                if (to == null)
                    return new ResponseMessage { Message = $"Technology object '{toName}' not found in '{softwarePath}': {lookupError}" };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TryExportEngineeringObject(to, exportPath, out var err);
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
                _logger?.LogError(ex, "ExportTechnologyObject failed");
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

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found." });
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
                    var name = item.Name ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (regex != null && !regex.IsMatch(name)) continue;

                    // TOs of a user folder land in a sub directory of the same name (ImportTechnologyObjectsFromDirectory takes folderPath)
                    var directory = folder.Length == 0 ? exportDir : Path.Combine(exportDir, folder.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, name + ".xml");
                    TryExportEngineeringObject(item, path, out var err);
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
    }
}

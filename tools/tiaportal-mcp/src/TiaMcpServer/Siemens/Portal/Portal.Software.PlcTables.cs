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
        #region software - PlcTables

        /// <summary>
        /// List every PLC tag table, including the ones nested in user groups. Tables inside a group
        /// come back group-qualified ("驱动/变频器变量表"); root-level tables keep their bare name.
        /// Returns null only when the PLC software itself cannot be resolved.
        /// </summary>
        /// <remarks>
        /// Do NOT route this through TryListNamesFromCollection with a "TagTables" hint: the object in
        /// hand is already the PlcTagTableComposition, so asking it for a *.TagTables* property misses,
        /// a non-empty hint list also skips the plain-IEnumerable path, and the helper swallows that
        /// into an empty list. The tool then answered "this PLC has no tag tables" for every
        /// S7-1200/1500 project ever built — GitHub issue #22.
        /// </remarks>
        public List<string>? GetPlcTagTables(string softwarePath)
        {
            return GetPlcTagTables(softwarePath, out _);
        }

        /// <summary>
        /// 同上，外加一份「走过了什么」的诊断。空清单时它是唯一的证据来源。
        /// </summary>
        public List<string>? GetPlcTagTables(string softwarePath, out TagTableWalkDiagnostics diagnostics)
        {
            diagnostics = new TagTableWalkDiagnostics();
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcTagTableGroup(plc);
            if (group == null)
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag table group not found on '{softwarePath}' (plcType={plc.GetType().FullName}). " +
                    "Tag tables cannot be enumerated for this software object.");

            diagnostics.RootGroupType = group.GetType().FullName ?? group.GetType().Name;
            var result = new List<string>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectTagTableNames(group, "", result, visited, diagnostics);
            diagnostics.TablesFound = result.Count;
            return result;
        }

        /// <summary>
        /// Export one PLC tag table. <paramref name="tagTableName"/> takes either the bare table name
        /// (matched anywhere in the group tree) or the group-qualified form returned by
        /// <see cref="GetPlcTagTables"/>; backslashes count as separators too, so the path printed by
        /// GetCrossReferences can be pasted straight in.
        /// </summary>
        public bool ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath, out string? error)
        {
            error = null;
            if (IsProjectNull()) { error = "no project is open"; return false; }
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) { error = $"PLC software not found at '{softwarePath}'"; return false; }

            var group = ResolvePlcTagTableGroup(plc);
            if (group == null) { error = $"tag table group not found on '{softwarePath}'"; return false; }

            var wanted = (tagTableName ?? string.Empty).Replace('\\', '/').Trim('/');
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var table = FindTagTable(group, "", wanted, visited);
            if (table == null)
            {
                // "no such table" and "found it, but Openness refused the export" used to share one
                // message, so a caller could not tell a typo from a real failure (GitHub issue #22).
                var known = GetPlcTagTables(softwarePath) ?? new List<string>();
                error = $"no tag table named '{tagTableName}' in '{softwarePath}'" +
                        (known.Count > 0 ? ". Available: " + string.Join(", ", known) : " (this PLC has no tag tables)");
                return false;
            }
            return TryExportEngineeringObject(table, exportPath, out error);
        }

        /// <summary>The container that owns TagTables (and the user groups below it).</summary>
        private static object? ResolvePlcTagTableGroup(object plc)
            => TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder")
               // HMI-shaped software hangs the composition straight off the root.
               ?? (TryGetPropertyValue(plc, "TagTables") != null ? plc : null);


        /// <summary>
        /// 枚举变量表时**顺手记下走过了什么**，专门给「返回空清单」这种情况用。
        ///
        /// 为什么要有它：空清单有三种完全不同的成因 —— 这个 PLC 确实没有表、
        /// TagTables 属性在这个版本上叫别的名字、读属性时抛了异常被吞掉。
        /// 三者返回的东西一模一样，调用方（和维护者）无从分辨，
        /// 用户报「V20 上枚举返回空但删除工具能找到同一张表」时，我们手上没有任何证据。
        /// 有了这几行，空清单至少能自证是哪一种。
        /// </summary>
        public sealed class TagTableWalkDiagnostics
        {
            public string RootGroupType { get; set; } = "";
            public bool TagTablesPropertyFound { get; set; }
            public string? TagTablesPropertyError { get; set; }
            public int GroupsVisited { get; set; }
            public int TablesFound { get; set; }
            public List<string> Notes { get; } = new List<string>();
        }

        private static void CollectTagTableNames(object group, string prefix, List<string> result,
            HashSet<object> visited, TagTableWalkDiagnostics? diag = null)
        {
            if (!visited.Add(group)) return;
            if (diag != null) diag.GroupsVisited++;

            // 直接反射一次，把「属性不存在」「读属性抛了」「读到了但是 null」分开记。
            // TryGetPropertyValue 会把这三种全折成 null —— 那正是空清单无法自证的根因。
            object? tables = null;
            if (diag != null && string.IsNullOrEmpty(prefix))
            {
                var prop = group.GetType().GetProperty("TagTables",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    diag.Notes.Add("根组上没有 TagTables 属性（type=" + group.GetType().Name + "）");
                }
                else
                {
                    diag.TagTablesPropertyFound = true;
                    try { tables = prop.GetValue(group); }
                    catch (Exception ex)
                    {
                        diag.TagTablesPropertyError = ex.GetBaseException().Message;
                        diag.Notes.Add("读 TagTables 抛异常：" + diag.TagTablesPropertyError);
                    }
                    if (tables == null && diag.TagTablesPropertyError == null)
                        diag.Notes.Add("TagTables 属性存在但取到 null");
                }
            }
            else
            {
                tables = TryGetPropertyValue(group, "TagTables");
            }

            if (tables is IEnumerable tEnum and not string)
            {
                foreach (var t in tEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    if (name.Length == 0) continue;
                    result.Add(string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name);
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    CollectTagTableNames(sub, next, result, visited, diag);
                }
            }
        }

        /// <summary>Walks the same tree as <see cref="CollectTagTableNames"/>, matching a table on
        /// either its bare name or the group-qualified path that walk would have produced.</summary>
        private static object? FindTagTable(object group, string prefix, string wanted, HashSet<object> visited)
        {
            if (!visited.Add(group)) return null;

            var tables = TryGetPropertyValue(group, "TagTables");
            if (tables is IEnumerable tEnum and not string)
            {
                foreach (var t in tEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    if (name.Length == 0) continue;
                    var qualified = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(qualified, wanted, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    var hit = FindTagTable(sub, next, wanted, visited);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");

            try
            {
                object root = TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                // TagTables collection lives on group
                var tables = TryGetPropertyValue(group, "TagTables") ?? TryGetPropertyValue(root, "TagTables");
                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. plcType={plc.GetType().FullName} groupType={group.GetType().FullName}");

                // Route through PrepareXmlForImport so the hardcoded <Engineering version="V21"/>
                // header is rewritten to the connected portal version (and a UTF-8 BOM is ensured).
                // Without this, tag-table imports fail on a V20 portal with
                // "The engineering version 'V21' ... is not supported." (block/type imports already
                // sanitize via PrepareXmlForImport; tag tables previously skipped it).
                if (TryImportEngineeringObjectIntoCollection(tables, PrepareXmlForImport(importPath), out _, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportPlcTagTable failed");
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

        public ResponseImportBatch ImportPlcTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
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

                    try { ImportPlcTagTable(softwarePath, folderPath, file); imported.Add(name); }
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

        public List<string>? GetPlcWatchTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null)
            {
                return TryListNamesFromCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, "WatchTables");
            }

            return EnumeratePlcWatchTables(group)
                .Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool ExportPlcWatchTable(string softwarePath, string watchTableName, string exportPath)
        {
            if (IsProjectNull()) return false;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return false;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            object? table = null;
            if (group != null)
            {
                table = EnumeratePlcWatchTables(group)
                    .FirstOrDefault(x =>
                        string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase))
                    .Table;
            }
            else
            {
                table = TryFindByNameInCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, watchTableName);
            }

            if (table == null) return false;
            return TryExportEngineeringObject(table, exportPath, out _);
        }

        public ResponseImportBatch ExportPlcWatchTablesToDirectory(string softwarePath, string dir, string regexName = "")
        {
            var exported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                var names = GetPlcWatchTables(softwarePath);
                if (names == null)
                {
                    failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found" });
                    return new ResponseImportBatch { Imported = exported, Failed = failed };
                }

                Directory.CreateDirectory(dir);
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var name in names)
                {
                    if (regex != null && !regex.IsMatch(name)) continue;
                    var outPath = Path.Combine(dir, MakeSafeFileName(name) + ".xml");
                    if (ExportPlcWatchTable(softwarePath, name, outPath)) exported.Add(outPath);
                    else failed.Add(new ImportFailure { Path = name, Error = "Export failed" });
                }

                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }
        }

        // ── Force Tables ──────────────────────────────────────────────────────

        public List<string>? GetPlcForceTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null) return new List<string>();

            var result = new List<string>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectForceTableNames(group, "", result, visited);
            return result;
        }

        private static void CollectForceTableNames(object group, string prefix, List<string> result, HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var forceTables = TryGetPropertyValue(group, "ForceTables", "PlcForceTables");
            if (forceTables is IEnumerable ftEnum and not string)
            {
                foreach (var t in ftEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    result.Add(string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name);
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    CollectForceTableNames(sub, next, result, visited);
                }
            }
        }

        // 2.7.50 (real machine, 项目1): PlcWatchTable.Entries.Create() only yields a comment row and the typed entry properties are
        // read-only, so no Openness call adds a tag row. The official route is the SimaticML round trip: export the table, add or
        // update the row in the XML, import it back with ImportOptions.Override into the table's own group, then read the row back.
        // The old code also looked for the table at the root only and created a fresh one on every call (MCP_WT_1 … MCP_WT_5).
        public ResponseMessage EnsureWatchTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string modifyValue,
            string trigger = "Permanent")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(address)) return new ResponseMessage { Message = "tableName and address are required." };
            var triggerType = typeof(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTablePreDefinedTrigger);
            object triggerValue;
            try { triggerValue = Enum.Parse(triggerType, (trigger ?? "").Trim(), true); }
            catch (ArgumentException) { return new ResponseMessage { Message = "trigger must be one of: " + string.Join("/", Enum.GetNames(triggerType)) + " (case-sensitive)." }; }
            var triggerName = triggerValue.ToString();

            var meta = new JsonObject
            {
                ["softwarePath"] = softwarePath, ["tableName"] = tableName, ["address"] = address, ["modifyValue"] = modifyValue, ["trigger"] = triggerName,
                ["mayHaveChanged"] = false, ["readbackVerified"] = false,
                ["method"] = "SimaticML round trip: PlcWatchTable.Export -> XML upsert -> PlcWatchTableComposition.Import(ImportOptions.Override) (the typed API cannot create tag rows)"
            };
            string? tempFile = null;
            try
            {
                var root = plc.WatchAndForceTableGroup;
                var (table, group, resolvedPath, ambiguity) = ResolveWatchTable(root, tableName);
                if (ambiguity != null) return new ResponseMessage { Message = ambiguity, Meta = meta };
                meta["resolvedTablePath"] = resolvedPath;
                meta["tableExisted"] = table != null;

                XDocument doc;
                if (table != null)
                {
                    tempFile = Path.Combine(Path.GetTempPath(), "tia-mcp-wt-" + Guid.NewGuid().ToString("N") + ".xml");
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    table.Export(new FileInfo(tempFile), ExportOptions.None);
                    doc = XDocument.Load(tempFile);
                    meta["entriesBefore"] = WatchTableEntryXml.Entries(doc).Count();
                }
                else
                {
                    doc = WatchTableEntryXml.NewTable(resolvedPath.Contains("/") ? resolvedPath.Substring(resolvedPath.LastIndexOf('/') + 1) : resolvedPath, PortalMajorVersion);
                    meta["entriesBefore"] = 0;
                }
                var action = WatchTableEntryXml.Upsert(doc, address, modifyValue, triggerName);
                meta["rowAction"] = action;
                tempFile ??= Path.Combine(Path.GetTempPath(), "tia-mcp-wt-" + Guid.NewGuid().ToString("N") + ".xml");
                doc.Save(tempFile);

                meta["mayHaveChanged"] = true;
                var imported = group.WatchTables.Import(new FileInfo(tempFile), ImportOptions.Override).ToArray();
                meta["importedCount"] = imported.Length;
                meta["apiCallSuccess"] = true;

                // readback from a fresh navigation (the old proxy is dead after Override)
                var (after, _, _, _) = ResolveWatchTable(plc.WatchAndForceTableGroup, resolvedPath);
                if (after == null) return new ResponseMessage { Message = $"Watch table '{resolvedPath}' is missing after the import; nothing verified.", Meta = meta };
                var rows = EngineeringGroupOperations.Items(after.Entries).ToArray();
                meta["entriesAfter"] = rows.Length;
                global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry? hit = null;
                var attribute = WatchTableEntryXml.AttributeFor(address);
                foreach (var row in rows.OfType<global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry>())
                {
                    var key = attribute == "Address" ? row.Address ?? "" : row.Name ?? "";
                    if (attribute == "Address" ? string.Equals(key, address.Trim(), StringComparison.OrdinalIgnoreCase) : WatchTableEntryXml.SameSymbol(key, address)) { hit = row; break; }
                }
                if (hit == null)
                    return new ResponseMessage { Message = $"Watch table '{resolvedPath}' was re-imported ({rows.Length} rows) but no row for '{address}' came back; TIA may have rejected the row (check the address / symbol).", Meta = meta };
                meta["after"] = new JsonObject { ["Name"] = hit.Name, ["Address"] = hit.Address, ["DisplayFormat"] = hit.DisplayFormat.ToString(), ["ModifyValue"] = hit.ModifyValue, ["ModifyTrigger"] = hit.ModifyTrigger.ToString(), ["MonitorTrigger"] = hit.MonitorTrigger.ToString(), ["ModifyIntention"] = hit.ModifyIntention };
                bool valueOk = string.Equals(hit.ModifyValue ?? "", modifyValue ?? "", StringComparison.OrdinalIgnoreCase);
                bool triggerOk = string.Equals(hit.ModifyTrigger.ToString(), triggerName, StringComparison.OrdinalIgnoreCase);
                meta["readbackVerified"] = valueOk && triggerOk;
                meta["note"] = "Value will be applied to the PLC when TIA Portal is online and the trigger fires. Project not saved.";
                meta["success"] = valueOk && triggerOk;   // 2.7.52: the bridge reads Meta.success (was missing -> operationStatus unknown)
                if (!valueOk || !triggerOk)
                    return new ResponseMessage { Message = $"Watch table '{resolvedPath}': row for '{address}' {action}, but the readback shows ModifyValue='{hit.ModifyValue}' Trigger={hit.ModifyTrigger} (requested '{modifyValue}' / {triggerName}).", Meta = meta };
                return new ResponseMessage { Message = $"Watch table '{resolvedPath}': row for '{address}' {action} with ModifyValue='{modifyValue}' Trigger={triggerName} (readback verified; {rows.Length} rows).", Meta = meta };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EnsureWatchTableEntry failed");
                meta["success"] = false;
                meta["error"] = ex.Message;
                return new ResponseMessage { Message = $"Error: {ex.Message}", Meta = meta };
            }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        // Exact group-qualified path first ("MCP_W/MCP_WT"); a bare name is searched through every user group. Returns the table
        // (null when absent), the group that owns or would own it, the resolved path, and an ambiguity message when a bare name
        // exists in several groups.
        private static (global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable? Table, global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup Group, string Path, string? Ambiguity)
            ResolveWatchTable(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup root, string tableName)
        {
            var wanted = tableName.Replace('\\', '/').Trim('/');
            var parts = EngineeringGroupOperations.Parts(wanted);
            var groupPath = string.Join("/", parts.Take(parts.Length - 1));
            var leaf = parts.Last();
            var group = (global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup)EngineeringGroupOperations.Group(root, groupPath);
            var direct = group.WatchTables.Find(leaf);
            if (direct != null || parts.Length > 1) return (direct, group, wanted, null);

            var hits = new List<(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable, global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup, string)>();
            void Walk(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup g, string path)
            {
                foreach (var sub in EngineeringGroupOperations.Items(g.Groups).Cast<global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup>())
                {
                    var subPath = path.Length == 0 ? sub.Name : path + "/" + sub.Name;
                    var t = sub.WatchTables.Find(leaf);
                    if (t != null) hits.Add((t, sub, subPath + "/" + leaf));
                    Walk(sub, subPath);
                }
            }
            Walk(root, "");
            if (hits.Count == 1) return (hits[0].Item1, hits[0].Item2, hits[0].Item3, null);
            if (hits.Count > 1) return (null, group, wanted, $"'{leaf}' exists in {hits.Count} groups ({string.Join(", ", hits.Select(h => h.Item3))}); give the group-qualified path.");
            return (null, group, wanted, null);
        }

        public ResponseMessage EnsureForceTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string forceValue)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null) return new ResponseMessage { Message = "WatchAndForceTableGroup not accessible." };

                var table = FindOrCreateForceTable(group, tableName);
                if (table == null) return new ResponseMessage { Message = $"Could not find or create force table '{tableName}'." };

                var entry = FindOrCreateTableEntry(table, "Entries", address, out var entryCreated);
                if (entry == null) return new ResponseMessage { Message = $"Could not create force entry for address '{address}': PlcForceTable.Entries.Create() returned nothing." };

                var refused = new JsonObject();
                SetWatchEntryAttribute(entry, WatchEntryAddressAttribute(address), address, refused);
                SetWatchEntryAttribute(entry, "ForceValue", forceValue, refused);
                var after = ReadWatchEntryAttributes(entry, "Name", "Address", "ForceValue", "ForceIntention", "MonitorTrigger", "DisplayFormat");
                bool addressVerified = string.Equals(Convert.ToString(after["Address"]), address, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Convert.ToString(after["Name"]), address.Trim('"'), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Convert.ToString(after["Name"]), address, StringComparison.OrdinalIgnoreCase);
                bool valueVerified = string.Equals(Convert.ToString(after["ForceValue"]), forceValue, StringComparison.OrdinalIgnoreCase);
                var meta = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["tableName"] = tableName,
                    ["address"] = address,
                    ["forceValue"] = forceValue,
                    ["entryCreated"] = entryCreated,
                    ["after"] = after,
                    ["refusedAttributes"] = refused,
                    ["readbackVerified"] = addressVerified && valueVerified,
                    ["note"] = "Force will be applied continuously while TIA Portal is online with this CPU."
                };
                if (!addressVerified || !valueVerified)
                    return new ResponseMessage
                    {
                        Message = $"Force table '{tableName}': entry for '{address}' was {(entryCreated ? "created" : "found")} but the readback does not show the requested "
                            + (!addressVerified ? "address/name" : "force value") + " (refused: " + string.Join(", ", refused.Select(r => r.Key + "=" + r.Value)) + ").",
                        Meta = meta
                    };
                return new ResponseMessage
                {
                    Message = $"Force table '{tableName}': entry '{address}' set to ForceValue='{forceValue}' (readback verified).",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EnsureForceTableEntry failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        private static object? FindOrCreateWatchTable(object group, string tableName)
        {
            var watchTables = TryGetPropertyValue(group, "WatchTables", "PlcWatchTables");
            if (watchTables == null) return null;

            // Search existing
            if (watchTables is IEnumerable wte and not string)
            {
                foreach (var t in wte)
                {
                    if (t == null) continue;
                    if (string.Equals(TryGetPropertyValue(t, "Name")?.ToString(), tableName, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            // Create new
            return TryInvokeMethodByName(watchTables, "Create", tableName);
        }

        private static object? FindOrCreateForceTable(object group, string tableName)
        {
            var forceTables = TryGetPropertyValue(group, "ForceTables", "PlcForceTables");
            if (forceTables == null) return null;

            if (forceTables is IEnumerable fte and not string)
            {
                foreach (var t in fte)
                {
                    if (t == null) continue;
                    if (string.Equals(TryGetPropertyValue(t, "Name")?.ToString(), tableName, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return TryInvokeMethodByName(forceTables, "Create", tableName);
        }

        private static object? FindOrCreateTableEntry(object table, string entriesPropertyName, string address, out bool created)
        {
            created = false;
            var entries = TryGetPropertyValue(table, entriesPropertyName, "WatchTableEntries", "ForceTableEntries", "Rows");
            if (entries == null) return null;

            // Search existing entry with the same address or symbolic name (both spellings: "Tag" and Tag)
            var bare = address.Trim('"');
            if (entries is IEnumerable ee and not string)
            {
                foreach (var e in ee)
                {
                    if (e == null) continue;
                    var addr = TryGetPropertyValue(e, "Address")?.ToString();
                    var name = TryGetPropertyValue(e, "Name")?.ToString();
                    if (string.Equals(addr, address, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(name) && (string.Equals(name, address, StringComparison.OrdinalIgnoreCase) || string.Equals(name, bare, StringComparison.OrdinalIgnoreCase))))
                        return e;
                }
            }

            // Official factory: PlcTableCommentEntryComposition.Create() - no arguments (2.7.49)
            var entry = TryInvokeMethodByName(entries, "Create") ?? TryInvokeMethodByName(entries, "Create", address);
            created = entry != null;
            return entry;
        }

        private static string WatchEntryAddressAttribute(string address) => PlcBlockServicesLogic.WatchEntryAddressAttribute(address);

        private static void SetWatchEntryAttribute(object entry, string name, object? value, JsonObject refused)
        {
            if (entry is not IEngineeringObject engineeringObject) { refused[name] = "entry is not an IEngineeringObject"; return; }
            try
            {
                object? typedValue = value;
                if (name is "ModifyTrigger" or "MonitorTrigger" && value is string triggerName)
                {
                    var enumType = typeof(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTablePreDefinedTrigger);
                    try { typedValue = Enum.Parse(enumType, triggerName, true); }
                    catch (ArgumentException) { refused[name] = "unknown trigger '" + triggerName + "'; valid: " + string.Join("/", Enum.GetNames(enumType)); return; }
                }
                engineeringObject.SetAttribute(name, typedValue);
            }
            catch (Exception ex) { refused[name] = ex.Message; }
        }

        private static JsonObject ReadWatchEntryAttributes(object entry, params string[] names)
        {
            var o = new JsonObject();
            if (entry is not IEngineeringObject engineeringObject) return o;
            foreach (var name in names)
            {
                try { var v = engineeringObject.GetAttribute(name); o[name] = v == null ? null : JsonValue.Create(Convert.ToString(v)); }
                catch { /* attribute not on this entry kind */ }
            }
            return o;
        }

        private static object? TryInvokeMethodByName(object target, string methodName, params object?[] args)
        {
            try
            {
                var method = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
                return method?.Invoke(target, args);
            }
            catch { return null; }
        }

        private static void SetEnumPropertyByName(object target, string propertyName, string valueName)
        {
            try
            {
                var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.PropertyType.IsEnum) return;
                var enumValue = Enum.Parse(prop.PropertyType, valueName, ignoreCase: true);
                prop.SetValue(target, enumValue);
            }
            catch { }
        }

        // ── Watch Table Current Values (read-only) ────────────────────────────

        public ModelContextProtocol.ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
        {
            var data = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["softwarePath"] = softwarePath,
                ["watchTableName"] = watchTableName,
                ["safety"] = new JsonObject
                {
                    ["readOnly"] = true,
                    ["modifiesWatchTables"] = false,
                    ["writesValues"] = false,
                    ["usesForce"] = false
                }
            };

            try
            {
                if (IsProjectNull())
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "Project is null. Attach to the open project first.", Data = data };

                var plc = GetPlcSoftware(softwarePath);
                if (plc == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found at '" + softwarePath + "'", Data = data };

                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data };

                var tables = EnumeratePlcWatchTables(group);
                data["watchTables"] = new JsonArray(tables.Select(x => JsonValue.Create(x.Path)).ToArray());
                var table = tables.FirstOrDefault(x =>
                    string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
                if (table == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "Watch table not found.", Data = data };

                data["tableType"] = table.GetType().FullName ?? table.GetType().Name;
                data["tableMembers"] = new JsonArray(DescribeMembers(table, 220).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());
                var entries = TryGetPropertyValue(table, "Entries", "WatchTableEntries", "Rows", "Items");
                data["entriesCollectionType"] = entries?.GetType().FullName ?? "";
                var rows = new JsonArray();
                if (entries is IEnumerable enumerable && entries is not string)
                {
                    foreach (var entry in enumerable)
                    {
                        if (entry == null) continue;
                        rows.Add(ReadWatchTableEntryReadOnly(entry, rows.Count == 0));
                        if (rows.Count >= Math.Max(1, maxEntries)) break;
                    }
                }

                data["entries"] = rows;
                data["entryCountRead"] = rows.Count;
                data["currentValueReadOk"] = rows.OfType<JsonObject>().Any(x => x["currentValue"] != null || x["monitorValue"] != null || x["value"] != null);
                data["evidence"] = data["currentValueReadOk"]?.GetValue<bool>() == true
                    ? "online-current-value-read"
                    : "No explicit current/monitor value property was readable from the public watch-table API.";
                return new ModelContextProtocol.ResponseJsonReport
                {
                    Ok = data["currentValueReadOk"]?.GetValue<bool>() == true,
                    Message = data["currentValueReadOk"]?.GetValue<bool>() == true ? "Read current values from existing watch table without writes." : "Watch table was read, but no current value property was exposed.",
                    Data = data
                };
            }
            catch (Exception ex)
            {
                data["error"] = FormatExceptionDetail(ex);
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        public ModelContextProtocol.ResponseJsonReport ProbePlcMonitorOnlineCapabilities(string softwarePath)
        {
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "read-only-probe",
                ["safety"] = "No online/offline transition, no watch-table modification, no value write, and no force-table operation is executed by this probe."
            };

            var warnings = new JsonArray();
            var members = new JsonArray();
            var services = new JsonArray();

            try
            {
                var plc = GetPlcSoftware(softwarePath);
                if (plc == null)
                {
                    data["warnings"] = new JsonArray("PLC software not found.");
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found", Data = data };
                }

                data["plcType"] = plc.GetType().FullName ?? plc.GetType().Name;
                foreach (var m in DescribeMembers(plc, 800))
                {
                    var name = m.Name ?? "";
                    if (name.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    if (name.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Offline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        members.Add($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}");
                    }
                }

                var likelyServiceSuffixes = new[]
                {
                    "OnlineProvider",
                    "OnlineService",
                    "DownloadProvider",
                    "PlcOnlineProvider",
                    "WatchTableProvider"
                };

                foreach (var suffix in likelyServiceSuffixes)
                {
                    var st = FindTypeBySuffix(suffix);
                    if (st == null)
                    {
                        services.Add(new JsonObject { ["suffix"] = suffix, ["status"] = "type-not-found" });
                        continue;
                    }

                    var svc = TryGetService(plc, st);
                    services.Add(new JsonObject
                    {
                        ["suffix"] = suffix,
                        ["type"] = st.FullName ?? st.Name,
                        ["status"] = svc == null ? "not-available" : "available",
                        ["serviceType"] = svc?.GetType().FullName ?? ""
                    });
                }

                var watchTables = GetPlcWatchTables(softwarePath) ?? new List<string>();
                data["watchTables"] = new JsonArray(watchTables.Select(x => JsonValue.Create(x)).ToArray());
                data["matchingMembers"] = members;
                data["serviceProbe"] = services;
                warnings.Add("Online value monitoring is not executed by this tool. It only probes read-only API surfaces for a later separately verified current-value read workflow.");
                warnings.Add("Force-table APIs are intentionally excluded by product safety policy.");
                data["warnings"] = warnings;

                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = "PLC monitor/online capability probe completed", Data = data };
            }
            catch (Exception ex)
            {
                data["error"] = ex.ToString();
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        private static object? ResolvePlcWatchAndForceTableGroup(object plc)
        {
            // 只解析“监控与强制表”的容器对象；后续只读取 WatchTables，不读取 ForceTables。
            // TIA V21 的真实属性名通常是 WatchAndForceTableGroup，早期猜测的 WatchTables 不覆盖这个层级。
            return TryGetPropertyValue(
                plc,
                "WatchAndForceTableGroup",
                "WatchAndForceTables",
                "WatchAndForceTableSystemGroup",
                "WatchTableGroup");
        }

        private static List<(string Name, string Path, object Table)> EnumeratePlcWatchTables(object group)
        {
            var result = new List<(string Name, string Path, object Table)>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            EnumeratePlcWatchTablesRecursive(group, "", result, visited);
            return result;
        }

        private static void EnumeratePlcWatchTablesRecursive(object group, string groupPath, List<(string Name, string Path, object Table)> result, HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var watchTables = TryGetPropertyValue(group, "WatchTables", "PlcWatchTables", "Tables");
            if (watchTables is System.Collections.IEnumerable tableEnumerable && watchTables is not string)
            {
                foreach (var table in tableEnumerable)
                {
                    if (table == null) continue;
                    var name = TryGetName(table) ?? TryGetPropertyValue(table, "Name")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var path = string.IsNullOrWhiteSpace(groupPath) ? name! : groupPath + "/" + name;
                    result.Add((name!, path, table));
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "WatchAndForceTableGroups", "UserGroups");
            if (groups is System.Collections.IEnumerable groupEnumerable && groups is not string)
            {
                foreach (var child in groupEnumerable)
                {
                    if (child == null) continue;
                    var childName = TryGetName(child) ?? TryGetPropertyValue(child, "Name")?.ToString();
                    var childPath = string.IsNullOrWhiteSpace(childName)
                        ? groupPath
                        : string.IsNullOrWhiteSpace(groupPath) ? childName! : groupPath + "/" + childName;
                    EnumeratePlcWatchTablesRecursive(child, childPath, result, visited);
                }
            }
        }

        private static JsonObject ReadWatchTableEntryReadOnly(object entry, bool includeMembers)
        {
            var row = new JsonObject
            {
                ["type"] = entry.GetType().FullName ?? entry.GetType().Name
            };

            foreach (var propName in new[] { "Name", "Address", "DisplayFormat", "Comment", "DataType", "Value", "CurrentValue", "ActualValue", "MonitorValue", "OnlineValue", "Status" })
            {
                var value = TryGetPropertyValue(entry, propName);
                if (value != null)
                {
                    var key = propName switch
                    {
                        "CurrentValue" => "currentValue",
                        "ActualValue" => "currentValue",
                        "MonitorValue" => "monitorValue",
                        "OnlineValue" => "currentValue",
                        "Value" => "value",
                        _ => char.ToLowerInvariant(propName[0]) + propName.Substring(1)
                    };
                    row[key] = value.ToString();
                }
            }

            var attributes = new JsonObject();
            var methods = entry.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var infos = getInfos?.Invoke(entry, Array.Empty<object>()) as IEnumerable;
            if (infos != null && getAttr != null)
            {
                foreach (var info in infos)
                {
                    var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var lower = name.ToLowerInvariant();
                    if (!new[] { "name", "address", "display", "value", "actual", "current", "monitor", "online", "status", "comment", "type" }.Any(lower.Contains))
                        continue;
                    try
                    {
                        var value = getAttr.Invoke(entry, new object[] { name });
                        attributes[name] = value?.ToString() ?? "";
                    }
                    catch { }
                }
            }

            if (attributes.Count > 0)
                row["attributes"] = attributes;
            if (includeMembers)
                row["members"] = new JsonArray(DescribeMembers(entry, 180).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());

            return row;
        }

        #endregion
    }
}

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

using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcTablesService
    {
        private readonly IEngineeringSession _session;

        public PlcTablesService(IEngineeringSession session) => _session = session;

        public ResponseImportBatch ImportPlcTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (_session.IsProjectNull())
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

                    try { _session.ImportPlcTagTable(softwarePath, folderPath, file); imported.Add(name); }
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
            if (_session.IsProjectNull()) return null;
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null)
            {
                return _session.TryListNamesFromCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, "WatchTables");
            }

            return EnumeratePlcWatchTables(group)
                .Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool ExportPlcWatchTable(string softwarePath, string watchTableName, string exportPath)
        {
            if (_session.IsProjectNull()) return false;
            var plc = _session.GetPlcSoftware(softwarePath);
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
                table = _session.TryFindByNameInCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, watchTableName);
            }

            if (table == null) return false;
            return _session.TryExportEngineeringObject(table, exportPath, out _);
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
                    failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found" + _session.AvailablePlcPathsSuffix() });
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
                    var outPath = Path.Combine(dir, _session.MakeSafeFileName(name) + ".xml");
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
            if (_session.IsProjectNull()) return null;
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null) return new List<string>();

            var result = new List<string>();
            var visited = new HashSet<object>(_session.ReferenceEqualityComparer);
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

        // TIA V21 native evidence (2026-09-21): PlcWatchTable.Entries.Create() only yields a comment row and the typed entry properties are
        // read-only, so no Openness call adds a tag row. The official route is the SimaticML round trip: export the table, add or
        // update the row in the XML, import it back with ImportOptions.Override into the table's own group, then read the row back.
        // Keep group-qualified lookup and readback; see docs/reference/real-machine-ledger.md for the native evidence.
        public ResponseMessage EnsureWatchTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string modifyValue,
            string trigger = "Permanent")
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(address)) return new ResponseMessage { Message = "tableName and address are required." };
            var triggerType = typeof(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTablePreDefinedTrigger);
            object triggerValue;
            try { triggerValue = Enum.Parse(triggerType, (trigger ?? "").Trim(), true); }
            catch (ArgumentException) /* swallow(parse-fallback): Invalid trigger names return the existing list of allowed values. */ { return new ResponseMessage { Message = "trigger must be one of: " + string.Join("/", Enum.GetNames(triggerType)) + " (case-sensitive)." }; }
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
                    doc = WatchTableEntryXml.NewTable(resolvedPath.Contains("/") ? resolvedPath.Substring(resolvedPath.LastIndexOf('/') + 1) : resolvedPath, _session.PortalMajorVersion);
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
                // envelope: legacy-single-verdict
                meta["success"] = valueOk && triggerOk;   // The bridge reads Meta.success to determine operationStatus.
                if (!valueOk || !triggerOk)
                    return new ResponseMessage { Message = $"Watch table '{resolvedPath}': row for '{address}' {action}, but the readback shows ModifyValue='{hit.ModifyValue}' Trigger={hit.ModifyTrigger} (requested '{modifyValue}' / {triggerName}).", Meta = meta };
                return new ResponseMessage { Message = $"Watch table '{resolvedPath}': row for '{address}' {action} with ModifyValue='{modifyValue}' Trigger={triggerName} (readback verified; {rows.Length} rows).", Meta = meta };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "EnsureWatchTableEntry failed");
                meta["success"] = false;
                meta["error"] = ex.Message;
                return new ResponseMessage { Message = $"Error: {ex.Message}", Meta = meta };
            }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { /* swallow(cleanup): Temporary watch-table XML cleanup must not replace the edit result. */ }
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
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };

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
                _session.Logger?.LogError(ex, "EnsureForceTableEntry failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        private object? FindOrCreateWatchTable(object group, string tableName)
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
            return _session.TryInvokeMethodByName(watchTables, "Create", tableName);
        }

        private object? FindOrCreateForceTable(object group, string tableName)
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

            return _session.TryInvokeMethodByName(forceTables, "Create", tableName);
        }

        private object? FindOrCreateTableEntry(object table, string entriesPropertyName, string address, out bool created)
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

            // Official factory: PlcTableCommentEntryComposition.Create() - no arguments.
            var entry = _session.TryInvokeMethodByName(entries, "Create") ?? _session.TryInvokeMethodByName(entries, "Create", address);
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
                    catch (ArgumentException) /* swallow(parse-fallback): Invalid trigger names are reported in refused attributes without attempting a write. */ { refused[name] = "unknown trigger '" + triggerName + "'; valid: " + string.Join("/", Enum.GetNames(enumType)); return; }
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
                catch { /* swallow(probe-optional): Attributes absent from this entry kind are omitted from the readback. */ }
            }
            return o;
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
            catch { /* swallow(probe-optional): Unsupported optional enum properties leave the object unchanged. */ }
        }

        // ── Watch Table Current Values (read-only) ────────────────────────────

        public ModelContextProtocol.ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
        {
            // envelope: legacy-roundtrip-data-stamp
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
                if (_session.IsProjectNull())
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "Project is null. Attach to the open project first.", Data = data };

                var plc = _session.GetPlcSoftware(softwarePath);
                if (plc == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found at '" + softwarePath + "'" + _session.AvailablePlcPathsSuffix(), Data = data };

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
                data["tableMembers"] = new JsonArray(_session.DescribeMembers(table, 220).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());
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
                data["error"] = _session.FormatExceptionDetail(ex);
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
                var plc = _session.GetPlcSoftware(softwarePath);
                if (plc == null)
                {
                    data["warnings"] = new JsonArray("PLC software not found." + _session.AvailablePlcPathsSuffix());
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found" + _session.AvailablePlcPathsSuffix(), Data = data };
                }

                data["plcType"] = plc.GetType().FullName ?? plc.GetType().Name;
                foreach (var m in _session.DescribeMembers(plc, 800))
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
                    var st = _session.FindTypeBySuffix(suffix);
                    if (st == null)
                    {
                        services.Add(new JsonObject { ["suffix"] = suffix, ["status"] = "type-not-found" });
                        continue;
                    }

                    var svc = _session.TryGetService(plc, st);
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

        private List<(string Name, string Path, object Table)> EnumeratePlcWatchTables(object group)
        {
            var result = new List<(string Name, string Path, object Table)>();
            var visited = new HashSet<object>(_session.ReferenceEqualityComparer);
            EnumeratePlcWatchTablesRecursive(group, "", result, visited);
            return result;
        }

        private void EnumeratePlcWatchTablesRecursive(object group, string groupPath, List<(string Name, string Path, object Table)> result, HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var watchTables = TryGetPropertyValue(group, "WatchTables", "PlcWatchTables", "Tables");
            if (watchTables is System.Collections.IEnumerable tableEnumerable && watchTables is not string)
            {
                foreach (var table in tableEnumerable)
                {
                    if (table == null) continue;
                    var name = _session.TryGetName(table) ?? TryGetPropertyValue(table, "Name")?.ToString();
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
                    var childName = _session.TryGetName(child) ?? TryGetPropertyValue(child, "Name")?.ToString();
                    var childPath = string.IsNullOrWhiteSpace(childName)
                        ? groupPath
                        : string.IsNullOrWhiteSpace(groupPath) ? childName! : groupPath + "/" + childName;
                    EnumeratePlcWatchTablesRecursive(child, childPath, result, visited);
                }
            }
        }

        private JsonObject ReadWatchTableEntryReadOnly(object entry, bool includeMembers)
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
                    catch { /* swallow(probe-optional): Unreadable optional watch attributes are omitted while other attributes remain available. */ }
                }
            }

            if (attributes.Count > 0)
                row["attributes"] = attributes;
            if (includeMembers)
                row["members"] = new JsonArray(_session.DescribeMembers(entry, 180).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());

            return row;
        }



        public ModelContextProtocol.ResponseJsonReport MonitorWatchTableLiveS7(
            string softwarePath, string watchTableName, string ip, int rack = 0, int slot = 1, string expectModuleContains = "")
        {
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["watchTableName"] = watchTableName,
                ["ip"] = ip,
                ["channel"] = "watch-table (Openness, read-only) + S7 live read",
                ["safety"] = new JsonObject { ["readOnly"] = true, ["modifiesWatchTables"] = false, ["writesValues"] = false, ["usesForce"] = false }
            };

            if (_session.IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open. Attach first.", Data = data };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Data = data };

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data };

            var tables = EnumeratePlcWatchTables(group);
            data["availableWatchTables"] = new JsonArray(tables.Select(x => JsonValue.Create(x.Name)).ToArray());
            var table = tables.FirstOrDefault(x =>
                string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
            if (table == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"Watch table '{watchTableName}' not found.", Data = data };

            // Extract entry rows (address + name + display format).
            var rows = new List<(string name, string address, string fmt)>();
            var entries = TryGetPropertyValue(table, "Entries", "WatchTableEntries", "Rows", "Items");
            if (entries is IEnumerable en && !(entries is string))
            {
                foreach (var e in en)
                {
                    if (e == null) continue;
                    string addr = ReadEntryAttr(e, "Address");
                    string name = ReadEntryAttr(e, "Name").Trim().Trim('"');
                    string fmt = ReadEntryAttr(e, "DisplayFormat", "MonitorDisplayFormat", "Format");
                    if (string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(name)) continue;
                    rows.Add((name, addr, fmt));
                }
            }
            data["entryCount"] = rows.Count;

            if (rows.Count == 0)
            {
                data["note"] = "This watch table exposed no entries through Openness. It may be empty, or Openness cannot read rows authored in the TIA UI (known limitation). Read the values directly with ReadPlcLiveValuesS7 using explicit addresses.";
                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = "Watch table has 0 readable entries via Openness.", Data = data };
            }

            // Build S7 specs for resolvable absolute addresses.
            var specs = new List<string>();
            var specToRow = new Dictionary<string, (string name, string address)>();
            var unresolved = new JsonArray();
            foreach (var r in rows)
            {
                string? spec = S7LiveReader.TiaAddressToSpec(r.address, r.fmt);
                if (spec == null)
                {
                    unresolved.Add(new JsonObject { ["name"] = r.name, ["address"] = r.address, ["reason"] = "symbolic / optimized / no absolute address — use OPC UA" });
                    continue;
                }
                if (!specToRow.ContainsKey(spec)) { specs.Add(spec); specToRow[spec] = (r.name, r.address); }
            }

            var outRows = new JsonArray();
            long elapsed = 0;
            if (specs.Count > 0)
            {
                var read = S7LiveReader.ReadItems(ip, rack, slot, specs,
                    string.IsNullOrWhiteSpace(expectModuleContains) ? null : expectModuleContains);
                elapsed = read.ElapsedMs;
                data["identity"] = new JsonObject
                {
                    ["moduleTypeName"] = read.Identity.ModuleTypeName,
                    ["szlError"] = read.Identity.SzlError
                };
                if (read.Error != null) data["readError"] = read.Error;
                foreach (var it in read.Items)
                {
                    string rowName = "", rowAddr = it.Spec;
                    if (specToRow.TryGetValue(it.Spec, out var ri)) { rowName = ri.name; rowAddr = ri.address; }
                    var o = new JsonObject { ["name"] = rowName, ["address"] = rowAddr, ["spec"] = it.Spec, ["type"] = it.Type };
                    if (it.Error != null) o["error"] = it.Error;
                    else o["value"] = JsonValue.Create(it.Value);
                    outRows.Add(o);
                }
            }

            data["values"] = outRows;
            data["unresolved"] = unresolved;
            data["elapsedMs"] = elapsed;

            bool ok = specs.Count > 0 && outRows.OfType<JsonObject>().All(o => o["error"] == null);
            return new ModelContextProtocol.ResponseJsonReport
            {
                Ok = ok,
                Message = ok
                    ? $"Monitored {outRows.Count} live value(s) from watch table '{watchTableName}' in {elapsed} ms ({unresolved.Count} unresolved)."
                    : $"Watch table '{watchTableName}': {rows.Count} entries, {specs.Count} absolute, {unresolved.Count} unresolved. See values/unresolved.",
                Data = data,
                Meta = ResponseMeta.Basic(DateTime.Now, ok)
            };
        }

        private static string ReadEntryAttr(object entry, params string[] names)
        {
            // Try a property first, then GetAttribute(name).
            var prop = TryGetPropertyValue(entry, names);
            if (prop != null) return prop.ToString() ?? "";
            var getAttr = entry.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getAttr != null)
            {
                foreach (var n in names)
                {
                    try { var v = getAttr.Invoke(entry, new object[] { n }); if (v != null) return v.ToString() ?? ""; }
                    catch { /* swallow(probe-optional): An unavailable entry attribute falls through to the remaining candidate names. */ }
                }
            }
            return "";
        }


        public ResponseMessage ImportPlcWatchTableOffline(string softwarePath, string filePath, string groupPath = "", bool dryRun = true)
            => _session.RunHmiStepTool("ImportPlcWatchTableOffline", meta => {
                var source = new FileInfo(filePath); if (!source.Exists) throw new FileNotFoundException("Watch table XML missing.", filePath);
                meta["expectedCount"] = WatchTableImportValidation.Validate(source.FullName);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                var group = (PlcWatchAndForceTableGroup)EngineeringGroupOperations.Group(plc.WatchAndForceTableGroup, groupPath);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    var imported = group.WatchTables.Import(source, ImportOptions.None).ToArray();
                    meta["actualCount"] = imported.Length;
                    meta["names"] = new JsonArray(imported.Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray());
                    meta["dataComplete"] = imported.Length == meta["expectedCount"]!.GetValue<int>();
                    if (!meta["dataComplete"]!.GetValue<bool>()) throw new InvalidOperationException("Import returned a different number of watch tables; inspect project before retry.");
                }
                return dryRun ? "Offline watch table import preview. No online monitoring or force operations." : "Offline watch table import completed without overwrite, save, online action or download.";
            });

        // ---- tag table constants ----------------------------------------------------------------------------------------------------------
        private static JsonObject ConstantRow(PlcConstant constant, string kind) => new JsonObject { ["name"] = constant.Name, ["dataTypeName"] = constant.DataTypeName, ["value"] = constant.Value, ["kind"] = kind, ["constantClass"] = constant.GetType().Name };
        public ResponseMessage ReadPlcTagTableConstants(string softwarePath, string tablePath, string kind = "all", string unitName = "", string unitKind = "unit", int offset = 0, int limit = 200)
            => _session.RunHmiStepTool("ReadPlcTagTableConstants", meta => {
                PlcTableRules.ValidateConstantRequest(tablePath, kind, unitName, unitKind, offset, limit);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcTagTableSystemGroup root = unit == null ? plc.TagTableGroup : unit.TagTableGroup;
                var table = (PlcTagTable)_session.ExactObjectUnder(root, tablePath, "TagTables", "PLC tag table");
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
            => _session.RunHmiStepTool("ManagePlcTableEntries", meta => {
                bool writing = PlcTableRules.ValidateTableEntryRequest(tableKind, tablePath, action, entryIndex, confirmDelete, dryRun, offset, limit);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                PlcWatchAndForceTableGroup root = plc.WatchAndForceTableGroup;
                PlcTableCommentEntryComposition entries; string tableName; bool consistent; PlcWatchTable? watchTable = null;
                if (tableKind == "watch") { var table = (PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table"); watchTable = table; entries = table.Entries; tableName = table.Name; consistent = table.IsConsistent; }
                else { var table = (PlcForceTable)_session.ExactObjectUnder(root, tablePath, "ForceTables", "force table"); entries = table.Entries; tableName = table.Name; consistent = table.IsConsistent; }
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["table"] = new JsonObject { ["name"] = tableName, ["kind"] = tableKind, ["isConsistent"] = consistent, ["entryCount"] = entries.Count };
                var all = EngineeringGroupOperations.Items(entries).Cast<PlcTableCommentEntry>().ToArray();
                if (action == "deleteTable")
                {
                    // Capture all rows before deleting the watch table and verify it is absent afterwards.
                    meta["before"] = new JsonArray(all.Select((e, i) => (JsonNode)TableEntryRow(e, i)).ToArray());
                    if (!writing) return "Watch table deletion preview (" + all.Length + " rows would go with it); no changes.";
                    meta["mayHaveChanged"] = true;
                    watchTable!.Delete(); meta["apiCallSuccess"] = true;
                    bool absent;
                    try { _session.ExactObjectUnder(plc.WatchAndForceTableGroup, tablePath, "WatchTables", "watch table"); absent = false; }
                    catch (PortalException) /* swallow(native-fallback): Failed exact lookup is the existing post-delete absence check. */ { absent = true; }
                    meta["verifiedAbsent"] = absent;
                    if (!absent) throw new InvalidOperationException("Watch table still resolvable after Delete().");
                    return "Watch table '" + tableName + "' deleted and verified absent; project not saved.";
                }
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
                    // TIA V21 native evidence (2026-09-19): the Create proxy can retain the old Count; count on a fresh navigation.
                    // See docs/reference/real-machine-ledger.md for the native evidence.
                    int after = EngineeringGroupOperations.Items(((PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table")).Entries).Count(); meta["entryCountAfter"] = after;
                    if (after != all.Length + 1) throw new InvalidOperationException("Entry count did not increase by one after Create (fresh readback).");
                    meta["after"] = TableEntryRow(created, after - 1);
                    return "Comment entry appended to the watch table and counted back; project not saved.";
                }
                if (entryIndex >= all.Length) throw new PortalException(PortalErrorCode.NotFound, "entryIndex " + entryIndex + " is outside 0.." + (all.Length - 1) + ".");
                meta["before"] = TableEntryRow(all[entryIndex], entryIndex);
                if (!writing) return "Entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                all[entryIndex].Delete(); meta["apiCallSuccess"] = true;
                int remaining = EngineeringGroupOperations.Items(((PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table")).Entries).Count(); meta["entryCountAfter"] = remaining;
                if (remaining != all.Length - 1) throw new InvalidOperationException("Entry count did not decrease by one after Delete.");
                return "Watch table entry deleted and counted back; project not saved.";
            });

    }
}

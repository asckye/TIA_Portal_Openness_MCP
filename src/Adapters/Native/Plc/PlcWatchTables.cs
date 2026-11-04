using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
#if PLC_WATCH_READ
using Siemens.Engineering.SW.WatchAndForceTables;
#endif
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
#if PLC_WATCH_READ
        public List<string>? GetPlcWatchTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = _session.GetPlcSoftware(softwarePath);
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
                table = WatchFindByNameInCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, watchTableName);
            }

            if (table == null) return false;
            if (table is global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable watch)
                RequireWatchExport(watch);
            return Equals(TechnologyExport(table, TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath))["success"], true);
        }

        public static void RequireWatchExport(PlcWatchTable watch)
            => TiaOpenness.Shared.NativeExportPolicy.RequireApply(watch.IsConsistent ? "planned" : "inconsistent");

        public PlcSeedHmiReply ExportPlcWatchTablesToDirectory(string softwarePath, string dir, string regexName = "")
        {
            if (!IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                _session.PlcSoftwarePath(_session.GetPlcSoftware(softwarePath)), true);

            var exported = new List<string>();
            var failed = new List<PlcSeedImportFailure>();

            try
            {
                var names = GetPlcWatchTables(softwarePath);
                if (names == null)
                {
                    failed.Add(new PlcSeedImportFailure { Path = softwarePath, Error = "PLC software not found" + _session.AvailablePlcPathsSuffix() });
                    return new PlcSeedHmiReply { Imported = exported, Failed = failed };
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
                    else failed.Add(new PlcSeedImportFailure { Path = name, Error = "Export failed" });
                }

                return new PlcSeedHmiReply { Imported = exported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new PlcSeedImportFailure { Path = dir, Error = ex.ToString() });
                return new PlcSeedHmiReply { Imported = exported, Failed = failed };
            }
        }

        // ── Force Tables ──────────────────────────────────────────────────────

        public List<string>? GetPlcForceTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
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

            var forceTables = WatchProperty(group, "ForceTables", "PlcForceTables");
            if (forceTables is IEnumerable ftEnum and not string)
            {
                foreach (var t in ftEnum)
                {
                    if (t == null) continue;
                    var name = WatchProperty(t, "Name")?.ToString() ?? string.Empty;
                    result.Add(string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name);
                }
            }

            var groups = WatchProperty(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = WatchProperty(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    CollectForceTableNames(sub, next, result, visited);
                }
            }
        }

        // TIA V21 native evidence (2026-09-21): PlcWatchTable.Entries.Create() only yields a comment row and the typed entry properties are
        // read-only, so no Openness call adds a tag row. The official route is the SimaticML round trip: export the table, add or
        // update the row in the XML, import it back with ImportOptions.Override into the table's own group, then read the row back.
        // Keep group-qualified lookup and readback; see docs/reference/real-machine-ledger.md for the native evidence.
#if PLC_WATCH_ENTRIES
        public HardwareAddressingReply EnsureWatchTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string modifyValue,
            string trigger = "Permanent")
        {
            if (IsProjectNull()) return new HardwareAddressingReply { Message = "No project open.", Meta = WatchMeta(false, "PROJECT_NOT_BOUND") };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new HardwareAddressingReply {
                Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = WatchMeta(false, "NOT_FOUND") };
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(address)) return new HardwareAddressingReply {
                Message = "tableName and address are required.", Meta = WatchMeta(false, "INVALID_ARGUMENT") };
            var triggerType = typeof(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTablePreDefinedTrigger);
            object triggerValue;
            try { triggerValue = Enum.Parse(triggerType, (trigger ?? "").Trim(), true); }
            catch (ArgumentException) /* swallow(parse-fallback): Invalid trigger names return the existing list of allowed values. */ { return new HardwareAddressingReply { Message = "trigger must be one of: " + string.Join("/", Enum.GetNames(triggerType)) + " (case-sensitive).", Meta = WatchMeta(false, "INVALID_ARGUMENT") }; }
            var triggerName = triggerValue.ToString();

            var meta = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath, ["tableName"] = tableName, ["address"] = address, ["modifyValue"] = modifyValue, ["trigger"] = triggerName,
                ["mayHaveChanged"] = false, ["readbackVerified"] = false,
                ["method"] =
                    "SimaticML round trip: PlcWatchTable.Export -> XML upsert -> PlcWatchTableComposition.Import(ImportOptions.Override) (the typed API " +
                    "cannot create tag rows)"
            };
            string? tempFile = null;
            try
            {
                var root = WatchNative.WatchGroup(plc);
                var (table, group, resolvedPath, ambiguity) = ResolveWatchTable(root, tableName);
                if (ambiguity != null) return new HardwareAddressingReply { Message = ambiguity, Meta = meta };
                meta["resolvedTablePath"] = resolvedPath;
                meta["tableExisted"] = table != null;

                XDocument doc;
                if (table != null)
                {
                    tempFile = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia-mcp-wt-" + Guid.NewGuid().ToString("N") + ".xml");
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    WatchNative.Export(table, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempFile)), ExportOptions.None);
                    doc = XDocument.Load(tempFile);
                    meta["entriesBefore"] = WatchTableEntryXml.Entries(doc).Count();
                }
                else
                {
                    doc = WatchTableEntryXml
                        .NewTable(resolvedPath.Contains("/") ? resolvedPath.Substring(resolvedPath.LastIndexOf('/') + 1) : resolvedPath, _session.ReleaseKey);
                    meta["entriesBefore"] = 0;
                }
                var action = WatchTableEntryXml.Upsert(doc, address, modifyValue, triggerName);
                meta["rowAction"] = action;
                tempFile ??= Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia-mcp-wt-" + Guid.NewGuid().ToString("N") + ".xml");
                doc.Save(tempFile);

                meta["mayHaveChanged"] = true; MutationStarted = true;
                var imported = WatchNative
                    .Import(WatchNative.WatchTables(group), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempFile)), ImportOptions.Override)
                    .ToArray();
                meta["importedCount"] = imported.Length;
                meta["apiCallSuccess"] = true;

                // readback from a fresh navigation (the old proxy is dead after Override)
                var (after, _, _, _) = ResolveWatchTable(WatchNative.WatchGroup(plc), resolvedPath);
                if (after == null) return new HardwareAddressingReply {
                    Message = $"Watch table '{resolvedPath}' is missing after the import; nothing verified.", Meta = meta };
                var rows = PlcGroupOperations.Items(WatchNative.Entries(after)).ToArray();
                meta["entriesAfter"] = rows.Length;
                global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry? hit = null;
                var attribute = WatchTableEntryXml.AttributeFor(address);
                foreach (var row in rows.OfType<global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry>())
                {
                    var key = attribute == "Address" ? WatchNative.Address(row) ?? "" : WatchNative.Name(row) ?? "";
                    if (attribute == "Address" ? string.Equals(key, address.Trim(), StringComparison.OrdinalIgnoreCase) : WatchTableEntryXml
                            .SameSymbol(key, address))
                        { hit = row; break; }
                }
                if (hit == null)
                    return new HardwareAddressingReply {
                        Message =
                            $"Watch table '{resolvedPath}' was re-imported ({rows.Length} rows) but no row for '{address}' came back; TIA may have rejected the row (check the address / symbol).",
                            Meta = meta
                    };
                meta["after"] = new Dictionary<string, object?> {
                    ["Name"] = WatchNative.Name(hit), ["Address"] = WatchNative.Address(hit), ["DisplayFormat"] = WatchNative.DisplayFormat(hit).ToString(),
                        ["ModifyValue"] = WatchNative.ModifyValue(hit), ["ModifyTrigger"] = WatchNative.ModifyTrigger(hit).ToString(), ["MonitorTrigger"] =
                        WatchNative.MonitorTrigger(hit).ToString(), ["ModifyIntention"] = WatchNative.ModifyIntention(hit)
                };
                bool valueOk = string.Equals(WatchNative.ModifyValue(hit) ?? "", modifyValue ?? "", StringComparison.OrdinalIgnoreCase);
                bool triggerOk = string.Equals(WatchNative.ModifyTrigger(hit).ToString(), triggerName, StringComparison.OrdinalIgnoreCase);
                meta["readbackVerified"] = valueOk && triggerOk;
                meta["note"] = "Value will be applied to the PLC when TIA Portal is online and the trigger fires. Project not saved.";
                // envelope: legacy-single-verdict
                meta["success"] = valueOk && triggerOk;   // The bridge reads Meta.success to determine operationStatus.
                if (!valueOk || !triggerOk)
                    return new HardwareAddressingReply {
                        Message =
                            $"Watch table '{resolvedPath}': row for '{address}' {action}, but the readback shows ModifyValue='{WatchNative.ModifyValue(hit)}' Trigger={WatchNative.ModifyTrigger(hit)} (requested '{modifyValue}' / {triggerName}).",
                            Meta = meta
                    };
                return new HardwareAddressingReply {
                    Message =
                        $"Watch table '{resolvedPath}': row for '{address}' {action} with ModifyValue='{modifyValue}' Trigger={triggerName} (readback verified; {rows.Length} rows).",
                        Meta = meta
                };
            }
            catch (Exception ex)
            {
                _session.LogEngineeringFailure(ex, "EnsureWatchTableEntry failed");
                meta["success"] = false;
                meta["error"] = ex.Message;
                return new HardwareAddressingReply { Message = $"Error: {ex.Message}", Meta = meta };
            }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { /* swallow(cleanup): Temporary watch-table XML cleanup must not replace the edit result. */ }
            }
        }

#else
        public HardwareAddressingReply EnsureWatchTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string modifyValue,
            string trigger = "Permanent")
            => throw new NotSupportedException("Typed watch-entry readback requires V17.");
#endif
        // Exact group-qualified path first ("MCP_W/MCP_WT"); a bare name is searched through every user group. Returns the table
        // (null when absent), the group that owns or would own it, the resolved path, and an ambiguity message when a bare name
        // exists in several groups.
        private static (global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable? Table, global::Siemens.Engineering.SW.WatchAndForceTables
                .PlcWatchAndForceTableGroup Group, string Path, string? Ambiguity)
            ResolveWatchTable(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup root, string tableName)
        {
            var wanted = tableName.Replace('\\', '/').Trim('/');
            var parts = PlcGroupOperations.Parts(wanted);
            var groupPath = string.Join("/", parts.Take(parts.Length - 1));
            var leaf = parts.Last();
            var group = (global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup)PlcGroupOperations.Group(root, groupPath);
            var direct = WatchNative.Find(WatchNative.WatchTables(group), leaf);
            if (direct != null || parts.Length > 1) return (direct, group, wanted, null);

            var hits =
                new List<(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable, global::Siemens.Engineering.SW.WatchAndForceTables
                    .PlcWatchAndForceTableGroup, string)>();
            void Walk(global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup g, string path)
            {
                foreach (var sub in PlcGroupOperations.Items(WatchNative.Groups(g)).Cast<global::Siemens.Engineering.SW.WatchAndForceTables
                        .PlcWatchAndForceTableUserGroup>())
                {
                    var subPath = path.Length == 0 ? WatchNative.Name(sub) : path + "/" + WatchNative.Name(sub);
                    var t = WatchNative.Find(WatchNative.WatchTables(sub), leaf);
                    if (t != null) hits.Add((t, sub, subPath + "/" + leaf));
                    Walk(sub, subPath);
                }
            }
            Walk(root, "");
            if (hits.Count == 1) return (hits[0].Item1, hits[0].Item2, hits[0].Item3, null);
            if (hits.Count > 1)
                return (null, group, wanted,
                    $"'{leaf}' exists in {hits.Count} groups ({string.Join(", ", hits.Select(h => h.Item3))}); give the group-qualified path.");
            return (null, group, wanted, null);
        }

        public HardwareAddressingReply ImportPlcWatchTableOffline(string softwarePath, string filePath, string groupPath = "", bool dryRun = true)
            => _session.RunHmiStepTool("ImportPlcWatchTableOffline", meta => {
                var source = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (!source.Exists)
                    throw new FileNotFoundException("Watch table XML missing.", filePath);
                meta["expectedCount"] = WatchTableImportValidation.Validate(source.FullName);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                var group = (PlcWatchAndForceTableGroup)PlcGroupOperations.Group(WatchNative.WatchGroup(plc), groupPath);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    var imported = WatchNative.Import(WatchNative.WatchTables(group), source, ImportOptions.None).ToArray();
                    meta["actualCount"] = imported.Length;
                    meta["names"] = new List<object?>(imported.Select(x => (object?)WatchNative.Name(x)).ToArray());
                    meta["dataComplete"] = imported.Length == (int)meta["expectedCount"]!;
                    if (!Equals(meta["dataComplete"], true))
                        throw new InvalidOperationException("Import returned a different number of watch tables; inspect project before retry.");
                }
                return dryRun ? "Offline watch table import preview. No online monitoring or force operations."
                    : "Offline watch table import completed without overwrite, save, online action or download.";
            });

        // ---- tag table constants ----------------------------------------------------------------------------------------------------------




        // ---- watch / force table entries ------------------------------------------------------------------------------------------------------
        private static Dictionary<string, object?> TableEntryRow(PlcTableCommentEntry entry, int index)
        {
            var row = new Dictionary<string, object?> { ["index"] = index, ["entryClass"] = entry.GetType().Name };
            switch (entry)
            {
#if PLC_WATCH_ENTRIES
                case PlcWatchTableEntry w:
                    row["kind"] = "watch"; row["name"] = WatchNative.Name(w); row["address"] = WatchNative.Address(w); row["displayFormat"] = WatchNative
                        .DisplayFormat(w).ToString(); row["monitorTrigger"] = WatchNative.MonitorTrigger(w).ToString();
                    row["modifyTrigger"] = WatchNative.ModifyTrigger(w).ToString(); row["modifyValue"] = WatchNative.ModifyValue(w); row["modifyIntention"] =
                        WatchNative.ModifyIntention(w); break;
                case PlcForceTableEntry f:
                    row["kind"] = "force"; row["name"] = WatchNative.Name(f); row["address"] = WatchNative.Address(f); row["displayFormat"] = WatchNative
                        .DisplayFormat(f).ToString(); row["monitorTrigger"] = WatchNative.MonitorTrigger(f).ToString();
                    row["forceValue"] = WatchNative.ForceValue(f); row["forceIntention"] = WatchNative.ForceIntention(f); break;
#endif
                default: row["kind"] = "comment"; break;
            }
            return row;
        }
        public HardwareAddressingReply ManagePlcTableEntries(string softwarePath, string tableKind, string tablePath, string action = "read", int entryIndex =
                -1, bool confirmDelete = false, bool dryRun = true, int offset = 0, int limit = 200)
            => _session.RunHmiStepTool("ManagePlcTableEntries", meta => {
                bool writing = PlcWatchPolicy.ValidateTableEntryRequest(tableKind, tablePath, action, entryIndex, confirmDelete, dryRun, offset, limit);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                PlcWatchAndForceTableGroup root = WatchNative.WatchGroup(plc);
                PlcTableCommentEntryComposition entries; string tableName; bool consistent; PlcWatchTable? watchTable = null;
                if (tableKind == "watch") {
                    var table = (PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table"); watchTable = table; entries =
                        WatchNative.Entries(table); tableName = WatchNative.Name(table); consistent = WatchNative.IsConsistent(table);
                }
                else {
                    var table = (PlcForceTable)_session.ExactObjectUnder(root, tablePath, "ForceTables", "force table"); entries = WatchNative.Entries(table);
                    tableName = WatchNative.Name(table); consistent = WatchNative.IsConsistent(table);
                }
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["table"] = new Dictionary<string, object?> {
                    ["name"] = tableName, ["kind"] = tableKind, ["isConsistent"] = consistent, ["entryCount"] = WatchNative.Count(entries) };
                var all = PlcGroupOperations.Items(entries).Cast<PlcTableCommentEntry>().ToArray();
                if (action == "deleteTable")
                {
                    // Capture all rows before deleting the watch table and verify it is absent afterwards.
                    meta["before"] = new List<object?>(all.Select((e, i) => (object?)TableEntryRow(e, i)).ToArray());
                    if (!writing) return "Watch table deletion preview (" + all.Length + " rows would go with it); no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    WatchNative.Delete(watchTable!); meta["apiCallSuccess"] = true;
                    bool absent;
                    try { _session.ExactObjectUnder(WatchNative.WatchGroup(plc), tablePath, "WatchTables", "watch table"); absent = false; }
                    catch (PlcSoftwareException) /* swallow(native-fallback): Failed exact lookup is the existing post-delete absence check. */ { absent = true; }
                    meta["verifiedAbsent"] = absent;
                    if (!absent) throw new InvalidOperationException("Watch table still resolvable after Delete().");
                    return "Watch table '" + tableName + "' deleted and verified absent; project not saved.";
                }
                if (action == "read")
                {
                    Page(all.Select((e, i) => (object?)TableEntryRow(e, i)).ToArray(), offset, limit, meta);
                    meta["apiCallSuccess"] = true;
                    meta["scope"] =
                        "PlcWatchTableEntry / PlcForceTableEntry scalars and comment rows (PlcTableCommentEntry) in native order; values are configuration, " +
                        "not online data (GetPlcWatchTableCurrentValuesReadOnly).";
                    return "Table entries read; no modification.";
                }
                if (action == "createComment")
                {
                    if (!writing) return "Comment entry creation preview; no changes.";
                    meta["mayHaveChanged"] = true; MutationStarted = true;
                    PlcTableCommentEntry created = WatchNative.Create(entries); meta["apiCallSuccess"] = true;
                    // TIA V21 native evidence (2026-09-19): the Create proxy can retain the old Count; count on a fresh navigation.
                    // See docs/reference/real-machine-ledger.md for the native evidence.
                    int after = PlcGroupOperations
                        .Items(WatchNative.Entries((PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table"))).Count();
                    meta["entryCountAfter"] = after;
                    if (after != all.Length + 1) throw new InvalidOperationException("Entry count did not increase by one after Create (fresh readback).");
                    meta["after"] = TableEntryRow(created, after - 1);
                    return "Comment entry appended to the watch table and counted back; project not saved.";
                }
                if (entryIndex >= all.Length)
                    throw new PlcSoftwareException("NotFound", "entryIndex " + entryIndex + " is outside 0.." + (all.Length - 1) + ".");
                meta["before"] = TableEntryRow(all[entryIndex], entryIndex);
                if (!writing) return "Entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true; MutationStarted = true;
                WatchNative.Delete(all[entryIndex]); meta["apiCallSuccess"] = true;
                int remaining = PlcGroupOperations
                    .Items(WatchNative.Entries((PlcWatchTable)_session.ExactObjectUnder(root, tablePath, "WatchTables", "watch table"))).Count();
                meta["entryCountAfter"] = remaining;
                if (remaining != all.Length - 1) throw new InvalidOperationException("Entry count did not decrease by one after Delete.");
                return "Watch table entry deleted and counted back; project not saved.";
            });

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static Dictionary<string, object?> WatchMeta(bool success, string? rejection = null)
        {
            return LegacyResponseMetadata.Basic(DateTime.Now, success, rejection);
        }
#endif
    }
}

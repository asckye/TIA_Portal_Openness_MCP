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

#if TIA_SHARED_ADAPTER_PATHS
using Native = TiaMcp.Adapters.WatchTechnologyPrimitives;
#else
using Native = TiaMcpServer.Siemens.LocalWatchTechnology.WatchTechnologyPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcTablesService
    {
        private readonly IEngineeringSession _session;

        public PlcTablesService(IEngineeringSession session) => _session = session;



        public List<string>? GetPlcWatchTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
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
                table = _session.TryFindByNameInCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, watchTableName);
            }

            if (table == null) return false;
            if (table is global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable watch)
                TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.RequireWatchExport(watch);
            return _session.TryExportEngineeringObject(table, TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath), out _);
        }



        // TIA V21 native evidence (2026-09-21): PlcWatchTable.Entries.Create() only yields a comment row and the typed entry properties are
        // read-only, so no Openness call adds a tag row. The official route is the SimaticML round trip: export the table, add or
        // update the row in the XML, import it back with ImportOptions.Override into the table's own group, then read the row back.
        // Keep group-qualified lookup and readback; see docs/reference/real-machine-ledger.md for the native evidence.
        public ResponseMessage EnsureForceTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string forceValue)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Basic(false, ("v4Rejection", "PROJECT_NOT_BOUND")) };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Basic(false, ("v4Rejection", "NOT_FOUND")) };

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
                Native.SetAttribute(engineeringObject, name, typedValue);
            }
            catch (Exception ex) { refused[name] = ex.Message; }
        }

        private static JsonObject ReadWatchEntryAttributes(object entry, params string[] names)
        {
            var o = new JsonObject();
            if (entry is not IEngineeringObject engineeringObject) return o;
            foreach (var name in names)
            {
                try { var v = Native.Attribute(engineeringObject, name); o[name] = v == null ? null : JsonValue.Create(Convert.ToString(v)); }
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

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
        public PlcWatchReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
        {
            // envelope: legacy-roundtrip-data-stamp
            var data = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["softwarePath"] = softwarePath,
                ["watchTableName"] = watchTableName,
                ["safety"] = new Dictionary<string, object?>
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
                    return new PlcWatchReport { Ok = false, Message = "Project is null. Attach to the open project first.", Data = data };

                var plc = _session.GetPlcSoftware(softwarePath);
                if (plc == null)
                    return new PlcWatchReport {
                        Ok = false, Message = "PLC software not found at '" + softwarePath + "'" + _session.AvailablePlcPathsSuffix(), Data = data };

                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null)
                    return new PlcWatchReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data };

                var tables = EnumeratePlcWatchTables(group);
                data["watchTables"] = new List<object?>(tables.Select(x => x.Path).ToArray());
                var table = tables.FirstOrDefault(x =>
                    string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
                if (table == null)
                    return new PlcWatchReport { Ok = false, Message = "Watch table not found.", Data = data };

                data["tableType"] = table.GetType().FullName ?? table.GetType().Name;
                data["tableMembers"] = new List<object?>(DescribeMembers(table, 220).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}").ToArray());
                var entries = WatchProperty(table, "Entries", "WatchTableEntries", "Rows", "Items");
                data["entriesCollectionType"] = entries?.GetType().FullName ?? "";
                var rows = new List<object?>();
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
                data["currentValueReadOk"] = rows.OfType<Dictionary<string, object?>>()
                    .Any(x => new[] { "currentValue", "monitorValue", "value" }.Any(k => x.TryGetValue(k, out var value) && value != null));
                data["evidence"] = Equals(data["currentValueReadOk"], true)
                    ? "online-current-value-read"
                    : "No explicit current/monitor value property was readable from the public watch-table API.";
                return new PlcWatchReport
                {
                    Ok = Equals(data["currentValueReadOk"], true),
                    Message = Equals(data["currentValueReadOk"], true) ? "Read current values from existing watch table without writes."
                        : "Watch table was read, but no current value property was exposed.",
                    Data = data
                };
            }
            catch (Exception ex)
            {
                data["error"] = FormatExceptionDetail(ex);
                return new PlcWatchReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        public PlcWatchReport ProbePlcMonitorOnlineCapabilities(string softwarePath)
        {
            var data = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "read-only-probe",
                ["safety"] =
                    "No online/offline transition, no watch-table modification, no value write, and no force-table operation is executed by this probe."
            };

            var warnings = new List<object?>();
            var members = new List<object?>();
            var services = new List<object?>();

            try
            {
                var plc = _session.GetPlcSoftware(softwarePath);
                if (plc == null)
                {
                    data["warnings"] = new List<object?> { "PLC software not found." + _session.AvailablePlcPathsSuffix() };
                    return new PlcWatchReport { Ok = false, Message = "PLC software not found" + _session.AvailablePlcPathsSuffix(), Data = data };
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
                        services.Add(new Dictionary<string, object?> { ["suffix"] = suffix, ["status"] = "type-not-found" });
                        continue;
                    }

                    var svc = TryGetService(plc, st);
                    services.Add(new Dictionary<string, object?>
                    {
                        ["suffix"] = suffix,
                        ["type"] = st.FullName ?? st.Name,
                        ["status"] = svc == null ? "not-available" : "available",
                        ["serviceType"] = svc?.GetType().FullName ?? ""
                    });
                }

                var watchTables = GetPlcWatchTables(softwarePath) ?? new List<string>();
                data["watchTables"] = new List<object?>(watchTables.Select(x => x).ToArray());
                data["matchingMembers"] = members;
                data["serviceProbe"] = services;
                warnings
                    .Add("Online value monitoring is not executed by this tool. It only probes read-only API surfaces for a later separately verified " +
                    "current-value read workflow.");
                warnings.Add("Force-table APIs are intentionally excluded by product safety policy.");
                data["warnings"] = warnings;

                return new PlcWatchReport { Ok = true, Message = "PLC monitor/online capability probe completed", Data = data };
            }
            catch (Exception ex)
            {
                data["error"] = ex.ToString();
                return new PlcWatchReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        private static object? ResolvePlcWatchAndForceTableGroup(object plc)
        {
            // 只解析“监控与强制表”的容器对象；后续只读取 WatchTables，不读取 ForceTables。
            // TIA V21 的真实属性名通常是 WatchAndForceTableGroup，早期猜测的 WatchTables 不覆盖这个层级。
            return WatchProperty(
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

        private void EnumeratePlcWatchTablesRecursive(object group, string groupPath, List<(string Name, string Path, object Table)> result,
                HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var watchTables = WatchProperty(group, "WatchTables", "PlcWatchTables", "Tables");
            if (watchTables is System.Collections.IEnumerable tableEnumerable && watchTables is not string)
            {
                foreach (var table in tableEnumerable)
                {
                    if (table == null) continue;
                    var name = TryGetName(table) ?? WatchProperty(table, "Name")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var path = string.IsNullOrWhiteSpace(groupPath) ? name! : groupPath + "/" + name;
                    result.Add((name!, path, table));
                }
            }

            var groups = WatchProperty(group, "Groups", "WatchAndForceTableGroups", "UserGroups");
            if (groups is System.Collections.IEnumerable groupEnumerable && groups is not string)
            {
                foreach (var child in groupEnumerable)
                {
                    if (child == null) continue;
                    var childName = TryGetName(child) ?? WatchProperty(child, "Name")?.ToString();
                    var childPath = string.IsNullOrWhiteSpace(childName)
                        ? groupPath
                        : string.IsNullOrWhiteSpace(groupPath) ? childName! : groupPath + "/" + childName;
                    EnumeratePlcWatchTablesRecursive(child, childPath, result, visited);
                }
            }
        }

        private Dictionary<string, object?> ReadWatchTableEntryReadOnly(object entry, bool includeMembers)
        {
            var row = new Dictionary<string, object?>
            {
                ["type"] = entry.GetType().FullName ?? entry.GetType().Name
            };

            foreach (var propName in new[] {
                    "Name", "Address", "DisplayFormat", "Comment", "DataType", "Value", "CurrentValue", "ActualValue", "MonitorValue", "OnlineValue", "Status"
                })
            {
                var value = WatchProperty(entry, propName);
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

            var attributes = new Dictionary<string, object?>();
            var methods = entry.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods
                .FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var infos = getInfos?.Invoke(entry, Array.Empty<object>()) as IEnumerable;
            if (infos != null && getAttr != null)
            {
                foreach (var info in infos)
                {
                    var name = WatchProperty(info!, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var lower = name.ToLowerInvariant();
                    if (!new[] { "name", "address", "display", "value", "actual", "current", "monitor", "online", "status", "comment", "type" }
                            .Any(lower.Contains))
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
                row["members"] = new List<object?>(DescribeMembers(entry, 180).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}").ToArray());

            return row;
        }



        public PlcWatchReport MonitorWatchTableLiveS7(
            string softwarePath, string watchTableName, string ip, int rack = 0, int slot = 1, string expectModuleContains = "")
        {
            var data = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["watchTableName"] = watchTableName,
                ["ip"] = ip,
                ["channel"] = "watch-table (Openness, read-only) + S7 live read",
                ["safety"] = new Dictionary<string, object?> {
                    ["readOnly"] = true, ["modifiesWatchTables"] = false, ["writesValues"] = false, ["usesForce"] = false }
            };

            if (IsProjectNull())
                return new PlcWatchReport { Ok = false, Message = "No project open. Attach first.", Data = data };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
                return new PlcWatchReport {
                    Ok = false, Message = $"PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Data = data };

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null)
                return new PlcWatchReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data };

            var tables = EnumeratePlcWatchTables(group);
            data["availableWatchTables"] = new List<object?>(tables.Select(x => x.Name).ToArray());
            var table = tables.FirstOrDefault(x =>
                string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
            if (table == null)
                return new PlcWatchReport { Ok = false, Message = $"Watch table '{watchTableName}' not found.", Data = data };

            // Extract entry rows (address + name + display format).
            var rows = new List<(string name, string address, string fmt)>();
            var entries = WatchProperty(table, "Entries", "WatchTableEntries", "Rows", "Items");
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
                data["note"] =
                    "This watch table exposed no entries through Openness. It may be empty, or Openness cannot read rows authored in the TIA UI (known " +
                    "limitation). Read the values directly with GetPlcLiveValuesS7 using explicit addresses.";
                return new PlcWatchReport { Ok = true, Message = "Watch table has 0 readable entries via Openness.", Data = data };
            }

            return new PlcWatchReport {
                Ok = true, Data = data, Rows = rows.Select(r => new PlcWatchRow { Name = r.name, Address = r.address, Format = r.fmt }).ToArray() };
        }

        private static string ReadEntryAttr(object entry, params string[] names)
        {
            // Try a property first, then GetAttribute(name).
            var prop = WatchProperty(entry, names);
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


#endif
    }
}

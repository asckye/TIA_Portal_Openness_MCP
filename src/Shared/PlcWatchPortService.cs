using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class PlcOrganisationPortService
    {
        private static ResponseJsonReport WatchReport(JsonObject reply)
        {
            var report = reply["Report"]!.AsObject();
            return new ResponseJsonReport { Ok = (bool?)report["Ok"], Message = (string?)report["Message"], Data = report["Data"]?.DeepClone() as JsonObject,
                Meta = report["Meta"]?.DeepClone() as JsonObject, Warnings = report["Warnings"] == null ? null : JsonSerializer.Deserialize<string[]>(report["Warnings"]!.ToJsonString()) };
        }
        public List<string>? GetPlcForceTables(string softwarePath)
        {
            if (!hasProject()) return null;
            var reply = Invoke(new PlcSoftwareRequest { Operation = "ListPlcForceTables", SoftwarePath = softwarePath });
            forceTablePathsSuffix = (string?)reply["Reason"] ?? "";
            var names = reply["Names"];
            return names == null ? null : JsonSerializer.Deserialize<List<string>>(names.ToJsonString());
        }
        private string forceTablePathsSuffix = "";
        public string ForceTablePathsSuffix() => forceTablePathsSuffix;
        public ResponseImportBatch ExportPlcWatchTablesToDirectory(string softwarePath, string dir, string regexName = "")
            => TechnologyBatch(Invoke(new PlcSoftwareRequest { Operation = "ExportPlcWatchTablesToDirectory", SoftwarePath = softwarePath, FilePath = dir, RegexName = regexName }));
        public ResponseMessage EnsureWatchTableEntry(string softwarePath, string tableName, string address, string modifyValue, string trigger = "Permanent")
            => OrganisationStep(new PlcSoftwareRequest { Operation = "SetPlcWatchTableModifyValue", SoftwarePath = softwarePath, Path = tableName, AddressOrValue = address, Value = new HardwareScalar { Kind = "string", Text = modifyValue }, GenerateOption = trigger, DryRun = false });
        public ResponseMessage ImportPlcWatchTableOffline(string softwarePath, string filePath, string groupPath = "", bool dryRun = true)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "ImportPlcWatchTableOffline", SoftwarePath = softwarePath, FilePath = filePath, GroupPath = groupPath, DryRun = dryRun });
        public ResponseMessage ManagePlcTableEntries(string softwarePath, string tableKind, string tablePath, string action = "read", int entryIndex = -1, bool confirmDelete = false, bool dryRun = true, int offset = 0, int limit = 200)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "ManagePlcTableEntries", SoftwarePath = softwarePath, Kind = tableKind, Path = tablePath, Action = action, EntryIndex = entryIndex, Confirm = confirmDelete, DryRun = dryRun, Offset = offset, Limit = limit });
        public ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
            => WatchReport(Invoke(new PlcSoftwareRequest { Operation = "GetPlcWatchTableCurrentValuesReadOnly", SoftwarePath = softwarePath, Path = watchTableName, Limit = maxEntries }));
        public ResponseJsonReport ProbePlcMonitorOnlineCapabilities(string softwarePath)
            => WatchReport(Invoke(new PlcSoftwareRequest { Operation = "ProbePlcMonitorOnlineCapabilities", SoftwarePath = softwarePath }));
        public ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
            => WatchReport(Invoke(new PlcSoftwareRequest { Operation = "TraceTagCause", SoftwarePath = softwarePath, Name = tag, RegexName = blockScope }));
        public ResponseJsonReport MonitorWatchTableLiveS7(string softwarePath, string watchTableName, string ip, int rack = 0, int slot = 1, string expectModuleContains = "")
        {
            var reply = Invoke(new PlcSoftwareRequest { Operation = "MonitorPlcWatchTableS7", SoftwarePath = softwarePath, Path = watchTableName, Ip = ip });
            var report = WatchReport(reply); if (report.Ok != true || report.Data == null || report.Message != null) return report;
            var data = report.Data;
            var rows = JsonSerializer.Deserialize<PlcWatchRow[]>(reply["Report"]!["Rows"]!.ToJsonString())!.Select(r => (name: r.Name, address: r.Address, fmt: r.Format)).ToList();
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
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = ok
                    ? $"Monitored {outRows.Count} live value(s) from watch table '{watchTableName}' in {elapsed} ms ({unresolved.Count} unresolved)."
                    : $"Watch table '{watchTableName}': {rows.Count} entries, {specs.Count} absolute, {unresolved.Count} unresolved. See values/unresolved.",
                Data = data,
                Meta = ResponseMeta.Basic(DateTime.Now, ok)
            };
        }

        public ResponseJsonReport TraceTagCauseLive(
            string softwarePath, string tag, string ip, int rack = 0, int slot = 1,
            string blockScope = "", string expectModuleContains = "")
        {
            if (string.IsNullOrWhiteSpace(ip))
                return new ResponseJsonReport
                {
                    Ok = false,
                    Message = "ip is required for live tracing. For the offline-only trace, use TraceTagCause."
                };

            var trace = TraceTagCause(softwarePath, tag, blockScope);
            if (trace.Ok != true || trace.Data == null) return trace;

            var conditions = trace.Data["gatingConditions"] as JsonArray;
            var live = new JsonArray();
            var specToOperand = new Dictionary<string, string>();
            var specs = new List<string>();

            var addressReply = Invoke(new PlcSoftwareRequest { Operation = "TraceTagCauseLive", Action = "tagMap", SoftwarePath = softwarePath });
            var map = JsonSerializer.Deserialize<Dictionary<string, PlcTagAddress>>(addressReply["TagAddresses"]!.ToJsonString())!;

            if (conditions != null)
            {
                foreach (var c in conditions)
                {
                    string operand = c?.ToString() ?? "";
                    if (operand.Length == 0) continue;
                    string key = CausalTraceParser.NormalizeOperand(operand);
                    if (map.TryGetValue(key, out var hit) && hit.Address.StartsWith("%"))
                    {
                        string? spec = S7LiveReader.TiaTagToSpec(hit.Address, hit.DataType);
                        if (spec != null)
                        {
                            if (!specToOperand.ContainsKey(spec)) { specs.Add(spec); specToOperand[spec] = operand; }
                            continue;
                        }
                    }
                    live.Add(new JsonObject
                    {
                        ["operand"] = operand,
                        ["resolved"] = false,
                        ["hint"] = "no absolute PLC-tag address (DB member / optimized / symbolic) — read via OPC UA symbolic or a watch table"
                    });
                }
            }

            long elapsed = 0;
            if (specs.Count > 0)
            {
                var read = S7LiveReader.ReadItems(ip, rack, slot, specs,
                    string.IsNullOrWhiteSpace(expectModuleContains) ? null : expectModuleContains);
                elapsed = read.ElapsedMs;
                if (read.Error != null) trace.Data["liveReadError"] = read.Error;
                trace.Data["liveIdentity"] = new JsonObject
                {
                    ["moduleTypeName"] = read.Identity.ModuleTypeName,
                    ["szlError"] = read.Identity.SzlError
                };
                foreach (var it in read.Items)
                {
                    string op = specToOperand.TryGetValue(it.Spec, out var v) ? v : it.Spec;
                    var o = new JsonObject { ["operand"] = op, ["resolved"] = true, ["address"] = it.Spec, ["type"] = it.Type };
                    if (it.Error != null) o["error"] = it.Error;
                    else o["value"] = JsonValue.Create(it.Value);
                    live.Add(o);
                }
            }

            trace.Data["liveChannel"] = "S7 / ISO-on-TCP (read-only)";
            trace.Data["liveGatingConditions"] = live;
            trace.Data["liveElapsedMs"] = elapsed;
            trace.Data["safety"] = new JsonObject { ["readOnly"] = true, ["writesValues"] = false, ["usesForce"] = false, ["changesCpuMode"] = false };

            int total = conditions?.Count ?? 0;
            trace.Message = $"{trace.Message} Live-read {specs.Count}/{total} gating condition(s) over S7 ({total - specs.Count} unresolved).";
            return trace;
        }

    }
}

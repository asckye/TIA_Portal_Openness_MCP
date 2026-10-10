using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Blocks;
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
        public PlcWatchReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
        {
            var data = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["tag"] = tag,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["readOnly"] = true
            };
            var warnings = new List<object?>();

            if (IsProjectNull())
                return new PlcWatchReport { Ok = false, Message = "No project open. Attach first.", Data = data };
            if (string.IsNullOrWhiteSpace(tag))
                return new PlcWatchReport { Ok = false, Message = "tag is required.", Data = data };

            string normTag = PlcCausalTraceParser.NormalizeOperand(tag);

            List<PlcBlock>? blocks;
            try { blocks = _session.GetBlocks(softwarePath, blockScope ?? ""); }
            catch (Exception ex) { return new PlcWatchReport { Ok = false, Message = $"ListPlcBlocks failed: {ex.Message}", Data = data }; }

            // null = 没连接/没打开项目。以前 GetBlocks 这时返回空列表，于是一路扫出
            // 「0 个块、没找到任何写点」，报成一份看起来正常的空结果。
            if (blocks == null)
            {
                return new PlcWatchReport
                {
                    Ok = false,
                    Message = "No TIA project is open. Call ConnectPortal / OpenProject (or AttachOpenProject) first.",
                    Data = data
                };
            }

            var codeBlocks = blocks.Where(b => !(b is DataBlock)).ToList();
            data["scannedBlockCount"] = codeBlocks.Count;

            var tmpDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_trace_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);

            var writeSites = new List<object?>();
            var readSites = new List<object?>();
            var allConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int analyzedOk = 0;

            try
            {
                foreach (var block in codeBlocks)
                {
                    string blockName = block.Name;
                    string blockPath = _session.GetBlockPath(block);
                    if (!block.IsConsistent) { warnings.Add($"Skipped inconsistent block '{blockName}' (compile first)."); continue; }

                    string xmlPath = Path.Combine(tmpDir, blockName + ".xml");
                    try { block.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xmlPath)), ExportOptions.None); }
                    catch (Exception ex) { warnings.Add($"Export failed for '{blockName}': {ex.Message}"); continue; }

                    XDocument doc;
                    try { doc = XDocument.Load(xmlPath); }
                    catch (Exception ex) { warnings.Add($"Parse failed for '{blockName}': {ex.Message}"); continue; }

                    PlcCausalTraceParser.AnalyzeBlock(doc, blockName, blockPath, normTag, tag, writeSites, readSites, allConditions);
                    analyzedOk++;
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch /* swallow(cleanup): temporary export cleanup must not replace causal-trace results */ { }
            }

            data["writeSites"] = writeSites;
            data["readSites"] = readSites;
            data["gatingConditions"] = new List<object?>(allConditions.OrderBy(x => x).Select(x => x).ToArray());
            data["analyzedBlockCount"] = analyzedOk;
            if (warnings.Count > 0) data["warnings"] = warnings;

            string summary;
            if (codeBlocks.Count > 0 && analyzedOk == 0)
            {
                summary = $"INCONCLUSIVE: none of {codeBlocks.Count} code block(s) could be exported/parsed (see warnings); no trace was performed.";
            }
            else if (writeSites.Count == 0)
                summary = $"No block writes '{tag}'. It may be set by HMI, an instruction's output, an indirect/optimized access this parser does not resolve, or the name differs from the project symbol.";
            else
                summary = $"'{tag}' is written at {writeSites.Count} site(s). {allConditions.Count} distinct gating condition operand(s) found. " +
                          "Live-read those with GetPlcLiveValuesS7 to see which is currently driving the value.";

            return new PlcWatchReport
            {
                Ok = true,
                Message = summary,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.Select(w => w!.ToString()).ToArray() : null,
                Meta = LegacyResponseMetadata.Basic(DateTime.Now, true)
            };
        }

        // name (normalized) -> (raw name, absolute LogicalAddress, TIA DataTypeName) for
        // every PLC tag in the software's tag tables (recursing user group folders).
        // Read-only reflection; tolerant of V20/V21 shape differences.
        private Dictionary<string, (string name, string address, string dataType)> BuildPlcTagAddressMap(object plc)
        {
            var map = new Dictionary<string, (string name, string address, string dataType)>(StringComparer.Ordinal);
            object? tables = WatchProperty(plc, "TagTables")
                ?? WatchProperty(WatchProperty(plc, "TagTableGroup", "TagTableFolder") ?? plc, "TagTables");
            CollectTagsFromTables(tables, map);
            CollectTagGroups(WatchProperty(plc, "TagTableGroup", "TagTableFolder"), map, new HashSet<object>());
            return map;
        }

        private void CollectTagGroups(object? group, Dictionary<string, (string name, string address, string dataType)> map, HashSet<object> visited)
        {
            if (group == null || !visited.Add(group)) return;
            CollectTagsFromTables(WatchProperty(group, "TagTables"), map);
            var subs = WatchProperty(group, "Groups");
            if (subs is IEnumerable en && !(subs is string))
                foreach (var g in en) if (g != null) CollectTagGroups(g, map, visited);
        }

        private void CollectTagsFromTables(object? tables, Dictionary<string, (string name, string address, string dataType)> map)
        {
            if (!(tables is IEnumerable ten) || tables is string) return;
            foreach (var table in ten)
            {
                if (table == null) continue;
                var tags = WatchProperty(table, "Tags");
                if (!(tags is IEnumerable gen) || tags is string) continue;
                foreach (var tag in gen)
                {
                    if (tag == null) continue;
                    string name = WatchProperty(tag, "Name")?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    string addr = WatchProperty(tag, "LogicalAddress")?.ToString() ?? "";
                    string dt = WatchProperty(tag, "DataTypeName")?.ToString() ?? "";
                    string keyNorm = PlcCausalTraceParser.NormalizeOperand(name);
                    if (keyNorm.Length > 0 && !map.ContainsKey(keyNorm)) map[keyNorm] = (name, addr, dt);
                }
            }
        }
        public Dictionary<string, PlcTagAddress> ReadPlcTraceTagAddresses(string softwarePath)
        {
            var plc = _session.GetPlcSoftware(softwarePath);
            return (plc == null ? new Dictionary<string, (string name, string address, string dataType)>() : BuildPlcTagAddressMap(plc))
                .ToDictionary(p => p.Key, p => new PlcTagAddress { Name = p.Value.name, Address = p.Value.address, DataType = p.Value.dataType }, StringComparer.Ordinal);
        }
    }
}

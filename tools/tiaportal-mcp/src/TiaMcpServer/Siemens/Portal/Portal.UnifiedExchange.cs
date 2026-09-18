using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.Scripts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Native WinCC Unified file exchange: tag tables (WinCC ML .hmi.yml via HmiTagComposition.Export/Import),
    // global script modules (HmiScriptModuleComposition / HmiScriptModule Export/Import, the IChromDataExchangeExport
    // interface) and OPC UA alarm definitions (OpcUaAlarm.Import / GetNodeId / DisplayNames on an HMI connection).
    public partial class Portal
    {
        private HmiSoftware ExactUnifiedSoftware(string softwarePath) => (HmiSoftware)ExactUnifiedRoot(softwarePath);

        // Tag tables live at the root or inside nested TagTableGroups; accept an absolute /Group/Sub/Table path or a unique table name.
        private static HmiTagTable? FindUnifiedTagTable(HmiSoftware hmi, string nameOrPath)
        {
            if (nameOrPath.StartsWith("/", StringComparison.Ordinal))
            {
                var parts = nameOrPath.Substring(1).Split('/');
                if (parts.Length == 0 || parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("Invalid absolute tag table path.");
                HmiTagTableGroup? group = null;
                for (int i = 0; i < parts.Length - 1; i++)
                    group = (group == null ? hmi.TagTableGroups : group.Groups).Find(parts[i]) ?? throw new PortalException(PortalErrorCode.NotFound, "Tag table group not found: " + parts[i]);
                return (group == null ? hmi.TagTables : group.TagTables).Find(parts[parts.Length - 1]);
            }
            var matches = new List<HmiTagTable>();
            void Walk(HmiTagTableComposition tables, HmiTagTableGroupComposition groups)
            {
                foreach (var table in tables) if (string.Equals(table.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) matches.Add(table);
                foreach (var group in groups) { if (matches.Count > 1) return; Walk(group.TagTables, group.Groups); }
            }
            Walk(hmi.TagTables, hmi.TagTableGroups);
            if (matches.Count > 1) throw new InvalidOperationException("Ambiguous tag table name; use an absolute /Group/Table path: " + nameOrPath);
            return matches.SingleOrDefault();
        }

        public ResponseMessage ExchangeUnifiedTags(string softwarePath, string action, string directory, string tagTable = "", string fileName = "", string expectedTagNamesJson = "[]", bool dryRun = true)
            => RunHmiStepTool("ExchangeUnifiedTags", meta => {
                if (!UnifiedExchangeLogic.ExchangeActions.Contains(action)) throw new ArgumentException("action must be export or import.");
                bool exporting = action == "export";
                var dir = UnifiedExchangeLogic.ValidateDirectory(directory, exporting);
                var name = UnifiedExchangeLogic.ValidateFileName(fileName);
                var expected = UnifiedExchangeLogic.ParseExpectedNames(expectedTagNamesJson);
                if (exporting && expected.Length > 0) throw new ArgumentException("expectedTagNamesJson is only used by import.");
                using var access = !dryRun && !exporting ? AcquireHmiEditAccess() : null;
                var hmi = ExactUnifiedSoftware(softwarePath);
                // Official pattern: tagTable.Tags.Export(dir, name) / tagTable.Tags.Import(dir); the root Tags composition carries the same members for the whole device.
                HmiTagComposition tags;
                if (string.IsNullOrWhiteSpace(tagTable)) tags = hmi.Tags;
                else
                {
                    var table = FindUnifiedTagTable(hmi, tagTable) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact Unified tag table not found: " + tagTable);
                    tags = table.Tags;
                }
                meta["action"] = action; meta["dryRun"] = dryRun; meta["directory"] = dir.FullName; meta["tagTable"] = string.IsNullOrWhiteSpace(tagTable) ? null : tagTable;
                meta["fileName"] = name.Length == 0 ? null : name; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["tagCountBefore"] = tags.Count;
                meta["format"] = "WinCC ML (YAML): <name>.hmi.yml plus NameData.yml / .ile / *.def.hmi.yml side files written by TIA";
                if (exporting)
                {
                    if (dryRun) return "Unified tag export preview; no file written.";
                    dir.Create(); meta["mayHaveWrittenFiles"] = true;
                    var native = name.Length == 0 ? tags.Export(dir) : tags.Export(dir, name);
                    meta["apiCallSuccess"] = true; meta["files"] = UnifiedExchangeLogic.VerifyNativeFiles(native, dir); meta["dataComplete"] = false;
                    return "Unified tags exported by TIA (WinCC ML) and hashed; content not parsed. No project change.";
                }
                meta["importCandidates"] = UnifiedExchangeLogic.ListImportCandidates(dir, new[] { ".hmi.yml" });
                if (((JsonArray)meta["importCandidates"]!).Count == 0) throw new FileNotFoundException("No *.hmi.yml file in the import directory.");
                meta["expectedCount"] = expected.Length;
                if (dryRun) return "Unified tag import preview; no changes.";
                meta["mayHaveChanged"] = true;
                bool ok = name.Length == 0 ? tags.Import(dir) : tags.Import(dir, name);
                meta["nativeSuccess"] = ok; meta["apiCallSuccess"] = true;
                if (!ok) throw new InvalidOperationException("Native HmiTagComposition.Import returned false; project may have changed partially.");
                meta["tagCountAfter"] = tags.Count;
                var missing = expected.Where(n => tags.Find(n) == null).ToArray();
                meta["missingNames"] = new JsonArray(missing.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                meta["actualCount"] = expected.Length - missing.Length; meta["fullContentVerified"] = false;
                if (missing.Length != 0) throw new InvalidOperationException("Expected tag names absent after import: " + string.Join(", ", missing));
                return "Native tag import returned true" + (expected.Length > 0 ? " and every expected tag name is present" : "") + "; tag properties are not independently compared. No save/compile/download.";
            });

        public ResponseMessage ExchangeUnifiedScriptModules(string softwarePath, string action, string directory, string moduleName = "", string fileName = "", bool dryRun = true)
            => RunHmiStepTool("ExchangeUnifiedScriptModules", meta => {
                if (!UnifiedExchangeLogic.ExchangeActions.Contains(action)) throw new ArgumentException("action must be export or import.");
                bool exporting = action == "export";
                var dir = UnifiedExchangeLogic.ValidateDirectory(directory, exporting);
                var name = UnifiedExchangeLogic.ValidateFileName(fileName);
                if (!exporting && !string.IsNullOrWhiteSpace(moduleName)) throw new ArgumentException("moduleName selects one module for export; import takes the directory (optionally one fileName).");
                using var access = !dryRun && !exporting ? AcquireHmiEditAccess() : null;
                var hmi = ExactUnifiedSoftware(softwarePath);
                HmiScriptModuleComposition modules = hmi.Scripts;
                HmiScriptModule? module = null;
                if (!string.IsNullOrWhiteSpace(moduleName)) module = modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.Ordinal)) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact script module not found: " + moduleName);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["directory"] = dir.FullName; meta["moduleName"] = module?.Name; meta["fileName"] = name.Length == 0 ? null : name;
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["moduleCountBefore"] = modules.Count;
                meta["modules"] = new JsonArray(modules.Take(500).Select(m => (JsonNode)JsonValue.Create(m.Name)!).ToArray());
                if (exporting)
                {
                    if (module == null && name.Length > 0) throw new ArgumentException("fileName applies to a single module export (HmiScriptModule.Export(dir, name)); the composition export names files itself.");
                    if (dryRun) return "Unified script module export preview; no file written.";
                    dir.Create(); meta["mayHaveWrittenFiles"] = true;
                    var native = module == null ? modules.Export(dir) : name.Length == 0 ? module.Export(dir) : module.Export(dir, name);
                    meta["apiCallSuccess"] = true; meta["files"] = UnifiedExchangeLogic.VerifyNativeFiles(native, dir); meta["dataComplete"] = false;
                    return "Unified script module(s) exported by TIA and hashed; script content not parsed. No project change.";
                }
                meta["importCandidates"] = UnifiedExchangeLogic.ListImportCandidates(dir, new[] { ".js", ".yml", ".json" });
                if (dryRun) return "Unified script module import preview; no changes.";
                meta["mayHaveChanged"] = true;
                bool ok = name.Length == 0 ? modules.Import(dir) : modules.Import(dir, name);
                meta["nativeSuccess"] = ok; meta["apiCallSuccess"] = true;
                if (!ok) throw new InvalidOperationException("Native HmiScriptModuleComposition.Import returned false; project may have changed partially.");
                meta["moduleCountAfter"] = modules.Count;
                meta["modulesAfter"] = new JsonArray(modules.Take(500).Select(m => (JsonNode)JsonValue.Create(m.Name)!).ToArray());
                return "Native script module import returned true; module names listed, script bodies not compared. No save/compile/download.";
            });

        public ResponseMessage ImportUnifiedOpcUaAlarms(string softwarePath, string connectionName, string action = "read", string xmlPath = "", string displayName = "", bool dryRun = true)
            => RunHmiStepTool("ImportUnifiedOpcUaAlarms", meta => {
                if (!UnifiedExchangeLogic.OpcUaAlarmActions.Contains(action)) throw new ArgumentException("action must be read or import.");
                if (string.IsNullOrWhiteSpace(connectionName)) throw new ArgumentException("Exact HMI connection name required.");
                var xml = action == "import" ? UnifiedExchangeLogic.ValidateXmlPath(xmlPath) : "";
                if (action == "import" && !File.Exists(xml)) throw new FileNotFoundException("OPC UA alarm xml not found.", xml);
                if (action == "read" && !string.IsNullOrEmpty(xmlPath)) throw new ArgumentException("xmlPath is only used by import.");
                using var access = action == "import" && !dryRun ? AcquireHmiEditAccess() : null;
                var hmi = ExactUnifiedSoftware(softwarePath);
                var connection = hmi.Connections.Find(connectionName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact HMI connection not found: " + connectionName);
                meta["connection"] = new JsonObject { ["name"] = connection.Name, ["communicationDriver"] = connection.CommunicationDriver, ["partner"] = connection.Partner };
                // Official service on the OPC UA connection; a non-OPC UA connection yields null.
                var service = connection.GetService<OpcUaAlarm>();
                meta["serviceAvailable"] = service != null;
                if (service == null) throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "OpcUaAlarm service unavailable on connection " + connectionName + " (only OPC UA connections expose it).");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                JsonArray Names() => new JsonArray((service.DisplayNames ?? Array.Empty<string>()).Take(2000).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                meta["displayNames"] = Names();
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    try { meta["nodeId"] = service.GetNodeId(displayName); }
                    catch (Exception ex) { meta["nodeId"] = null; meta["nodeIdError"] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
                }
                if (action == "read") { meta["apiCallSuccess"] = true; return "OPC UA alarm display names read" + (string.IsNullOrWhiteSpace(displayName) ? "" : " and node id resolved") + "; no change."; }
                meta["xmlPath"] = xml;
                if (dryRun) return "OPC UA alarm import preview; native Import not called.";
                meta["mayHaveChanged"] = true;
                bool ok = service.Import(xml);
                meta["nativeSuccess"] = ok; meta["apiCallSuccess"] = true;
                if (!ok) throw new InvalidOperationException("Native OpcUaAlarm.Import returned false.");
                meta["displayNamesAfter"] = Names();
                return "OPC UA alarm information imported from xml (native true); display names listed after import. No save/compile/download.";
            });
    }
}

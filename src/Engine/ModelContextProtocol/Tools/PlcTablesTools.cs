using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcTablesTools
    {
        private readonly PlcTablesService _tables;
        private readonly IEngineeringSession _session;

        public PlcTablesTools(PlcTablesService tables, IEngineeringSession session)
        {
            _tables = tables;
            _session = session;
        }

        #region plc software - PlcTables

        [McpServerTool(Name = "GetPlcTagTables"), Description("[L2][PLC-Software] List all PLC tag table names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts.")]
        public ResponseStringList GetPlcTagTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = _session.GetPlcTagTables(softwarePath, out var walk);
                if (items != null)
                {
                    // 空清单有三种完全不同的成因：这个 PLC 确实没有表 / TagTables 属性
                    // 在这个版本上叫别的名字 / 读属性时抛了异常被吞掉。三者返回的东西
                    // 一模一样，用户报「枚举返回空但删除工具能找到同一张表」时我们手上
                    // 没有任何证据。所以空清单必须把「走过了什么」一并带回来。
                    var meta = ResponseMeta.Basic(DateTime.Now, true);
                    if (items.Count == 0)
                    {
                        meta["walkedGroupType"] = walk.RootGroupType;
                        meta["tagTablesPropertyFound"] = walk.TagTablesPropertyFound;
                        meta["tagTablesPropertyError"] = walk.TagTablesPropertyError;
                        meta["groupsVisited"] = walk.GroupsVisited;
                        meta["notes"] = new JsonArray(
                            walk.Notes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                    }

                    return new ResponseStringList
                    {
                        Message = items.Count > 0
                            ? $"PLC tag tables listed for '{softwarePath}'"
                            : $"'{softwarePath}' 上没有枚举到任何变量表。这**不一定**表示它没有表 —— "
                              + "读属性失败也长这样，所以 Meta 里带了这次遍历的证据"
                              + "（walkedGroupType / tagTablesPropertyFound / tagTablesPropertyError / groupsVisited / notes）。"
                              + "若你确信有表，把这几项贴给维护者。",
                        Items = items,
                        Meta = meta
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC tag tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcTagTable"), Description("[L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file")]
        public ResponseExportFile ExportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("tagTableName: PLC tag table name")] string tagTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = _session.ExportPlcTagTable(softwarePath, tagTableName, exportPath, out var reason);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC tag table '{tagTableName}' exported",
                        ExportPath = exportPath,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException(
                    $"Failed exporting PLC tag table '{tagTableName}' from '{softwarePath}'" +
                    (string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason),
                    McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTable"), Description("[L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort)")]
        public ResponseMessage ImportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of PLC tag table XML")] string importPath)
        {
            try
            {
                _session.ImportPlcTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"PLC tag table imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTablesFromDirectory"), Description("[L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort)")]
        public ResponseImportBatch ImportPlcTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing PLC tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = _tables.ImportPlcTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} PLC tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = ResponseMeta.Basic(DateTime.Now, (result.Failed == null || !result.Failed.Any()))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcWatchTables"), Description("[L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only.")]
        public ResponseStringList GetPlcWatchTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = _tables.GetPlcWatchTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC watch tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC watch tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcForceTables"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " List all force table names in the PLC software." +
            " Force tables configure which variables are continuously forced to specific values while the CPU is online." +
            " Read-only: this server exposes NO tool for creating or editing force entries — forcing overrides live PLC logic" +
            " (a forced output stays forced regardless of what the program writes) and is deliberately kept out of the AI tool surface." +
            " Create or edit force entries in the TIA Portal UI. For a one-shot value written from a watch table instead," +
            " use SetWatchTableModifyValue (the variable reverts to PLC logic afterwards).")]
        public ResponseStringList GetPlcForceTables(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var names = _tables.GetPlcForceTables(softwarePath);
                if (names == null)
                {
                    // 原来这里返回 Items=[] + 一句「not found」的**正常**响应。
                    // 只读 Items 的调用方看到的是「这台 PLC 没有强制表」——和「你路径写错了」
                    // 是完全不同的结论，而它分辨不出来。
                    throw new McpException(
                        $"GetPlcForceTables: PLC software not found at '{softwarePath}'. "
                        + "Use GetProjectTree to get the exact PLC path." + _session.AvailablePlcPathsSuffix(),
                        McpErrorCode.InvalidParams);
                }

                return new ResponseStringList
                {
                    Items = names,
                    Message = $"{names.Count} force table(s) found.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing force tables for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetWatchTableModifyValue"), Description(
            "[L2][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+GoOnline]" +
            " Configure a watch table entry to write a value to a PLC variable once (or on a trigger)." +
            " This is an OFFLINE CONFIGURATION step — the value is written to the PLC only when TIA Portal is online and the trigger fires." +
            " Trigger options: Permanent (every cycle), PermanentAtStart (every cycle, at scan start), OnceOnlyAtStart (single write at scan start), PermanentAtEnd, OnceOnlyAtEnd, OnceOnlyAtStop." +
            " Use GoOnline before calling this for the write to reach the PLC." +
            " SAFETY: the target is a variable in the physical CPU, not a simulation. The moment the trigger fires the value" +
            " lands on the real address — if that address is a coil, a valve, a contactor or a drive enable, the machine moves" +
            " at that instant, with no acknowledgement step. Before calling, know exactly what the address drives and confirm" +
            " nobody is at or inside the machine. The resulting physical motion is NOT undone by calling this tool again with" +
            " another value; the equipment stays wherever it moved to." +
            " Not a Force: this writes the value once per trigger event and the variable then follows PLC logic again" +
            " (a force would keep overriding the program continuously). This server exposes no tool for force entries —" +
            " GetPlcForceTables only lists them; use TIA Portal directly to force a value." +
            " Implementation (2.7.50): the typed Openness API cannot create tag rows (PlcWatchTable.Entries.Create() only makes comment rows), so the table is exported," +
            " the row added or updated in the SimaticML and the table re-imported (override mode) into its own group; the row is read back (meta.after, readbackVerified). Project not saved." +
            " Example: SetWatchTableModifyValue('PLC_1', 'Debug_WT', 'DB1.DBX0.0', 'TRUE', 'OnceOnlyAtStart')")]
        public ResponseMessage SetWatchTableModifyValue(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: watch table name or group-qualified path (e.g. 'MCP_W/MCP_WT'); a bare name is searched through every group (ambiguity refused) and the table is created at the root only when it exists nowhere")] string tableName,
            [Description("address: absolute address ('%M0.0', 'DB1.DBX0.0') goes to the row's Address; a symbol ('MyTag', '\"DB\".Member') goes to its Name (quoted per segment like TIA)")] string address,
            [Description("modifyValue: value to write, e.g. 'TRUE', '42', '3.14'")] string modifyValue,
            [Description("trigger: when to apply the write — Permanent | PermanentAtStart | OnceOnlyAtStart | PermanentAtEnd | OnceOnlyAtEnd | OnceOnlyAtStop (default: Permanent)")] string trigger = "Permanent")
        {
            try
            {
                return _tables.EnsureWatchTableEntry(softwarePath, tableName, address, modifyValue, trigger);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting watch table entry: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Force-write capability retained in the Portal layer but intentionally NOT exposed as an MCP tool:
        // forcing overrides live PLC logic and must not be AI-invocable. Online monitoring stays read-only
        // (see RunOnlineMonitoringSafetySelfTest / Test_OnlineMonitoringNoUnsafeToolNames). Use TIA Portal
        // directly for commissioning forces.
        public ResponseMessage SetForceTableEntry(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the force table to configure (created if not existing)")] string tableName,
            [Description("address: variable address to force, e.g. 'DB1.DBX0.0', '%M0.0'")] string address,
            [Description("forceValue: value to force, e.g. 'TRUE', '42'")] string forceValue)
        {
            try
            {
                return _tables.EnsureForceTableEntry(softwarePath, tableName, address, forceValue);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting force table entry: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTable"), Description("[L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project.")]
        public ResponseExportFile ExportPlcWatchTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("watchTableName: PLC watch table name")] string watchTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = _tables.ExportPlcWatchTable(softwarePath, watchTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC watch table '{watchTableName}' exported",
                        ExportPath = exportPath,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"Failed exporting PLC watch table '{watchTableName}' from '{softwarePath}'" + _session.AvailablePlcPathsSuffix(), McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTablesToDirectory"), Description("[L2][PLC-Software]Export all PLC watch/monitor tables to XML files. Read-only against the TIA project.")]
        public ResponseImportBatch ExportPlcWatchTablesToDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("dir: output directory")] string dir,
            [Description("regexName: optional regex filter applied to table name")] string regexName = "")
        {
            try
            {
                var result = _tables.ExportPlcWatchTablesToDirectory(softwarePath, dir, regexName);
                return new ResponseImportBatch
                {
                    Message = $"Exported {result.Imported?.Count() ?? 0} PLC watch tables to '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = ResponseMeta.Basic(DateTime.Now, (result.Failed == null || !result.Failed.Any()))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbePlcMonitorOnlineCapabilities"), Description("[L2][Online-Monitoring]Read-only probe for PLC online/offline/watch/monitor API surfaces. It does not go online/offline, change watch tables, write values, or touch restricted safety APIs.")]
        public ResponseJsonReport ProbePlcMonitorOnlineCapabilities(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var result = _tables.ProbePlcMonitorOnlineCapabilities(softwarePath);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing PLC monitor/online capabilities: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ReadPlcWatchTableCurrentValuesReadOnly"), Description("[L2][Online-Monitoring] Read current/monitor value properties from an existing PLC watch table only. It does not create/modify watch tables, write PLC values, go offline, or use force operations.")]
        public ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("watchTableName: existing PLC watch table path/name returned by GetPlcWatchTables.")] string watchTableName,
            [Description("maxEntries: maximum entries to inspect.")] int maxEntries = 50)
        {
            try
            {
                var result = _tables.ReadPlcWatchTableCurrentValuesReadOnly(softwarePath, watchTableName, maxEntries);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading PLC watch table values read-only: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyMonitoring"), Description("[L2][Online-Monitoring] Validate an online-monitoring request shape without connecting to TIA Portal. Read-only preflight only: no go-online/offline, no watch-table modification, no value write, and no force operation.")]
        public ResponseJsonReport PlanOnlineReadOnlyMonitoring(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("tagPathsJson: JSON array of symbolic PLC tag/member paths, for example [\"DB_HMI.MotorRun\",\"DB_HMI.SpeedSet\"]. Do not pass guessed M bits.")] string tagPathsJson,
            [Description("mode: current-values or watch-table-export-plan. Both are read-only planning modes.")] string mode = "current-values")
        {
            try
            {
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var policy = new JsonArray();
                foreach (var policyLine in OnlineToolPolicy.GetOnlineMonitoringSafetyPolicy())
                {
                    policy.Add(policyLine);
                }
                var normalizedMode = (mode ?? string.Empty).Trim();
                var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "current-values",
                    "watch-table-export-plan"
                };

                if (!allowedModes.Contains(normalizedMode))
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, $"Unsupported mode '{mode}'. Supported values: current-values, watch-table-export-plan.");
                }

                if (string.IsNullOrWhiteSpace(softwarePath))
                {
                    warnings.Add("softwarePath is empty. Resolve the PLC software path from GetProjectTree before real online monitoring.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array of symbolic PLC paths. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? string.Empty;
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (acceptedTags.Count == 0)
                {
                    warnings.Add("No accepted tag paths. Real online monitoring requires at least one declared PLC symbol or DB member.");
                }

                var ok = rejectedTags.Count == 0 && acceptedTags.Count > 0;
                var message = ok
                    ? "Online read-only monitoring plan validated. This preflight did not connect to TIA Portal."
                    : "Online read-only monitoring plan rejected. Fix rejected tag paths before any real online workflow.";

                return BuildOnlineMonitoringPlanResponse(ok, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning online read-only monitoring: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildOnlineMonitoringPlanResponse(bool ok, string softwarePath, string mode, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["mode"] = mode,
                    ["readOnly"] = true,
                    ["connectsToTia"] = false,
                    ["goesOnlineOrOffline"] = false,
                    ["modifiesWatchTables"] = false,
                    ["writesPlcValues"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy
                },
                Meta = ResponseMeta.Basic(DateTime.Now, ok)
            };
        }

        private static string? GetOnlineMonitoringTagRejectReason(string tagPath)
        {
            if (string.IsNullOrWhiteSpace(tagPath))
            {
                return "Tag path is empty.";
            }

            var forbiddenIntent = new[]
            {
                "force", "write", "modify", "update", "create",
                "delete", "remove", "import", "insert", "download", "activate", "start", "stop",
                "goonline", "gooffline", "watchtable", "forcetable"
            };
            var compact = Regex.Replace(tagPath, @"[\s_\-\.]+", string.Empty);
            var segments = Regex.Split(tagPath, @"[\.\s_\-]+").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var forbidden = forbiddenIntent.FirstOrDefault(x =>
                compact.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                segments.Any(segment => segment.StartsWith(x, StringComparison.OrdinalIgnoreCase)));
            if (forbidden != null)
            {
                return $"Tag path contains unsafe online/write/force/watch-table intent keyword '{forbidden}'.";
            }

            if (Regex.IsMatch(tagPath, @"^%?[MIQ][BWD]?\d+(\.\d+)?$", RegexOptions.IgnoreCase))
            {
                return "Absolute I/Q/M address is not accepted for HMI/online planning. Use a declared PLC symbol or DB member read back from the project.";
            }

            if (!Regex.IsMatch(tagPath, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            {
                return "Use a symbolic PLC path with at least one member separator, for example DB_HMI.MotorRun.";
            }

            return null;
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyDataProvider"), Description("[L2][Online-Monitoring] Plan the commercial current-value path through an external read-only data provider such as opcua or s7-readonly. This is a preflight only: it does not connect, write PLC values, modify watch tables, go online/offline through TIA, or use force operations.")]
        public ResponseJsonReport PlanOnlineReadOnlyDataProvider(
            [Description("provider: opcua or s7-readonly. opcua is preferred for commercial symbolic readback.")] string provider,
            [Description("endpoint: OPC UA endpoint URL or PLC endpoint/IP. It is validated only for shape and is not opened.")] string endpoint,
            [Description("tagPathsJson: JSON array of declared symbolic PLC tags/DB members. Guessed M bits and unsafe intent names are rejected.")] string tagPathsJson,
            [Description("optionsJson: optional JSON object such as {\"pollMs\":1000,\"source\":\"watch-table-export\"}.")] string optionsJson = "{}")
        {
            try
            {
                var normalizedProvider = (provider ?? "").Trim().ToLowerInvariant();
                var allowedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "opcua",
                    "s7-readonly"
                };

                var policy = new JsonArray(OnlineToolPolicy.GetOnlineMonitoringSafetyPolicy().Select(x => JsonValue.Create(x)).ToArray());
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var options = ToolJsonArguments.ParseJsonObjectOrEmpty(optionsJson, "optionsJson");

                if (!allowedProviders.Contains(normalizedProvider))
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, $"Unsupported provider '{provider}'. Supported providers: opcua, s7-readonly.");
                }

                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    warnings.Add("endpoint is empty. Real read-only providers require an OPC UA endpoint URL or PLC endpoint/IP before execution.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? "";
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (normalizedProvider == "s7-readonly")
                {
                    warnings.Add("s7-readonly must be implemented as a read-only adapter with no Write/Force API surface exposed by MCP.");
                }

                var ok = acceptedTags.Count > 0 && rejectedTags.Count == 0;
                return BuildReadOnlyProviderPlan(
                    ok,
                    normalizedProvider,
                    endpoint,
                    acceptedTags,
                    rejectedTags,
                    warnings,
                    policy,
                    options,
                    ok
                        ? "Read-only data provider plan validated. This preflight did not open a network connection."
                        : "Read-only data provider plan rejected. Fix rejected tags/provider settings before any real read workflow.");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning read-only data provider: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildReadOnlyProviderPlan(bool ok, string provider, string endpoint, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, JsonObject options, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["provider"] = provider,
                    ["endpoint"] = endpoint ?? "",
                    ["implementationPath"] = provider.Equals("opcua", StringComparison.OrdinalIgnoreCase)
                        ? "Use OPC UA read/subscribe as the preferred commercial current-value channel."
                        : "Use a strictly read-only S7 adapter for address/symbol reads when OPC UA is unavailable.",
                    ["status"] = "planned-read-only-provider",
                    ["usesTiaOpennessForCurrentValues"] = false,
                    ["usesTiaOpennessForTagDiscovery"] = true,
                    ["readOnly"] = true,
                    ["connectsNow"] = false,
                    ["writesPlcValues"] = false,
                    ["modifiesWatchTables"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy,
                    ["options"] = options
                },
                Meta = ResponseMeta.Basic(DateTime.Now, ok)
            };
        }



        #endregion

        [McpServerTool(Name = "MonitorWatchTableLiveS7"), Description("[L2][Online-Monitoring] Read-only: live-monitor an existing TIA watch table. Reads the table's entry addresses (Openness, read-only) and live-reads them over the S7 protocol (port 102). Returns name/address/value rows. Never edits the watch table, writes, or forces. Absolute-address entries (e.g. %DB1.DBW0, %MW4) are read; symbolic/optimized entries are listed as unresolved (use OPC UA for those). Identity guard via expectModuleContains. Use GetPlcWatchTables to list table names.")]
        public ResponseJsonReport MonitorWatchTableLiveS7(
            [Description("softwarePath: PLC software path, e.g. '安全PLC'.")] string softwarePath,
            [Description("watchTableName: existing watch table name from GetPlcWatchTables.")] string watchTableName,
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("expectModuleContains: optional identity guard (e.g. '1211C'); aborts on a positive module-type mismatch.")] string expectModuleContains = "")
        {
            try
            {
                var result = _tables.MonitorWatchTableLiveS7(softwarePath, watchTableName, ip, rack, slot, expectModuleContains);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"MonitorWatchTableLiveS7 failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name="ImportPlcWatchTableOffline"), Description("[L2][PLC-Software][WRITE] Import native SimaticML watch tables into an exact existing group. Offline-only, no overwrite. Rejects force-table and mixed-object XML and DTD. dryRun=true default. Does not execute modify values, start monitoring, save or download. filePath is on the MCP server.")]
        public ResponseMessage ImportPlcWatchTableOffline(string softwarePath,string filePath,string groupPath="",bool dryRun=true)
            => _tables.ImportPlcWatchTableOffline(softwarePath,filePath,groupPath,dryRun);

        [McpServerTool(Name="ReadPlcTagTableConstants"), Description("[L2][PLC-Software][READ] Constants of one exact PLC tag table (tablePath under TagTableGroup, or a unit's TagTableGroup via unitName + unitKind): PlcTagTable.UserConstants and SystemConstants as typed PlcConstant rows (Name, DataTypeName, Value, kind user/system); kind filters. Paginated. User constants are edited with ManagePlcTag kind=constant. No modification.")]
        public ResponseMessage ReadPlcTagTableConstants(
            string softwarePath,
            string tablePath,
            [Description("kind: all | user | system.")] string kind="all",
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            int offset=0,
            int limit=200)
            => _tables.ReadPlcTagTableConstants(softwarePath,tablePath,kind,unitName,unitKind,offset,limit);

        [McpServerTool(Name="ManagePlcTableEntries"), Description("[L2][PLC-Software][WRITE] Entries of one exact watch or force table (tableKind + tablePath under WatchAndForceTableGroup) as typed rows in native order: PlcWatchTableEntry (Name, Address, DisplayFormat, MonitorTrigger, ModifyTrigger, ModifyValue, ModifyIntention), PlcForceTableEntry (Name, Address, DisplayFormat, MonitorTrigger, ForceValue, ForceIntention) and comment rows (PlcTableCommentEntry). Watch tables also accept createComment (PlcTableCommentEntryComposition.Create), deleteEntry (0-based entryIndex from a read, confirmDelete=true) and deleteTable (PlcWatchTable.Delete, confirmDelete=true, verified absent), each counted back; tag rows are added with SetWatchTableModifyValue (SimaticML round trip - the typed API only creates comment rows); force tables are read-only here on purpose. Configuration values only, never online data. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManagePlcTableEntries(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tableKind: watch | force.")] string tableKind,
            [Description("tablePath: 'Group/Table' path under the watch-and-force-table group, e.g. 'MCP_W/MCP_WT' (bare name for a root table).")] string tablePath,
            [Description("action: read | createComment | deleteEntry | deleteTable (force tables: read only).")] string action="read",
            [Description("entryIndex: 0-based row index from a read, for deleteEntry.")] int entryIndex=-1,
            [Description("confirmDelete: must be true together with dryRun=false for deleteEntry / deleteTable.")] bool confirmDelete=false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true,
            [Description("offset: first row to return (paging).")] int offset=0,
            [Description("limit: maximum rows to return.")] int limit=200)
            => _tables.ManagePlcTableEntries(softwarePath,tableKind,tablePath,action,entryIndex,confirmDelete,dryRun,offset,limit);
    }
}

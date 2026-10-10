using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcCausalTraceTools
    {
        private readonly PlcOrganisationPortService _session;
        public PlcCausalTraceTools(PlcOrganisationPortService service) => _session = service;
        [McpServerTool(Name = "TraceTagCause"), Description("[L2][Online-Monitoring] Answer 'why is tag X this value / what sets it' by static analysis of the OFFLINE project. Read-only: exports code blocks to SimaticML and finds every network that WRITES the tag (LAD coils S/R/=, or StructuredText ':=' assignments) plus the gating condition operands in those networks. Cross-reference service is not needed. Then live-read the returned gatingConditions with GetPlcLiveValuesS7 to see which condition is currently driving the value. Tip: pass blockScope to limit which blocks are scanned (faster). NOTE: block export needs the project OFFLINE in TIA — Openness cannot export blocks while it is connected online to the PLC. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult TraceTagCauseV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. '安全PLC'.")] string softwarePath,
            [Description("tag: symbol to trace. Accepts a full path ('Crew_Data.Saddle_locationX') or a member name ('故障代码'). Quotes/whitespace are ignored.")] string tag,
            [Description("blockScope: optional regex to limit scanned code blocks by name (e.g. 'FC|Main'). Empty scans all code blocks.")] string blockScope = "")
            => RuntimeToolContract.Run("TraceTagCause", true, true, () =>
            {
                return TraceTagCause(softwarePath, tag, blockScope);
            });

        public ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
        {
            try
            {
                var result = _session.TraceTagCause(softwarePath, tag, blockScope);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"TraceTagCause failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "TraceTagCauseLive"), Description("[L2][Online-Monitoring] Live causal trace: runs the offline TraceTagCause to find what writes a tag and its gating conditions, then resolves each gating operand to an absolute address via the PLC tag table and LIVE-READS the resolvable ones over S7 (port 102) — so you see which interlock/condition is currently TRUE and driving the value. Read-only. Operands that are DB members / optimized / symbolic have no absolute PLC-tag address and are returned unresolved (read those via OPC UA). Requires the CPU ip; for the offline-only trace use TraceTagCause. NOTE: the offline block export needs the project OFFLINE in TIA (Openness cannot export in online mode); the S7 live-read is a separate direct connection, unaffected by going offline. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult TraceTagCauseLiveV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. '安全PLC'.")] string softwarePath,
            [Description("tag: symbol to trace (full path or member name; quotes/whitespace ignored).")] string tag,
            [Description("ip: CPU IP address for the live read, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("blockScope: optional regex to limit scanned code blocks by name. Empty scans all.")] string blockScope = "",
            [Description("expectModuleContains: optional identity guard substring (e.g. '1211C').")] string expectModuleContains = "")
            => RuntimeToolContract.Run("TraceTagCauseLive", true, true, () =>
            {
                if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("ip is required for live tracing.", nameof(ip));
                return TraceTagCauseLive(softwarePath, tag, ip, rack, slot, blockScope, expectModuleContains);
            });

        public ResponseJsonReport TraceTagCauseLive(string softwarePath, string tag, string ip, int rack = 0, int slot = 1, string blockScope = "", string expectModuleContains = "")
        {
            try
            {
                var result = _session.TraceTagCauseLive(softwarePath, tag, ip, rack, slot, blockScope, expectModuleContains);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"TraceTagCauseLive failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

    }
}

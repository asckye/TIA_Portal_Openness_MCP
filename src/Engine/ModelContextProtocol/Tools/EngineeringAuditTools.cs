using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class EngineeringAuditTools
    {
        private readonly EngineeringAuditService _audit;

        public EngineeringAuditTools(EngineeringAuditService service) => _audit = service;

        [McpServerTool(Name = "ReadPlcBlockScopes"), Description("[L2][PLC-Software][READ] Recursively discover blocks in the PLC root, every software unit and safety unit, including system block groups. Each result retains unitName, unitKind and exact blockPath. Enumeration failures fail the operation, never masquerade as an empty scope. offset/limit paginate the complete inventory; no compilation or cross-reference query.")]
        public ResponseMessage ReadPlcBlockScopes([Description("Exact PLC software name/path; empty selects project only where documented.")] string softwarePath, [Description("Zero-based page offset, at least 0.")] int offset = 0, [Description("Page size 1..1000.")] int limit = 100)
            => _audit.ReadPlcBlockScopes(softwarePath, offset, limit);

        [McpServerTool(Name = "ManagePlcBlockDocuments"), Description("[L2][PLC-Software][WRITE] Exact scoped PLC block list/read/export/import using SIMATIC SD. unitName empty selects root; otherwise unitKind unit/safety. groupPath is relative to that scope; name is a single block/document basename. Existing output files are refused. Default dryRun=true. Actual import requires an Offline PLC. Import result distinguishes exact existence verification from unknown content completeness; verifyDocumentReadback exports one imported block to a fresh retained subdirectory and compares .s7dcl/.s7res text after line-ending normalization (sdDocumentsMatch). SIMATIC SD excludes some original properties; comparison is not proof of a lossless project round trip. No automatic save, compile or download.")]
        public ResponseMessage ManagePlcBlockDocuments([Description("Exact PLC software name/path; empty selects project only where documented.")] string softwarePath, [Description("list | read | export | import. Operation name from the supported actions in the tool description.")] string action, [Description("One exact block name or SIMATIC SD basename without extension.")] string name = "", [Description("Exact relative user group path; empty selects the scope root.")] string groupPath = "", [Description("Exact software/safety unit name; empty selects PLC root.")] string unitName = "", [Description("unit | safety. Ignored for empty unitName.")] string unitKind = "unit", [Description("Existing absolute directory on the MCP server.")] string directoryPath = "", [Description("None | Override | SkipInactiveCultures | ActivateInactiveCultures.")] string importOption = "Override", [Description("true previews without invoking the native mutation/export; false executes.")] bool dryRun = true, [Description("Export imported block into a fresh retained folder and compare SD text.")] bool verifyDocumentReadback = false)
            => _audit.ManagePlcBlockDocuments(softwarePath, action, name, groupPath, unitName, unitKind, directoryPath, importOption, dryRun, verifyDocumentReadback);

    }
}

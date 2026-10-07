using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ImportStagingTools
    {
        private readonly Lazy<ImportStagingStore> store;
        public ImportStagingTools() { store = new Lazy<ImportStagingStore>(() => ImportStagingStore.Create(McpServer.ReleaseKey)); }
        internal ImportStagingTools(ImportStagingStore store) { this.store = new Lazy<ImportStagingStore>(() => store); }
        private CallToolResult Run(string tool, StagedTextFile[]? files = null, string batchId = "", bool dryRun = true)
        {
            Envelope result;
            try { result = store.Value.Run(tool, files, batchId, dryRun, InvocationJournal.CorrelationId); }
            catch (IOException ex) { result = ImportStagingStore.Unavailable(tool, McpServer.ReleaseKey, ex, InvocationJournal.CorrelationId); }
            var mapped = McpResult.From(result);
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        [McpServerTool(Name = "StageImportFiles"), Description("[L1][PLC-Software][FILE] Stage caller text on the TIA machine under the installed bundle staging/session folder next to runtime. files contains fileName, kind and content; kinds scl, simaticml, tagtable, udt, and V20/V21 s7dcl/s7res (V20 requires pairs; V21 resources are optional). ASCII without BOM for external sources, UTF-8 Document XML otherwise. Preview by default, dryRun=false requires Workbench approval when enabled. Up to 128 files/32 MiB per session, 4 MiB per file, no overwrite or caller destination. Returns directory, file paths and SHA-256 for reviewed imports. Import/generation requires separate calls. Use ListStagedImportFiles and CleanupStagedImportFiles before ending the session.")]
        public CallToolResult StageImportFiles([Description("One batch of fileName, kind and text content; server owns the destination.")] StagedTextFile[] files, [Description("Preview only by default; false applies after approval.")] bool dryRun = true) => Run("StageImportFiles", files, dryRun: dryRun);
        [McpServerTool(Name = "ListStagedImportFiles"), Description("[L1][PLC-Software][READ] List retained staged import batches in this MCP session, including absolute TIA-machine paths and SHA-256. No TIA calls or file writes.")]
        public CallToolResult ListStagedImportFiles() => Run("ListStagedImportFiles");
        [McpServerTool(Name = "CleanupStagedImportFiles"), Description("[L1][PLC-Software][FILE] Delete one server-owned staged batch selected by batchId from this session's list. Preview by default; dryRun=false requires Workbench approval when enabled. No caller filesystem path or recursive deletion. Import recovery exports are separate and retained.")]
        public CallToolResult CleanupStagedImportFiles([Description("Exact batchId from ListStagedImportFiles in this session.")] string batchId, [Description("Preview only by default; false applies after approval.")] bool dryRun = true) => Run("CleanupStagedImportFiles", batchId: batchId, dryRun: dryRun);
    }
}

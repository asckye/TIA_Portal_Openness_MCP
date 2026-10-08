using System;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.Json;
using System.IO;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class ImportStagingHostLifetime : IDisposable
    {
        private readonly Lazy<ImportStagingSession> session = new Lazy<ImportStagingSession>(() => new ImportStagingSession(Guid.NewGuid().ToString("N")));
        internal ImportStagingSession Session => session.Value;
        public void Dispose() { if (session.IsValueCreated) session.Value.Dispose(); }
    }
    [McpServerToolType]
    internal sealed class ImportStagingTools
    {
        private readonly Lazy<ImportStagingStore> store;
        private readonly ConditionalWeakTable<ImportStagingSession, Lazy<ImportStagingStore>> sessionStores = new ConditionalWeakTable<ImportStagingSession, Lazy<ImportStagingStore>>();
        private static readonly ConcurrentDictionary<string, ImportStagingSession> HttpSessions = new ConcurrentDictionary<string, ImportStagingSession>(StringComparer.Ordinal);
        private static readonly AsyncLocal<ImportStagingSession?> CurrentSession = new AsyncLocal<ImportStagingSession?>();
        internal static ImportStagingSession? CurrentOwner => CurrentSession.Value;
        internal const string SessionMetadata = "tiaMcpStagingSession";
        internal static bool HttpTransport { get; set; }
        internal static void RegisterHttpSession(ImportStagingSession session) => HttpSessions[session.SessionId] = session;
        internal static void RemoveHttpSession(ImportStagingSession session) => HttpSessions.TryRemove(session.SessionId, out _);
        internal static IDisposable? UseSession(RequestContext<CallToolRequestParams> request)
        {
            if (!HttpTransport || CurrentSession.Value != null) return null;
            ImportStagingSession? session = null;
#if TIA_ENGINE_HOST
            // The SDK owns the HTTP session; the staging owner records that id as its MCP session id.
            string? mcpSessionId = request.Server.SessionId;
            if (mcpSessionId != null)
                foreach (var candidate in HttpSessions.Values)
                    if (candidate.McpSessionId == mcpSessionId) { session = candidate; break; }
#else
            var meta = JsonSerializer.SerializeToNode(request.Params?.Meta, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            string? token = (string?)meta?[SessionMetadata];
            if (token != null) HttpSessions.TryGetValue(token, out session);
#endif
            if (session == null) throw new InvalidOperationException("The HTTP MCP session is unavailable; initialize a new session.");
            return new SessionScope(session);
        }
        private sealed class SessionScope : IDisposable
        {
            private readonly ImportStagingSession? previous = CurrentSession.Value;
            private readonly IDisposable request;
            internal SessionScope(ImportStagingSession session) { request = session.EnterRequest(); CurrentSession.Value = session; }
            public void Dispose() { CurrentSession.Value = previous; request.Dispose(); }
        }
        public ImportStagingTools() : this(new ImportStagingHostLifetime()) { }
        public ImportStagingTools(ImportStagingHostLifetime lifetime) { store = new Lazy<ImportStagingStore>(() => ImportStagingStore.Create(McpServer.ReleaseKey, lifetime.Session)); }
        internal ImportStagingTools(ImportStagingStore store) { this.store = new Lazy<ImportStagingStore>(() => store); }
        private CallToolResult Run(string tool, StagedTextFile[]? files = null, string batchId = "", bool dryRun = true)
        {
            Envelope result;
            try
            {
                var selected = CurrentSession.Value == null ? store : sessionStores.GetValue(CurrentSession.Value,
                    session => new Lazy<ImportStagingStore>(() => ImportStagingStore.Create(McpServer.ReleaseKey, session)));
                result = selected.Value.Run(tool, files, batchId, dryRun, InvocationJournal.CorrelationId);
            }
            catch (IOException ex) { result = ImportStagingStore.Unavailable(tool, McpServer.ReleaseKey, ex, InvocationJournal.CorrelationId); }
            var mapped = McpResult.From(result);
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
        [McpServerTool(Name = "StageImportFiles"), Description("[L1][PLC-Software][FILE] Stage caller text on the TIA machine under the installed bundle staging/session folder next to runtime. files contains fileName, kind and content; kinds scl, simaticml, tagtable, udt, and V20/V21 s7dcl/s7res (V20 requires pairs; V21 resources are optional). ASCII without BOM for external sources, UTF-8 Document XML otherwise. Preview by default, dryRun=false requires Workbench approval when enabled. Up to 128 files/32 MiB per session, 4 MiB per file, no overwrite or caller destination. Returns directory, file paths and SHA-256 for reviewed imports. Import/generation requires separate calls. Use ListStagedImportFiles and CleanupStagedImportFiles before ending the session.")]
        public CallToolResult StageImportFiles([Description("One batch of fileName, kind and text content; server owns the destination.")] StagedTextFile[] files, [Description("Preview only by default; false applies after approval.")] bool dryRun = true) => Run("StageImportFiles", files, dryRun: dryRun);
        [McpServerTool(Name = "ListStagedImportFiles"), Description("[L1][PLC-Software][READ] List every retained batch under bundle staging, including sessionId, batchId, createdUtc, files, bytes, currentSession, mcpSessionId, hostInstanceId and ownerState (current/live-other/ended/unknown). Missing/invalid manifests are identified=false and cannot be cleaned. Includes absolute TIA-machine paths and SHA-256. No TIA calls or file writes.")]
        public CallToolResult ListStagedImportFiles() => Run("ListStagedImportFiles");
        [McpServerTool(Name = "CleanupStagedImportFiles"), Description("[L1][PLC-Software][FILE] Delete staging-owned leaves of an identified batchId from ListStagedImportFiles, including ended MCP sessions; live-other and unknown owners are refused. Unknown entries are listed by preview and retained on apply with folderRetained=true and a warning. Preview by default; dryRun=false requires Workbench approval when enabled. No caller filesystem path or recursive deletion. Import recovery exports are separate and retained.")]
        public CallToolResult CleanupStagedImportFiles([Description("Exact identified batchId from ListStagedImportFiles, including earlier sessions.")] string batchId, [Description("Preview only by default; false applies after approval.")] bool dryRun = true) => Run("CleanupStagedImportFiles", batchId: batchId, dryRun: dryRun);
    }
}

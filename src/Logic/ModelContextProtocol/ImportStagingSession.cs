using System;
using System.Collections.Concurrent;

namespace TiaMcp.Logic.ModelContextProtocol
{
    // Transport lifetimes own these leases; completing a tool call does not end a session.
    public sealed class ImportStagingSession : IDisposable
    {
        private static readonly string HostIdentity = Guid.NewGuid().ToString("N");
        private static readonly ConcurrentDictionary<string, ImportStagingSession> Sessions = new ConcurrentDictionary<string, ImportStagingSession>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private int active;
        private bool closing;
        public string SessionId { get; }
        public string McpSessionId { get; }
        public string HostInstanceId => HostIdentity;
        public ImportStagingSession(string mcpSessionId) : this(Guid.NewGuid().ToString("N"), mcpSessionId) { }
        internal ImportStagingSession(string sessionId, string mcpSessionId)
        {
            if (!Guid.TryParseExact(sessionId, "N", out _) || string.IsNullOrWhiteSpace(mcpSessionId) || mcpSessionId.Length > 256)
                throw new ArgumentException("A server-issued staging and MCP session identity is required.");
            SessionId = sessionId; McpSessionId = mcpSessionId;
        }
        internal void RegisterBatch()
        {
            var registered = Sessions.GetOrAdd(SessionId, this);
            if (!ReferenceEquals(registered, this)) throw new ArgumentException("Staging session identity is already registered.");
        }
        public IDisposable EnterRequest()
        {
            lock (gate)
            {
                if (closing) throw new InvalidOperationException("The MCP session has ended; start a new session.");
                active++; return new Request(this);
            }
        }
        private sealed class Request : IDisposable
        {
            private ImportStagingSession? owner;
            internal Request(ImportStagingSession owner) { this.owner = owner; }
            public void Dispose()
            {
                var session = System.Threading.Interlocked.Exchange(ref owner, null);
                if (session != null) lock (session.gate) session.active--;
            }
        }
        internal static string OwnerState(string host, string session, string mcp)
        {
            if (host != HostIdentity || !Sessions.TryGetValue(session, out var owner) || owner.McpSessionId != mcp) return "unknown";
            lock (owner.gate) return owner.closing && owner.active == 0 ? "ended" : "live-other";
        }
        public void Dispose() { lock (gate) closing = true; }
    }
}

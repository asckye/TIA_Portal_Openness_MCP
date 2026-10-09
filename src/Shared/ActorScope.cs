using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TiaOpenness.Shared
{
    // Set at the host transport boundary, never from tool arguments or client metadata.
    internal sealed class ActorScope : IDisposable
    {
        internal const string Mcp = "mcp";
        internal const string Workbench = "workbench";
        private static readonly AsyncLocal<ActorScope?> Current = new AsyncLocal<ActorScope?>();
        private static readonly ConditionalWeakTable<object, SessionIdentity> Sessions = new ConditionalWeakTable<object, SessionIdentity>();
        private static readonly string AnonymousSession = Guid.NewGuid().ToString("N");
        private sealed class SessionIdentity { internal readonly string Id = Guid.NewGuid().ToString("N"); }
        private readonly ActorScope? previous;
        private readonly string actor;
        private readonly string? session, operatorCallId;
        private bool disposed;
        internal static string Actor => Current.Value?.actor ?? Mcp;
        internal static string? McpSession => Current.Value?.session;
        internal static string? OperatorCallId => Current.Value?.operatorCallId;
        internal static bool IsValid(string? value) => value == Mcp || value == Workbench;

        private ActorScope(string actor, string? session, string? operatorCallId)
        {
            previous = Current.Value;
            this.actor = actor; this.session = session; this.operatorCallId = operatorCallId;
            Current.Value = this;
        }
        // Nested bridge/batch calls retain the initiating transport and attached session.
        internal static IDisposable EnterCall(string? mcpSessionId, object? transportSession = null)
            => new ActorScope(Actor, Current.Value?.session ?? HashSession(mcpSessionId
                ?? (transportSession == null ? AnonymousSession : Sessions.GetValue(transportSession, _ => new SessionIdentity()).Id)), OperatorCallId);
        internal static IDisposable EnterWorkbench(string mcpSessionId, string? operatorCallId = null)
        {
            if (string.IsNullOrWhiteSpace(mcpSessionId)) throw new ArgumentException("A host session id is required.", nameof(mcpSessionId));
            if (operatorCallId != null && !Guid.TryParseExact(operatorCallId, "N", out _))
                throw new ArgumentException("An operator call id must be a 32-character GUID.", nameof(operatorCallId));
            return new ActorScope(Workbench, HashSession(mcpSessionId), operatorCallId);
        }
        private static string HashSession(string id)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "").ToLowerInvariant();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Current.Value = previous;
        }
    }
}

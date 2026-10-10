using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class OperatorConnection(HostRegistration registration, Func<CancellationToken, Task<IHostSession>> create, Func<int, bool> isOwner)
{
    internal async Task Serve(Stream pipe, int? verifiedPeer, CancellationToken token)
    {
        // A missing kernel identity closes the connection without reading or serving a request.
        if (verifiedPeer == null) return;
        IHostSession? created = null;
        IDisposable? membership = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await OperatorProtocol.Read<OperatorHandshake>(pipe, deadline.Token).ConfigureAwait(false);
            if (request.Workbench.Pid != verifiedPeer) return;
            IHostSession? session = null;
            string? error = request.Version != 1 ? "UNSUPPORTED_VERSION" : null;
            if (error == null && request.Action == "attach")
            {
                session = registration.Find(request.SessionKey!);
                if (session == null || session.Closed.IsCancellationRequested) error = "SESSION_NOT_FOUND";
                else if (session.OperatorOwned && !isOwner(verifiedPeer.Value)) error = "ACCESS_DENIED";
            }
            if (error == null && request.Action == "create")
            {
                if (!isOwner(verifiedPeer.Value)) error = "CREATE_NOT_ALLOWED";
                else { session = created = await create(deadline.Token).ConfigureAwait(false); membership = registration.Register(session); }
            }
            using var attachment = error == null ? session?.AttachOperator() : null;
            await OperatorProtocol.Write(pipe, new OperatorHandshakeResult { Accepted = error == null, Error = error,
                SessionKey = session?.SessionKey, Sessions = request.Action == "list" ? registration.List() : session == null ? [] : [session.Snapshot()] }, deadline.Token).ConfigureAwait(false);
            if (error != null || session == null) return;
            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var tool in session.OperatorTools) tools.Add(tool);
            await using var server = McpServerFactory.Create(new StreamServerTransport(pipe, pipe, "TiaMcp.Operator"), new McpServerOptions {
                ServerInfo = new Implementation { Name = "TiaMcp.Operator", Version = "1" }, ServerInstructions = session.Instructions,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ToolCollection = tools } } });
            await using var notifications = new SessionNotifications(session, server, OperatorEndpoint.Report);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, session.Closed);
            await server.RunAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally { created?.Dispose(); membership?.Dispose(); }
    }
}

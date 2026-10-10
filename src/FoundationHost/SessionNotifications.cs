using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class SessionNotifications : IAsyncDisposable
{
    private readonly IHostSession session;
    private readonly IMcpServer server;
    private readonly Action<Exception> report;
    private readonly Channel<JsonObject> pending = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly Task sending;
    internal SessionNotifications(IHostSession session, IMcpServer server, Action<Exception> report)
    {
        this.session = session; this.server = server; this.report = report;
        session.StateChanged += Publish;
        sending = Send();
    }
    private void Publish() => pending.Writer.TryWrite(session.Snapshot());
    private async Task Send()
    {
        try
        {
            await foreach (var state in pending.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await server.SendNotificationAsync(OperatorProtocol.SessionStateNotification, state, cancellationToken: deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or McpException)
        { if (!stopping.IsCancellationRequested) report(error); }
    }
    public async ValueTask DisposeAsync()
    {
        session.StateChanged -= Publish;
        pending.Writer.TryComplete();
        stopping.CancelAfter(TimeSpan.FromSeconds(2));
        await sending.ConfigureAwait(false);
        stopping.Dispose();
    }
}

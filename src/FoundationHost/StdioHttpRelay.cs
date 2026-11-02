using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal static class StdioHttpRelay
{
    internal static HttpClient CreateClient() => new(new HttpClientHandler {
        Proxy = new RemoteProxyPolicy(), UseProxy = true, AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<int> Run(Uri endpoint, string key, TextReader input, TextWriter output, HttpClient? client = null, CancellationToken token =
            default)
    {
        HttpAccessKeyStore.Validate(key);
        using var ownedClient = client == null ? CreateClient() : null;
        client ??= ownedClient!;
        // Keep transport logging disabled: HTTP failures must never echo headers or response bodies.
        await using var connector = new SseClientTransport(new SseClientTransportOptions {
            Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, Name = "remote-relay",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString() }
        }, client, NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var transport = await connector.ConnectAsync(token);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receive = Receive(transport, output, lifetime.Token);
        var pending = new List<Task>();
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Send(JsonRpcMessage message)
        {
            try { await transport.SendMessageAsync(message, lifetime.Token); }
            catch (Exception error) { failure.TrySetResult(error); throw; }
        }
        try
        {
            while (true)
            {
                var read = input.ReadLineAsync(lifetime.Token).AsTask();
                var completed = await Task.WhenAny(read, receive, failure.Task);
                if (completed == failure.Task) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(await failure.Task).Throw();
                if (completed == receive) { await receive; throw new IOException("Remote MCP transport closed."); }
                string? line = await read;
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var message = JsonSerializer.Deserialize<JsonRpcMessage>(line, ModelContextProtocol.McpJsonUtilities.DefaultOptions)
                    ?? throw new JsonException("Expected a JSON-RPC message.");
                // An HTTP POST can remain open until the result arrives; keep reading cancellation, notifications and client replies.
                pending.RemoveAll(task => task.IsCompletedSuccessfully);
                var send = Send(message);
                pending.Add(send);
                // Notifications and replies receive an HTTP 202 immediately. Preserve their ordering (notably initialized)
                // while requests remain concurrent so a later cancellation or server-request reply can still pass through.
                if (message is not JsonRpcRequest) await send;
                var failed = pending.FirstOrDefault(task => task.IsFaulted);
                if (failed != null) await failed;
                if (receive.IsCompleted) await receive;
            }
            await Task.WhenAll(pending);
            await transport.DisposeAsync();
            await receive;
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            try { await receive; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) /* swallow(teardown): relay shutdown stops its background receive loop */ { }
        }
    }

    private static async Task Receive(ITransport transport, TextWriter output, CancellationToken token)
    {
        await foreach (var message in transport.MessageReader.ReadAllAsync(token))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(message, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            await output.FlushAsync(token);
        }
    }
}

using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace TiaMcp.FoundationHost;

internal sealed class StdioFrameTransport(ITransport input, Stream output) : ITransport
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly TaskCompletionSource _sendersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _writeFailure;
    private volatile bool _disposing;
    private int _senders;
    private Task? _disposal;
    public string? SessionId => input.SessionId;
    public ChannelReader<JsonRpcMessage> MessageReader => input.MessageReader;

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _senders++;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, McpJsonUtilities.DefaultOptions);
            var frame = new byte[payload.Length + 1];
            payload.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposing, this);
                if (_writeFailure != null)
                    throw new IOException("STDIO output failed during an earlier frame.", _writeFailure);
                // The SDK cancels JSON and its delimiter separately. Once a frame starts, finish it
                // with its newline before admitting another writer, even if its request is cancelled.
                await output.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                _writeFailure ??= error;
                throw;
            }
            finally { _writeLock.Release(); }
        }
        finally
        {
            lock (_lifetimeLock)
                if (--_senders == 0 && _disposing)
                    _sendersDrained.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            if (_disposal != null)
                return new ValueTask(_disposal);
            _disposing = true;
            if (_senders == 0)
                _sendersDrained.TrySetResult();
            return new ValueTask(_disposal = DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        bool writeLockHeld = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            try
            {
                await _sendersDrained.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                await _writeLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                writeLockHeld = true;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // A blocked peer can keep stdout full indefinitely. Only final teardown may cut a frame.
                Console.Error.WriteLine("STDIO frame cut at shutdown after the one-second drain timeout.");
            }
        }
        finally
        {
            try
            {
                try
                { await input.DisposeAsync().ConfigureAwait(false); }
                finally { await output.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                // After a timeout a sender can still own the semaphore and must be able to release it.
                if (writeLockHeld)
                    _writeLock.Dispose();
            }
        }
    }
}

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;

namespace TiaMcp.WorkerProtocol.AsyncPreview;

public sealed class FrameLimits
{
    public const int HardMaxFrames = 32;
    public const int HardMaxExchangeBytes = 4 * 1024 * 1024;
    public int MaxFrames { get; }
    public int MaxFrameBytes { get; }
    // Includes four-byte frame headers, even when the adapter returns decoded frames.
    public int MaxExchangeBytes { get; }
    public FrameLimits(int maxFrames = HardMaxFrames, int maxFrameBytes = StrictCodec.MaxFrameBytes,
        int maxExchangeBytes = HardMaxExchangeBytes)
    {
        if (maxFrames is < 1 or > HardMaxFrames || maxFrameBytes is < 1 or > StrictCodec.MaxFrameBytes ||
            maxExchangeBytes is < 5 or > HardMaxExchangeBytes)
            throw new IdentityViolation("ExchangeLimitsInvalid");
        MaxFrames = maxFrames; MaxFrameBytes = maxFrameBytes; MaxExchangeBytes = maxExchangeBytes;
    }
}

// One instance per request; no retry or cross-request response queue. Ownership transfers
// on DispatchAsync invocation, not on success. All methods' synchronous portions must
// return promptly. DispatchAsync must honor cancellation before/between partial writes.
// The session conservatively considers invoking it a dispatch attempt, even if it throws.
// ReadFramesAsync must apply limits BEFORE allocating/reading frame payloads; the session
// independently checks emitted frames. It must terminate immediately after this request's
// reply boundary (not wait for process-wide EOF). No stdout logs may be mixed into frames.
// Abort must be prompt, idempotent, nonthrowing and unblock any pending dispatch/read/dispose.
// Disposal must be safe after Abort, including while a canceled async operation unwinds.
// A broken adapter cannot be made resource-safe merely by abandoning its Task.
public interface IAsyncV2Exchange : IAsyncDisposable
{
    ValueTask DispatchAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(FrameLimits limits, CancellationToken cancellationToken);
    void Abort();
}

// A source is already scoped to ONE exchange, not a process-wide pipe. Zero means its
// explicit end boundary. It must never return more bytes than the supplied buffer size.
public interface IAsyncFrameSource
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

// Optional adapter building block for the preview's four-byte little-endian framing.
// Caps are enforced incrementally, before payload allocation. No pipes/processes here.
public static class BoundedAsyncFrames
{
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(IAsyncFrameSource source,
        FrameLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(limits);
        int count = 0, total = 0;
        byte[] header = new byte[4];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = await Read(source, header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (first == 0) yield break;
            if (count == limits.MaxFrames || total > limits.MaxExchangeBytes - 4)
                throw new IdentityViolation("ExchangeBoundsExceeded");
            await Fill(source, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 1 || size > limits.MaxFrameBytes || size > limits.MaxExchangeBytes - total - 4)
                throw new IdentityViolation("ExchangeBoundsExceeded");
            total += size + 4; count++;
            byte[] payload = new byte[size];
            await Fill(source, payload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return payload;
        }
    }
    static async ValueTask Fill(IAsyncFrameSource source, Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            int read = await Read(source, buffer, token).ConfigureAwait(false);
            if (read == 0) throw new IdentityViolation("TruncatedFrame");
            buffer = buffer[read..];
        }
    }
    static async ValueTask<int> Read(IAsyncFrameSource source, Memory<byte> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Session separately bounds MoveNextAsync even if a source ignores this token.
        int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read < 0 || read > buffer.Length) throw new IdentityViolation("InvalidReadCount");
        return read;
    }
}

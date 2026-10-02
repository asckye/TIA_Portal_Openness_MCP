using System.Buffers.Binary;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;
namespace TiaMcp.WorkerProtocol.HostPreview;

// Fake IPC only: immutable, finite, length-prefixed response bytes, never a pipe.
// This framing is a preview, not the legacy newline wire or modern JSON-RPC framing.
public sealed class BoundedMemoryExchange : IV2Exchange
{
    public const int MaxFrames = 32;
    public const int MaxExchangeBytes = 4 * 1024 * 1024;
    readonly byte[] response;
    readonly CancellationToken cancellation;
    bool attempted;
    bool readStarted;
    public int DispatchAttempts { get; private set; }

    public BoundedMemoryExchange(ReadOnlyMemory<byte> response, CancellationToken cancellation = default)
    {
        if (response.Length > MaxExchangeBytes) throw new IdentityViolation("ExchangeSizeInvalid");
        this.response = response.ToArray(); // Bound allocation; caller mutation cannot change it.
        this.cancellation = cancellation;
    }

    public IEnumerable<ReadOnlyMemory<byte>> Exchange(ReadOnlyMemory<byte> request)
    {
        if (attempted) throw new IdentityViolation("ExchangeAlreadyAttempted");
        attempted = true;
        cancellation.ThrowIfCancellationRequested();
        if (request.Length is < 1 or > StrictCodec.MaxFrameBytes)
            throw new IdentityViolation("RequestSizeInvalid");
        DispatchAttempts++;
        return ReadFrames();
    }

    IEnumerable<ReadOnlyMemory<byte>> ReadFrames()
    {
        if (readStarted) throw new IdentityViolation("ExchangeAlreadyConsumed");
        readStarted = true;
        int offset = 0, count = 0;
        while (offset < response.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > MaxFrames || response.Length - offset < sizeof(int))
                throw new IdentityViolation("ExchangeFramingInvalid");
            int size = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            if (size is < 1 or > StrictCodec.MaxFrameBytes || size > response.Length - offset)
                throw new IdentityViolation("ExchangeFramingInvalid");
            // Codec parses synchronously. Each emitted frame is separately owned.
            yield return new ReadOnlyMemory<byte>(response, offset, size).ToArray();
            offset += size;
        }
        cancellation.ThrowIfCancellationRequested();
    }
}

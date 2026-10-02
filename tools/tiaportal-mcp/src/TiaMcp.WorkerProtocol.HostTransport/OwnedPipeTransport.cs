using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TiaMcp.WorkerProtocol.AsyncPreview;
using TiaMcp.WorkerProtocol.JsonV2;

namespace TiaMcp.WorkerProtocol.HostTransport;

// Isolated preview. No existing production launcher invokes this class.
// Only launches a caller-selected executable: never adopts a PID or discovers workers.
public sealed class OwnedPipeTransport : IAsyncDisposable
{
    readonly Process process;
    readonly long started;
    readonly Stream input, output;
    readonly CancellationTokenSource lifetime = new();
    readonly Task stderrDrain;
    int aborted, active;
    volatile bool confirmedExited;
    Task? cleanup;
    public int OwnedProcessId { get; }
    public bool IdentityVerified { get; private set; }
    public bool HasExited { get { if (confirmedExited) return true; try { return process.HasExited; } catch { return false; } } }
    public bool ShutdownConfirmed => confirmedExited;
    OwnedPipeTransport(Process process)
    {
        this.process = process; OwnedProcessId = process.Id;
        started = process.StartTime.ToUniversalTime().Ticks;
        input = process.StandardInput.BaseStream; output = process.StandardOutput.BaseStream;
        // Drain with a fixed buffer and retain no logs, avoiding both deadlock and unbounded memory.
        stderrDrain = Drain(process.StandardError.BaseStream);
    }
    static async Task Drain(Stream stream)
    {
        try { byte[] buffer = new byte[1024]; while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public static async Task<OwnedPipeTransport> StartAsync(string executable, IEnumerable<string> arguments,
        AsyncJsonSession session, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] token = RandomNumberGenerator.GetBytes(32);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["TIA_TRANSPORT_OWNER_TOKEN"] = Convert.ToHexString(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var process = new Process { StartInfo = info };
        OwnedPipeTransport? owner = null;
        bool launched = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("ProcessStartFailed");
            launched = true;
            owner = new OwnedPipeTransport(process);
            // Start is synchronous OS work; the same startup budget still applies afterwards.
            deadline.Token.ThrowIfCancellationRequested();
            byte[] identity = new byte[36];
            await owner.output.ReadExactlyAsync(identity, deadline.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(token, identity.AsSpan(0,32)) ||
                BinaryPrimitives.ReadInt32LittleEndian(identity.AsSpan(32,4)) != owner.OwnedProcessId ||
                owner.process.StartTime.ToUniversalTime().Ticks != owner.started)
                throw new IdentityViolation("OwnedProcessIdentityMismatch");
            owner.IdentityVerified = true;
            byte[]? hello = null;
            await foreach (var frame in BoundedAsyncFrames.ReadAsync(new ExchangeSource(owner.output),
                new FrameLimits(1), deadline.Token).ConfigureAwait(false)) hello = frame.ToArray();
            if (hello == null) throw new IdentityViolation("HelloRequired");
            session.AcceptHello(hello);
            deadline.Token.ThrowIfCancellationRequested();
            return owner;
        }
        catch
        {
            if (owner != null) await owner.DisposeAsync().ConfigureAwait(false);
            else
            {
                // Even partial construction owns only the exact object returned by our Start.
                if (launched)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                    if (!process.HasExited) throw new IdentityViolation("OwnedProcessExitUnconfirmed");
                }
                process.Dispose();
            }
            throw new IdentityViolation("OwnedProcessStartupRejected");
        }
        finally { info.Environment.Remove("TIA_TRANSPORT_OWNER_TOKEN"); CryptographicOperations.ZeroMemory(token); }
    }
    public IAsyncV2Exchange CreateExchange()
    {
        if (!IdentityVerified || Volatile.Read(ref aborted) != 0) throw new IdentityViolation("TransportUnavailable");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw new IdentityViolation("RequestAlreadyPending");
        return new Exchange(this);
    }
    public void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) != 0) return;
        try { lifetime.Cancel(); } catch { }
        try { input.Dispose(); } catch { }
        try { output.Dispose(); } catch { }
        // The Process object is retained from Start, never reconstructed from a PID.
        // Start time additionally guards identity. Never kill a process tree or unrelated PID.
        try { if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == started) process.Kill(entireProcessTree: false); } catch { }
        try { process.StandardError.Dispose(); } catch { }
    }
    public ValueTask DisposeAsync()
    {
        lock (lifetime) return new ValueTask(cleanup ??= Cleanup());
    }
    async Task Cleanup()
    {
        Abort();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
        try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        if (!HasExited) throw new IdentityViolation("OwnedProcessExitUnconfirmed");
        confirmedExited = true;
        process.Dispose();
    }
    sealed class Exchange(OwnedPipeTransport owner) : IAsyncV2Exchange
    {
        int dispatched, disposed, reading;
        bool ended;
        public async ValueTask DispatchAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref dispatched, 1) != 0)
                throw new IdentityViolation("ExchangeAlreadyUsed");
            if (request.Length < 1 || request.Length > StrictCodec.MaxFrameBytes) throw new IdentityViolation("ExchangeBoundsExceeded");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
            await owner.input.WriteAsync(header, linked.Token).ConfigureAwait(false);
            // Bounded chunks provide cancellation opportunities during backpressure.
            for (int offset = 0; offset < request.Length; offset += 4096)
                await owner.input.WriteAsync(request.Slice(offset, Math.Min(4096, request.Length-offset)), linked.Token).ConfigureAwait(false);
            await owner.input.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(FrameLimits limits,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref reading, 1) != 0 || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref dispatched) == 0) throw new IdentityViolation("ExchangeNotDispatched");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.lifetime.Token);
            await foreach (var frame in BoundedAsyncFrames.ReadAsync(new ExchangeSource(owner.output), limits, linked.Token).ConfigureAwait(false))
                yield return frame;
            ended = true;
        }
        public void Abort() => owner.Abort();
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                if (Volatile.Read(ref dispatched) != 0 && !ended) owner.Abort();
                Volatile.Write(ref owner.active, 0);
            }
            return ValueTask.CompletedTask;
        }
    }
    // Wire boundary: zero length ends ONE exchange; OS EOF is always premature.
    // Exposes the header unchanged to BoundedAsyncFrames, which checks it before allocation.
    sealed class ExchangeSource(Stream stream) : IAsyncFrameSource
    {
        readonly byte[] header = new byte[4];
        int headerOffset = 4, remaining;
        bool end;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (end) return 0;
            if (remaining == 0 && headerOffset == 4)
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                remaining = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (remaining == 0) { end = true; return 0; }
                headerOffset = 0;
            }
            if (headerOffset < 4)
            {
                int count = Math.Min(buffer.Length, 4-headerOffset);
                header.AsMemory(headerOffset,count).CopyTo(buffer); headerOffset += count; return count;
            }
            int read = await stream.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("PrematurePipeExit");
            remaining -= read; return read;
        }
    }
}

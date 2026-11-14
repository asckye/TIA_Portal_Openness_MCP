extern alias foundationhost;
using System.IO.Pipelines;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Abstractions;
using StdioFrameTransport = foundationhost::TiaMcp.FoundationHost.StdioFrameTransport;

namespace TiaMcp.EngineHost.Tests;

public sealed class StdioFramingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(128)]
    [InlineData(4096)]
    [InlineData(32768)]
    public async Task Cancellation_after_payload_keeps_notification_and_reply_on_separate_lines(int payloadSize)
    {
        var incoming = new Pipe();
        using var bytes = new CancelAfterWriteStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), bytes), bytes);
        var directory = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "stdio-frame",
            "cancellation-" + payloadSize + "-" + Guid.NewGuid().ToString("N"));
        int corrupted = 0;
        try
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                bytes.SetLength(0);
                using var cancellation = new CancellationTokenSource();
                bytes.CancelAfterWrite = cancellation;
                var notification = new JsonRpcNotification
                {
                    Method = "notifications/tiamcp/sessionState",
                    Params = new JsonObject { ["padding"] = new string('x', payloadSize) }
                };
                var deliveryError = await Record.ExceptionAsync(() => transport.SendMessageAsync(notification, cancellation.Token));
                await transport.SendMessageAsync(new JsonRpcResponse { Id = new RequestId(iteration), Result = new JsonObject() });
                var raw = bytes.ToArray();
                var lines = Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (deliveryError != null || lines.Length != 2)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, iteration + ".jsonl"), raw);
                    corrupted++;
                    continue;
                }
                using var first = JsonDocument.Parse(lines[0]);
                using var second = JsonDocument.Parse(lines[1]);
                Assert.Equal(notification.Method, first.RootElement.GetProperty("method").GetString());
                Assert.Equal(iteration, second.RootElement.GetProperty("id").GetInt32());
                Assert.True(cancellation.IsCancellationRequested);
            }
        }
        finally { await incoming.Writer.CompleteAsync(); }
        output.WriteLine($"Cancellation boundary stress: {corrupted}/100 corrupt frames; evidence {directory}");
        Assert.Equal(0, corrupted);
    }

    [Fact]
    public async Task Cancellation_before_sending_leaves_no_bytes()
    {
        var incoming = new Pipe();
        using var bytes = new MemoryStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), Stream.Null), bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendMessageAsync(Reply(1), cancellation.Token));
            Assert.Empty(bytes.ToArray());
            await transport.SendMessageAsync(Reply(2));
            using var reply = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal(2, reply.RootElement.GetProperty("id").GetInt32());
        }
        finally { await incoming.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task Cancellation_while_queued_does_not_interrupt_the_active_frame()
    {
        var incoming = new Pipe();
        using var bytes = new BlockingWriteStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), Stream.Null), bytes);
        using var activeCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var active = transport.SendMessageAsync(Reply(1), activeCancellation.Token);
        try
        {
            await bytes.Entered.Task.WaitAsync(TestDeadlines.Hang);
            var queued = transport.SendMessageAsync(Reply(2), queuedCancellation.Token);
            activeCancellation.Cancel();
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Empty(bytes.ToArray());
            bytes.Release.TrySetResult();
            await active.WaitAsync(TestDeadlines.Hang);
            await transport.SendMessageAsync(Reply(3));
            var lines = Encoding.UTF8.GetString(bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(3, second.RootElement.GetProperty("id").GetInt32());
            Assert.False(bytes.SawCancellableWrite);
        }
        finally
        {
            bytes.Release.TrySetResult();
            await active.WaitAsync(TestDeadlines.Hang);
            await incoming.Writer.CompleteAsync();
        }
    }

    [Fact]
    public async Task Concurrent_notifications_and_replies_remain_whole_frames()
    {
        var incoming = new Pipe();
        using var bytes = new YieldingWriteStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), Stream.Null), bytes);
        try
        {
            var sends = Enumerable.Range(0, 1000).Select(async id =>
            {
                await transport.SendMessageAsync(new JsonRpcNotification
                {
                    Method = "notifications/progress",
                    Params = new JsonObject { ["progressToken"] = id, ["progress"] = id }
                });
                await transport.SendMessageAsync(Reply(id));
            });
            await Task.WhenAll(sends).WaitAsync(TestDeadlines.Hang);
            var lines = Encoding.UTF8.GetString(bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2000, lines.Length);
            var replies = new HashSet<int>();
            var notifications = new HashSet<int>();
            foreach (var line in lines)
            {
                using var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("id", out var id))
                    Assert.True(replies.Add(id.GetInt32()));
                else
                {
                    Assert.Equal("notifications/progress", message.RootElement.GetProperty("method").GetString());
                    Assert.True(notifications.Add(message.RootElement.GetProperty("params").GetProperty("progressToken").GetInt32()));
                }
            }
            Assert.Equal(Enumerable.Range(0, 1000), replies.Order());
            Assert.Equal(Enumerable.Range(0, 1000), notifications.Order());
            Assert.Equal(1, bytes.MaximumWriters);
        }
        finally { await incoming.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task Partial_output_failure_refuses_subsequent_frames()
    {
        var incoming = new Pipe();
        using var bytes = new BrokenWriteStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), Stream.Null), bytes);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => transport.SendMessageAsync(Reply(1)));
            var partial = bytes.ToArray();
            Assert.NotEmpty(partial);
            await Assert.ThrowsAsync<IOException>(() => transport.SendMessageAsync(Reply(2)));
            Assert.Equal(partial, bytes.ToArray());
        }
        finally { await incoming.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task Serialization_failure_leaves_output_available_for_a_reply()
    {
        var incoming = new Pipe();
        using var bytes = new MemoryStream();
        await using var transport = new StdioFrameTransport(new StreamServerTransport(incoming.Reader.AsStream(), Stream.Null), bytes);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => transport.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/progress",
                Params = new JsonObject { ["progress"] = double.NaN }
            }));
            Assert.Empty(bytes.ToArray());
            await transport.SendMessageAsync(Reply(1));
            using var reply = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal(1, reply.RootElement.GetProperty("id").GetInt32());
        }
        finally { await incoming.Writer.CompleteAsync(); }
    }

    private static JsonRpcResponse Reply(int id) => new() { Id = new RequestId(id), Result = new JsonObject() };

    [Fact]
    public async Task Disposal_finishes_the_active_frame_and_rejects_queued_and_later_sends()
    {
        var input = new IdleTransport();
        using var bytes = new PartialBlockingWriteStream();
        var transport = new StdioFrameTransport(input, bytes);
        var active = transport.SendMessageAsync(Reply(1));
        Task? queued = null;
        try
        {
            await bytes.Entered.Task.WaitAsync(TestDeadlines.Hang);
            Assert.NotEmpty(bytes.ToArray());
            queued = transport.SendMessageAsync(Reply(2));
            var disposal = transport.DisposeAsync().AsTask();
            var repeatedDisposal = transport.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(bytes.DisposedWhileWriting);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendMessageAsync(Reply(3)));
            bytes.Release.TrySetResult();
            await active.WaitAsync(TestDeadlines.Hang);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(TestDeadlines.Hang);
            Assert.Same(disposal, repeatedDisposal);
            Assert.False(bytes.DisposedWhileWriting);
            Assert.Equal(1, bytes.Disposals);
            Assert.Equal(1, input.Disposals);
            var raw = bytes.ToArray();
            Assert.Equal((byte)'\n', raw[^1]);
            using var reply = JsonDocument.Parse(raw);
            Assert.Equal(1, reply.RootElement.GetProperty("id").GetInt32());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendMessageAsync(Reply(4)));
        }
        finally
        {
            bytes.Release.TrySetResult();
            await Record.ExceptionAsync(() => active.WaitAsync(TestDeadlines.Hang));
            if (queued != null)
                await Record.ExceptionAsync(() => queued.WaitAsync(TestDeadlines.Hang));
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposed_output_failure_is_latched_before_another_frame_can_start()
    {
        var input = new IdleTransport();
        using var bytes = new DisposedWriteStream();
        await using var transport = new StdioFrameTransport(input, bytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendMessageAsync(Reply(1)));
        var partial = bytes.ToArray();
        Assert.NotEmpty(partial);
        var failure = await Assert.ThrowsAsync<IOException>(() => transport.SendMessageAsync(Reply(2)));
        Assert.IsType<ObjectDisposedException>(failure.InnerException);
        Assert.Equal(partial, bytes.ToArray());
        Assert.Equal(1, bytes.WriteAttempts);
    }

    [Fact]
    public async Task Disposal_cuts_a_permanently_blocked_frame_after_one_second_without_disposing_its_semaphore()
    {
        var input = new IdleTransport();
        using var bytes = new NeverReleasedWriteStream();
        var transport = new StdioFrameTransport(input, bytes);
        using var errors = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(errors);
        var active = transport.SendMessageAsync(Reply(1));
        Task? queued = null;
        try
        {
            await bytes.Entered.Task.WaitAsync(TestDeadlines.Hang);
            Assert.NotEmpty(bytes.ToArray());
            queued = transport.SendMessageAsync(Reply(2));
            var watch = Stopwatch.StartNew();
            var disposal = transport.DisposeAsync().AsTask();
            // The test guard includes scheduling and teardown after the one-second product deadline.
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            TestDeadlines.Report("stdio.shutdown-blocked-frame", watch.Elapsed.TotalMilliseconds, "ms");
            Assert.Equal(1, input.Disposals);
            Assert.Equal(1, bytes.Disposals);
            await Assert.ThrowsAsync<IOException>(() => active);
            var rejected = await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            Assert.Equal(typeof(StdioFrameTransport).FullName, rejected.ObjectName);
            Assert.Equal("STDIO frame cut at shutdown after the one-second drain timeout.", errors.ToString().Trim());
            Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
            await transport.DisposeAsync();
            Assert.Equal(1, input.Disposals);
            Assert.Equal(1, bytes.Disposals);
        }
        finally
        {
            bytes.Dispose();
            await Record.ExceptionAsync(() => active.WaitAsync(TestDeadlines.Hang));
            if (queued != null)
                await Record.ExceptionAsync(() => queued.WaitAsync(TestDeadlines.Hang));
            try
            { await transport.DisposeAsync(); }
            finally { Console.SetError(previousError); }
        }
    }

    private sealed class NeverReleasedWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span[..5]);
            Entered.TrySetResult();
            await _disposed.Task;
            throw new IOException("Synthetic blocked frame interrupted by output disposal");
        }

        protected override void Dispose(bool disposing)
        {
            Disposals++;
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class IdleTransport : ITransport
    {
        public string? SessionId => null;
        public ChannelReader<JsonRpcMessage> MessageReader { get; } = Channel.CreateUnbounded<JsonRpcMessage>().Reader;
        internal int Disposals { get; private set; }
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class PartialBlockingWriteStream : MemoryStream
    {
        private bool _writing;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DisposedWhileWriting { get; private set; }
        internal int Disposals { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writing = true;
            try
            {
                int split = buffer.Length / 2;
                Write(buffer.Span[..split]);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Write(buffer.Span[split..]);
            }
            finally { _writing = false; }
        }

        protected override void Dispose(bool disposing)
        {
            DisposedWhileWriting |= _writing;
            Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class DisposedWriteStream : MemoryStream
    {
        internal int WriteAttempts { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            Write(buffer.Span[..5]);
            if (WriteAttempts == 1)
                throw new ObjectDisposedException("Synthetic STDIO output");
            Write(buffer.Span[5..]);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingWriteStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SawCancellableWrite { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            SawCancellableWrite |= cancellationToken.CanBeCanceled;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Write(buffer.Span);
        }
    }

    private sealed class YieldingWriteStream : MemoryStream
    {
        private int _writers;
        internal int MaximumWriters { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumWriters = Math.Max(MaximumWriters, Interlocked.Increment(ref _writers));
            foreach (var value in buffer.ToArray())
            {
                WriteByte(value);
                await Task.Yield();
            }
            Interlocked.Decrement(ref _writers);
        }
    }

    private sealed class BrokenWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span[..5]);
            throw new IOException("Synthetic partial STDIO write");
        }
    }

    private sealed class CancelAfterWriteStream : MemoryStream
    {
        internal CancellationTokenSource? CancelAfterWrite { get; set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            var cancellation = CancelAfterWrite;
            CancelAfterWrite = null;
            cancellation?.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}

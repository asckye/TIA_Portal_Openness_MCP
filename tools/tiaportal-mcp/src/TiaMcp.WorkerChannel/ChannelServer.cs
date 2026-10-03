using System;
using System.IO;
using System.Threading;

namespace TiaMcp.WorkerChannel
{
    // Synchronous by design: observation, dispatch, progress and reply all stay on
    // the worker's owning thread. This library never creates a native-call thread.
    public sealed class ChannelServer
    {
        private readonly Stream input, output;
        private readonly ChannelIdentity identity;
        private readonly Func<ChannelBinding> observe;
        private readonly Func<ChannelRequest, ChannelResponse> dispatch;
        private long lastId;
        private bool hello;
        private volatile bool active;
        private volatile bool dispatched;
        private int busy, faulted;
        public bool Poisoned => Volatile.Read(ref faulted) != 0;

        public ChannelServer(Stream input, Stream output, ChannelIdentity identity, Func<ChannelBinding> observe,
            Func<ChannelRequest, ChannelResponse> dispatch)
        { this.input = input; this.output = output; this.identity = identity; this.observe = observe; this.dispatch = dispatch; }

        public void Run()
        {
            WriteHello();
            var reader = new LineFraming(input, ChannelCodec.RequestLimit);
            try
            {
                byte[]? bytes;
                while ((bytes = reader.Read()) != null) Handle(bytes);
            }
            catch (Exception ex) { throw Fault(ex); }
        }

        public void WriteHello()
        {
            try
            {
                RequireUsable();
                if (hello) throw new IOException("Duplicate worker hello.");
                var binding = observe();
                if (binding.Epoch != 0 || binding.Bound) throw new IOException("Worker hello requires a fresh unbound session.");
                hello = true;
                Emit(ChannelCodec.Hello(identity, binding));
            }
            catch (Exception ex) { throw Fault(ex); }
        }

        public void Handle(byte[] bytes)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw Fault(new IOException("Concurrent worker dispatch."));
            try
            {
                RequireUsable();
                if (!hello) throw new IOException("Worker hello required before dispatch.");
                using (var document = ChannelCodec.Parse(bytes, ChannelCodec.RequestLimit))
                {
                    var root = document.RootElement;
                    ChannelCodec.Fields(root, "jsonrpc", "id", "method", "params", "bindingEpoch"); ChannelCodec.Version(root);
                    long id = ChannelCodec.Number(root, "id");
                    if (id <= lastId) throw new IOException("Duplicate or stale worker request id.");
                    var method = ChannelCodec.Text(root, "method");
                    if (!method.StartsWith("adapter.", StringComparison.Ordinal) || method.Length == 8) throw new IOException("Unknown worker method namespace.");
                    var args = root.GetProperty("params");
                    if (args.ValueKind != System.Text.Json.JsonValueKind.Object) throw new IOException("Worker parameters must be an object.");
                    var before = observe();
                    if (ChannelCodec.Number(root, "bindingEpoch") != before.Epoch) throw new IOException("Worker binding changed before dispatch.");
                    RequireUsable();
                    lastId = id;
                    int owner = Thread.CurrentThread.ManagedThreadId, progress = 0;
                    active = true;
                    var request = new ChannelRequest(id, method, args.GetRawText(), percent =>
                    {
                        try
                        {
                            RequireUsable();
                            if (!active || lastId != id || owner != Thread.CurrentThread.ManagedThreadId || percent < 0 || percent > 100 || ++progress > ChannelCodec.ProgressLimit)
                                throw new IOException("Late, invalid or cross-thread worker progress.");
                            Emit(ChannelCodec.Progress(id, progress, percent));
                        }
                        catch (Exception ex) { throw Fault(ex); }
                    });
                    RequireUsable();
                    dispatched = true;
                    var response = dispatch(request);
                    active = false;
                    RequireUsable();
                    var after = observe();
                    RequireUsable();
                    Emit(ChannelCodec.Reply(id, before.Epoch, after.Epoch, response));
                    if (response.Failure?.Outcome == ChannelOutcome.Unknown) throw new IOException("Worker native outcome is unknown; session stopped.");
                }
            }
            catch (Exception ex) { throw Fault(ex); }
            finally { active = false; dispatched = false; Volatile.Write(ref busy, 0); }
        }

        private void Emit(byte[] bytes)
        { RequireUsable(); output.Write(bytes, 0, bytes.Length); output.Flush(); RequireUsable(); }
        private void RequireUsable() { if (Poisoned) throw new IOException("Worker session is poisoned; requests are never replayed."); }
        private ChannelFault Fault(Exception cause)
        { Interlocked.Exchange(ref faulted, 1); return new ChannelFault(cause.Message, dispatched || (cause is ChannelFault fault && fault.OutcomeUnknown), cause); }
    }
}

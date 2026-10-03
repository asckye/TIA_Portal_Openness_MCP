using System;
using System.IO;

namespace TiaMcp.WorkerChannel
{
    public sealed class ChannelIdentity
    {
        public string ReleaseKey { get; }
        public string WorkerSha256 { get; }
        public string AdapterSha256 { get; }
        public int ProcessId { get; }
        public string Nonce { get; }

        public ChannelIdentity(string releaseKey, string workerSha256, string adapterSha256, int processId, string nonce)
        {
            if (string.IsNullOrWhiteSpace(releaseKey) || !Hex(workerSha256, 64) || !Hex(adapterSha256, 64) || processId <= 0 || !Hex(nonce, 64))
                throw new ArgumentException("A precise release, SHA-256 hashes, pid and launch nonce are required.");
            ReleaseKey = releaseKey; WorkerSha256 = workerSha256; AdapterSha256 = adapterSha256; ProcessId = processId; Nonce = nonce;
        }

        private static bool Hex(string text, int size)
        {
            if (text == null || text.Length != size) return false;
            foreach (var c in text) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }

    public enum ChannelOutcome { RejectedBeforeNative, ReadFailed, Unknown }
    public enum BindingChange { None, Advance, MayAdvance }

    public sealed class ChannelBinding
    {
        public long Epoch { get; }
        public bool Bound { get; }
        public ChannelBinding(long epoch, bool bound)
        {
            if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            Epoch = epoch; Bound = bound;
        }
    }

    public sealed class ChannelFailure : Exception
    {
        public int Code { get; }
        public ChannelOutcome Outcome { get; }
        public string EvidenceJson { get; }
        public ChannelFailure(string message, int code, ChannelOutcome outcome, string evidenceJson = "null") : base(message)
        {
            if (code != -32602 && code != -32603) throw new ArgumentOutOfRangeException(nameof(code));
            Code = code; Outcome = outcome; EvidenceJson = evidenceJson;
        }
    }

    public sealed class ChannelFault : IOException
    {
        public bool OutcomeUnknown { get; }
        public ChannelFault(string message, bool outcomeUnknown, Exception? inner = null) : base(message, inner) { OutcomeUnknown = outcomeUnknown; }
    }

    public sealed class ChannelRequest
    {
        public long Id { get; }
        public string Method { get; }
        public string ArgumentsJson { get; }
        public Action<int> Progress { get; }
        internal ChannelRequest(long id, string method, string argumentsJson, Action<int> progress)
        { Id = id; Method = method; ArgumentsJson = argumentsJson; Progress = progress; }
    }

    public sealed class ChannelResponse
    {
        public string ResultJson { get; }
        public ChannelFailure? Failure { get; }
        private ChannelResponse(string resultJson, ChannelFailure? failure) { ResultJson = resultJson; Failure = failure; }
        public static ChannelResponse Success(string resultJson) => new ChannelResponse(resultJson, null);
        public static ChannelResponse Error(ChannelFailure failure) => new ChannelResponse("null", failure);
    }
}

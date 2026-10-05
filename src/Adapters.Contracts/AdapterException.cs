using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TiaMcp.Adapters.Contracts
{
    public enum AdapterErrorCode
    {
        InvalidArgument, NotFound, Ambiguous, InvalidState,
        NotSupportedOnRelease, NativeFailure, ProcessLost
    }

    // Unknown does not imply that a native operation can safely be retried.
    public enum AdapterOutcome { RejectedBeforeNative, ReadFailed, Unknown }

    // Hosts own the wire code/message mapping. No existing adapter throws this type yet.
    public sealed class AdapterException : Exception
    {
        public AdapterErrorCode Code { get; }
        public AdapterOutcome Outcome { get; }
        public IReadOnlyDictionary<string, string> Evidence { get; }

        public AdapterException(AdapterErrorCode code, AdapterOutcome outcome, string message,
            IReadOnlyDictionary<string, string>? evidence = null, Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
            Outcome = outcome;
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            if(evidence != null)
                foreach(var pair in evidence) snapshot.Add(pair.Key, pair.Value);
            Evidence = new ReadOnlyDictionary<string, string>(snapshot);
        }
    }
}

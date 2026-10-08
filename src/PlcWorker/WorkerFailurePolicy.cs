using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.WorkerChannel;
using TiaOpenness.Shared;

internal sealed class WorkerFailureClassification
{
    internal int Code { get; }
    internal ChannelOutcome Outcome { get; }

    internal WorkerFailureClassification(int code, ChannelOutcome outcome)
    { Code = code; Outcome = outcome; }
}

internal static class WorkerFailurePolicy
{
    internal static Exception DiagnosticCause(Exception cause) => HostFailurePolicy.Precondition(cause) ?? cause;
    internal static WorkerFailureClassification Classify(Exception cause, bool enteredOperation, bool readOnly)
    {
        var kind = HostFailurePolicy.Classify(cause, enteredOperation, readOnly);
        int code = kind == HostFailureKind.Argument ? -32602 : -32603;
        ChannelOutcome outcome = kind == HostFailureKind.Unknown ? ChannelOutcome.Unknown
            : kind == HostFailureKind.ReadFailed ? ChannelOutcome.ReadFailed : ChannelOutcome.RejectedBeforeNative;
        return new WorkerFailureClassification(code, outcome);
    }
}

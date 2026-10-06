using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.WorkerChannel;

internal sealed class WorkerFailureClassification
{
    internal int Code { get; }
    internal ChannelOutcome Outcome { get; }

    internal WorkerFailureClassification(int code, ChannelOutcome outcome)
    { Code = code; Outcome = outcome; }
}

internal static class WorkerFailurePolicy
{
    internal static WorkerFailureClassification Classify(Exception cause, bool enteredOperation, bool readOnly)
    {
        bool explicitPrecondition = cause is AdapterPreconditionException;
        int code = cause is AdapterPreconditionException typed ? typed.IsArgument ? -32602 : -32603
            : cause is ArgumentException ? -32602 : -32603;
        ChannelOutcome outcome = !enteredOperation || explicitPrecondition ? ChannelOutcome.RejectedBeforeNative
            : readOnly ? ChannelOutcome.ReadFailed : ChannelOutcome.Unknown;
        return new WorkerFailureClassification(code, outcome);
    }
}

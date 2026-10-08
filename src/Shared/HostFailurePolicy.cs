using System;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    internal enum HostFailureKind { Argument, Precondition, Cancelled, InternalError, ReadFailed, Unknown }

    internal static class HostFailurePolicy
    {
        internal static string? ProcessLossReason(Exception? error) => ProcessLossPolicy.Reason(error);

        internal static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }

        internal static string? Parameter(Exception error)
        {
            for (var cause = error; cause != null; cause = cause.InnerException)
                if (cause is ArgumentException argument && argument.ParamName != null) return argument.ParamName;
            return null;
        }

        internal static AdapterPreconditionException? Precondition(Exception error)
        {
            for (var cause = error; cause != null; cause = cause.InnerException)
                if (cause is AdapterPreconditionException precondition) return precondition;
            return null;
        }

        internal static HostFailureKind Classify(Exception error, bool dispatched, bool readOnly,
            string? reportedOutcome = null, int? reportedCode = null, string? reportedExceptionType = null, bool nativeRead = true)
        {
            error = Unwrap(error);
            if (Precondition(error) is AdapterPreconditionException precondition)
                return precondition.IsArgument ? HostFailureKind.Argument : HostFailureKind.Precondition;
            if (reportedCode == -32602 && (!dispatched || readOnly || reportedOutcome == "rejected-before-operation")) return HostFailureKind.Argument;
            if ((!dispatched || readOnly) && (error is OperationCanceledException || reportedExceptionType == nameof(OperationCanceledException)))
                return HostFailureKind.Cancelled;
            if (reportedOutcome == "rejected-before-operation")
                return reportedCode == -32602 ? HostFailureKind.Argument : HostFailureKind.Precondition;
            // Untyped exceptions from an issued write do not prove that nothing changed.
            if (!dispatched)
                return error is OperationCanceledException ? HostFailureKind.Cancelled
                    : error is ArgumentException || reportedCode == -32602 ? HostFailureKind.Argument : HostFailureKind.Precondition;
            if (readOnly && error is OperationCanceledException) return HostFailureKind.Cancelled;
            return readOnly || reportedOutcome == "read-failed"
                ? nativeRead ? HostFailureKind.ReadFailed : HostFailureKind.InternalError : HostFailureKind.Unknown;
        }

    }
}

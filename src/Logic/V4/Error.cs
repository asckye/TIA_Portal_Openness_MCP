using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    public enum ErrorCode
    {
        InvalidArgument, LimitExceeded, UnsupportedCapability, ToolNotFound, ProjectNotBound, NotFound, TargetAmbiguous, IdentityMismatch, AlreadyExists, ConfirmationRequired, PlanStale, PreconditionFailed, OfflineRequired, AuthenticationRequired, AccessDenied, SessionResetRequired, ResourceUnavailable, IoFailed, NativeOperationFailed, Cancelled, Timeout, NotExecuted, PartialFailure, OutcomeUnknown, InternalError
    }

    public sealed class Error
    {
        [JsonPropertyOrder(0)] public ErrorCode Code => Details.Code;
        [JsonPropertyOrder(1)] public string Message { get; }
        [JsonPropertyOrder(2)] public ErrorDetails Details { get; }

        public Error(string message, ErrorDetails details)
        {
            V4Validation.Text(message, nameof(message));
            V4Validation.Require(details != null, "Error details are required.");
            Message = message;
            Details = details!;
        }
    }

    // Only the code-specific types in this assembly can extend this union.
    public abstract class ErrorDetails
    {
        internal ErrorDetails() { }
        internal abstract ErrorCode Code { get; }
    }

    public sealed class InvalidArgumentDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.InvalidArgument;
        [JsonPropertyOrder(0)] public string? Parameter { get; }
        [JsonPropertyOrder(1)] public IReadOnlyList<string> AllowedValues { get; }
        public InvalidArgumentDetails(string? parameter, IReadOnlyList<string> allowedValues)
        {
            Parameter = parameter;
            AllowedValues = V4Validation.List(allowedValues);
            V4Validation.Details(this);
        }
    }

    public sealed class LimitExceededDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.LimitExceeded;
        [JsonPropertyOrder(0)] public string? Parameter { get; }
        [JsonPropertyOrder(1)] public long? Limit { get; }
        [JsonPropertyOrder(2)] public long? Actual { get; }
        public LimitExceededDetails(string? parameter, long? limit, long? actual)
        {
            Parameter = parameter;
            Limit = limit;
            Actual = actual;
            V4Validation.Details(this);
        }
    }

    public sealed class UnsupportedCapabilityDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.UnsupportedCapability;
        [JsonPropertyOrder(0)] public string? ReleaseKey { get; }
        [JsonPropertyOrder(1)] public string? Capability { get; }
        [JsonPropertyOrder(2)] public string? Action { get; }
        public UnsupportedCapabilityDetails(string? releaseKey, string? capability, string? action)
        {
            ReleaseKey = releaseKey;
            Capability = capability;
            Action = action;
            V4Validation.Details(this);
        }
    }

    public sealed class ToolNotFoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ToolNotFound;
        [JsonPropertyOrder(0)] public string? Tool { get; }
        public ToolNotFoundDetails(string? tool)
        {
            Tool = tool;
            V4Validation.Details(this);
        }
    }

    public sealed class ProjectNotBoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ProjectNotBound;

        public ProjectNotBoundDetails()
        {

            V4Validation.Details(this);
        }
    }

    public sealed class NotFoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NotFound;
        [JsonPropertyOrder(0)] public string? Target { get; }
        public NotFoundDetails(string? target)
        {
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class TargetAmbiguousDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.TargetAmbiguous;
        [JsonPropertyOrder(0)] public string? Target { get; }
        [JsonPropertyOrder(1)] public IReadOnlyList<string> Candidates { get; }
        public TargetAmbiguousDetails(string? target, IReadOnlyList<string> candidates)
        {
            Target = target;
            Candidates = V4Validation.List(candidates);
            V4Validation.Details(this);
        }
    }

    public sealed class IdentityMismatchDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.IdentityMismatch;
        [JsonPropertyOrder(0)] public string? Target { get; }
        [JsonPropertyOrder(1)] public string? Expected { get; }
        [JsonPropertyOrder(2)] public string? Actual { get; }
        public IdentityMismatchDetails(string? target, string? expected, string? actual)
        {
            Target = target;
            Expected = expected;
            Actual = actual;
            V4Validation.Details(this);
        }
    }

    public sealed class AlreadyExistsDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AlreadyExists;
        [JsonPropertyOrder(0)] public string? Target { get; }
        public AlreadyExistsDetails(string? target)
        {
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class ConfirmationRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ConfirmationRequired;
        [JsonPropertyOrder(0)] public string Reason { get; }
        [JsonPropertyOrder(1)] public string? PlanHash { get; }
        [JsonPropertyOrder(2)] public string? RequestId { get; }
        public ConfirmationRequiredDetails(string? planHash)
            : this("plan-confirmation", planHash, null) { }
        [JsonConstructor]
        public ConfirmationRequiredDetails(string reason, string? planHash, string? requestId)
        {
            Reason = reason;
            PlanHash = planHash;
            RequestId = requestId;
            V4Validation.Details(this);
        }
    }

    public sealed class PlanStaleDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PlanStale;
        [JsonPropertyOrder(0)] public string? PlanHash { get; }
        [JsonPropertyOrder(1)] public string? Reason { get; }
        public PlanStaleDetails(string? planHash, string? reason)
        {
            PlanHash = planHash;
            Reason = reason;
            V4Validation.Details(this);
        }
    }

    public sealed class PreconditionFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PreconditionFailed;
        [JsonPropertyOrder(0)] public string? Condition { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        public PreconditionFailedDetails(string? condition, string? target)
        {
            Condition = condition;
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class OfflineRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.OfflineRequired;
        [JsonPropertyOrder(0)] public IReadOnlyList<string> Targets { get; }
        public OfflineRequiredDetails(IReadOnlyList<string> targets)
        {
            Targets = V4Validation.List(targets);
            V4Validation.Details(this);
        }
    }

    public sealed class AuthenticationRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AuthenticationRequired;
        [JsonPropertyOrder(0)] public string? Capability { get; }
        public AuthenticationRequiredDetails(string? capability)
        {
            Capability = capability;
            V4Validation.Details(this);
        }
    }

    public sealed class AccessDeniedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AccessDenied;
        [JsonPropertyOrder(0)] public string? Operation { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        public AccessDeniedDetails(string? operation, string? target)
        {
            Operation = operation;
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class SessionResetRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.SessionResetRequired;
        [JsonPropertyOrder(0)] public string? Reason { get; }
        public SessionResetRequiredDetails(string? reason)
        {
            Reason = reason;
            V4Validation.Details(this);
        }
    }

    public sealed class ResourceUnavailableDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ResourceUnavailable;
        [JsonPropertyOrder(0)] public string? Resource { get; }
        public ResourceUnavailableDetails(string? resource)
        {
            Resource = resource;
            V4Validation.Details(this);
        }
    }

    public sealed class IoFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.IoFailed;
        [JsonPropertyOrder(0)] public string? Operation { get; }
        [JsonPropertyOrder(1)] public string? Path { get; }
        public IoFailedDetails(string? operation, string? path)
        {
            Operation = operation;
            Path = path;
            V4Validation.Details(this);
        }
    }

    public sealed class NativeOperationFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NativeOperationFailed;
        [JsonPropertyOrder(0)] public string? NativeCode { get; }
        [JsonPropertyOrder(1)] public string? NativeMessage { get; }
        [JsonPropertyOrder(2)] public IReadOnlyDictionary<string, JsonElement> Evidence { get; }
        public NativeOperationFailedDetails(string? nativeCode, string? nativeMessage, IReadOnlyDictionary<string, JsonElement> evidence)
        {
            NativeCode = nativeCode;
            NativeMessage = nativeMessage;
            Evidence = V4Validation.Object(evidence);
            V4Validation.Details(this);
        }
    }

    public sealed class CancelledDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.Cancelled;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        public CancelledDetails(string? stage)
        {
            Stage = stage;
            V4Validation.Details(this);
        }
    }

    public sealed class TimeoutDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.Timeout;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        public TimeoutDetails(string? stage)
        {
            Stage = stage;
            V4Validation.Details(this);
        }
    }

    public sealed class NotExecutedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NotExecuted;
        [JsonPropertyOrder(0)] public int? CauseIndex { get; }
        public NotExecutedDetails(int? causeIndex)
        {
            CauseIndex = causeIndex;
            V4Validation.Details(this);
        }
    }

    public sealed class PartialFailureDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PartialFailure;
        [JsonPropertyOrder(0)] public int Succeeded { get; }
        [JsonPropertyOrder(1)] public int Failed { get; }
        [JsonPropertyOrder(2)] public int NotExecuted { get; }
        public PartialFailureDetails(int succeeded, int failed, int notExecuted)
        {
            Succeeded = succeeded;
            Failed = failed;
            NotExecuted = notExecuted;
            V4Validation.Details(this);
        }
    }

    public sealed class OutcomeUnknownDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.OutcomeUnknown;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        [JsonPropertyOrder(1)] public IReadOnlyDictionary<string, JsonElement> Evidence { get; }
        [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; }
        public OutcomeUnknownDetails(string? stage, IReadOnlyDictionary<string, JsonElement> evidence, string? reason = null)
        {
            Stage = stage;
            Evidence = V4Validation.Object(evidence);
            if (reason != null && reason != "awaiting-openness-confirmation") throw new System.ArgumentException("Unknown session timeout reason.");
            Reason = reason;
            V4Validation.Details(this);
        }
    }

    public sealed class InternalErrorDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.InternalError;
        [JsonPropertyOrder(0)] public string? DiagnosticId { get; }
        public InternalErrorDetails(string? diagnosticId)
        {
            DiagnosticId = diagnosticId;
            V4Validation.Details(this);
        }
    }
}

using TiaOpenness.Core;

namespace TiaOpenness.Gui.Localization;

public static class EngineResultText
{
    public static LocalizedText Outcome(EngineToolResult result) => result.DisplayOutcome switch
    {
        "succeeded" => LocalizedText.Key("Engine.Outcome.Succeeded"),
        "rejected-before-operation" => LocalizedText.Key("Engine.Outcome.RejectedBeforeOperation"),
        "read-failed" => LocalizedText.Key("Engine.Outcome.ReadFailed"),
        "failed" => LocalizedText.Key("Engine.Outcome.Failed"),
        "partial" => LocalizedText.Key("Engine.Outcome.Partial"),
        "unknown" => LocalizedText.Key("Engine.Outcome.Unknown"),
        _ => LocalizedText.Key("Engine.Outcome.Unknown")
    };

    public static string Error(string code) => code switch
    {
        "INVALID_ARGUMENT" => Loc.Current["Engine.Error.InvalidArgument"],
        "LIMIT_EXCEEDED" => Loc.Current["Engine.Error.LimitExceeded"],
        "UNSUPPORTED_CAPABILITY" => Loc.Current["Engine.Error.UnsupportedCapability"],
        "TOOL_NOT_FOUND" => Loc.Current["Engine.Error.ToolNotFound"],
        "PROJECT_NOT_BOUND" => Loc.Current["Engine.Error.ProjectNotBound"],
        "NOT_FOUND" => Loc.Current["Engine.Error.NotFound"],
        "TARGET_AMBIGUOUS" => Loc.Current["Engine.Error.TargetAmbiguous"],
        "IDENTITY_MISMATCH" => Loc.Current["Engine.Error.IdentityMismatch"],
        "ALREADY_EXISTS" => Loc.Current["Engine.Error.AlreadyExists"],
        "CONFIRMATION_REQUIRED" => Loc.Current["Engine.Error.ConfirmationRequired"],
        "PLAN_STALE" => Loc.Current["Engine.Error.PlanStale"],
        "PRECONDITION_FAILED" => Loc.Current["Engine.Error.PreconditionFailed"],
        "OFFLINE_REQUIRED" => Loc.Current["Engine.Error.OfflineRequired"],
        "AUTHENTICATION_REQUIRED" => Loc.Current["Engine.Error.AuthenticationRequired"],
        "ACCESS_DENIED" => Loc.Current["Engine.Error.AccessDenied"],
        "SESSION_RESET_REQUIRED" => Loc.Current["Engine.Error.SessionResetRequired"],
        "RESOURCE_UNAVAILABLE" => Loc.Current["Engine.Error.ResourceUnavailable"],
        "IO_FAILED" => Loc.Current["Engine.Error.IoFailed"],
        "NATIVE_OPERATION_FAILED" => Loc.Current["Engine.Error.NativeOperationFailed"],
        "CANCELLED" => Loc.Current["Engine.Error.Cancelled"],
        "TIMEOUT" => Loc.Current["Engine.Error.Timeout"],
        "NOT_EXECUTED" => Loc.Current["Engine.Error.NotExecuted"],
        "PARTIAL_FAILURE" => Loc.Current["Engine.Error.PartialFailure"],
        "OUTCOME_UNKNOWN" => Loc.Current["Engine.Error.OutcomeUnknown"],
        "INTERNAL_ERROR" => Loc.Current["Engine.Error.InternalError"],
        _ => string.Empty
    };

    public static string Error(EngineToolResult result) => result.ErrorCode is null ? string.Empty : Error(result.ErrorCode);
}

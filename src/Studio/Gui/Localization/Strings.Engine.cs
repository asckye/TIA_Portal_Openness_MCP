namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    private static (string Key, string En, string Zh)[] EngineCatalogue =>
    [
        ("Engine.Error.InvalidArgument", "Invalid argument.", "参数无效。"),
        ("Engine.Error.LimitExceeded", "An input limit was exceeded.", "输入超出限制。"),
        ("Engine.Error.UnsupportedCapability", "This capability is not supported.", "不支持此功能。"),
        ("Engine.Error.ToolNotFound", "The tool is not available in this release.", "此版本中没有该工具。"),
        ("Engine.Error.ProjectNotBound", "No project is bound.", "尚未绑定工程。"),
        ("Engine.Error.NotFound", "The requested object or file was not found.", "找不到指定的对象或文件。"),
        ("Engine.Error.TargetAmbiguous", "More than one target matches.", "存在多个匹配目标。"),
        ("Engine.Error.IdentityMismatch", "The target identity has changed.", "目标身份已改变。"),
        ("Engine.Error.AlreadyExists", "The target already exists.", "目标已存在。"),
        ("Engine.Error.ConfirmationRequired", "Confirmation is required before execution.", "执行前需要确认。"),
        ("Engine.Error.PlanStale", "The plan is no longer current.", "计划已过期。"),
        ("Engine.Error.PreconditionFailed", "An operation precondition was not met.", "不满足操作前提条件。"),
        ("Engine.Error.OfflineRequired", "The affected devices must be offline.", "受影响的设备必须处于离线状态。"),
        ("Engine.Error.AuthenticationRequired", "Authentication is required.", "需要身份验证。"),
        ("Engine.Error.AccessDenied", "Access was denied.", "访问被拒绝。"),
        ("Engine.Error.SessionResetRequired", "Reset the session before continuing.", "请重置会话后再继续。"),
        ("Engine.Error.ResourceUnavailable", "A required resource is unavailable.", "所需资源不可用。"),
        ("Engine.Error.IoFailed", "A file operation failed.", "文件操作失败。"),
        ("Engine.Error.NativeOperationFailed", "The native operation failed.", "原生操作失败。"),
        ("Engine.Error.Cancelled", "The operation was cancelled.", "操作已取消。"),
        ("Engine.Error.Timeout", "The operation timed out.", "操作超时。"),
        ("Engine.Error.NotExecuted", "The operation was not executed.", "操作未执行。"),
        ("Engine.Error.PartialFailure", "The operation completed only partially.", "操作仅部分完成。"),
        ("Engine.Error.OutcomeUnknown", "The outcome is unknown. Verify the state before retrying.", "结果未知。重试前请核实状态。"),
        ("Engine.Error.InternalError", "An internal error occurred.", "发生内部错误。"),
        ("Engine.Outcome.Succeeded", "Succeeded", "成功"),
        ("Engine.Outcome.RejectedBeforeOperation", "Rejected before operation", "执行前被拒绝"),
        ("Engine.Outcome.ReadFailed", "Read failed", "读取失败"),
        ("Engine.Outcome.Failed", "Failed", "失败"),
        ("Engine.Outcome.Partial", "Partial result", "部分结果"),
        ("Engine.Outcome.Unknown", "Unknown outcome", "结果未知"),
    ];
}

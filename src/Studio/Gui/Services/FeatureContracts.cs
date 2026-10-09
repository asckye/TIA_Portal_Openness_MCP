using System;
using System.Collections.Generic;
using System.ComponentModel;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

public enum ApprovalState { Pending, Approved, Executing, Completed, Failed, Partial, Unknown, Rejected, TimedOut, Disconnected }
public enum CallResult { Success, Rejected, Failed, Partial, Unknown, Pending, Approved, Executing }
public enum AuditEventType { Request, Approve, Reject, Timeout, Start, End, Toggle }
public enum CheckStatus { Pass, Warn, Fail, Checking, Unchecked }
public enum DiagnosticState { Idle, Running, Done }

public sealed record ApprovalOperation(LocalizedText Action, string Object, string Tool);
public sealed record ApprovalRequest(string Id, string Client, string Transport, string Host, string Release,
    string Project, string Plc, IReadOnlyList<ApprovalOperation> Operations, IReadOnlyList<LocalizedText> Risks,
    string PlanHash, int TimeoutSeconds, DateTimeOffset Deadline, ApprovalState State, string ParametersJson)
{
    public string Actor { get; init; } = "mcp";
    public string? OperatorCallId { get; init; }
}

public sealed record CallRecord(string RequestId, DateTimeOffset Time, string Host, string Release, string Tool,
    bool IsWrite, CallResult Result, int? DurationMs, string Target, string ParametersJson,
    LocalizedText Summary, string ErrorCode, LocalizedText ErrorMessage, LocalizedText ApprovalRecord)
{
    public string JournalKey { get; init; } = RequestId;
    public string? Actor { get; init; }
    public string? McpSession { get; init; }
    public string ResultJson { get; init; } = "null";
    public string Outcome { get; init; } = "";
    public string Execution { get; init; } = "";
    public string Completeness { get; init; } = "";
    public bool ParametersTruncated { get; init; }
    public bool ResultTruncated { get; init; }
}

public sealed record ConnectionInfo(string Address, string Transport, string ConfigurationJson);

/// <summary>Snapshots and change notifications for the workbench journal; no native session is owned here.</summary>
public interface ICallJournalService : INotifyPropertyChanged
{
    IReadOnlyList<CallRecord> Calls { get; }
    ConnectionInfo Connection { get; }
}

public sealed record AuditEvent(long Index, DateTimeOffset Time, AuditEventType Type, string ToolObject,
    string Result, string Hash)
{
    public string? Actor { get; init; }
}
public sealed record AuditChainVerification(string Chain, bool Passed, int Count, long? BreakIndex, string? File);
public sealed record AuditVerification(bool? Passed, int Count = 0, long? BreakIndex = null)
{
    public string? Chain { get; init; }
    public string? File { get; init; }
    public IReadOnlyList<AuditChainVerification> Chains { get; init; } = Array.Empty<AuditChainVerification>();
}

public interface IAuditLogService : INotifyPropertyChanged
{
    IReadOnlyList<AuditEvent> Events { get; }
    int TotalCount { get; }
    int FileSizeMb { get; set; }
    int Copies { get; set; }
    LocalizedText Coverage { get; }
    AuditVerification Verify();
    LocalizedText OpenFolder();
}

public sealed record EnvironmentCheck(string Id, CheckStatus Status, LocalizedText Name, LocalizedText Result,
    LocalizedText FixAction, LocalizedText Detail);
public sealed record EnvironmentGroup(LocalizedText Name, IReadOnlyList<EnvironmentCheck> Rows);

public interface IEnvironmentCheckService : INotifyPropertyChanged
{
    IReadOnlyList<EnvironmentGroup> Groups { get; }
    IReadOnlyList<string> LogTail { get; }
    LocalizedText Recheck();
    LocalizedText Fix(string checkId);
}

public sealed record DiagnosticProgress(DiagnosticState State, LocalizedText Step, int Percent = 0, string Path = "");

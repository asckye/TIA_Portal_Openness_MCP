using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class OnlineRequest
{
    public string Action { get; set; } = "";
    public string BlockPath { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ObjectKind { get; set; } = "";
    public string TargetOption { get; set; } = "";
    public string[] ObjectPaths { get; set; } = Array.Empty<string>();
    public bool ConfirmValueChange { get; set; }
    public string Operation { get; set; } = "";
    public string SoftwarePath { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Password { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserType { get; set; } = "";
    public string RhTarget { get; set; } = "";
    public string PgPcInterface { get; set; } = "";
    public string TargetIpAddress { get; set; } = "";
    public bool TrustDeviceCertificate { get; set; }
    public bool AllowNonTlsLogin { get; set; }
    public bool AllowSafetyProgramDownload { get; set; }
    public bool AllowHardwareDifferences { get; set; }
    public bool AllowActiveTestCancel { get; set; }
    public bool AllowExpandedDownload { get; set; }
    public bool AllowIdentificationDataLoad { get; set; }
    public string DownloadScope { get; set; } = "software";
    public bool ConsistentBlocksOnly { get; set; } = true;
    public bool KeepActualValues { get; set; } = true;
    public bool StartAfterDownload { get; set; } = true;
    public bool StopBeforeDownload { get; set; } = true;
    public string UserManagementMode { get; set; } = "keep";
    public string PromptAnswersJson { get; set; } = "{}";
    public Dictionary<string, string> PromptAnswers { get; set; } = new Dictionary<string, string>();
    public string? PromptAnswersError { get; set; }
    public string ModuleAccessPassword { get; set; } = "";
    public string BlockBindingPassword { get; set; } = "";
    public string MasterSecretPassword { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public string TargetForSoftware { get; set; } = "CPU";
    public bool OverwriteOnMemoryCard { get; set; }
    public bool ConfirmDownload { get; set; }
    public bool ConfirmUpload { get; set; }
    public bool DryRun { get; set; } = true;
    public int MaxDepth { get; set; } = 4;
    public int MaxEntries { get; set; } = 200;
    public int MaxItems { get; set; } = 500;
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public string[] DevicePath { get; set; } = Array.Empty<string>();
    public string[] ItemPath { get; set; } = Array.Empty<string>();
}

public sealed class OnlineReply : IWorkerOperationReply
{
    public object? Value { get; set; }
    public PlcSoftwareFailure? Failure { get; set; }
    public bool MayHaveChanged { get; set; }
    public bool RequiresSessionReset { get; set; }
    public bool BlockReadsAfterUncertain => true;
}

public sealed class OnlineStateResult
{
    public string? State { get; set; }
    public bool? IsOnline { get; set; }
    public bool? IsReachable { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?>? Meta { get; set; }
}

public sealed class OnlineReadinessResult
{
    public bool? Ready { get; set; }
    public bool? HasDownloadProvider { get; set; }
    public bool? HasConfiguration { get; set; }
    public bool? IsConsistent { get; set; }
    public string[]? Issues { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?>? Meta { get; set; }
}

public sealed class OnlineDownloadResult
{
    public bool? Ok { get; set; }
    public string? State { get; set; }
    public int? ErrorCount { get; set; }
    public int? WarningCount { get; set; }
    public string[]? Errors { get; set; }
    public string[]? Warnings { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?>? Meta { get; set; }
}

public sealed class OnlineCompareEntry
{
    public string? Path { get; set; }
    public string? LeftName { get; set; }
    public string? RightName { get; set; }
    public string? Status { get; set; }
    public string? Details { get; set; }
}

public sealed class OnlineCompareResult
{
    public bool? IsOnline { get; set; }
    public OnlineCompareEntry[]? Entries { get; set; }
    public Dictionary<string, int>? Summary { get; set; }
    public bool? Truncated { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?>? Meta { get; set; }
}

using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class MultiuserRequest
{
    public string Action { get; set; } = "read";
    public string ServerName { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Protocol { get; set; } = "Https";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string CommitComment { get; set; } = "";
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public bool ConfirmChange { get; set; }
    public bool DryRun { get; set; } = true;
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}

public sealed class ProjectRetrieveRequest
{
    public string ArchivePath { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public bool Upgrade { get; set; }
    public bool DryRun { get; set; } = true;
}

public sealed class ProjectSaveCopyResult : IWorkerOperationReply
{
    public string Message { get; set; } = "";
    public string PreviousProjectFile { get; set; } = "";
    public string NewProjectFile { get; set; } = "";
    public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    public bool MayHaveChanged => true;
    public bool RequiresSessionReset { get; set; }
    public bool BlockReadsAfterUncertain => true;
}

// The scaffold orchestrator keeps its JSON and response types in Shared. Each
// primitive borrows the same worker session and the existing family implementation.
public sealed class ProjectScaffoldPrimitive
{
    public string Kind { get; set; } = "";
    public string[] Text { get; set; } = Array.Empty<string>();
    public uint[] Numbers { get; set; } = Array.Empty<uint>();
    public bool[] Flags { get; set; } = Array.Empty<bool>();
}

using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class OptionalProductRequest
{
    public string Operation { get; set; } = "";
    public string? ValidationError { get; set; }
    public string ValidationKind { get; set; } = "";
    public string SoftwarePath { get; set; } = "";
    public string Action { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public long Filter { get; set; }
    public bool Unattended { get; set; }
    public bool DeleteAtTarget { get; set; }
    public bool DryRun { get; set; } = true;
    public string ChartNamesJson { get; set; } = "";
    public bool SkipChartPreflight { get; set; }
    public string ChartName { get; set; } = "";
    public string CurrentPassword { get; set; } = "";
    public string NewHashedPassword { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public int Offset { get; set; }
    public int Limit { get; set; }
    public string Kind { get; set; } = "";
    public string ImportOptions { get; set; } = "";
    public string LoadOptions { get; set; } = "";
    public bool ConfirmExternalExecution { get; set; }
    public string NamesJson { get; set; } = "";
    public bool RunAll { get; set; }
    public string NewName { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public string OpcUaServerAddress { get; set; } = "";
    public string ServerInterfaceType { get; set; } = "";
    public string InterfaceFolderPath { get; set; } = "";
    public string UpdateOptions { get; set; } = "";
    public string ScopeJson { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string MasterCopyPath { get; set; } = "";
    public string LibraryName { get; set; } = "";
    public string[] DevicePathJson { get; set; } = Array.Empty<string>();
    public string[] ItemPathJson { get; set; } = Array.Empty<string>();
    public string[] ModifiedDevicePathJson { get; set; } = Array.Empty<string>();
    public string[] ModifiedItemPathJson { get; set; } = Array.Empty<string>();
    public string Mode { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Author { get; set; } = "";
    public string Password { get; set; } = "";
    public string[] FilesJson { get; set; } = Array.Empty<string>();
    public bool ConfirmSafetyChange { get; set; }
    public OptionalProductPlan Plan { get; set; } = new OptionalProductPlan();
}

public sealed class OptionalProductPlan
{
    public bool Writes { get; set; }
    public bool External { get; set; }
    public string Kind { get; set; } = "case";
    public string ImportOption { get; set; } = "None";
    public string[] LoadFlags { get; set; } = Array.Empty<string>();
    public string[] ChartNames { get; set; } = Array.Empty<string>();
    public string[] Names { get; set; } = Array.Empty<string>();
    public string ExecutionMode { get; set; } = "";
    public string ServerInterface { get; set; } = "";
    public string UpdateOption { get; set; } = "";
    public TestSuiteScope[] Scope { get; set; } = Array.Empty<TestSuiteScope>();
}

public sealed class TestSuiteScope
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string SoftwarePath { get; set; } = "";
    public string GroupPath { get; set; } = "";
}

public sealed class OptionalProductReply : IWorkerOperationReply
{
    public string Message { get; set; } = "";
    public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    public bool RequiresSessionReset { get; set; }
    public bool MayHaveChanged => Meta.TryGetValue("mayHaveChanged", out var changed) && changed is true;
    public bool BlockReadsAfterUncertain => true;
}

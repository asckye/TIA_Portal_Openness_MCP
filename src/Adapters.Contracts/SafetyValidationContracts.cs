using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class SafetyValidationRequest
{
    public string Operation { get; set; } = "";
    public string[] GroupPath { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Faults { get; set; } = new Dictionary<string, string>();
    public SafetyValidationPlan Plan { get; set; } = new SafetyValidationPlan();
    public string Name { get; set; } = "";
    public bool IncludeSafetyFunctions { get; set; } = false;
    public bool IncludeConditions { get; set; } = false;
    public int Offset { get; set; } = 0;
    public int Limit { get; set; } = 100;
    public string Action { get; set; } = "read";
    public string EvaluationDeviceName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string MasterCopyPath { get; set; } = "";
    public string LibraryName { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ExportOptions { get; set; } = "";
    public string DocumentInfoOptions { get; set; } = "";
    public string ImportOptions { get; set; } = "";
    public bool ConfirmDelete { get; set; } = false;
    public bool DryRun { get; set; } = true;
    public string NewName { get; set; } = "";
    public string ActivationTest { get; set; } = "";
    public string SafetyFunction { get; set; } = "";
    public int Index { get; set; } = -1;
    public string DeviceName { get; set; } = "";
    public string SignalUsage { get; set; } = "";
    public string SignalName { get; set; } = "";
}

public sealed class SafetyValidationPlan
{
    public string Action { get; set; } = "";
    public bool Writes { get; set; }
    public bool WritesFile { get; set; }
    public string[] ExportFlags { get; set; } = Array.Empty<string>();
    public string[] DocumentFlags { get; set; } = Array.Empty<string>();
    public string ImportOption { get; set; } = "None";
    public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>();
    public string[]? Signals { get; set; }
}

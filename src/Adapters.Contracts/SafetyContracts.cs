using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class SafetyRequest
{
    public string Operation { get; set; } = "";
    public string SoftwarePath { get; set; } = "";
    public string Action { get; set; } = "read";
    public string RuntimeGroup { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public string Password { get; set; } = "";
    public bool ConfirmSafetyChange { get; set; }
    public string MainSafetyBlockPath { get; set; } = "";
    public string MainSafetyInstanceDbPath { get; set; } = "";
    public string BlockPath { get; set; } = "";
    public bool IncludeProgramSignatures { get; set; } = true;
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public string FilePath { get; set; } = "";
    public string Printer { get; set; } = "MicrosoftPrintToPdf";
    public string Option { get; set; } = "All";
    public string DocumentLayout { get; set; } = "";
    public Dictionary<string, HardwareScalar> Scalars { get; set; } = new Dictionary<string, HardwareScalar>();
    public Dictionary<string, HardwareScalar> Attributes { get; set; } = new Dictionary<string, HardwareScalar>();
    public Dictionary<string, HardwareScalar> BlockNumbers { get; set; } = new Dictionary<string, HardwareScalar>();
    public string? SafetySystemVersion { get; set; }
    public string? ChangesError { get; set; }
}

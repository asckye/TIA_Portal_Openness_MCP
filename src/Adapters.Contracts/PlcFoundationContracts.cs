using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcObjectInfo
    {
        public string Path { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public string DataType { get; internal set; } = "";
        public string Value { get; internal set; } = "";
    }
    public sealed class PlcMutationResult
    {
        public string Operation { get; internal set; } = "";
        public bool Executed { get; internal set; }
        public string[] AffectedNames { get; internal set; } = new string[0];
        public string? OutputFile { get; internal set; }
        public string? ProjectFile { get; internal set; }
        public string? InputFile { get; internal set; }
        public string? InputSha256 { get; internal set; }
        public string? RecoveryDirectory { get; internal set; }
        public string? RecoveryStatus { get; internal set; }
        public System.Collections.Generic.Dictionary<string, string> RecoveryFiles { get; internal set; } = new System.Collections.Generic.Dictionary<string, string>();
        public string? XmlContent { get; internal set; }
        public bool? FullProgramRestoreSupported { get; internal set; }
        public string[] Warnings { get; internal set; } = new string[0];
    }
    public sealed class PlcCompileResult
    {
        public bool Executed { get; internal set; }
        public string? ProjectFile { get; internal set; }
        public string? State { get; internal set; }
        public int? ErrorCount { get; internal set; }
        public int? WarningCount { get; internal set; }
        public string[] Messages { get; internal set; } = new string[0];
        public string[] Errors { get; internal set; } = new string[0];
        public string[] Warnings { get; internal set; } = new string[0];
        public string[] Info { get; internal set; } = new string[0];
        public string[] OfflineStateNotExposedByDevices { get; internal set; } = new string[0];
    }
}

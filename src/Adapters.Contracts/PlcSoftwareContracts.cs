using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class PlcCrossReference
    {
        public string? SourceName { get; set; }
        public string? SourcePath { get; set; }
        public string? ReferenceName { get; set; }
        public string? ReferencePath { get; set; }
        public string? LocationName { get; set; }
        public string? ReferenceLocation { get; set; }
        public string? ReferenceType { get; set; }
        public string? Access { get; set; }
        public string? SourceTypeName { get; set; }
        public string? SourceAddress { get; set; }
        public string? SourceDevice { get; set; }
        public string? SourceObjectClass { get; set; }
        public string? ReferenceTypeName { get; set; }
        public string? ReferenceAddress { get; set; }
        public string? ReferenceDevice { get; set; }
        public string? ReferenceObjectClass { get; set; }
    }

    public sealed class PlcTagComment { public string? Culture { get; set; } public string Text { get; set; } = ""; }

    public sealed class PlcVerifiedCandidate { public string Xml { get; set; } = ""; public string Name { get; set; } = ""; }
    public sealed class PlcVerifiedExecution
    {
        public PlcSoftwareRequest Request { get; set; } = null!;
        public string CandidateXml { get; set; } = "";
        public string Binding { get; set; } = "";
        public Action<string> Export { get; set; } = null!;
        public Action<string> Import { get; set; } = null!;
        public Action VerifyBinding { get; set; } = null!;
        public Action? Compile { get; set; }
        public Dictionary<string, object?> Meta { get; set; } = null!;
    }

    public sealed class PlcCompilerEvidence
    {
        public string State { get; set; } = "";
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Info { get; set; } = new List<string>();
        public List<string> RawMessages { get; set; } = new List<string>();
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class SeedHmiRequest { public string Kind { get; set; } = ""; public string SoftwarePath { get; set; } = ""; public string FolderPath { get; set; } = ""; public string Directory { get; set; } = ""; }

    public sealed class PlcSeedHmiReply
    {
        public string? Message { get; set; }
        public List<string>? Imported { get; set; }
        public List<PlcSeedImportFailure>? Failed { get; set; }
        public Dictionary<string, object?>? Meta { get; set; }
    }
    public sealed class PlcSeedImportFailure { public string? Path { get; set; } public string? Error { get; set; } }

    public sealed class PlcSoftwareRequest
    {
        public string Operation { get; set; } = "";
        public string ValidationError { get; set; } = "";
        public string RegexName { get; set; } = "";
        public bool Overwrite { get; set; } = true;
        public string SoftwarePath { get; set; } = "";
        public string Path { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string Filter { get; set; } = "AllObjects";
        public string Family { get; set; } = "";
        public string Action { get; set; } = "";
        public string Name { get; set; } = "";
        public string NewName { get; set; } = "";
        public bool AutoNumber { get; set; } = true;
        public int Number { get; set; }
        public string[] DevicePath { get; set; } = Array.Empty<string>();
        public string[] ItemPath { get; set; } = Array.Empty<string>();
        public string[] Paths { get; set; } = Array.Empty<string>();
        public string EvidenceDirectory { get; set; } = "";
        public string ExpectedToken { get; set; } = "";
        public bool CompileAfterImport { get; set; }
        public string FilePath { get; set; } = "";
        public string LibraryName { get; set; } = "";
        public string MasterCopyPath { get; set; } = "";
        public string CopyMode { get; set; } = "";
        public string GenerateOption { get; set; } = "None";
        public string TargetKind { get; set; } = "";
        public string TargetGroupPath { get; set; } = "";
        public string DataType { get; set; } = "";
        public string AddressOrValue { get; set; } = "";
        public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>();
        public PlcTagComment[] Comments { get; set; } = Array.Empty<PlcTagComment>();
        public string Kind { get; set; } = "all";
        public int Offset { get; set; }
        public int Limit { get; set; } = 200;
        public string Password { get; set; } = "";
        public bool Confirm { get; set; }
        public bool CrossReferences { get; set; }
        public bool AutoCreateGroup { get; set; } = true;
        public bool DryRun { get; set; } = true;
        public string UnitName { get; set; } = "";
        public string UnitKind { get; set; } = "unit";
        public bool IncludeBlocks { get; set; } = true;
        public int MaxDepth { get; set; } = 4;
    }

    public sealed class PlcSoftwareFailure
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string[]? Candidates { get; set; }
        public Dictionary<string, object?> Evidence { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class PlcSoftwareReply : IWorkerOperationReply
    {
        public string Message { get; set; } = "";
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
        public Dictionary<string, object?>? Data { get; set; }
        public string[] Created { get; set; } = Array.Empty<string>();
        public PlcCrossReference[]? References { get; set; }
        public string? Reason { get; set; }
        public bool Queried { get; set; }
        public PlcSeedHmiReply? Batch { get; set; }
        public PlcCompilerEvidence? Compiler { get; set; }
        public bool Found { get; set; }
        public PlcSoftwareFailure? Failure { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool MayHaveChanged { get; set; }
        public bool BlockReadsAfterUncertain => true;
    }

    public sealed class PlcSoftwareException : Exception
    {
        public string Code { get; }
        public IEnumerable<string>? Candidates { get; }
        public PlcSoftwareException(string code, string message, IEnumerable<string>? candidates = null, Exception? inner = null)
            : base(message, inner) { Code = code; Candidates = candidates; }
    }
}

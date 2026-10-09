using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public class PlcExternalSourceWorkflowResult : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempted;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => false;
        public string Operation { get; internal set; } = "";
        public string Status { get; internal set; } = "planned";
        public bool Attempted { get; internal set; }
        public bool Executed { get; internal set; }
        public bool RequiresSessionReset { get; internal set; }
        public string Error { get; internal set; } = "";
        public string Release { get; internal set; } = "";
        public string ProjectFile { get; internal set; } = "";
        public int ProcessId { get; internal set; }
        public string SoftwarePath { get; internal set; } = "";
        public string GroupPath => "";
        public string SourceName { get; internal set; } = "";
        public string PlanHash { get; internal set; } = "";
        public string Compilation => "notRun";
        public string Save => "notRun";
        public string Download => "notRun";
    }
    public sealed class PlcExternalSourceImportResult : PlcExternalSourceWorkflowResult
    {
        public string FilePath { get; internal set; } = "";
        public string RequestedSourceName { get; internal set; } = "";
        public string InputSha256 { get; internal set; } = "";
        public long ByteCount { get; internal set; }
        public string[] SourceNamesAfter { get; internal set; } = new string[0];
        public string Generation => "notRun";
    }
    public sealed class PlcExternalSourceObject
    {
        public string Kind { get; internal set; } = "";
        public string Path { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string TypeName { get; internal set; } = "";
        public string ProgrammingLanguage { get; internal set; } = "";
        public bool IsConsistent { get; internal set; }
        public DateTime ModifiedDate { get; internal set; }
    }
    public sealed class PlcExternalSourceGenerationResult : PlcExternalSourceWorkflowResult
    {
        public string SourceIdentity { get; internal set; } = "";
        public string GenerationOption { get; internal set; } = "";
        public string ResultBasis { get; internal set; } = "";
        public bool MayOverwriteExistingBlocks => true;
        public bool SourceContentReviewed => false;
        public PlcExternalSourceObject[] ObjectsBefore { get; internal set; } = new PlcExternalSourceObject[0];
        public PlcExternalSourceObject[] ObjectsAfter { get; internal set; } = new PlcExternalSourceObject[0];
        public PlcExternalSourceObject[] GeneratedObjects { get; internal set; } = new PlcExternalSourceObject[0];
        public PlcExternalSourceObject[] ObservedChanges { get; internal set; } = new PlcExternalSourceObject[0];
    }
}

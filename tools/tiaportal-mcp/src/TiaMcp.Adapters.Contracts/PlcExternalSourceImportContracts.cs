using System;
using System.Collections.Generic;
using System.Linq;
using PlcExternalSourceImportPolicy = TiaMcp.Adapters.Contracts.ExternalSourceImportContract;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcExternalSourceImportPlan
    {
        public string Status { get; internal set; } = "planned";
        public string MutationStatus => "notAttempted";
        public bool Executed => false;
        public bool Attempted => false;
        public int CreatedCount => 0;
        public bool ApplyBlocked => true;
        public string ApplyBlockedReason => PlcExternalSourceImportPolicy.ApplyBlock;
        public string Generation => "notRun";
        public string Compilation => "notRun";
        public string Save => "notRun";
        public string Download => "notRun";
        public string Release { get; internal set; } = "";
        public string ProjectFile { get; internal set; } = "";
        public int ProcessId { get; internal set; }
        public string SoftwarePath { get; internal set; } = "";
        public string GroupPath => "";
        public string SessionKind => "ordinary-project";
        public string FilePath { get; internal set; } = "";
        public string SourceName { get; internal set; } = "";
        public string Extension { get; internal set; } = "";
        public long ByteCount { get; internal set; }
        public string InputSha256 { get; internal set; } = "";
        public string PlanHash { get; internal set; } = "";
        public int InventoryCount { get; internal set; }
        public string CollisionStatus => "none-in-complete-root-snapshot-not-race-proof";
        public string ValidationScope => "wrapper-policy: ASCII printable plus TAB/CR/LF, unchanged bytes, 1..4194304 bytes; syntax/native validity/encoding support/source lifetime not established";
        public string Policy => "root-external-source-plan-v1";
    }
}

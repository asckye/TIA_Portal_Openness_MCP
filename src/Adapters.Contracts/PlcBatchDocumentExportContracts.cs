using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcBatchDocumentExportItem
    {
        public string BlockPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string Language { get; set; } = "";
        public bool Consistent { get; set; }
        public string Status { get; set; } = "planned";
        public string[] Files { get; set; } = new string[0];
    }
    public sealed class PlcBatchDocumentExportResult
    {
        public bool Executed { get; set; }
        public string ReleaseKey { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public bool Recursive { get; set; }
        public int MaxItems { get; set; }
        public string OutputDirectory { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public bool InventoryComplete { get; set; } = true;
        public string Status { get; set; } = "planned";
        public bool RequiresSessionReset { get; set; }
        public string RecoveryDirectory { get; set; } = "";
        public string Options { get; set; } = "native-default-two-argument-overload";
        public string Evidence { get; set; } = "official-manual-source-candidate; exact-sdk-build/native-acceptance-pending; no-cross-version-roundtrip-claim";
        public PlcBatchDocumentExportItem[] Items { get; set; } = new PlcBatchDocumentExportItem[0];
    }
}

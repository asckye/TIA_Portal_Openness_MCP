using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    // Deliberately narrower than the original probing tool: selection is read-only, creation is once.
    public sealed class PlcDeviceAddResult : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempted;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => false;
        public string Status { get; internal set; } = "planned";
        public bool Attempted { get; internal set; }
        public bool Executed { get; internal set; }
        public bool RequiresSessionReset { get; internal set; }
        public string Error { get; internal set; } = "";
        public string Release { get; internal set; } = "";
        public string ProjectFile { get; internal set; } = "";
        public int ProcessId { get; internal set; }
        public string DeviceName { get; internal set; } = "";
        public string TypeIdentifier { get; internal set; } = "";
        public string ArticleNumber { get; internal set; } = "";
        public string Version { get; internal set; } = "";
        public string Family { get; internal set; } = "";
        public string PlanHash { get; internal set; } = "";
        public string[] Inventory { get; internal set; } = new string[0];
        public string Policy => "exact-catalog-root-device-add-v1";
        public string Compatibility => "partial-exact-selection-only-no-fallback-probes";
        public string Save => "notRun";
        public string Download => "notRun";
        public string Recovery => "notEstablished-no-automatic-backup-or-rollback";
    }
}

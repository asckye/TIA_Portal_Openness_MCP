using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcBatchExportItem
    {
        public string ObjectPath { get; internal set; } = "";
        public string OutputFile { get; internal set; } = "";
        public string Status { get; internal set; } = "planned";
        public string XmlContent { get; internal set; } = "native-xml";
        public string[] Warnings { get; internal set; } = new string[0];
        public Dictionary<string,string> Evidence { get; internal set; } = new Dictionary<string,string>();
    }
    public sealed class PlcBatchExportResult : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Executed || RequiresSessionReset;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => false;
        public bool Executed { get; internal set; }
        public string ProjectFile { get; internal set; } = "";
        public string SoftwarePath { get; internal set; } = "";
        public string GroupPath { get; internal set; } = "";
        public bool Recursive { get; internal set; }
        public string InventoryHash { get; internal set; } = "";
        public bool InventoryComplete { get; internal set; } = true;
        public bool RequiresSessionReset { get; internal set; }
        public PlcBatchExportItem[] Items { get; internal set; } = new PlcBatchExportItem[0];
    }
}

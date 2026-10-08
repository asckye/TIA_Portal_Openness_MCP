using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcBatchDocumentImportItem
    {
        public string Name {get;internal set;}="";
        public string Status {get;internal set;}="not-attempted";
        public PlcDocumentImportResult Preview {get;internal set;}=null!;
        public PlcDocumentImportResult? Outcome {get;internal set;}
        public string[] PostInventory {get;internal set;}=new string[0];
    }
    public sealed class PlcBatchDocumentImportResult
    {
        public string Policy => "batch-document-global-db-v1";
        public string PlanHash {get;internal set;}="";
        public string Status {get;internal set;}="planned";
        public bool Attempted {get;internal set;}
        public bool MayHaveChanged => Attempted;
        public bool RequiresSessionReset {get;internal set;}
        public string Error {get;internal set;}="";
        public PlcBatchDocumentImportItem[] Items {get;internal set;}=new PlcBatchDocumentImportItem[0];
    }
}

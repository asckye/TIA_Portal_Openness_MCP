using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcExternalSourceDeleteResult
    {
        public string Status {get;internal set;}="planned";
        public bool Executed {get;internal set;}
        public bool Attempted {get;internal set;}
        public bool Deleted {get;internal set;}
        public bool RequiresSessionReset {get;internal set;}
        public string Error {get;internal set;}="";
        public string Release {get;internal set;}="";
        public string ProjectFile {get;internal set;}="";
        public int ProcessId {get;internal set;}
        public string SoftwarePath {get;internal set;}="";
        public string GroupPath => "";
        public string SourceName {get;internal set;}="";
        public string TargetIdentity {get;internal set;}="";
        public string RootIdentity {get;internal set;}="";
        public string PlanHash {get;internal set;}="";
        public string[] Inventory {get;internal set;}=new string[0];
        public string Generation => "notRun";
        public string Compilation => "notRun";
        public string Save => "notRun";
        public string Download => "notRun";
        public string Recovery => "notEstablished-no-automatic-backup-or-rollback";
        public string Policy => "root-external-source-delete-v1";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcDocumentImportResult
    {
        public string Status {get;internal set;}="planned";
        public bool Executed {get;internal set;}
        public bool Attempted {get;internal set;}
        public bool MayHaveChanged {get;internal set;}
        public bool RequiresSessionReset {get;internal set;}
        public bool ExistsVerified {get;internal set;}
        public bool ContentVerified => false;
        public string Error {get;internal set;}="";
        public string Release {get;internal set;}="";
        public string ProjectFile {get;internal set;}="";
        public int ProcessId {get;internal set;}
        public string TargetIdentity {get;internal set;}="";
        public string SoftwarePath {get;internal set;}="";
        public string GroupPath {get;internal set;}="";
        public string InputDirectory {get;internal set;}="";
        public string DeclaredName {get;internal set;}="";
        public string Kind => "GlobalDB";
        public string Language => "DB";
        public string CodeSha256 {get;internal set;}="";
        public string ResourceSha256 {get;internal set;}="";
        public string PlanHash {get;internal set;}="";
        public string[] Inventory {get;internal set;}=new string[0];
        public string[] ImportedIdentities {get;internal set;}=new string[0];
        public string[] Messages {get;internal set;}=new string[0];
        public string NativeState {get;internal set;}="notRun";
        public string NativeOptions => "None";
        public string Compilation => "notRun";
        public string Save => "notRun";
        public string Download => "notRun";
        public string InstalledUpdate => "unknown-not-inferred-from-major-version";
        public string Validation => "bounded-global-db-lexical-admission-not-Siemens-syntax-or-content-validity";
        public string Recovery => "unknown-no-automatic-retry-or-rollback";
        public string Policy => "single-document-global-db-v1";
    }
}

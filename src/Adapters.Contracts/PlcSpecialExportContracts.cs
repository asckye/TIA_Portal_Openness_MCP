using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcSpecialExportResult
    {
        public bool Executed { get; set; }
        public string ProjectFile { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string OutputFile { get; set; } = "";
        public string ReleaseKey { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Scope { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public string ExportOptions { get; set; } = "None";
        public string MethodEvidence { get; set; } = "static-sdk-signature-verified";
        public string SemanticsEvidence { get; set; } = "unverified";
        public string Status { get; set; } = "planned";
        public bool RequiresSessionReset { get; set; }
        public Dictionary<string,string> Evidence { get; set; } = new Dictionary<string,string>();
    }
}

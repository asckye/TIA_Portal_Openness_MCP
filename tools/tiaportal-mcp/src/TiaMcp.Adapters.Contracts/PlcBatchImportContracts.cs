using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcBatchImportObject
    {
        public string Name { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public string GroupPath { get; internal set; } = "";
        public int? Number { get; internal set; }
    }
    public sealed class PlcBatchImportItem
    {
        public string RelativePath { get; internal set; } = "";
        public string InputSha256 { get; internal set; } = "";
        public PlcBatchImportObject Planned { get; internal set; } = new PlcBatchImportObject();
        public string Status { get; internal set; } = "planned";
        public bool Attempted { get; internal set; }
        public string Failure { get; internal set; } = "";
        public string[] RecognizedDependencies { get; internal set; } = new string[0];
        public PlcBatchImportObject[] ReturnedObjects { get; internal set; } = new PlcBatchImportObject[0];
    }
    public sealed class PlcBatchImportFailure
    {
        public string Path { get; internal set; } = "";
        public string Error { get; internal set; } = "";
    }
    public sealed class PlcBatchImportResult
    {
        public bool Executed { get; internal set; }
        public string ProjectFile { get; internal set; } = "";
        public string SoftwarePath { get; internal set; } = "";
        public string Release { get; internal set; } = "";
        public string PlanHash { get; internal set; } = "";
        public bool Recursive { get; internal set; }
        public string DependencyStatus { get; internal set; } = "unverified-caller-order-required";
        public bool RequiresSessionReset { get; internal set; }
        public string[] Imported => Items.Where(x=>x.Status=="imported").SelectMany(x=>x.ReturnedObjects.Select(o=>o.Name)).ToArray();
        public PlcBatchImportFailure[] Failed => Items.Where(x=>x.Status=="failed").Select(x=>new PlcBatchImportFailure {Path=x.RelativePath,Error=x.Failure}).ToArray();
        public int ImportedCount => Imported.Length;
        public int FailedCount => Failed.Length;
        public PlcBatchImportItem[] Items { get; internal set; } = new PlcBatchImportItem[0];
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseCompileDiagnose : ResponseMessage
    {
        public string? State { get; set; }
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public IEnumerable<string>? Errors { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public IEnumerable<string>? Info { get; set; }
        public IEnumerable<string>? RawMessages { get; set; }
    }

    public class ResponseCrossReferences : ResponseMessage
    {
        public IEnumerable<CrossReferenceEntry>? Items { get; set; }
    }

    public class ResponseRepairAndCompile : ResponseMessage
    {
        public bool? Imported { get; set; }
        public string? ImportError { get; set; }
        public ResponseCompileDiagnose? Compile { get; set; }
        public IEnumerable<string>? Suggestions { get; set; }
    }

    public class ResponseSeed : ResponseMessage
    {
        public IEnumerable<string>? Imported { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
        public JsonObject? Placeholders { get; set; }
        public string? TempDir { get; set; }
    }

    public class CrossReferenceEntry
    {
        public string? SourceName { get; set; }
        public string? SourcePath { get; set; }
        public string? ReferenceName { get; set; }
        public string? ReferencePath { get; set; }
        public string? LocationName { get; set; }
        public string? ReferenceLocation { get; set; }
        public string? ReferenceType { get; set; }
        public string? Access { get; set; }
        // Typed CrossReference.SourceObject / ReferenceObject fields (null on the reflective fallback path).
        public string? SourceTypeName { get; set; }
        public string? SourceAddress { get; set; }
        public string? SourceDevice { get; set; }
        public string? SourceObjectClass { get; set; }
        public string? ReferenceTypeName { get; set; }
        public string? ReferenceAddress { get; set; }
        public string? ReferenceDevice { get; set; }
        public string? ReferenceObjectClass { get; set; }
    }
}

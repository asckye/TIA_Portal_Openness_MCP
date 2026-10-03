using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcProjectDetails
    {
        public string? Message { get; internal set; }
        public object? Meta { get; internal set; }
        public string? Name { get; internal set; }
        public PlcAttributeValue[]? Attributes { get; internal set; }
    }
    // Field names/types follow V17 Responses.cs and Types.cs at 70758f2.
    // Exact object paths are supplied separately in response Meta, not invented attributes.
    public sealed class PlcAttributeValue
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
        public string? AccessMode { get; set; }
    }
    public class PlcTypeDetails
    {
        public string? Path { get; set; }
        public string? Message { get; set; }
        public object? Meta { get; set; }
        public PlcAttributeValue[]? Attributes { get; set; }
        public string? Name { get; set; }
        public string? TypeName { get; set; }
        public string? Namespace { get; set; }
        public bool? IsConsistent { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool? IsKnowHowProtected { get; set; }
        public string? Description { get; set; }
    }
    public sealed class PlcBlockDetails : PlcTypeDetails
    {
        public string? ProgrammingLanguage { get; set; }
        public string? MemoryLayout { get; set; }
        public string? HeaderName { get; set; }
    }
    public sealed class PlcBlockHierarchy
    {
        public string? Name { get; set; }
        public PlcBlockHierarchy[]? Groups { get; set; }
        public PlcBlockDetails[]? Blocks { get; set; }
    }
}

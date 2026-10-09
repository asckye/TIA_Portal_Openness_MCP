using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class HardwareDeviceAttribute
    { public string Name { get; set; } = ""; public object? Value { get; set; } public string? AccessMode { get; set; } }
    public sealed class HardwareDeviceDescription
    { public string Name { get; set; } = ""; public string? Description { get; set; } public List<HardwareDeviceAttribute> Attributes { get; set; } = new List<HardwareDeviceAttribute>(); }
    public class HardwareGsdCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? Vendor { get; set; }
        public string? ProductFamily { get; set; }
        public string? MainFamily { get; set; }
        public string? DapId { get; set; }
        public string? DapName { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public string? GsdmlPath { get; set; }
        public int? Score { get; set; }
    }

    public class HardwareCatalogEntry
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public bool? Insertable { get; set; }
        public int? Score { get; set; }
    }

}

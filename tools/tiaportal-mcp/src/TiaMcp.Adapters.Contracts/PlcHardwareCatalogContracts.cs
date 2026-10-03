using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcHardwareCatalogCandidate
    {
        public string Source { get; set; } = "HardwareCatalog";
        public string Keyword { get; set; } = "";
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
    public sealed class PlcHardwareCatalogSearchResult
    {
        public string Keyword { get; set; } = "";
        public int Count { get; set; }
        public PlcHardwareCatalogCandidate[] Items { get; set; } = new PlcHardwareCatalogCandidate[0];
        public Dictionary<string,object> Meta { get; set; } = new Dictionary<string,object>();
    }
}

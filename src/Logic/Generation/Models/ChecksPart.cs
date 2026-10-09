using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class ChecksPart
    {
        public List<ChecksPartRulesItem> Rules { get; set; } = new List<ChecksPartRulesItem>();
    }

    public sealed class ChecksPartRulesItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Severity { get; set; } = "";
        public Dictionary<string, string> Description { get; set; } = new Dictionary<string, string>();
        public List<string>? Files { get; set; }
        public string? Xpath { get; set; }
        public int? MinCount { get; set; }
        public int? MaxCount { get; set; }
        public string? ValuePattern { get; set; }
        public string? ObjectKind { get; set; }
        public string? NamingRule { get; set; }
        public string? LibraryType { get; set; }
        public string? VersionRange { get; set; }
        public ChecksPartRulesItemTestSuite? TestSuite { get; set; }
        public string? Reference { get; set; }
        public Dictionary<string, string>? FixHint { get; set; }
        public bool? AutoFixable { get; set; }
    }

    public sealed class ChecksPartRulesItemTestSuite
    {
        public string RuleId { get; set; } = "";
        public Dictionary<string, JsonElement>? Properties { get; set; }
    }
}

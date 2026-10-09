using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class NamingPart
    {
        public List<NamingPartRulesItem> Rules { get; set; } = new List<NamingPartRulesItem>();
    }

    public sealed class NamingPartRulesItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Template { get; set; } = "";
        public string Pattern { get; set; } = "";
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public string? Case { get; set; }
        public List<string>? CommentLanguages { get; set; }
    }
}

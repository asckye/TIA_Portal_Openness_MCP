using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Domain
{
    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class BranchStep : DomainDto
    {
        internal BranchStep(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PropertyBranchStep : BranchStep
    {
        internal PropertyBranchStep(JsonElement json) : base(json) { }
        public string Property => Required<string>("property");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class AttributeBranchStep : BranchStep
    {
        internal AttributeBranchStep(JsonElement json) : base(json) { }
        public string Attribute => Required<string>("attribute");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class IndexBranchStep : BranchStep
    {
        internal IndexBranchStep(JsonElement json) : base(json) { }
        public int Index => Required<int>("index");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class NamedBranchStep : BranchStep
    {
        internal NamedBranchStep(JsonElement json) : base(json) { }
        public string Name => Required<string>("name");
        public string? Key => Optional<string?>("key", null);
    }
}

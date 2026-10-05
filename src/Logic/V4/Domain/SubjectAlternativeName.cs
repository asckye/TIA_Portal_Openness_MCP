using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Domain
{
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class SubjectAlternativeName : DomainDto
    {
        internal SubjectAlternativeName(JsonElement json) : base(json) { }
        public string Type => Required<string>("type");
        public string Value => Required<string>("value");
    }
}

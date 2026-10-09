using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.Generation
{
    public sealed class PackageReference
    {
        public string Package { get; set; } = "";
        public string Version { get; set; } = "";
    }

    public sealed class FileDigest
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public sealed class InterfaceMember
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Role { get; set; } = "";
        public Dictionary<string, string>? Comment { get; set; }
    }

    public sealed class ParameterSchema
    {
        public string Type { get; set; } = "";
        public Dictionary<string, ParameterSchema>? Properties { get; set; }
        public List<string>? Required { get; set; }
        public bool? AdditionalProperties { get; set; }
        public ParameterSchema? Items { get; set; }
        public List<JsonElement>? Enum { get; set; }
        public JsonElement? Minimum { get; set; }
        public JsonElement? Maximum { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public int? MinItems { get; set; }
        public int? MaxItems { get; set; }
        public string? Pattern { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonElement Default { get; set; }
        public string? Description { get; set; }
    }
}

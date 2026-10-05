using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Construction
{
    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class Member : ConstructionNode
    {
        public string Name { get; }
        public string Datatype { get; }
        public bool? ExternalWritable { get; }
        public string? Comment { get; }
        public string? CommentZhCn { get; }
        public string? StartValue { get; }
        internal string EffectiveComment => CommentZhCn ?? Comment ?? "";

        internal Member(JsonElement json) : base(json)
        {
            Name = json.GetProperty("name").GetString()!;
            Datatype = json.GetProperty("datatype").GetString()!;
            ExternalWritable = json.TryGetProperty("externalWritable", out var externalWritable) ? externalWritable.GetBoolean() : null;
            Comment = json.TryGetProperty("comment", out var comment) ? comment.GetString() : null;
            CommentZhCn = json.TryGetProperty("commentZhCn", out var commentZhCn) ? commentZhCn.GetString() : null;
            StartValue = json.TryGetProperty("startValue", out var startValue) ? startValue.GetString() : null;
        }
    }

    // The tool's outer kind selects this closed union; kind is not duplicated inside spec.
    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class PlcArtifactSpec : ConstructionSpec
    {
        internal PlcArtifactSpec(JsonElement json) : base(json) { }
        public static PlcArtifactSpec Deserialize(string kind, string json) => kind switch
        {
            "udt" => ConstructionJson.Deserialize<UdtSpec>(json),
            "tagtable" => ConstructionJson.Deserialize<PlcTagTableSpec>(json),
            "globaldb" => ConstructionJson.Deserialize<GlobalDbSpec>(json),
            "fc" => ConstructionJson.Deserialize<FcBlockSpec>(json),
            "fb" => ConstructionJson.Deserialize<FbBlockSpec>(json),
            _ => throw new ArgumentException("kind must be udt, tagtable, globaldb, fc or fb.", nameof(kind))
        };
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class UdtSpec : PlcArtifactSpec
    {
        public string? Name { get; }
        public IReadOnlyList<Member> Members { get; }
        internal UdtSpec(JsonElement json) : base(json)
        {
            Name = json.TryGetProperty("name", out var name) ? name.GetString() : null;
            Members = ConstructionJson.Rows(json, "members", x => new Member(x))!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class GlobalDbSpec : PlcArtifactSpec
    {
        public string DbName { get; }
        public int DbNumber { get; }
        public IReadOnlyList<Member> StaticMembers { get; }
        internal GlobalDbSpec(JsonElement json) : base(json)
        {
            DbName = json.GetProperty("dbName").GetString()!;
            DbNumber = json.GetProperty("dbNumber").GetInt32();
            StaticMembers = ConstructionJson.Rows(json, "staticMembers", x => new Member(x))!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class PlcTag : ConstructionNode
    {
        public string Name { get; }
        public string DataTypeName { get; }
        public string LogicalAddress { get; }
        internal PlcTag(JsonElement json) : base(json)
        {
            Name = json.GetProperty("name").GetString()!;
            DataTypeName = json.GetProperty("dataTypeName").GetString()!;
            LogicalAddress = json.GetProperty("logicalAddress").GetString()!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class PlcTagTableSpec : PlcArtifactSpec
    {
        public string TableName { get; }
        public IReadOnlyList<PlcTag> Tags { get; }
        internal PlcTagTableSpec(JsonElement json) : base(json)
        {
            TableName = json.GetProperty("tableName").GetString()!;
            Tags = ConstructionJson.Rows(json, "tags", x => new PlcTag(x))!;
        }
    }
}

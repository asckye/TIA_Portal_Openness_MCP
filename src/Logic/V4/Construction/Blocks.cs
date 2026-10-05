using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Construction
{
    public enum CallSourceKind { Global, Local, Constant }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class CallParameter : ConstructionNode
    {
        public string Name { get; }
        public string Section { get; }
        public string DataType { get; }
        public CallSourceKind? SourceKind { get; }
        public IReadOnlyList<string>? SymbolPath { get; }
        public string? ConstantValue { get; }
        internal CallSourceKind EffectiveSource => SourceKind ?? CallSourceKind.Global;
        internal CallParameter(JsonElement json) : base(json)
        {
            Name = json.GetProperty("name").GetString()!;
            Section = json.GetProperty("section").GetString()!;
            DataType = json.GetProperty("dataType").GetString()!;
            SourceKind = (json.TryGetProperty("sourceKind", out var sourceKind) ? sourceKind.GetString() : null) switch
            {
                null => null, "global" => CallSourceKind.Global, "local" => CallSourceKind.Local,
                "constant" => CallSourceKind.Constant, _ => throw new ArgumentException("Unknown call sourceKind.")
            };
            SymbolPath = ConstructionJson.Rows(json, "symbolPath", x => x.GetString()!);
            ConstantValue = json.TryGetProperty("constantValue", out var constantValue) ? constantValue.GetString() : null;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class FlgNetCallSpec : ConstructionSpec
    {
        public string CallName { get; }
        public IReadOnlyList<CallParameter> Parameters { get; }
        internal FlgNetCallSpec(JsonElement json) : base(json)
        {
            CallName = json.GetProperty("callName").GetString()!;
            Parameters = ConstructionJson.Rows(json, "parameters", x => new CallParameter(x))!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class SclBlockSpec : PlcArtifactSpec
    {
        public string BlockName { get; }
        public int BlockNumber { get; }
        public IReadOnlyList<Member>? Inputs { get; }
        public IReadOnlyList<Member>? Outputs { get; }
        public StructuredTextSpec StructuredText { get; }
        public string? CommentZhCn { get; }
        public string? TitleZhCn { get; }
        public string? NetworkCommentZhCn { get; }
        public string? NetworkTitleZhCn { get; }

        internal SclBlockSpec(JsonElement json) : base(json)
        {
            BlockName = json.GetProperty("blockName").GetString()!;
            BlockNumber = json.GetProperty("blockNumber").GetInt32();
            Inputs = Members(json, "inputs");
            Outputs = Members(json, "outputs");
            StructuredText = new StructuredTextSpec(json.GetProperty("structuredText"));
            CommentZhCn = json.TryGetProperty("commentZhCn", out var commentZhCn) ? commentZhCn.GetString() : null;
            TitleZhCn = json.TryGetProperty("titleZhCn", out var titleZhCn) ? titleZhCn.GetString() : null;
            NetworkCommentZhCn = json.TryGetProperty("networkCommentZhCn", out var networkCommentZhCn) ? networkCommentZhCn.GetString() : null;
            NetworkTitleZhCn = json.TryGetProperty("networkTitleZhCn", out var networkTitleZhCn) ? networkTitleZhCn.GetString() : null;
        }

        internal static IReadOnlyList<Member>? Members(JsonElement json, string field) =>
            ConstructionJson.Rows(json, field, x => new Member(x));
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class FcBlockSpec : SclBlockSpec
    {
        internal FcBlockSpec(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class FbBlockSpec : SclBlockSpec
    {
        public IReadOnlyList<Member>? Inouts { get; }
        public IReadOnlyList<Member>? Statics { get; }
        public IReadOnlyList<Member>? Temps { get; }
        internal FbBlockSpec(JsonElement json) : base(json)
        {
            Inouts = Members(json, "inouts");
            Statics = Members(json, "statics");
            Temps = Members(json, "temps");
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadNetwork : ConstructionNode
    {
        public FlgNetCallSpec Call { get; }
        public string? TitleZhCn { get; }
        public string? CommentZhCn { get; }
        internal LadNetwork(JsonElement json) : base(json)
        {
            Call = new FlgNetCallSpec(json.GetProperty("call"));
            TitleZhCn = json.TryGetProperty("titleZhCn", out var titleZhCn) ? titleZhCn.GetString() : null;
            CommentZhCn = json.TryGetProperty("commentZhCn", out var commentZhCn) ? commentZhCn.GetString() : null;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadFcBlockSpec : ConstructionSpec
    {
        public string BlockName { get; }
        public int BlockNumber { get; }
        public IReadOnlyList<Member>? Inputs { get; }
        public IReadOnlyList<Member>? Outputs { get; }
        public IReadOnlyList<LadNetwork> Networks { get; }
        public string? CommentZhCn { get; }
        public string? TitleZhCn { get; }
        internal LadFcBlockSpec(JsonElement json) : base(json)
        {
            BlockName = json.GetProperty("blockName").GetString()!;
            BlockNumber = json.GetProperty("blockNumber").GetInt32();
            Inputs = SclBlockSpec.Members(json, "inputs");
            Outputs = SclBlockSpec.Members(json, "outputs");
            Networks = ConstructionJson.Rows(json, "networks", x => new LadNetwork(x))!;
            CommentZhCn = json.TryGetProperty("commentZhCn", out var commentZhCn) ? commentZhCn.GetString() : null;
            TitleZhCn = json.TryGetProperty("titleZhCn", out var titleZhCn) ? titleZhCn.GetString() : null;
        }
    }
}

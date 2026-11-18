using System;
using System.Collections.Generic;
using System.Linq;
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
        public FlgNetCallSpec? Call { get; }
        public LadRung? Rung { get; }
        public string? TitleZhCn { get; }
        public string? CommentZhCn { get; }
        internal LadNetwork(JsonElement json) : base(json)
        {
            Call = json.TryGetProperty("call", out var call) ? new FlgNetCallSpec(call) : null;
            Rung = json.TryGetProperty("rung", out var rung) ? new LadRung(rung) : null;
            TitleZhCn = json.TryGetProperty("titleZhCn", out var titleZhCn) ? titleZhCn.GetString() : null;
            CommentZhCn = json.TryGetProperty("commentZhCn", out var commentZhCn) ? commentZhCn.GetString() : null;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadComponent : ConstructionNode
    {
        public string Name { get; }
        public IReadOnlyList<int>? Index { get; }
        internal LadComponent(JsonElement json) : base(json)
        {
            Name = json.ValueKind == JsonValueKind.String ? json.GetString()! : json.GetProperty("name").GetString()!;
            Index = json.ValueKind == JsonValueKind.Object ? ConstructionJson.Rows(json, "index", x => x.GetInt32()) : null;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadOperand : ConstructionNode
    {
        public string Kind { get; }
        public IReadOnlyList<LadComponent>? Components { get; }
        public string? DataType { get; }
        public string? Value { get; }
        internal LadOperand(JsonElement json) : base(json)
        {
            Kind = json.EnumerateObject().First().Name;
            var value = json.GetProperty(Kind);
            if (Kind == "global" || Kind == "local")
                Components = Array.AsReadOnly(value.EnumerateArray().Select(x => new LadComponent(x)).ToArray());
            else if (Kind == "literal")
            {
                DataType = value.GetProperty("type").GetString()!;
                Value = value.GetProperty("value").GetString()!;
            }
            else Value = value.GetString()!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadCondition : ConstructionNode
    {
        public string Kind { get; }
        public LadOperand? Operand { get; }
        public LadOperand? Memory { get; }
        public string? Op { get; }
        public string? DataType { get; }
        public LadOperand? Left { get; }
        public LadOperand? Right { get; }
        public IReadOnlyList<IReadOnlyList<LadCondition>>? Paths { get; }
        internal LadCondition(JsonElement json) : base(json)
        {
            Kind = json.GetProperty("kind").GetString()!;
            Operand = json.TryGetProperty("operand", out var operand) ? new LadOperand(operand) : null;
            Memory = json.TryGetProperty("memory", out var memory) ? new LadOperand(memory) : null;
            Op = json.TryGetProperty("op", out var op) ? op.GetString() : null;
            DataType = json.TryGetProperty("type", out var type) ? type.GetString() : null;
            Left = json.TryGetProperty("left", out var left) ? new LadOperand(left) : null;
            Right = json.TryGetProperty("right", out var right) ? new LadOperand(right) : null;
            Paths = ConstructionJson.Rows<IReadOnlyList<LadCondition>>(json, "paths",
                path => Array.AsReadOnly(path.EnumerateArray().Select(x => new LadCondition(x)).ToArray()));
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadCallPin : ConstructionNode
    {
        public string Name { get; }
        public string Section { get; }
        public string DataType { get; }
        public LadOperand Value { get; }
        internal LadCallPin(JsonElement json) : base(json)
        {
            Name = json.GetProperty("name").GetString()!;
            Section = json.GetProperty("section").GetString()!;
            DataType = json.GetProperty("type").GetString()!;
            Value = new LadOperand(json.GetProperty("value"));
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadOutput : ConstructionNode
    {
        public string Kind { get; }
        public LadOperand? Operand { get; }
        public LadOperand? In { get; }
        public IReadOnlyList<LadOperand>? Out { get; }
        public IReadOnlyList<LadOutput>? Then { get; }
        public string? DataType { get; }
        public LadOperand? Instance { get; }
        public LadOperand? Pt { get; }
        public LadOperand? Et { get; }
        public string? ValueType { get; }
        public LadOperand? Pv { get; }
        public LadOperand? Cv { get; }
        public IReadOnlyList<LadCondition>? Reset { get; }
        public IReadOnlyList<LadCondition>? Load { get; }
        public IReadOnlyList<LadCondition>? Down { get; }
        public IReadOnlyList<LadOutput>? Q { get; }
        public IReadOnlyList<LadOutput>? Qd { get; }
        public string? Block { get; }
        public string? BlockType { get; }
        public string? InstanceDb { get; }
        public IReadOnlyList<LadCallPin>? Pins { get; }
        internal LadOutput(JsonElement json) : base(json)
        {
            Kind = json.GetProperty("kind").GetString()!;
            Operand = json.TryGetProperty("operand", out var operand) ? new LadOperand(operand) : null;
            In = json.TryGetProperty("in", out var input) ? new LadOperand(input) : null;
            Out = ConstructionJson.Rows(json, "out", x => new LadOperand(x));
            Then = ConstructionJson.Rows(json, "then", x => new LadOutput(x));
            DataType = json.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (json.TryGetProperty("instance", out var instance))
            {
                if (Kind == "call") InstanceDb = instance.GetString();
                else Instance = new LadOperand(instance);
            }
            Pt = json.TryGetProperty("pt", out var pt) ? new LadOperand(pt) : null;
            Et = json.TryGetProperty("et", out var et) ? new LadOperand(et) : null;
            ValueType = json.TryGetProperty("valueType", out var valueType) ? valueType.GetString() : null;
            Pv = json.TryGetProperty("pv", out var pv) ? new LadOperand(pv) : null;
            Cv = json.TryGetProperty("cv", out var cv) ? new LadOperand(cv) : null;
            Reset = ConstructionJson.Rows(json, "reset", x => new LadCondition(x));
            Load = ConstructionJson.Rows(json, "load", x => new LadCondition(x));
            Down = ConstructionJson.Rows(json, "down", x => new LadCondition(x));
            Q = ConstructionJson.Rows(json, "q", x => new LadOutput(x));
            Qd = ConstructionJson.Rows(json, "qd", x => new LadOutput(x));
            Block = json.TryGetProperty("block", out var block) ? block.GetString() : null;
            BlockType = json.TryGetProperty("blockType", out var blockType) ? blockType.GetString() : null;
            Pins = ConstructionJson.Rows(json, "pins", x => new LadCallPin(x));
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadBranch : ConstructionNode
    {
        public IReadOnlyList<LadCondition> Series { get; }
        public IReadOnlyList<LadOutput> Outputs { get; }
        internal LadBranch(JsonElement json) : base(json)
        {
            Series = ConstructionJson.Rows(json, "series", x => new LadCondition(x))!;
            Outputs = ConstructionJson.Rows(json, "outputs", x => new LadOutput(x))!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LadRung : ConstructionNode
    {
        public IReadOnlyList<LadCondition> Series { get; }
        public IReadOnlyList<LadOutput> Outputs { get; }
        public IReadOnlyList<LadBranch>? FanOut { get; }
        internal LadRung(JsonElement json) : base(json)
        {
            Series = ConstructionJson.Rows(json, "series", x => new LadCondition(x))!;
            Outputs = ConstructionJson.Rows(json, "outputs", x => new LadOutput(x))!;
            FanOut = ConstructionJson.Rows(json, "fanOut", x => new LadBranch(x));
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

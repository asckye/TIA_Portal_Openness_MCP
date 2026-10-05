using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Construction
{
    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class StructuredTextSpec : ConstructionSpec
    {
        public int? FirstUid { get; }
        public IReadOnlyList<Statement> Operations { get; }
        internal StructuredTextSpec(JsonElement json) : base(json)
        {
            FirstUid = json.TryGetProperty("firstUid", out var firstUid) ? firstUid.GetInt32() : null;
            Operations = ConstructionJson.Rows(json, "operations", Statement.Read)!;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class Statement : ConstructionNode
    {
        public string Op { get; }
        internal Statement(JsonElement json) : base(json)
        {
            Op = json.GetProperty("op").GetString()!;
        }

        internal static Statement Read(JsonElement json)
        {
            return json.GetProperty("op").GetString() switch
            {
                "assign" => new AssignmentStatement(json),
                "if" or "elsif" => new ConditionStatement(json),
                "else" or "endif" => new BoundaryStatement(json),
                "symbol" or "global" or "local" => new SymbolStatement(json),
                "literal" => new LiteralStatement(json),
                "token" => new TokenStatement(json),
                "blank" => new BlankStatement(json),
                "newline" => new NewLineStatement(json),
                "line" => new LineStatement(json),
                _ => throw new ArgumentException("Unknown StructuredText op.")
            };
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class IndentedStatement : Statement
    {
        public int? Indent { get; }
        internal IndentedStatement(JsonElement json) : base(json)
        { Indent = json.TryGetProperty("indent", out var indent) ? indent.GetInt32() : null; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class AssignmentStatement : IndentedStatement
    {
        public string Target { get; }
        public string? Source { get; }
        public string? LiteralValue { get; }
        internal AssignmentStatement(JsonElement json) : base(json)
        {
            Target = json.GetProperty("target").GetString()!;
            Source = json.TryGetProperty("source", out var source) ? source.GetString() : null;
            LiteralValue = json.TryGetProperty("literalValue", out var literalValue) ? literalValue.GetString() : null;
        }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class ConditionStatement : IndentedStatement
    {
        public string Condition { get; }
        internal ConditionStatement(JsonElement json) : base(json)
        { Condition = json.GetProperty("condition").GetString()!; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class BoundaryStatement : IndentedStatement
    {
        internal BoundaryStatement(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class SymbolStatement : IndentedStatement
    {
        public string Name { get; }
        internal SymbolStatement(JsonElement json) : base(json)
        { Name = json.GetProperty("name").GetString()!; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class TokenStatement : IndentedStatement
    {
        public string Text { get; }
        internal TokenStatement(JsonElement json) : base(json)
        { Text = json.GetProperty("text").GetString()!; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LiteralStatement : IndentedStatement
    {
        public string Value { get; }
        internal LiteralStatement(JsonElement json) : base(json)
        { Value = json.GetProperty("value").GetString()!; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class BlankStatement : Statement
    {
        public int? Count { get; }
        internal BlankStatement(JsonElement json) : base(json)
        { Count = json.TryGetProperty("count", out var count) ? count.GetInt32() : null; }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class NewLineStatement : Statement
    {
        internal NewLineStatement(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LineStatement : IndentedStatement
    {
        public IReadOnlyList<LineItem> Items { get; }
        internal LineStatement(JsonElement json) : base(json)
        { Items = ConstructionJson.Rows(json, "items", x => new LineItem(x))!; }
    }

    public enum LineItemKind { Symbol, Token, Literal, RawToken }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public sealed class LineItem : ConstructionNode
    {
        public LineItemKind Kind { get; }
        public string Text { get; }
        internal LineItem(JsonElement json) : base(json)
        {
            var key = json.EnumerateObject().Single().Name;
            Kind = key switch { "sym" => LineItemKind.Symbol, "token" => LineItemKind.Token,
                "lit" => LineItemKind.Literal, _ => LineItemKind.RawToken };
            // raw is a token with different spacing, never raw XML.
            Text = json.GetProperty(key).GetString()!;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    internal static class ConstructionSchemas
    {
        private static readonly InputSchema Text = InputSchema.String(ConstructionJson.MaxStringCharacters);
        private static readonly InputSchema Nonblank = InputSchema.String(ConstructionJson.MaxStringCharacters, pattern: @"\S");
        private static readonly InputSchema Integer = InputSchema.Integer();
        private static readonly IReadOnlyDictionary<Type, InputSchema> Schemas = Build();

        internal static InputSchema For(Type type) => Schemas.TryGetValue(type, out var schema) ? schema
            : throw new ArgumentException("Select a concrete construction type using the outer kind.", nameof(type));

        private static InputSchema Rows(InputSchema item) => InputSchema.Array(item, maximum: ConstructionJson.MaxItems);
        private static InputSchema Object(string[] required, params (string Name, InputSchema Schema)[] fields) =>
            InputSchema.Object(fields.ToDictionary(f => f.Name, f => f.Schema, StringComparer.Ordinal), required);

        private static IReadOnlyDictionary<Type, InputSchema> Build()
        {
            var schemas = new Dictionary<Type, InputSchema>();
            InputSchema MemberSchema(bool writable, bool startValue)
            {
                var fields = new List<(string, InputSchema)> { ("name", Nonblank), ("datatype", Nonblank) };
                if (writable) fields.Add(("externalWritable", InputSchema.Boolean()));
                if (startValue) fields.Add(("startValue", Text));
                return InputSchema.Union(new[] { "comment", "commentZhCn" }.Select(comment =>
                    Object(new[] { "name", "datatype" }, fields.Concat(new[] { (comment, Text) }).ToArray())).ToArray());
            }
            var member = MemberSchema(true, true);
            var blockMember = MemberSchema(false, false);
            schemas.Add(typeof(Member), member);
            schemas.Add(typeof(UdtSpec), Object(new[] { "members" }, ("name", Text), ("members", Rows(MemberSchema(true, false)))));
            schemas.Add(typeof(GlobalDbSpec), Object(new[] { "dbName", "dbNumber", "staticMembers" },
                ("dbName", Nonblank), ("dbNumber", Integer), ("staticMembers", Rows(member))));
            var tag = Object(new[] { "name", "dataTypeName", "logicalAddress" },
                ("name", Nonblank), ("dataTypeName", Nonblank), ("logicalAddress", Nonblank));
            schemas.Add(typeof(PlcTag), tag);
            schemas.Add(typeof(PlcTagTableSpec), Object(new[] { "tableName", "tags" }, ("tableName", Nonblank), ("tags", Rows(tag))));

            var lineItem = InputSchema.Union(new[] { "sym", "token", "lit", "raw" }
                .Select(key => Object(new[] { key }, (key, Nonblank))).ToArray());
            schemas.Add(typeof(LineItem), lineItem);
            InputSchema Operation(string[] ops, string[] required, bool indent, params (string Name, InputSchema Schema)[] fields) =>
                Object(new[] { "op" }.Concat(required).ToArray(), new[] { ("op", InputSchema.String(allowed: ops)) }
                    .Concat(indent ? new[] { ("indent", Integer) } : Array.Empty<(string, InputSchema)>()).Concat(fields).ToArray());
            var operations = new Dictionary<Type, InputSchema>
            {
                [typeof(AssignmentStatement)] = InputSchema.Union(
                    Operation(new[] { "assign" }, new[] { "target", "source" }, true, ("target", Nonblank), ("source", Text)),
                    Operation(new[] { "assign" }, new[] { "target", "literalValue" }, true, ("target", Nonblank), ("literalValue", Text))),
                [typeof(ConditionStatement)] = Operation(new[] { "if", "elsif" }, new[] { "condition" }, true, ("condition", Nonblank)),
                [typeof(BoundaryStatement)] = Operation(new[] { "else", "endif" }, Array.Empty<string>(), true),
                [typeof(SymbolStatement)] = Operation(new[] { "symbol", "global", "local" }, new[] { "name" }, true, ("name", Nonblank)),
                [typeof(LiteralStatement)] = Operation(new[] { "literal" }, new[] { "value" }, true, ("value", Nonblank)),
                [typeof(TokenStatement)] = Operation(new[] { "token" }, new[] { "text" }, true, ("text", Nonblank)),
                [typeof(BlankStatement)] = Operation(new[] { "blank" }, Array.Empty<string>(), false, ("count", Integer)),
                [typeof(NewLineStatement)] = Operation(new[] { "newline" }, Array.Empty<string>(), false),
                [typeof(LineStatement)] = Operation(new[] { "line" }, new[] { "items" }, true, ("items", Rows(lineItem)))
            };
            foreach (var entry in operations) schemas.Add(entry.Key, entry.Value);
            schemas.Add(typeof(IndentedStatement), InputSchema.Union(operations
                .Where(entry => typeof(IndentedStatement).IsAssignableFrom(entry.Key)).Select(entry => entry.Value).ToArray()));
            var statement = InputSchema.Union(operations.Values.ToArray());
            schemas.Add(typeof(Statement), statement);
            var st = Object(new[] { "operations" }, ("firstUid", Integer), ("operations", Rows(statement)));
            schemas.Add(typeof(StructuredTextSpec), st);

            var parameter = InputSchema.Union(
                Object(new[] { "name", "section", "dataType", "symbolPath" }, ("name", Nonblank), ("section", Nonblank), ("dataType", Nonblank),
                    ("sourceKind", InputSchema.String(allowed: new[] { "global", "local" })), ("symbolPath", Rows(Nonblank))),
                Object(new[] { "name", "section", "dataType", "sourceKind", "constantValue" }, ("name", Nonblank), ("section", Nonblank), ("dataType", Nonblank),
                    ("sourceKind", InputSchema.String(allowed: new[] { "constant" })), ("constantValue", Text)));
            schemas.Add(typeof(CallParameter), parameter);
            var call = Object(new[] { "callName", "parameters" }, ("callName", Nonblank), ("parameters", Rows(parameter)));
            schemas.Add(typeof(FlgNetCallSpec), call);
            var common = new[] { ("blockName", Nonblank), ("blockNumber", Integer), ("inputs", Rows(blockMember)), ("outputs", Rows(blockMember)),
                ("structuredText", st), ("commentZhCn", Text), ("titleZhCn", Text), ("networkCommentZhCn", Text), ("networkTitleZhCn", Text) };
            schemas.Add(typeof(FcBlockSpec), Object(new[] { "blockName", "blockNumber", "inputs", "outputs", "structuredText" }, common));
            schemas.Add(typeof(FbBlockSpec), Object(new[] { "blockName", "blockNumber", "structuredText" },
                common.Concat(new[] { ("inouts", Rows(blockMember)), ("statics", Rows(blockMember)), ("temps", Rows(blockMember)) }).ToArray()));
            var network = Object(new[] { "call" }, ("call", call), ("titleZhCn", Text), ("commentZhCn", Text));
            schemas.Add(typeof(LadNetwork), network);
            schemas.Add(typeof(LadFcBlockSpec), Object(new[] { "blockName", "blockNumber", "networks" },
                ("blockName", Nonblank), ("blockNumber", Integer), ("inputs", Rows(blockMember)), ("outputs", Rows(blockMember)),
                ("networks", Rows(network)), ("commentZhCn", Text), ("titleZhCn", Text)));
            return schemas;
        }
    }
}

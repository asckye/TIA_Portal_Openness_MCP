using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    internal static partial class ConstructionSchemas
    {
        private static void AddLadderSchemas(Dictionary<Type, InputSchema> schemas, InputSchema call, InputSchema member)
        {
            JsonNode S(InputSchema schema) => JsonNode.Parse(schema.Json.GetRawText())!;
            JsonObject Ref(string name) => new JsonObject { ["$ref"] = "#/$defs/" + name };
            JsonObject Obj(string[] required, params (string Name, JsonNode Schema)[] fields) => new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema)))
            };
            JsonObject Arr(JsonNode item, int min, int max) => new JsonObject
                { ["type"] = "array", ["items"] = item, ["minItems"] = min, ["maxItems"] = max };
            JsonObject Union(params JsonNode[] choices) => new JsonObject { ["anyOf"] = new JsonArray(choices) };
            JsonNode Enum(params string[] values) => S(InputSchema.String(allowed: values));
            JsonNode Kind(string kind) => new JsonObject { ["const"] = kind };
            JsonObject Field(string field, JsonNode yes, JsonNode no) => new JsonObject
                { ["if"] = new JsonObject { ["required"] = new JsonArray(field) }, ["then"] = yes, ["else"] = no };
            JsonObject Choose(string[] kinds, JsonNode yes, JsonNode no) => new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("kind"), ["properties"] = new JsonObject { ["kind"] = Enum(kinds) } },
                ["then"] = yes, ["else"] = no
            };
            var definitions = new JsonObject();
            definitions["component"] = Union(S(Nonblank), Obj(new[] { "name", "index" },
                ("name", S(Nonblank)), ("index", Arr(S(Integer), 1, 6))));
            JsonNode Symbol(string scope) => Obj(new[] { scope }, (scope, Arr(Ref("component"), 1, 32)));
            definitions["global"] = Symbol("global");
            definitions["global"]!["not"] = new JsonObject { ["required"] = new JsonArray("local") };
            var symbol = Field("global", Ref("global"), Symbol("local"));
            symbol["type"] = "object";
            symbol["anyOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("global") },
                new JsonObject { ["required"] = new JsonArray("local") });
            definitions["symbol"] = symbol;
            var literalTypes = new[] { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt",
                "LInt", "ULInt", "Real", "LReal" };
            var element = Enum(literalTypes.Concat(new[] { "Time", "LTime" }).ToArray());
            definitions["operand"] = Field("global", Ref("symbol"), Field("local", Ref("symbol"),
                Field("literal", Obj(new[] { "literal" },
                    ("literal", Obj(new[] { "type", "value" }, ("type", Enum(literalTypes)), ("value", S(Nonblank))))),
                    Field("typed", Obj(new[] { "typed" },
                        ("typed", S(InputSchema.String(ConstructionJson.MaxStringCharacters, pattern: @"^(T|TIME|LT|S5T)#.+")))),
                        Obj(new[] { "constant" }, ("constant", S(Nonblank)))))));
            definitions["simpleCondition"] = Choose(new[] { "compare" },
                Obj(new[] { "kind", "op", "type", "left", "right" }, ("kind", Kind("compare")),
                    ("op", Enum("eq", "ne", "gt", "ge", "lt", "le")), ("type", element.DeepClone()),
                    ("left", Ref("operand")), ("right", Ref("operand"))),
                Choose(new[] { "rising", "falling" },
                    Obj(new[] { "kind", "operand", "memory" }, ("kind", Enum("rising", "falling")),
                        ("operand", Ref("symbol")), ("memory", Ref("global"))),
                    Obj(new[] { "kind", "operand" }, ("kind", Enum("no", "nc")), ("operand", Ref("symbol")))));
            definitions["simpleCondition"]!["not"] = new JsonObject { ["required"] = new JsonArray("paths") };
            definitions["condition"] = new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("kind"), ["properties"] = new JsonObject { ["kind"] = Kind("or") } },
                ["then"] = Obj(new[] { "kind", "paths" }, ("kind", Kind("or")), ("paths", Arr(Arr(Ref("simpleCondition"), 1, 16), 2, 8))),
                ["else"] = Ref("simpleCondition")
            };
            var basicOutput = Union(
                Obj(new[] { "kind", "operand" }, ("kind", Enum("coil", "set", "reset")), ("operand", Ref("symbol"))),
                Obj(new[] { "kind", "in", "out" }, ("kind", Kind("move")), ("in", Ref("operand")),
                    ("out", Arr(Ref("symbol"), 1, 8)), ("then", Arr(Ref("output"), 1, 8))));
            var timer = Obj(new[] { "kind", "type", "instance", "pt" }, ("kind", Kind("timer")),
                ("type", Enum("TON", "TOF", "TP")), ("instance", Ref("global")), ("pt", Ref("operand")),
                ("q", Arr(Ref("output"), 1, 8)), ("et", Ref("symbol")));
            var counter = Obj(new[] { "kind", "type", "instance", "valueType", "pv" }, ("kind", Kind("counter")),
                ("type", Enum("CTU", "CTD", "CTUD")), ("instance", Ref("global")),
                ("valueType", Enum("SInt", "Int", "DInt", "USInt", "UInt", "UDInt", "LInt", "ULInt")),
                ("pv", Ref("operand")), ("reset", Arr(Ref("condition"), 1, 16)),
                ("load", Arr(Ref("condition"), 1, 16)), ("down", Arr(Ref("condition"), 1, 16)),
                ("cv", Ref("symbol")), ("q", Arr(Ref("output"), 1, 8)), ("qd", Arr(Ref("output"), 1, 8)));
            definitions["pin"] = Obj(new[] { "name", "section", "type", "value" }, ("name", S(Nonblank)),
                ("section", Enum("Input", "Output", "InOut")), ("type", element.DeepClone()), ("value", Ref("operand")));
            var userCall = Obj(new[] { "kind", "block", "blockType", "pins" }, ("kind", Kind("call")),
                ("block", S(Nonblank)), ("blockType", Enum("FC", "FB")), ("instance", S(Nonblank)),
                ("pins", Arr(Ref("pin"), 0, 64)), ("then", Arr(Ref("output"), 1, 8)));
            definitions["output"] = Choose(new[] { "timer" }, timer,
                Choose(new[] { "counter" }, counter, Choose(new[] { "call" }, userCall, basicOutput)));
            definitions["branch"] = Obj(new[] { "series", "outputs" },
                ("series", Arr(Ref("condition"), 1, 32)), ("outputs", Arr(Ref("output"), 1, 16)));
            definitions["rung"] = Obj(new[] { "series", "outputs" }, ("series", Arr(Ref("condition"), 0, 32)),
                ("outputs", Arr(Ref("output"), 0, 16)), ("fanOut", Arr(Ref("branch"), 1, 8)));
            JsonNode Network(string field, JsonNode schema) => Obj(new[] { field }, (field, schema),
                ("titleZhCn", S(Text)), ("commentZhCn", S(Text)));
            definitions["network"] = new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("rung") },
                ["then"] = Network("rung", Ref("rung")), ["else"] = Network("call", S(call))
            };
            void Add(Type type, JsonObject schema)
            {
                schema["$defs"] = definitions.DeepClone();
                schemas.Add(type, new InputSchema(JsonSerializer.SerializeToElement(schema)));
            }
            Add(typeof(LadComponent), Ref("component"));
            Add(typeof(LadOperand), Ref("operand"));
            Add(typeof(LadCondition), Ref("condition"));
            Add(typeof(LadOutput), Ref("output"));
            Add(typeof(LadCallPin), Ref("pin"));
            Add(typeof(LadBranch), Ref("branch"));
            Add(typeof(LadRung), Ref("rung"));
            Add(typeof(LadNetwork), Ref("network"));
            Add(typeof(LadFcBlockSpec), Obj(new[] { "blockName", "blockNumber", "networks" },
                ("blockName", S(Nonblank)), ("blockNumber", S(Integer)),
                ("inputs", S(Rows(member))), ("outputs", S(Rows(member))),
                ("networks", Arr(Ref("network"), 1, ConstructionJson.MaxNetworks)),
                ("commentZhCn", S(Text)), ("titleZhCn", S(Text))));
        }
    }
}

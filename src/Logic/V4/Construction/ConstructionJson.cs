using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    // Immutable inputs are created through V4Json. Keeping the validated element preserves
    // omission (including empty optional arrays/strings) without introducing wire defaults.
    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class ConstructionNode
    {
        internal JsonElement Json { get; }
        internal ConstructionNode(JsonElement json) { Json = json.Clone(); }
    }

    [JsonConverter(typeof(ConstructionJsonConverter))]
    public abstract class ConstructionSpec : ConstructionNode
    {
        internal ConstructionSpec(JsonElement json) : base(json) { }
    }

    internal static class ConstructionJson
    {
        internal const int MaxJsonCharacters = 262144;
        internal const int MaxDepth = 16;
        internal const int MaxStringCharacters = 4096;
        internal const int MaxItems = 1000;
        internal const int MaxXmlCharacters = 1048576;
        internal const int MaxNetworks = 64;
        internal static readonly InputBudget InputBudget = new InputBudget(depth: MaxDepth, stringLength: MaxStringCharacters, items: MaxItems);

        // Existing builder callers retain ArgumentException. Contracts below use the
        // original rejection so budget errors retain their V4 limit/actual details.
        internal static T Deserialize<T>(string json) where T : ConstructionNode
        {
            var result = new ConstructionInput<T>().Read(json, "spec");
            if (result.IsValid) return result.Value!;
            var error = new ArgumentException(result.Error!.Message, nameof(json));
            error.Data["V4Error"] = result.Error;
            throw error;
        }

        internal static void Require(bool condition, string message)
        {
            try { InputGuard.Require(condition); }
            catch (InputRejection rejection) { throw new ArgumentException(message, rejection); }
        }

        internal static void Limit(int actual, int limit)
        {
            try { InputGuard.Limit(actual, limit); }
            catch (InputRejection rejection) { throw new ArgumentException("Construction budget exceeded.", rejection); }
        }

        internal static IReadOnlyList<T>? Rows<T>(JsonElement json, string field, Func<JsonElement, T> read) =>
            json.TryGetProperty(field, out var value) ? Array.AsReadOnly(value.EnumerateArray().Select(read).ToArray()) : null;

        internal static void Budget(JsonElement json, JsonSerializerOptions options)
        {
            try { InputBudget.Check(json); XmlCharacters(json); }
            catch (InputRejection rejection) { throw new ArgumentException("Construction input exceeds its contract.", rejection); }
        }

        // Foundation counts decoded, compact XML-oriented JSON, whereas InputBudget
        // counts compact V4 escaping and raw text. Keep only that family policy here;
        // depth, strings, items and duplicate keys are checked by InputBudget first.
        internal static void XmlCharacters(JsonElement json)
        {
            int characters = 0;
            var pending = new Stack<JsonElement>();
            pending.Push(json);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                switch (node.ValueKind)
                {
                    case JsonValueKind.Object:
                        var properties = node.EnumerateObject().ToArray();
                        characters += 2 + Math.Max(0, properties.Length - 1);
                        foreach (var property in properties)
                        {
                            characters += JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(property.Name).Length + 3;
                            pending.Push(property.Value);
                        }
                        break;
                    case JsonValueKind.Array:
                        characters += 2 + Math.Max(0, node.GetArrayLength() - 1);
                        foreach (var child in node.EnumerateArray()) pending.Push(child);
                        break;
                    case JsonValueKind.String:
                        string text = node.GetString()!;
                        XmlConvert.VerifyXmlChars(text);
                        characters += JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text).Length + 2;
                        break;
                    default:
                        InputGuard.Require(node.ValueKind != JsonValueKind.Null);
                        characters += node.GetRawText().Length;
                        break;
                }
                InputGuard.Limit(characters, MaxJsonCharacters);
            }
        }

        internal static ConstructionNode CheckedRead(Type type, JsonElement json)
        {
            InputBudget.Check(json);
            ConstructionSchemas.For(type).Check(json);
            XmlCharacters(json);
            return Read(type, json);
        }

        internal static ConstructionNode Read(Type type, JsonElement json)
        {
            if (type == typeof(Member)) return new Member(json);
            if (type == typeof(UdtSpec)) return new UdtSpec(json);
            if (type == typeof(GlobalDbSpec)) return new GlobalDbSpec(json);
            if (type == typeof(PlcTag)) return new PlcTag(json);
            if (type == typeof(PlcTagTableSpec)) return new PlcTagTableSpec(json);
            if (type == typeof(StructuredTextSpec)) return new StructuredTextSpec(json);
            if (type == typeof(Statement)) return Statement.Read(json);
            if (type == typeof(LineItem)) return new LineItem(json);
            if (type == typeof(CallParameter)) return new CallParameter(json);
            if (type == typeof(FlgNetCallSpec)) return new FlgNetCallSpec(json);
            if (type == typeof(FcBlockSpec)) return new FcBlockSpec(json);
            if (type == typeof(FbBlockSpec)) return new FbBlockSpec(json);
            if (type == typeof(LadNetwork)) return new LadNetwork(json);
            if (type == typeof(LadFcBlockSpec)) return new LadFcBlockSpec(json);
            if (typeof(Statement).IsAssignableFrom(type))
            {
                var statement = Statement.Read(json);
                Require(type.IsInstanceOfType(statement), "Statement op does not match its type.");
                return statement;
            }
            throw new JsonException("Select the concrete construction type using the outer kind.");
        }
    }

    // Attribute registration keeps this family within V4Json without editing its shared options.
    public sealed class ConstructionJsonConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeof(ConstructionNode).IsAssignableFrom(typeToConvert);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(NodeConverter<>).MakeGenericType(typeToConvert), true)!;

        private sealed class NodeConverter<T> : JsonConverter<T> where T : ConstructionNode
        {
            public override bool HandleNull => true;
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                try { return (T)ConstructionJson.CheckedRead(typeToConvert, document.RootElement); }
                catch (InputRejection rejection) { throw new ArgumentException("Construction input does not satisfy its contract.", rejection); }
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (value == null) throw new JsonException("Construction inputs must not be null.");
                ConstructionJson.Budget(value.Json, options);
                JsonSerializer.Serialize(writer, value.Json, options);
            }
        }
    }
}

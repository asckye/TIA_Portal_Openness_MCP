using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.Testing
{
    [JsonConverter(typeof(PlcTestCaseDocumentConverter))]
    public sealed class PlcTestCaseDocument
    {
        public const string IdPattern = "^(?!(?:[cC][oO][nN]|[pP][rR][nN]|[aA][uU][xX]|[nN][uU][lL]|[cC][oO][mM][1-9]|[lL][pP][tT][1-9])"
            + "(?:\\.|(?![\\s\\S])))[A-Za-z0-9][A-Za-z0-9._-]{0,63}(?![\\s\\S])";
        private readonly JsonObject document;
        public string Id => (string)document["id"]!;
        public string Title => (string)document["title"]!;
        public string Kind => (string)document["kind"]!;
        public string? Requirement => (string?)document["requirement"];
        public IReadOnlyList<string> Tags => document["tags"]?.AsArray().Select(t => (string)t!).ToArray() ?? Array.Empty<string>();
        public JsonObject Document => (JsonObject)document.DeepClone();
        public string CanonicalJson => Format(document);
        public string Sha256 => Hash(Encoding.UTF8.GetBytes(CanonicalJson));

        private PlcTestCaseDocument(JsonObject document) => this.document = document;

        internal static InputSchema Schema { get; } = InputSchema.Object(new Dictionary<string, InputSchema> {
            ["schemaVersion"] = InputSchema.Integer(1, 1),
            ["id"] = InputSchema.String(64, 1, pattern: IdPattern),
            ["title"] = InputSchema.String(minLength: 1),
            ["requirement"] = InputSchema.String(),
            ["tags"] = InputSchema.Array(InputSchema.String(64, 1), maximum: 32),
            ["kind"] = InputSchema.String(allowed: new[] { "plcsimScenario", "testSuite" }),
            ["scenario"] = DomainSchemas.For(typeof(PlcSimScenario)),
            ["testSuite"] = InputSchema.Object(new Dictionary<string, InputSchema> {
                ["category"] = InputSchema.String(allowed: new[] { "styleGuide", "application", "system" }),
                ["kind"] = InputSchema.String(allowed: new[] { "case", "testSet" }),
                ["names"] = InputSchema.Array(InputSchema.String(minLength: 1), 1, 50),
                ["runAll"] = InputSchema.Boolean()
            }, new[] { "category", "kind" })
        }, new[] { "schemaVersion", "id", "title", "kind" });

        public static PlcTestCaseDocument Parse(string json)
        {
            using var parsed = JsonDocument.Parse(json);
            var input = parsed.RootElement;
            Schema.Check(input);
            var obj = JsonNode.Parse(json)!.AsObject();
            ValidateId((string)obj["id"]!);
            string kind = (string)obj["kind"]!;
            if (kind == "plcsimScenario")
            {
                if (obj["scenario"] == null || obj.ContainsKey("testSuite"))
                    throw new ArgumentException("plcsimScenario requires scenario and no testSuite.");
                _ = DomainValidation.Read<PlcSimScenario>(obj["scenario"]!.ToJsonString());
            }
            else
            {
                if (obj["testSuite"] is not JsonObject suite || obj.ContainsKey("scenario"))
                    throw new ArgumentException("testSuite requires testSuite and no scenario.");
                bool all = (bool?)suite["runAll"] == true;
                if (all == suite.ContainsKey("names"))
                    throw new ArgumentException("Supply either 1..50 names or runAll=true, never both.");
                if ((string?)suite["kind"] == "testSet" && (string?)suite["category"] != "application")
                    throw new ArgumentException("testSet requires category application.");
            }
            var canonical = Ordered(obj, "schemaVersion", "id", "title", "requirement", "tags", "kind", "scenario", "testSuite");
            if (canonical["scenario"] is JsonObject scenario)
            {
                canonical["scenario"] = Ordered(scenario, "instance", "mode", "stopOnFailure", "steps");
                var steps = canonical["scenario"]!["steps"]!.AsArray();
                for (int i = 0; i < steps.Count; i++) steps[i] = Ordered(steps[i]!.AsObject(), "write", "waitMs", "assert", "tolerance", "note");
            }
            if (canonical["testSuite"] is JsonObject selection)
                canonical["testSuite"] = Ordered(selection, "category", "kind", "names", "runAll");
            return new PlcTestCaseDocument(canonical);
        }

        public static void ValidateId(string id)
        {
            if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, IdPattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new ArgumentException("id must match " + IdPattern + ".", nameof(id));
        }

        private static JsonObject Ordered(JsonObject source, params string[] keys)
        {
            var result = new JsonObject();
            foreach (string key in keys) if (source.ContainsKey(key)) result[key] = source[key]?.DeepClone();
            return result;
        }

        internal static string Format(JsonNode node) => node.ToJsonString(new JsonSerializerOptions {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).Replace("\r\n", "\n") + "\n";

        public static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal sealed class PlcTestCaseDocumentConverter : JsonConverter<PlcTestCaseDocument>
    {
        public override PlcTestCaseDocument Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return PlcTestCaseDocument.Parse(document.RootElement.GetRawText());
        }
        public override void Write(Utf8JsonWriter writer, PlcTestCaseDocument value, JsonSerializerOptions options)
            => value.Document.WriteTo(writer);
    }
}

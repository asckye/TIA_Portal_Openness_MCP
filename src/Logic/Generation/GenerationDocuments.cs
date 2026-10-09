using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.Generation
{
    public static class GenerationDocuments
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = CanonicalJson.MaximumDepth
        };

        public static T Load<T>(string json) where T : class
        {
            var value = CanonicalJson.Parse(json);
            GenerationSchemas.RequireValid(SchemaName(typeof(T)), value);
            ValidateSemantics(SchemaName(typeof(T)), value);
            return Deserialize<T>(value);
        }

        public static string Canonical<T>(T model) where T : class
        {
            var value = JsonSerializer.SerializeToElement(model, Options);
            GenerationSchemas.RequireValid(SchemaName(typeof(T)), value);
            ValidateSemantics(SchemaName(typeof(T)), value);
            return Encoding.UTF8.GetString(CanonicalJson.Encode(value));
        }

        public static string MachineHash(string json)
        {
            var value = CanonicalJson.Parse(json);
            GenerationSchemas.RequireValid("machine", value);
            ValidateSemantics("machine", value);
            return CanonicalJson.Hash(value);
        }

        // planHash cannot hash itself. planId is retained because artifacts may reference it.
        public static string PlanHash(string json)
        {
            var value = CanonicalJson.Parse(json);
            GenerationSchemas.RequireValid("plan", value);
            ValidateSemantics("plan", value);
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                    if (property.Name != "planHash") property.WriteTo(writer);
                writer.WriteEndObject();
            }
            return CanonicalJson.Hash(CanonicalJson.Parse(stream.ToArray()));
        }

        internal static T Deserialize<T>(JsonElement value)
        {
            try { return JsonSerializer.Deserialize<T>(CanonicalJson.Encode(value), Options)!; }
            catch (JsonException ex) { throw CanonicalJson.Failure(ex.Path ?? "", "model", ex.Message); }
        }

        internal static void ValidateSemantics(string name, JsonElement value) => GenerationModelValidation.RequireValid(name, value);

        private static string SchemaName(Type type)
        {
            if (type == typeof(StandardPackageManifest)) return "package";
            if (type == typeof(MachineDescription)) return "machine";
            if (type == typeof(GenerationPlan)) return "plan";
            if (type == typeof(StandardCheckResult)) return "check-result";
            if (type == typeof(NamingPart)) return "naming";
            if (type == typeof(StructurePart)) return "structure";
            if (type == typeof(HardwarePart)) return "hardware";
            if (type == typeof(LibraryPart)) return "library";
            if (type == typeof(ModesPart)) return "modes";
            if (type == typeof(AlarmsPart)) return "alarms";
            if (type == typeof(HmiPart)) return "hmi";
            if (type == typeof(ChecksPart)) return "checks";
            if (type == typeof(DeviceTypeRule)) return "rule";
            throw new ArgumentException("Unsupported generation model: " + type.FullName);
        }
    }
}

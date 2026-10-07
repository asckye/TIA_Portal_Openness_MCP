using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerJson
    {
        // Foundation uses PascalCase DTOs, numeric enums and explicit nulls. The
        // host reads JsonNode values, so preserve number spellings as well as values.
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            options.Converters.Add(new WireStringConverter());
            options.Converters.Add(new WireDoubleConverter());
            options.Converters.Add(new WireSingleConverter());
            options.Converters.Add(new WireDecimalConverter());
            return options;
        }

        internal static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

        internal static string Evidence(Exception cause) => Serialize(new {
            exceptionType = cause.GetType().Name,
            parameter = TiaOpenness.Shared.HostFailurePolicy.Parameter(cause),
            isArgument = cause is AdapterPreconditionException precondition ? precondition.IsArgument : (bool?)null,
            inputFile=cause.Data["inputFile"],inputSha256=cause.Data["inputSha256"],outputFile=cause.Data["outputFile"],
            stagedFile=cause.Data["stagedFile"],recoveryDirectory=cause.Data["recoveryDirectory"],recoveryFiles=cause.Data["recoveryFiles"],exportPhase=cause.Data["exportPhase"],
            stagedSha256=cause.Data["stagedSha256"],safetyCleanup=cause.Data["safetyCleanup"],attemptedPath=cause.Data["attemptedPath"],recoveryStatus=cause.Data["recoveryStatus"] });

        internal static Dictionary<string, JsonElement> ParseArguments(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                CheckDuplicates(document.RootElement);
                return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            }
        }

        private static void CheckDuplicates(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate worker argument: " + property.Name);
                    CheckDuplicates(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
        }

        internal static JsonElement Get(Dictionary<string, JsonElement> values, string name) =>
            values.TryGetValue(name, out var value) ? value : default;

        internal static bool IsBoolean(JsonElement value) => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;

        private static readonly Regex IsoDate = new Regex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?)(?:[Zz]|[+-][0-9]{2}:?(?:[0-9]{2})?)?\z", RegexOptions.CultureInvariant);
        private static readonly Regex MicrosoftDate = new Regex(@"^/Date\((-?[0-9]+)(?:[+-][0-9]{2}(?:[0-9]{2})?)?\)/\z", RegexOptions.CultureInvariant);

        // JObject's default date recognition made date arguments non-strings.
        // Keep that admission rule while replacing the DOM, including string arrays.
        internal static bool IsString(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString()!;
            if (text.Length >= 19 && text.Length <= 40 && text[10] == 'T')
            {
                var match = IsoDate.Match(text);
                if (match.Success)
                {
                    var clock = match.Groups[1].Value;
                    bool midnight = clock.Substring(11, 2) == "24";
                    if (midnight) clock = clock.Substring(0, 11) + "00" + clock.Substring(13);
                    if (DateTime.TryParseExact(clock, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                        (!midnight || date.TimeOfDay == TimeSpan.Zero)) return false;
                }
            }
            var microsoft = MicrosoftDate.Match(text);
            return !microsoft.Success || !long.TryParse(microsoft.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        }

        internal static object?[] Arguments(ParameterInfo[] parameters, Dictionary<string, JsonElement> values)
        {
            if (values.Keys.Any(name => !parameters.Any(p => p.Name == name))) throw new ArgumentException("Unknown worker argument.");
            return parameters.Select(p =>
            {
                if (!values.TryGetValue(p.Name!, out var value))
                {
                    if (p.HasDefaultValue) return p.DefaultValue;
                    throw new ArgumentException("Missing worker argument: " + p.Name);
                }
                bool integer = value.ValueKind == JsonValueKind.Number && value.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) < 0;
                if ((p.ParameterType == typeof(string) && !IsString(value)) ||
                    (p.ParameterType == typeof(bool) && !IsBoolean(value)) ||
                    (p.ParameterType == typeof(int) && !integer) ||
                    (p.ParameterType == typeof(string[]) && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength()>256 || value.EnumerateArray().Any(x => !IsString(x)))))
                    throw new ArgumentException("Incorrect worker argument type: " + p.Name);
                if (p.ParameterType == typeof(int)) return (object)int.Parse(value.GetRawText(), CultureInfo.InvariantCulture);
                return JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, Options);
            }).ToArray();
        }

        private static string Floating(string value) => value.IndexOfAny(new[] { '.', 'E', 'e' }) < 0 ? value + ".0" : value;

        private sealed class WireDoubleConverter : JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                else writer.WriteRawValue(Floating(value.ToString("R", CultureInfo.InvariantCulture)));
            }
        }

        private sealed class WireSingleConverter : JsonConverter<float>
        {
            public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetSingle();
            public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                else writer.WriteRawValue(Floating(value.ToString("R", CultureInfo.InvariantCulture)));
            }
        }

        private sealed class WireDecimalConverter : JsonConverter<decimal>
        {
            public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
            public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
                writer.WriteRawValue(Floating(value.ToString(CultureInfo.InvariantCulture)));
        }

        private sealed class WireStringConverter : JsonConverter<string>
        {
            public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString();

            public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            {
                // Failure evidence is appended verbatim to the host's MCP error text.
                // Retain the old escaping, even for controls and supplementary Unicode.
                var text = new StringBuilder("\"");
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': text.Append("\\\""); break;
                        case '\\': text.Append("\\\\"); break;
                        case '\b': text.Append("\\b"); break;
                        case '\f': text.Append("\\f"); break;
                        case '\n': text.Append("\\n"); break;
                        case '\r': text.Append("\\r"); break;
                        case '\t': text.Append("\\t"); break;
                        default:
                            if (c < ' ' || c == '\u0085' || c == '\u2028' || c == '\u2029') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else text.Append(c);
                            break;
                    }
                }
                writer.WriteRawValue(text.Append('"').ToString());
            }
        }
    }
}

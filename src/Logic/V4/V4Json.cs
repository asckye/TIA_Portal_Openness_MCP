using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    public static class V4Json
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true,
                NumberHandling = JsonNumberHandling.Strict,
                WriteIndented = false
            };
            options.Converters.Add(new ClosedEnumConverter<Outcome>(JsonNamingPolicy.KebabCaseLower));
            options.Converters.Add(new ClosedEnumConverter<Execution>(JsonNamingPolicy.KebabCaseLower));
            options.Converters.Add(new ClosedEnumConverter<Completeness>(JsonNamingPolicy.CamelCase));
            options.Converters.Add(new ClosedEnumConverter<BehaviorPolicy>(JsonNamingPolicy.KebabCaseLower));
            options.Converters.Add(new ClosedEnumConverter<PagingMode>(JsonNamingPolicy.CamelCase));
            options.Converters.Add(new ClosedEnumConverter<ErrorCode>(JsonNamingPolicy.SnakeCaseUpper));
            options.Converters.Add(new ClosedEnumConverter<WarningCode>(JsonNamingPolicy.SnakeCaseUpper));
            options.Converters.Add(new UtcOffsetConverter());
            options.Converters.Add(new UtcDateConverter());
            options.Converters.Add(new ErrorConverter());
            options.MakeReadOnly(populateMissingResolver: true);
            return options;
        }

        public static string Serialize<T>(T value) => Encoding.UTF8.GetString(SerializeUtf8(value));

        public static byte[] SerializeUtf8<T>(T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            using var document = JsonDocument.Parse(bytes);
            V4Validation.Json(document.RootElement);
            return bytes;
        }

        public static T Deserialize<T>(string json)
        {
            using var document = JsonDocument.Parse(json);
            V4Validation.Json(document.RootElement);
            return document.RootElement.Deserialize<T>(Options)
                ?? throw new JsonException("A V4 value must not be null.");
        }

        internal const int MaximumInputDepth = 64;

        internal sealed class InputDepthException : JsonException { }

        // One extra level lets adapters diagnose their own depth budget. For deeper
        // input, identify the reader boundary without relying on localized messages.
        internal static JsonElement ParseInput(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumInputDepth + 1 });
                return document.RootElement.Clone();
            }
            catch (JsonException) when (ReachedInputDepth(json))
            { throw new InputDepthException(); }
        }

        private static bool ReachedInputDepth(string json)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = MaximumInputDepth + 1 });
            try
            {
                while (reader.Read())
                    if ((reader.TokenType == JsonTokenType.StartArray || reader.TokenType == JsonTokenType.StartObject)
                        && reader.CurrentDepth == MaximumInputDepth) return true;
            }
            catch (JsonException) /* swallow(parse-fallback): syntax failed before the depth boundary; retain the original parse error */
            { return false; }
            return false;
        }

        public static JsonElement? Data<T>(T value)
        {
            if (value == null) return null;
            using var document = JsonDocument.Parse(SerializeUtf8(value));
            if (document.RootElement.ValueKind == JsonValueKind.Null) return null;
            V4Validation.Require(document.RootElement.ValueKind == JsonValueKind.Object, "data must be an object or null.");
            return document.RootElement.Clone();
        }

        public static T ReadData<T>(Envelope envelope) => envelope.Data.HasValue
            ? Deserialize<T>(envelope.Data.Value.GetRawText()) : throw new JsonException("The envelope has no data.");

        public static Envelope Batch(BatchData data, Error? error, Meta meta)
        {
            return Envelope.Create(data, error, meta);
        }

        public static BatchData ReadBatch(Envelope envelope)
        {
            var data = ReadData<BatchData>(envelope);
            V4Validation.Batch(envelope, data);
            return data;
        }

        public static PreviewData ReadPreview(Envelope envelope)
        {
            var data = ReadData<PreviewData>(envelope);
            V4Validation.Preview(envelope, data);
            return data;
        }

        private static string Timestamp(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

        private sealed class ClosedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            private readonly Dictionary<string, T> values = new Dictionary<string, T>(StringComparer.Ordinal);
            private readonly Dictionary<T, string> names = new Dictionary<T, string>();

            internal ClosedEnumConverter(JsonNamingPolicy policy)
            {
                foreach (T value in Enum.GetValues(typeof(T)))
                {
                    string name = policy.ConvertName(Enum.GetName(typeof(T), value)!);
                    values.Add(name, value);
                    names.Add(value, name);
                }
            }

            public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String || !values.TryGetValue(reader.GetString()!, out var value))
                    throw new JsonException("Unknown " + typeof(T).Name + " spelling.");
                return value;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (!names.TryGetValue(value, out string? name)) throw new JsonException("Unknown " + typeof(T).Name + " value.");
                writer.WriteStringValue(name);
            }
        }

        private sealed class UtcOffsetConverter : JsonConverter<DateTimeOffset>
        {
            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String || !DateTimeOffset.TryParseExact(reader.GetString(),
                    new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                    throw new JsonException("A V4 timestamp must be UTC RFC3339 ending in Z.");
                return value;
            }

            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
                writer.WriteStringValue(Timestamp(value));
        }

        private sealed class UtcDateConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
                new UtcOffsetConverter().Read(ref reader, typeof(DateTimeOffset), options).UtcDateTime;

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                V4Validation.Require(value.Kind != DateTimeKind.Unspecified, "A timestamp must identify its time zone.");
                writer.WriteStringValue(Timestamp(new DateTimeOffset(value)));
            }
        }

        private sealed class ErrorConverter : JsonConverter<Error>
        {
            private static readonly IReadOnlyDictionary<ErrorCode, Type> DetailTypes = new Dictionary<ErrorCode, Type>
            {
                [ErrorCode.InvalidArgument] = typeof(InvalidArgumentDetails),
                [ErrorCode.LimitExceeded] = typeof(LimitExceededDetails),
                [ErrorCode.UnsupportedCapability] = typeof(UnsupportedCapabilityDetails),
                [ErrorCode.ToolNotFound] = typeof(ToolNotFoundDetails),
                [ErrorCode.ProjectNotBound] = typeof(ProjectNotBoundDetails),
                [ErrorCode.NotFound] = typeof(NotFoundDetails),
                [ErrorCode.TargetAmbiguous] = typeof(TargetAmbiguousDetails),
                [ErrorCode.IdentityMismatch] = typeof(IdentityMismatchDetails),
                [ErrorCode.AlreadyExists] = typeof(AlreadyExistsDetails),
                [ErrorCode.ConfirmationRequired] = typeof(ConfirmationRequiredDetails),
                [ErrorCode.PlanStale] = typeof(PlanStaleDetails),
                [ErrorCode.PreconditionFailed] = typeof(PreconditionFailedDetails),
                [ErrorCode.OfflineRequired] = typeof(OfflineRequiredDetails),
                [ErrorCode.AuthenticationRequired] = typeof(AuthenticationRequiredDetails),
                [ErrorCode.AccessDenied] = typeof(AccessDeniedDetails),
                [ErrorCode.SessionResetRequired] = typeof(SessionResetRequiredDetails),
                [ErrorCode.ResourceUnavailable] = typeof(ResourceUnavailableDetails),
                [ErrorCode.IoFailed] = typeof(IoFailedDetails),
                [ErrorCode.NativeOperationFailed] = typeof(NativeOperationFailedDetails),
                [ErrorCode.Cancelled] = typeof(CancelledDetails),
                [ErrorCode.Timeout] = typeof(TimeoutDetails),
                [ErrorCode.NotExecuted] = typeof(NotExecutedDetails),
                [ErrorCode.PartialFailure] = typeof(PartialFailureDetails),
                [ErrorCode.OutcomeUnknown] = typeof(OutcomeUnknownDetails),
                [ErrorCode.InternalError] = typeof(InternalErrorDetails),
            };

            public override Error Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("error must be an object.");
                int count = 0;
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name != "code" && property.Name != "message" && property.Name != "details")
                        throw new JsonException("Unknown error property: " + property.Name);
                    count++;
                }
                if (count != 3 || !root.TryGetProperty("code", out var codeJson)
                    || !root.TryGetProperty("message", out var messageJson) || !root.TryGetProperty("details", out var detailsJson))
                    throw new JsonException("error requires code, message and details.");
                var code = codeJson.Deserialize<ErrorCode>(options);
                if (!DetailTypes.TryGetValue(code, out var detailType)) throw new JsonException("Unknown error code.");
                if (code == ErrorCode.ConfirmationRequired && (!detailsJson.TryGetProperty("reason", out _)
                    || !detailsJson.TryGetProperty("planHash", out _) || !detailsJson.TryGetProperty("requestId", out _)))
                    throw new JsonException("Confirmation details require reason, planHash and requestId.");
                var details = (ErrorDetails?)detailsJson.Deserialize(detailType, options)
                    ?? throw new JsonException("Error details must be an object.");
                return new Error(messageJson.GetString()!, details);
            }

            public override void Write(Utf8JsonWriter writer, Error value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("code");
                JsonSerializer.Serialize(writer, value.Code, options);
                writer.WriteString("message", value.Message);
                writer.WritePropertyName("details");
                JsonSerializer.Serialize(writer, value.Details, DetailTypes[value.Code], options);
                writer.WriteEndObject();
            }
        }
    }
}

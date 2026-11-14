using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace TiaMcp.Logic.V4
{
    public enum ErrorCode
    {
        InvalidArgument, LimitExceeded, UnsupportedCapability, ToolNotFound, ProjectNotBound, NotFound, TargetAmbiguous, IdentityMismatch, AlreadyExists,
            ConfirmationRequired, PlanStale, PreconditionFailed, OfflineRequired, AuthenticationRequired, AccessDenied, SessionResetRequired,
            ResourceUnavailable, IoFailed, NativeOperationFailed, CompileErrors, Cancelled, Timeout, NotExecuted, PartialFailure, OutcomeUnknown,
            OperationInProgress, InternalError
    }

    public sealed class Error
    {
        [JsonPropertyOrder(0)] public ErrorCode Code => Details.Code;
        [JsonPropertyOrder(1)] public string Message { get; }
        [JsonPropertyOrder(2)] public ErrorDetails Details { get; }
        [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeInnerError? Innererror { get; }

        public Error(string message, ErrorDetails details) : this(message, details, null) { }

        public Error(string message, ErrorDetails details, NativeInnerError? innererror)
        {
            V4Validation.Text(message, nameof(message));
            V4Validation.Require(details != null, "Error details are required.");
            Message = details is NativeOperationFailedDetails ? NativeFailureDiagnostics.Text(message) ?? "The native operation failed." : message;
            Details = details!;
            Innererror = innererror ?? (details as NativeOperationFailedDetails)?.Diagnostic;
        }
    }

    public sealed class NativeInnerError
    {
        public string? ExceptionType { get; }
        public int? HResult { get; }
        public string? DiagnosticId { get; }
        public NativeInnerError(string? exceptionType, int? hResult, string? diagnosticId)
        {
            ExceptionType = NativeFailureDiagnostics.Text(exceptionType)?.Split('.', '+').Last();
            HResult = hResult;
            // This opaque journal key must remain identical to meta.requestId.
            DiagnosticId = diagnosticId;
        }
    }

    // Invocation-local values are captured before dispatch and restored for nested calls.
    // This boundary is also linked into Studio Core, so it has no host dependencies.
    public static class NativeFailureDiagnostics
    {
        private static readonly AsyncLocal<Scope?> Current = new AsyncLocal<Scope?>();
        public static string? RequestId => Current.Value?.RequestId;
        public static IDisposable Begin(string requestId, JsonElement arguments) => new Scope(requestId, arguments);

        public static Error FromException(Error error, Exception exception, string? nativeMessage = null)
        {
            if (error.Details is not NativeOperationFailedDetails details) return error;
            var cause = exception.GetBaseException();
            bool workerWrapper = cause.GetType().Name == "WorkerOperationException";
            var evidence = details.Evidence.ToDictionary(p => p.Key, p => p.Value);
            evidence["exceptionType"] = JsonSerializer.SerializeToElement(workerWrapper ? null : cause.GetType().Name);
            evidence["hResult"] = JsonSerializer.SerializeToElement(workerWrapper ? (int?)null : cause.HResult);
            return new Error(error.Message, new NativeOperationFailedDetails(details.NativeCode, nativeMessage ?? cause.Message, evidence));
        }

        private sealed class Scope : IDisposable
        {
            private readonly Scope? previous = Current.Value;
            internal readonly string RequestId;
            internal readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
            internal Scope(string requestId, JsonElement arguments)
            {
                RequestId = requestId;
                if (previous != null) Secrets.UnionWith(previous.Secrets);
                Collect(arguments, Secrets, false);
                Current.Value = this;
            }
            public void Dispose() => Current.Value = previous;
        }

        private static bool Credential(string name)
        {
            name = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return name != "releasekey" && (name.Contains("password") || name.Contains("passwd") || name.Contains("secret")
                || name.Contains("token") || name.EndsWith("key", StringComparison.Ordinal) || name.EndsWith("keys", StringComparison.Ordinal)
                || name.Contains("credential") || name.Contains("authorization") || name == "pwd" || name == "sk");
        }

        private static void Collect(JsonElement value, HashSet<string> secrets, bool credential)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                bool named = value.EnumerateObject().Any(p => (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("key", StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.String
                    && Credential(p.Value.GetString()!));
                foreach (var pair in value.EnumerateObject())
                {
                    if (!credential && !named && PolicyScalar(pair.Name, pair.Value.GetRawText())) continue;
                    Collect(pair.Value, secrets,
                        credential || Credential(pair.Name) || named && pair.Name.Equals("value", StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Collect(item, secrets, credential);
            else if (credential && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                secrets.Add(value.GetRawText());
            else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
            {
                if (credential)
                {
                    if (Replaceable(text)) secrets.Add(text);
                }
                else if (text.TrimStart().StartsWith("{", StringComparison.Ordinal) || text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        Collect(document.RootElement, secrets, false);
                    }
                    catch (JsonException) /* swallow(parse-fallback): ordinary argument text has no embedded credential fields */ { }
                }
            }
        }

        private const string CredentialPair =
            @"(?i)\b(?<name>[\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*)\s*[=:]\s*(?<value>""[^""]*""|'[^']*'|[^\s&;,""'<>]+)";
        private static readonly JsonSerializerOptions DiagnosticJson = new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static bool Replaceable(string value) => value.Length > 1 && !(value.Length == 2 && char.IsSurrogatePair(value, 0));
        private static bool NumericOrBoolean(string value) => bool.TryParse(value, out _) || double.TryParse(value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
            && !double.IsNaN(number) && !double.IsInfinity(number);
        private static bool PolicyScalar(string name, string value) =>
            new[] { "Validity", "Count", "Length", "Attempts", "Enabled" }.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            && NumericOrBoolean(value);

        private static string ReplaceSecrets(string text, IEnumerable<string> secrets)
        {
            foreach (string secret in secrets.OrderByDescending(s => s.Length))
            {
                bool multiline = secret.IndexOfAny(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }) >= 0;
                text = text.Replace(secret, multiline ? "••••\n" : "••••");
            }
            return text;
        }

        private static Match ExceptionPrefix(string text)
        {
            var prefix = Regex.Match(text, @"^[A-Za-z_][\w.`+]*(?:Exception|Error):\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (prefix.Value.EndsWith("Error: ", StringComparison.Ordinal) && prefix.Value != "KeyNotFoundError: "
                && new[] { "password", "passwd", "pwd", "secret", "token", "key", "credential" }
                    .Any(marker => prefix.Value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)) prefix = Match.Empty;
            return prefix;
        }

        public static string? Text(string? text) => Text(text, 0, Array.Empty<string>());

        private static string? Text(string? text, int depth, IEnumerable<string> collected)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var secrets = new HashSet<string>(collected, StringComparer.Ordinal);
                if (Current.Value is { } scope) secrets.UnionWith(scope.Secrets);
                var jsonPrefix = ExceptionPrefix(text);
                if (jsonPrefix.Success)
                {
                    string remainder = text.Substring(jsonPrefix.Length);
                    if (remainder.TrimStart().StartsWith("{", StringComparison.Ordinal)
                        || remainder.TrimStart().StartsWith("[", StringComparison.Ordinal))
                        text = jsonPrefix.Value + (Text(remainder, depth + 1, secrets) ?? "••••");
                }
                if (text.TrimStart().StartsWith("{", StringComparison.Ordinal) || text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    if (depth >= 16) return "••••";
                    JsonNode? embedded;
                    try { embedded = JsonNode.Parse(text); }
                    catch (JsonException) /* swallow(privacy): malformed embedded JSON is hidden at the diagnostic boundary */ { return "••••"; }
                    Collect(JsonSerializer.SerializeToElement(embedded), secrets, false);
                    Scrub(embedded, depth + 1, secrets);
                    text = embedded?.ToJsonString(DiagnosticJson) ?? "null";
                }
                text = Regex.Replace(text, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", "••••",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                text = ReplaceSecrets(text, secrets);
                int end = text.IndexOfAny(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' });
                if (end >= 0) text = text.Substring(0, end);
                if (string.IsNullOrWhiteSpace(text)) return null;
                int inner = text.IndexOf(" ---> ", StringComparison.Ordinal);
                if (inner >= 0) text = text.Substring(0, inner);
                var prefix = ExceptionPrefix(text);
                string body = text.Substring(prefix.Length);
                foreach (Match match in Regex.Matches(body, CredentialPair, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    string value = match.Groups["value"].Value;
                    bool quoted = value.StartsWith("\"", StringComparison.Ordinal) || value.StartsWith("'", StringComparison.Ordinal);
                    if (quoted) value = value.Substring(1, value.Length - 2);
                    if (Replaceable(value) && (quoted || !PolicyScalar(match.Groups["name"].Value, value))) secrets.Add(value);
                }
                foreach (Match match in Regex.Matches(body, @"(?i)\b(?:Bearer|Basic)\s+([^\s""'<>\\,;]+)",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    if (Replaceable(match.Groups[1].Value)) secrets.Add(match.Groups[1].Value);
                body = ReplaceSecrets(body, secrets);
                body = Regex.Replace(body, CredentialPair,
                    match => {
                        string value = match.Groups["value"].Value;
                        bool quoted = value.StartsWith("\"", StringComparison.Ordinal) || value.StartsWith("'", StringComparison.Ordinal);
                        if (!quoted && PolicyScalar(match.Groups["name"].Value, value)) return match.Value;
                        return match.Value.Substring(0, match.Groups["value"].Index - match.Index) + "••••";
                    }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                body = Regex.Replace(body, @"(?i)\b(Bearer|Basic)\s+[^\s""'<>\\,;]+", "$1 ••••",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                body = Regex.Replace(body, @"(?i)(https?://)[^/\s@]+@", "$1••••@", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                body = Regex.Replace(body, @"(?i)(?<![\w])(?:[a-z]:\\|\\\\)[^\r\n""<>|;]*", "<path>",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                text = prefix.Value + body;
                if (text.Length <= 4096) return text;
                int count = char.IsHighSurrogate(text[4079]) ? 4079 : 4080;
                return text.Substring(0, count) + "… [truncated]";
            }
            catch (Exception error) when (error is RegexMatchTimeoutException || error is ArgumentException)
            {
                /* swallow(privacy): retain a value-free native reason when parsing fails or text sanitization times out */
                return "Native operation failed.";
            }
        }

        internal static Error? Complete(Error? error, JsonElement? data, string requestId)
        {
            if (error?.Details is not NativeOperationFailedDetails details) return error;
            JsonElement source = data ?? default;
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("evidence", out var evidence)
                && evidence.ValueKind == JsonValueKind.Object) source = evidence;
            string? Field(string name) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
            var retained = details.Evidence.ToDictionary(p => p.Key, p => p.Value);
            foreach (string name in new[] { "exceptionType", "nativeExceptionType", "hResult", "nativeMessage", "workerMessage" })
                if (!retained.ContainsKey(name) && source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value))
                    retained[name] = value.Clone();
            var completed = new NativeOperationFailedDetails(details.NativeCode,
                details.NativeMessage ?? Field("nativeMessage") ?? Field("workerMessage") ?? Field("readError") ?? error.Message, retained);
            return new Error(error.Message, completed, new NativeInnerError(error.Innererror?.ExceptionType ?? completed.Diagnostic?.ExceptionType,
                error.Innererror?.HResult ?? completed.Diagnostic?.HResult, requestId));
        }

        internal static JsonElement Evidence(JsonElement value)
        {
            var node = JsonNode.Parse(value.GetRawText());
            var secrets = new HashSet<string>(StringComparer.Ordinal);
            Collect(value, secrets, false);
            Scrub(node, 0, secrets);
            return JsonSerializer.SerializeToElement(node);
        }

        private static void Scrub(JsonNode? node, int depth, IEnumerable<string> secrets)
        {
            if (node is JsonObject obj)
            {
                bool named = obj.Any(p => (p.Key.Equals("name", StringComparison.OrdinalIgnoreCase)
                    || p.Key.Equals("key", StringComparison.OrdinalIgnoreCase)) && p.Value is JsonValue name
                    && name.TryGetValue<string>(out var text) && Credential(text));
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0) obj.Remove(pair.Key);
                    else if (Credential(pair.Key) || named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase))
                        obj[pair.Key] = "••••";
                    else if (pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        obj[pair.Key] = Text(text, depth, secrets);
                    else if (depth >= 16) obj[pair.Key] = "••••";
                    else Scrub(pair.Value, depth + 1, secrets);
                }
            }
            else if (node is JsonArray array)
                for (int index = 0; index < array.Count; index++)
                    if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[index] = Text(text, depth, secrets);
                    else if (depth >= 16) array[index] = "••••";
                    else Scrub(array[index], depth + 1, secrets);
        }
    }

    // Only the code-specific types in this assembly can extend this union.
    public abstract class ErrorDetails
    {
        internal ErrorDetails() { }
        internal abstract ErrorCode Code { get; }
    }

    public sealed class InvalidArgumentDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.InvalidArgument;
        [JsonPropertyOrder(0)] public string? Parameter { get; }
        [JsonPropertyOrder(1)] public IReadOnlyList<string> AllowedValues { get; }
        [JsonPropertyOrder(2)] public IReadOnlyList<string> Parameters { get; }
        [JsonPropertyOrder(3)] public string? Expected { get; }
        internal bool WholeArgumentValidation { get; set; }
        public InvalidArgumentDetails(string? parameter, IReadOnlyList<string> allowedValues,
            IReadOnlyList<string>? parameters = null, string? expected = null)
        {
            string target = parameter ?? "";
            var fields = parameters ?? (target.StartsWith("/", System.StringComparison.Ordinal) ? new[] { target }
                : target.Split(new[] { ',', '/' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(System.StringComparer.Ordinal).ToArray());
            Parameters = V4Validation.List((fields.Count > 0 ? fields : new[] { "" })
                .Select(Pointer).Distinct(System.StringComparer.Ordinal).ToArray());
            Parameter = Parameters[0];
            AllowedValues = V4Validation.List(allowedValues);
            Expected = expected ?? (allowedValues.Count > 0 ? "one of the allowed values" : null);
            V4Validation.Details(this);
        }

        internal static string Segment(string name) => name.Replace("~", "~0").Replace("/", "~1");
        internal static string Pointer(string name) => name.Length == 0 || name.StartsWith("/", System.StringComparison.Ordinal)
            ? name : "/" + Segment(name);

        internal static Error Prefix(Error error, string prefix)
        {
            if (error.Details is not InvalidArgumentDetails details) return error;
            var pointers = details.Parameters.Select(p => prefix + p).ToArray();
            return new Error(error.Message, new InvalidArgumentDetails(pointers[0], details.AllowedValues, pointers, details.Expected));
        }
    }

    public sealed class LimitExceededDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.LimitExceeded;
        [JsonPropertyOrder(0)] public string? Parameter { get; }
        [JsonPropertyOrder(1)] public long? Limit { get; }
        [JsonPropertyOrder(2)] public long? Actual { get; }
        public LimitExceededDetails(string? parameter, long? limit, long? actual)
        {
            Parameter = string.IsNullOrWhiteSpace(parameter) ? "arguments" : parameter;
            Limit = limit;
            Actual = actual;
            V4Validation.Details(this);
        }
    }

    public sealed class UnsupportedCapabilityDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.UnsupportedCapability;
        [JsonPropertyOrder(0)] public string? ReleaseKey { get; }
        [JsonPropertyOrder(1)] public string? Capability { get; }
        [JsonPropertyOrder(2)] public string? Action { get; }
        public UnsupportedCapabilityDetails(string? releaseKey, string? capability, string? action)
        {
            ReleaseKey = releaseKey;
            Capability = capability;
            Action = action;
            V4Validation.Details(this);
        }
    }

    public sealed class ToolNotFoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ToolNotFound;
        [JsonPropertyOrder(0)] public string? Tool { get; }
        public ToolNotFoundDetails(string? tool)
        {
            Tool = tool;
            V4Validation.Details(this);
        }
    }

    public sealed class ProjectNotBoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ProjectNotBound;

        public ProjectNotBoundDetails()
        {

            V4Validation.Details(this);
        }
    }

    public sealed class NotFoundDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NotFound;
        [JsonPropertyOrder(0)] public string? Target { get; }
        public NotFoundDetails(string? target)
        {
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class TargetAmbiguousDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.TargetAmbiguous;
        [JsonPropertyOrder(0)] public string? Target { get; }
        [JsonPropertyOrder(1)] public IReadOnlyList<string> Candidates { get; }
        public TargetAmbiguousDetails(string? target, IReadOnlyList<string> candidates)
        {
            Target = target;
            Candidates = V4Validation.List(candidates);
            V4Validation.Details(this);
        }
    }

    public sealed class IdentityMismatchDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.IdentityMismatch;
        [JsonPropertyOrder(0)] public string? Target { get; }
        [JsonPropertyOrder(1)] public string? Expected { get; }
        [JsonPropertyOrder(2)] public string? Actual { get; }
        public IdentityMismatchDetails(string? target, string? expected, string? actual)
        {
            Target = target;
            Expected = expected;
            Actual = actual;
            V4Validation.Details(this);
        }
    }

    public sealed class AlreadyExistsDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AlreadyExists;
        [JsonPropertyOrder(0)] public string? Target { get; }
        public AlreadyExistsDetails(string? target)
        {
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class ConfirmationRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ConfirmationRequired;
        [JsonPropertyOrder(0)] public string Reason { get; }
        [JsonPropertyOrder(1)] public string? PlanHash { get; }
        [JsonPropertyOrder(2)] public string? RequestId { get; }
        public ConfirmationRequiredDetails(string? planHash)
            : this("plan-confirmation", planHash, null) { }
        [JsonConstructor]
        public ConfirmationRequiredDetails(string reason, string? planHash, string? requestId)
        {
            Reason = DiagnosticVocabulary.Key(reason);
            PlanHash = planHash;
            RequestId = requestId;
            V4Validation.Details(this);
        }
    }

    public sealed class PlanStaleDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PlanStale;
        [JsonPropertyOrder(0)] public string? PlanHash { get; }
        [JsonPropertyOrder(1)] public string? Reason { get; }
        [JsonPropertyOrder(2)] public string? PreviewToken { get; }
        public PlanStaleDetails(string? planHash, string? reason, string? previewToken = null)
        {
            PlanHash = planHash;
            Reason = DiagnosticVocabulary.Key(reason);
            PreviewToken = previewToken;
            V4Validation.Details(this);
        }
    }

    public sealed class PreconditionFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PreconditionFailed;
        [JsonPropertyOrder(0)] public string? Condition { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        [JsonPropertyOrder(2)] public string? Parameter { get; }
        public PreconditionFailedDetails(string? condition, string? target, string? parameter = null)
        {
            Condition = DiagnosticVocabulary.Key(condition);
            Target = target;
            Parameter = parameter;
            V4Validation.Details(this);
        }
    }

    public sealed class OfflineRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.OfflineRequired;
        [JsonPropertyOrder(0)] public IReadOnlyList<string> Targets { get; }
        public OfflineRequiredDetails(IReadOnlyList<string> targets)
        {
            Targets = V4Validation.List(targets);
            V4Validation.Details(this);
        }
    }

    public sealed class AuthenticationRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AuthenticationRequired;
        [JsonPropertyOrder(0)] public string? Capability { get; }
        public AuthenticationRequiredDetails(string? capability)
        {
            Capability = capability;
            V4Validation.Details(this);
        }
    }

    public sealed class AccessDeniedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.AccessDenied;
        [JsonPropertyOrder(0)] public string? Operation { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        public AccessDeniedDetails(string? operation, string? target)
        {
            Operation = DiagnosticVocabulary.Key(operation);
            Target = target;
            V4Validation.Details(this);
        }
    }

    public sealed class SessionResetRequiredDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.SessionResetRequired;
        [JsonPropertyOrder(0)] public string? Reason { get; }
        public SessionResetRequiredDetails(string? reason)
        {
            Reason = DiagnosticVocabulary.Key(reason);
            V4Validation.Details(this);
        }
    }

    public sealed class ResourceUnavailableDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.ResourceUnavailable;
        [JsonPropertyOrder(0)] public string? Resource { get; }
        public ResourceUnavailableDetails(string? resource)
        {
            Resource = DiagnosticVocabulary.Key(resource);
            V4Validation.Details(this);
        }
    }

    public sealed class IoFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.IoFailed;
        [JsonPropertyOrder(0)] public string? Operation { get; }
        [JsonPropertyOrder(1)] public string? Path { get; }
        public IoFailedDetails(string? operation, string? path)
        {
            Operation = DiagnosticVocabulary.Key(operation);
            Path = path;
            V4Validation.Details(this);
        }
    }

    public sealed class NativeOperationFailedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NativeOperationFailed;
        [JsonPropertyOrder(0)] public string? NativeCode { get; }
        [JsonPropertyOrder(1)] public string? NativeMessage { get; }
        [JsonPropertyOrder(2)] public IReadOnlyDictionary<string, JsonElement> Evidence { get; }
        internal NativeInnerError? Diagnostic { get; }
        public NativeOperationFailedDetails(string? nativeCode, string? nativeMessage, IReadOnlyDictionary<string, JsonElement> evidence)
        {
            NativeCode = nativeCode;
            string? Text(string key) => evidence.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            NativeMessage = NativeFailureDiagnostics.Text(nativeMessage) ?? NativeFailureDiagnostics.Text(Text("nativeMessage"))
                ?? NativeFailureDiagnostics.Text(Text("workerMessage")) ?? NativeFailureDiagnostics.Text(Text("readError"));
            Evidence = V4Validation.Object(NativeFailureDiagnostics.Evidence(JsonSerializer.SerializeToElement(evidence))
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
            int? hResult = evidence.TryGetValue("hResult", out var number) && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt32(out var code) ? code : null;
            string? type = Text("exceptionType") ?? Text("nativeExceptionType");
            var diagnostic = new NativeInnerError(type, hResult, NativeFailureDiagnostics.RequestId);
            if (diagnostic.ExceptionType != null || hResult != null || diagnostic.DiagnosticId != null) Diagnostic = diagnostic;
            V4Validation.Details(this);
        }
    }

    public sealed class CompileErrorMessage
    {
        [JsonPropertyOrder(0)] public string Path { get; }
        [JsonPropertyOrder(1)] public string Description { get; }
        public CompileErrorMessage(string path, string description)
        {
            V4Validation.Text(path, nameof(path));
            V4Validation.Text(description, nameof(description));
            Path = path; Description = description;
        }
    }
    public sealed class CompileErrorsDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.CompileErrors;
        [JsonPropertyOrder(0)] public int ErrorCount { get; }
        [JsonPropertyOrder(1)] public int WarningCount { get; }
        [JsonPropertyOrder(2)] public IReadOnlyList<CompileErrorMessage> Errors { get; }
        public CompileErrorsDetails(int errorCount, int warningCount, IReadOnlyList<CompileErrorMessage> errors)
        {
            V4Validation.Require(errorCount >= 0 && warningCount >= 0, "Compiler counts must be nonnegative.");
            ErrorCount = errorCount; WarningCount = warningCount; Errors = V4Validation.List(errors);
            V4Validation.Details(this);
        }
    }

    public sealed class CancelledDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.Cancelled;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        public CancelledDetails(string? stage)
        {
            Stage = DiagnosticVocabulary.Key(stage);
            V4Validation.Details(this);
        }
    }

    public sealed class TimeoutDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.Timeout;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        public TimeoutDetails(string? stage)
        {
            Stage = DiagnosticVocabulary.Key(stage);
            V4Validation.Details(this);
        }
    }

    public sealed class NotExecutedDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.NotExecuted;
        [JsonPropertyOrder(0)] public int? CauseIndex { get; }
        public NotExecutedDetails(int? causeIndex)
        {
            CauseIndex = causeIndex;
            V4Validation.Details(this);
        }
    }

    public sealed class PartialFailureDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.PartialFailure;
        [JsonPropertyOrder(0)] public int Succeeded { get; }
        [JsonPropertyOrder(1)] public int Failed { get; }
        [JsonPropertyOrder(2)] public int NotExecuted { get; }
        public PartialFailureDetails(int succeeded, int failed, int notExecuted)
        {
            Succeeded = succeeded;
            Failed = failed;
            NotExecuted = notExecuted;
            V4Validation.Details(this);
        }
    }

    public sealed class OutcomeUnknownDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.OutcomeUnknown;
        [JsonPropertyOrder(0)] public string? Stage { get; }
        [JsonPropertyOrder(1)] public IReadOnlyDictionary<string, JsonElement> Evidence { get; }
        [JsonPropertyOrder(2)] public string? Reason { get; }
        public OutcomeUnknownDetails(string? stage, IReadOnlyDictionary<string, JsonElement> evidence, string? reason = null)
        {
            Stage = DiagnosticVocabulary.Key(stage);
            Evidence = V4Validation.Object(evidence);
            if (reason != null && reason != "awaiting-openness-confirmation") throw new System.ArgumentException("Unknown session timeout reason.");
            Reason = DiagnosticVocabulary.Key(reason);
            V4Validation.Details(this);
        }
    }

    public sealed class OperationInProgressDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.OperationInProgress;
        [JsonPropertyOrder(0)] public string RequestId { get; }
        public OperationInProgressDetails(string requestId)
        {
            V4Validation.Text(requestId, nameof(requestId));
            RequestId = requestId;
        }
    }

    public sealed class InternalErrorDetails : ErrorDetails
    {
        internal override ErrorCode Code => ErrorCode.InternalError;
        [JsonPropertyOrder(0)] public string? DiagnosticId { get; }
        public InternalErrorDetails(string? diagnosticId)
        {
            DiagnosticId = diagnosticId;
            V4Validation.Details(this);
        }
    }
}

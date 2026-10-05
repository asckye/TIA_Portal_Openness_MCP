using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Logic.V4.Inputs
{
    public enum InputPresence { Missing, Null, Value }

    public sealed class InputResult<T>
    {
        public InputPresence Presence { get; }
        public bool IsValid => Error == null;
        public T? Value { get; }
        public Error? Error { get; }
        internal InputResult(InputPresence presence, T? value, Error? error)
        { Presence = presence; Value = value; Error = error; }
    }

    // Limits are measured before business normalization (trimming/deduplication must not
    // make an over-budget input admissible). Characters bound raw text before parsing
    // as well as compact V4 JSON for parsed and already-typed callers.
    public sealed class InputBudget
    {
        public int? Characters { get; }
        public int Depth { get; }
        public int? StringLength { get; }
        public int? Items { get; }
        public int? Utf8Bytes { get; }
        public InputBudget(int? characters = null, int depth = V4Json.MaximumInputDepth, int? stringLength = null, int? items = null, int? utf8Bytes = null)
        {
            if (characters < 0 || depth < 1 || depth > V4Json.MaximumInputDepth || stringLength < 0 || items < 0 || utf8Bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(characters));
            Characters = characters; Depth = depth; StringLength = stringLength; Items = items; Utf8Bytes = utf8Bytes;
        }

        internal void CheckText(string text)
        {
            InputGuard.Limit(text.Length, Characters);
            if (Utf8Bytes.HasValue) InputGuard.Limit(Encoding.UTF8.GetByteCount(text), Utf8Bytes);
        }

        internal void Check(JsonElement value)
        {
            if (Utf8Bytes.HasValue) InputGuard.Limit(Encoding.UTF8.GetByteCount(value.GetRawText()), Utf8Bytes);
            var pending = new Stack<(JsonElement Value, int Depth)>();
            pending.Push((value, 0));
            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                var node = entry.Value;
                if (node.ValueKind == JsonValueKind.Object || node.ValueKind == JsonValueKind.Array)
                {
                    InputGuard.Limit(entry.Depth + 1, Depth);
                    if (node.ValueKind == JsonValueKind.Array)
                    {
                        InputGuard.Limit(node.GetArrayLength(), Items);
                        foreach (var item in node.EnumerateArray()) pending.Push((item, entry.Depth + 1));
                    }
                    else
                    {
                        var properties = node.EnumerateObject().ToArray();
                        InputGuard.Limit(properties.Length, Items);
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var property in properties)
                        {
                            InputGuard.Require(names.Add(property.Name));
                            InputGuard.Limit(property.Name.Length, StringLength);
                            pending.Push((property.Value, entry.Depth + 1));
                        }
                    }
                }
                else if (node.ValueKind == JsonValueKind.String) InputGuard.Limit(node.GetString()!.Length, StringLength);
            }
            CheckText(V4Json.Serialize(value));
        }
    }

    // One adapter for parse and already-typed entry points. A host embeds Schema in the
    // parameter's inputSchema, then must still call Read/Validate before native work.
    public sealed class InputContract<T>
    {
        private readonly InputSchema schema;
        private readonly Func<T, T> normalize;
        private readonly Action<JsonElement>? validateInput;
        public InputBudget Budget { get; }
        public JsonElement Schema => schema.Json;

        public InputContract(InputSchema schema, InputBudget budget, Func<T, T>? normalize = null,
            Action<JsonElement>? validateInput = null)
        {
            this.schema = schema ?? throw new ArgumentNullException(nameof(schema));
            Budget = budget ?? throw new ArgumentNullException(nameof(budget));
            this.normalize = normalize ?? (value => value);
            this.validateInput = validateInput;
        }

        public InputResult<T> Read(string? json, string parameter, bool optional = false)
        {
            if (json == null) return Read(default(JsonElement), parameter, optional);
            try
            {
                Budget.CheckText(json);
                return Read(V4Json.ParseInput(json), parameter, optional);
            }
            catch (InputRejection rejection) { return Failure(InputPresence.Value, rejection.ToError(parameter)); }
            catch (V4Json.InputDepthException) /* swallow(privacy): map the bounded reader failure to value-free limit details */
            { return Failure(InputPresence.Value, new InputRejection(limit: Budget.Depth, actual: V4Json.MaximumInputDepth + 1).ToError(parameter)); }
            catch (JsonException) /* swallow(privacy): parser diagnostics may contain credentials; return a value-free V4 error */
            { return Failure(InputPresence.Value, InputGuard.Invalid(parameter)); }
        }

        public InputResult<T> Read(JsonElement input, string parameter, bool optional = false)
        {
            var presence = input.ValueKind == JsonValueKind.Undefined ? InputPresence.Missing
                : input.ValueKind == JsonValueKind.Null ? InputPresence.Null : InputPresence.Value;
            if (presence == InputPresence.Missing)
                return optional ? new InputResult<T>(presence, default, null) : Failure(presence, InputGuard.Invalid(parameter));
            try
            {
                Budget.Check(input);
                schema.Check(input);
                // Family aggregate budgets must run before constructors can reject
                // them as ordinary argument errors. The shape is already checked.
                validateInput?.Invoke(input);
                var value = normalize(V4Json.Deserialize<T>(V4Json.Serialize(input)));
                return new InputResult<T>(presence, value, null);
            }
            catch (InputRejection rejection) { return Failure(presence, rejection.ToError(parameter)); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException || ex is FormatException || ex is System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return Failure(presence, InputGuard.Invalid(parameter)); }
        }

        public InputResult<T> Validate(T value, string parameter)
        {
            if (value is NativeValue native) return Read(native.Json, parameter);
            if (value is Scalar scalar) return Read(scalar.Json, parameter);
            if (value is ToolArguments arguments) return Read(arguments.Json, parameter);
            try { return Read(V4Json.Serialize(value), parameter); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
            { return Failure(InputPresence.Value, InputGuard.Invalid(parameter)); }
        }

        private static InputResult<T> Failure(InputPresence presence, Error error) => new InputResult<T>(presence, default, error);
    }

    internal sealed class InputRejection : Exception
    {
        private readonly int? limit;
        private readonly int? actual;
        private readonly string[] allowed;
        internal bool IsLimit => limit.HasValue;
        internal InputRejection(string[]? allowed = null, int? limit = null, int? actual = null)
        { this.allowed = allowed ?? Array.Empty<string>(); this.limit = limit; this.actual = actual; }
        internal Error ToError(string parameter) => limit.HasValue
            ? new Error("Input exceeds its declared budget.", new LimitExceededDetails(parameter, limit, actual))
            : InputGuard.Invalid(parameter, allowed);
    }

    internal static class InputGuard
    {
        internal static Error Invalid(string parameter, string[]? allowed = null) =>
            new Error("Input does not satisfy the declared contract.", new InvalidArgumentDetails(parameter, allowed ?? Array.Empty<string>()));
        internal static void Require(bool condition, string[]? allowed = null)
        { if (!condition) throw new InputRejection(allowed); }
        internal static void Limit(int actual, int? limit)
        { if (limit.HasValue && actual > limit.Value) throw new InputRejection(limit: limit, actual: actual); }
        internal static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c == '_');
        internal static void Result(Error? error)
        {
            if (error == null) return;
            if (error.Details is LimitExceededDetails limit)
                throw new InputRejection(limit: (int?)limit.Limit, actual: (int?)limit.Actual);
            throw new InputRejection(error.Details is InvalidArgumentDetails invalid ? invalid.AllowedValues.ToArray() : null);
        }
        internal static void Closed(JsonElement value, string[] required, params string[] optional)
        {
            Require(value.ValueKind == JsonValueKind.Object);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                Require(names.Add(property.Name) && (required.Contains(property.Name, StringComparer.Ordinal) || optional.Contains(property.Name, StringComparer.Ordinal)));
            Require(required.All(names.Contains));
        }
    }
}

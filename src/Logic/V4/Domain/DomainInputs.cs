using System;
using System.Text.Json;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainValidation
    {
        // The contract is the integration boundary: parsed and typed callers receive
        // the same value-free V4 errors and retain missing/null/value presence.
        public static InputContract<T> Contract<T>(Action<T>? context = null) => new InputContract<T>(
            DomainSchemas.For(typeof(T)), Budget(typeof(T)), value =>
            {
                ValidateParameter(value);
                context?.Invoke(value);
                return value;
            }, validateInput: input => ValidateProtocol(typeof(T), input));

        internal static InputBudget Budget(Type type) => type == typeof(Artifact[]) ? new InputBudget(1024 * 1024)
            : type == typeof(MotionTarget) ? new InputBudget(16384)
            : type == typeof(GraphicSelectionPage[]) ? new InputBudget(4 * 1024 * 1024)
            : type == typeof(OpenPipeRequest) ? OpenPipeLimits.Budget()
            : typeof(OpenPipeParams).IsAssignableFrom(type) ? OpenPipeLimits.Budget(OpenPipeLimits.MaxDepth - 1)
            : type == typeof(NativeValue) ? OpenPipeLimits.Budget(OpenPipeLimits.MaxDepth - 2)
            : new InputBudget();

        private static void ValidateProtocol(Type type, JsonElement input)
        {
            // The schema admits streaming shapes so PrepareRawRequest can retain
            // its protocol refusal before DTO construction examines Params.
            if (type == typeof(OpenPipeRequest) && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                ValidateValue(type, input);
        }

        internal static void Check(Type type, JsonElement input)
        {
            Budget(type).Check(input);
            DomainSchemas.For(type).Check(input);
            ValidateValue(type, input);
        }

        // Retain the scaffold's throwing convenience API for existing callers/tests.
        // Integration uses Contract<T>.Read/Validate. The exception carries that exact
        // V4 error; only its legacy CLR category depends on the rejecting layer.
        public static T Read<T>(string json)
        {
            var result = Contract<T>().Read(json, typeof(T).Name);
            if (result.Error == null) return result.Value!;
            string message = V4Json.Serialize(result.Error);
            if (result.Error.Code == ErrorCode.InvalidArgument)
            {
                try { Budget(typeof(T)).Check(V4Json.ParseInput(json)); }
                catch (InputRejection rejection) when (!rejection.IsLimit) { throw new ArgumentException(message); }
                catch (ArgumentException) { throw new ArgumentException(message); }
                catch (JsonException) /* swallow(parse-fallback): malformed text retains the legacy JSON exception category */ { }
            }
            throw new JsonException(message);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcp.Logic.V4.Inputs
{
    public sealed class NativeValuePolicy
    {
        public InputSchema Schema { get; }
        public InputBudget Budget { get; }
        internal Func<NativeValue, bool>? Admission { get; }
        public NativeValuePolicy(InputSchema schema, InputBudget budget, Func<NativeValue, bool>? admission = null)
        { Schema = schema ?? throw new ArgumentNullException(nameof(schema)); Budget = budget ?? throw new ArgumentNullException(nameof(budget)); Admission = admission; }
    }

    public static class NativeValueValidator
    {
        public static InputContract<NativeValue> Create(NativeValuePolicy policy) => new InputContract<NativeValue>(policy.Schema, policy.Budget, value =>
        { InputGuard.Require(policy.Admission == null || policy.Admission(value)); return value; });
        public static NativeValuePolicy Scalar(bool nullable = false) => new NativeValuePolicy(InputSchema.Scalar(nullable), new InputBudget());

        // StartdriveLogic.cs:131-158. The only object branch is bicoSource; arrays
        // and null are not writes. No CLR type is accepted from the request.
        public static NativeValuePolicy DriveParameter() => new NativeValuePolicy(InputSchema.Union(InputSchema.Scalar(false),
            InputSchema.Object(new Dictionary<string, InputSchema> { ["bicoSource"] = InputSchema.String(64, 1) }, new[] { "bicoSource" })),
            new InputBudget(), value => value.Json.ValueKind != JsonValueKind.Object || ParameterName(value.Json.GetProperty("bicoSource").GetString()!));

        private static bool ParameterName(string name)
        {
            if (name.Length < 2 || name.Length > 64 || "pPrR".IndexOf(name[0]) < 0) return false;
            int i = 1;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i == 1) return false;
            if (i < name.Length && name[i] == '[')
            {
                int end = name.IndexOf(']', i);
                if (end <= i + 1 || !name.Substring(i + 1, end - i - 1).All(char.IsDigit)) return false;
                i = end + 1;
            }
            if (i < name.Length && name[i] == '.')
            { if (i == name.Length - 1 || !name.Substring(i + 1).All(char.IsDigit)) return false; i = name.Length; }
            return i == name.Length;
        }

        // StartdriveLogic.cs:208-280: the caller selects the action's exact field
        // schema, including required fields, enums and 0..65535 data-set ranges.
        // This adapter does not expose a permissive scalar|array|map default.
        public static NativeValuePolicy ActionObject(IReadOnlyDictionary<string, InputSchema> fields, string[] required, InputBudget budget) =>
            new NativeValuePolicy(InputSchema.Object(fields, required), budget);

        // PlcSimAdvancedLogic.cs:166-213 is already pure business validation in Logic.
        // Reuse its invariant-culture conversions, checked integer casts and hex rules.
        public static NativeValuePolicy Simulation(string primitiveType)
        {
            if (!PlcSimAdvancedLogic.PrimitiveTypes.Contains(primitiveType, StringComparer.Ordinal)) throw new ArgumentException("Unsupported primitive type.");
            return new NativeValuePolicy(InputSchema.Scalar(false), new InputBudget(), value =>
            {
                var converted = PlcSimAdvancedLogic.ConvertValue(JsonNode.Parse(V4Json.Serialize(value)), primitiveType);
                return !(converted is float f && (float.IsInfinity(f) || float.IsNaN(f)))
                    && !(converted is double d && (double.IsInfinity(d) || double.IsNaN(d)));
            });
        }
    }

    public static class WriteValueValidator
    {
        // RuntimeChannelsLogic.cs:111-160: trim names, preserve order, 1..500,
        // ordinal unique names, non-null scalars. PLCSIM uses the same V4 shape.
        public static InputContract<WriteValue[]> Create(NativeValuePolicy values, bool dryRun, bool confirmWrite,
            bool uniqueNames = true, Func<string, NativeValuePolicy>? targetValuePolicy = null)
        {
            var valueContract = NativeValueValidator.Create(values);
            var row = InputSchema.Object(new Dictionary<string, InputSchema> {
                ["name"] = InputSchema.String(minLength: 1), ["value"] = values.Schema }, new[] { "name", "value" });
            return new InputContract<WriteValue[]>(InputSchema.Array(row, 1, 500), new InputBudget(), writes =>
            {
                InputGuard.Require(dryRun || confirmWrite);
                var names = new HashSet<string>(StringComparer.Ordinal);
                var normalized = new List<WriteValue>();
                foreach (var write in writes)
                {
                    var name = write.Name.Trim();
                    InputGuard.Require(name.Length != 0 && (!uniqueNames || names.Add(name)));
                    var result = valueContract.Validate(write.Value, "value");
                    InputGuard.Result(result.Error);
                    if (targetValuePolicy != null)
                        result = NativeValueValidator.Create(targetValuePolicy(name)).Validate(write.Value, "value");
                    InputGuard.Result(result.Error);
                    normalized.Add(new WriteValue(name, result.Value));
                }
                return normalized.ToArray();
            });
        }
        public static InputContract<WriteValue[]> Runtime(bool dryRun = true, bool confirmWrite = false) =>
            Create(NativeValueValidator.Scalar(), dryRun, confirmWrite);
    }
}

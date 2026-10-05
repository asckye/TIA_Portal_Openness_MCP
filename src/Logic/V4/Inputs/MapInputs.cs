using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TiaMcp.Logic.V4.Inputs
{
    public sealed class AttributeRule
    {
        public InputSchema Values { get; }
        public bool Writable { get; }
        public AttributeRule(InputSchema values, bool writable = true)
        { Values = values ?? throw new ArgumentNullException(nameof(values)); Writable = writable; }
    }

    public static class AttributeMapValidator
    {
        internal static Dictionary<string, InputSchema> ScalarFields(IReadOnlyDictionary<string, AttributeRule> attributes) =>
            attributes.Where(p => p.Value.Writable).ToDictionary(p => p.Key,
                p => new InputSchema(V4Json.Data(new Dictionary<string, object?> {
                    ["allOf"] = new[] { InputSchema.Scalar().Json, p.Value.Values.Json } })!.Value), StringComparer.Ordinal);

        // Caller supplies the exact action/release/native-object admission table; an
        // empty table admits no writes. Read-only SDK attributes never enter the schema.
        public static InputContract<AttributeMap<Scalar>> Create(IReadOnlyDictionary<string, AttributeRule> attributes,
            InputBudget budget, int minimum = 0, int? maximum = null, bool identifierKeys = false)
        {
            var fields = ScalarFields(attributes);
            return new InputContract<AttributeMap<Scalar>>(InputSchema.Object(fields, minimum: minimum, maximum: maximum), budget, values =>
            {
                if (identifierKeys) InputGuard.Require(values.Keys.All(InputGuard.Identifier));
                return values;
            });
        }
        // ArgumentRules.cs:53-87. The schema is closed to the caller's admitted SDK keys.
        public static InputContract<AttributeMap<Scalar>> Hardware(IReadOnlyDictionary<string, AttributeRule> attributes, int minimum = 0) =>
            Create(attributes, new InputBudget(characters: 32768), minimum, 50, true);
        public static InputContract<AttributeMap<Scalar>> Safety(IReadOnlyDictionary<string, AttributeRule> attributes, int minimum = 0) =>
            Create(attributes, new InputBudget(characters: 16384), minimum, 50);
        // SoftwareUnitDeepLogic.cs:33-35,69-74.
        public static InputContract<AttributeMap<Scalar>> SoftwareUnit() => Create(new Dictionary<string, AttributeRule> {
            ["Author"] = new AttributeRule(InputSchema.String()), ["NamespacePreset"] = new AttributeRule(InputSchema.String()) }, new InputBudget());

        public static InputContract<CpuSettings> Cpu(IReadOnlyDictionary<string, AttributeRule> exactAttributes)
        {
            var inner = Hardware(exactAttributes);
            return new InputContract<CpuSettings>(InputSchema.Object(new Dictionary<string, InputSchema> {
                ["exactAttributes"] = new InputSchema(inner.Schema) }, new[] { "exactAttributes" }), new InputBudget(characters: 32768), settings =>
            {
                var result = inner.Validate(settings.ExactAttributes, "exactAttributes");
                InputGuard.Require(result.IsValid);
                return settings;
            });
        }
    }

    public sealed class CpuSettings
    {
        public AttributeMap<Scalar> ExactAttributes { get; }
        public CpuSettings(AttributeMap<Scalar> exactAttributes)
        { ExactAttributes = exactAttributes ?? throw new ArgumentNullException(nameof(exactAttributes)); }
    }

    public sealed class PromptAdmission
    {
        public IReadOnlyList<string> Selections { get; }
        public bool Checkbox { get; }
        public bool Password { get; }
        public PromptAdmission(string[]? selections = null, bool checkbox = false, bool password = false)
        { Selections = Array.AsReadOnly((string[])(selections ?? Array.Empty<string>()).Clone()); Checkbox = checkbox; Password = password; }
    }

    public static class TextMapValidator
    {
        public static InputContract<AttributeMap<string>> Texts(int minimum = 0, int? maximum = null, int? maxTextLength = null,
            InputBudget? budget = null) => new InputContract<AttributeMap<string>>(
                InputSchema.Map(InputSchema.String(maxTextLength), minimum, maximum), budget ?? new InputBudget());

        // PlcTagEditingLogic.cs:48-66. Active culture membership is supplied by the
        // caller (the parser's later native validation), while SDK keys remain exact.
        public static InputContract<AttributeMap<string>> Comments(IReadOnlyCollection<string>? activeCultures = null, bool requireCultureInfo = false, int minimum = 1)
            => new InputContract<AttributeMap<string>>(InputSchema.Map(InputSchema.String(), minimum,
                keys: InputSchema.String(16, 1)), new InputBudget(), values =>
            {
                foreach (var pair in values)
                {
                    InputGuard.Require(!string.IsNullOrWhiteSpace(pair.Key));
                    if (requireCultureInfo) _ = CultureInfo.GetCultureInfo(pair.Key);
                    if (activeCultures != null) InputGuard.Require(activeCultures.Contains(pair.Key, StringComparer.Ordinal));
                }
                return values;
            });

        // DownloadPromptPolicy.cs:43-57,79-100: boolean legacy answers become text;
        // runtime prompt metadata narrows options. Passwords use dedicated arguments.
        public static InputContract<AttributeMap<string>> PromptAnswers(IReadOnlyDictionary<string, PromptAdmission>? prompts = null)
        {
            var copy = prompts?.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            return new InputContract<AttributeMap<string>>(InputSchema.Map(InputSchema.String(64, 1),
                keys: InputSchema.String(minLength: 1, pattern: "^[\\p{L}\\p{Nd}]+$")), new InputBudget(), values =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in values)
                {
                    InputGuard.Require(names.Add(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));
                    string answer = pair.Value;
                    if (copy != null)
                    {
                        InputGuard.Require(copy.TryGetValue(pair.Key, out _));
                        var prompt = copy[pair.Key];
                        InputGuard.Require(!prompt.Password);
                        if (prompt.Selections.Count != 0)
                        {
                            answer = prompt.Selections.FirstOrDefault(s => s.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))!;
                            InputGuard.Require(answer != null, prompt.Selections.ToArray());
                        }
                        else
                        {
                            InputGuard.Require(prompt.Checkbox && bool.TryParse(pair.Value, out _));
                            answer = bool.Parse(pair.Value) ? "true" : "false";
                        }
                    }
                    normalized.Add(pair.Key, answer!);
                }
                return new AttributeMap<string>(normalized);
            });
        }

        // OpcUaService.cs:298-305: Safety areas only 0/1, other areas 0..4.
        public static InputContract<AttributeMap<int>> AccessLevels()
        {
            var fields = new[] { "Inputs", "Outputs", "Memory", "Counters", "Timers", "GlobalDBs", "InstanceDBs", "SafetyGlobalDBs", "SafetyInstanceDBs" }
                .ToDictionary(k => k, k => InputSchema.Integer(0, k.StartsWith("Safety", StringComparison.Ordinal) ? 1 : 4), StringComparer.Ordinal);
            return new InputContract<AttributeMap<int>>(InputSchema.Object(fields), new InputBudget());
        }
    }
}

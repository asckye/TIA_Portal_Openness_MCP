using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaMcp.Logic.V4.Inputs
{
    public static class PathValidator
    {
        // Portal.SessionResolvers.cs:84-104. Segments are already decoded strings;
        // '/', '\\', commas and quotes inside a station name are never split again.
        public static InputContract<string[]> Device() => Create(false);
        public static InputContract<string[]> Item() => Create(true);
        public static InputContract<string[]> Create(bool rootAllowed, int maximumDepth = 64, bool rejectDotSegments = false)
            => new InputContract<string[]>(InputSchema.Array(InputSchema.String(minLength: 1), rootAllowed ? 0 : 1, maximumDepth),
                new InputBudget(), values =>
                {
                    InputGuard.Require(values.All(s => !string.IsNullOrWhiteSpace(s)
                        && (!rejectDotSegments || s != "." && s != "..")));
                    return values;
                });
    }

    public sealed class NameListPolicy
    {
        public int Minimum { get; }
        public int? Maximum { get; }
        public int? MaxLength { get; }
        public bool Unique { get; }
        public bool Trim { get; }
        public bool IgnoreEmpty { get; }
        public bool Cultures { get; }
        public bool IgnoreCase { get; }
        public bool NoneExclusive { get; }
        public IReadOnlyList<string>? Allowed { get; }
        public InputBudget Budget { get; }
        public NameListPolicy(int minimum = 0, int? maximum = null, int? maxLength = null, bool unique = false,
            bool trim = false, bool ignoreEmpty = false, bool cultures = false, bool ignoreCase = false,
            bool noneExclusive = false, string[]? allowed = null, InputBudget? budget = null)
        {
            if (minimum < 0 || maximum < minimum || maxLength < 0) throw new ArgumentOutOfRangeException(nameof(minimum));
            Minimum = minimum; Maximum = maximum; MaxLength = maxLength; Unique = unique; Trim = trim;
            IgnoreEmpty = ignoreEmpty; Cultures = cultures; IgnoreCase = ignoreCase; NoneExclusive = noneExclusive;
            Allowed = allowed == null ? null : Array.AsReadOnly((string[])allowed.Clone()); Budget = budget ?? new InputBudget();
        }
    }

    public static class NameListValidator
    {
        // HardwareNetworkLogic.cs:51-67, LibraryDeepLogic.cs:99-108.
        public static InputContract<string[]> Hardware(int maximum = 64) => Create(new NameListPolicy(maximum: maximum, unique: true));
        public static InputContract<string[]> Harmonize() => Create(new NameListPolicy(minimum: 1, maximum: 64, unique: true,
            allowed: new[] { "HarmonizeNames", "HarmonizePaths" }));
        public static InputContract<string[]> Permissions(string[] allowed) => Create(new NameListPolicy(minimum: 1, maximum: 64,
            unique: true, allowed: allowed, noneExclusive: true));
        // RuntimeChannelsLogic.cs:76-105 / PlcSimAdvancedLogic.cs:148-162.
        public static InputContract<string[]> Runtime() => Create(new NameListPolicy(minimum: 1, maximum: 500, unique: true, trim: true));
        public static InputContract<string[]> Simulation() => Create(new NameListPolicy(maximum: 500, trim: true, ignoreEmpty: true));
        // StartdriveLogic.cs:97-120 / PlcBlockServicesLogic.cs:76-90.
        public static InputContract<string[]> Drive() => Create(new NameListPolicy(maximum: 200, maxLength: 64, unique: true));
        public static InputContract<string[]> CultureNames() => Create(new NameListPolicy(minimum: 1, maximum: 64, unique: true,
            trim: true, cultures: true, ignoreCase: true, budget: new InputBudget(characters: 4096)));

        public static InputContract<string[]> Create(NameListPolicy policy)
        {
            var items = InputSchema.String(policy.MaxLength, policy.IgnoreEmpty ? 0 : 1, policy.Trim ? null : policy.Allowed?.ToArray());
            return new InputContract<string[]>(InputSchema.Array(items, policy.Minimum, policy.Maximum,
                policy.Unique && !policy.Trim && !policy.IgnoreCase), policy.Budget, values =>
            {
                var normalized = values.Select(s => policy.Trim ? s.Trim() : s).ToArray();
                if (policy.IgnoreEmpty) normalized = normalized.Where(s => s.Length != 0).ToArray();
                InputGuard.Require(normalized.Length >= policy.Minimum && normalized.All(s => !string.IsNullOrWhiteSpace(s)));
                if (policy.Cultures) normalized = normalized.Select(s => CultureInfo.GetCultureInfo(s).Name).ToArray();
                var comparer = policy.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (policy.Unique) InputGuard.Require(normalized.Distinct(comparer).Count() == normalized.Length);
                if (policy.Allowed != null) InputGuard.Require(normalized.All(s => policy.Allowed.Contains(s, comparer)), policy.Allowed.ToArray());
                if (policy.NoneExclusive) InputGuard.Require(normalized.Length < 2 || !normalized.Contains("None", StringComparer.Ordinal));
                return normalized;
            });
        }
    }

    public static class NumberListValidator
    {
        // StartdriveLogic.cs:107-120: duplicates retain their order. Combined names and
        // numbers budget is 200. V4 int32[] denotes the old arrayIndex=-1 branch only.
        public static InputContract<int[]> Drive(int nameCount = 0)
        {
            if (nameCount < 0 || nameCount > 200) throw new ArgumentOutOfRangeException(nameof(nameCount));
            return new InputContract<int[]>(InputSchema.Array(InputSchema.Integer(0, 65535), maximum: 200 - nameCount), new InputBudget());
        }

        // All legacy {number,arrayIndex} selectors use this shape, including omitted
        // indices. Omission means -1; explicit indices and duplicate entries survive.
        public static InputContract<ParameterRef[]> ParameterReferences(int nameCount = 0)
        {
            if (nameCount < 0 || nameCount > 200) throw new ArgumentOutOfRangeException(nameof(nameCount));
            var item = InputSchema.Object(new Dictionary<string, InputSchema> {
                ["number"] = InputSchema.Integer(0, 65535), ["arrayIndex"] = InputSchema.Integer(-1, 32767) }, new[] { "number" });
            return new InputContract<ParameterRef[]>(InputSchema.Array(item, maximum: 200 - nameCount), new InputBudget());
        }
    }

    public sealed class PropertyAdmission
    {
        public bool PublicReadable { get; }
        public bool IsIndexer { get; }
        public PropertyPathPolicy? Next { get; }
        public PropertyAdmission(bool publicReadable, bool isIndexer = false, PropertyPathPolicy? next = null)
        { PublicReadable = publicReadable; IsIndexer = isIndexer; Next = next; }
    }

    // Descriptions supplied from the already-admitted SDK target, not a CLR Type from
    // input. Validation cannot load types, call getters, or enumerate native objects.
    public sealed class PropertyPathPolicy
    {
        public IReadOnlyDictionary<string, PropertyAdmission> Properties { get; }
        public bool AllowIndex { get; }
        public int? MaximumIndex { get; }
        public PropertyPathPolicy(IReadOnlyDictionary<string, PropertyAdmission> properties, bool allowIndex = false, int? maximumIndex = null)
        {
            Properties = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PropertyAdmission>(
                properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
            AllowIndex = allowIndex; MaximumIndex = maximumIndex;
        }
    }

    public static class PropertyPathValidator
    {
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        // EngineeringObjectAddress.cs:11-38: 24 steps, 32768 characters, public
        // non-indexer properties only. The current operation does not admit indices.
        public static InputContract<PropertyStep[]> Create(PropertyPathPolicy policy)
        {
            InputSchema Step(PropertyPathPolicy current)
            {
                var names = current.Properties.Where(p => p.Value.PublicReadable && !p.Value.IsIndexer
                    && !BackLinks.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && InputGuard.Identifier(p.Key)).Select(p => p.Key).ToArray();
                var properties = new Dictionary<string, InputSchema> { ["property"] = InputSchema.String(minLength: 1, allowed: names), ["name"] = InputSchema.String(minLength: 1) };
                if (current.AllowIndex) properties["index"] = InputSchema.Integer(0, current.MaximumIndex ?? int.MaxValue);
                var shape = InputSchema.Object(properties, new[] { "property" });
                return new InputSchema(V4Json.Data(new Dictionary<string, object?> {
                    ["allOf"] = new[] { shape.Json, V4Json.Deserialize<System.Text.Json.JsonElement>("{\"not\":{\"required\":[\"name\",\"index\"]}}") } })!.Value);
            }
            // Different steps may have different SDK owners; express the union of
            // admitted spellings, then validate the actual transition graph below.
            var schemas = new List<InputSchema>();
            var visited = new HashSet<PropertyPathPolicy>();
            void Collect(PropertyPathPolicy current, int depth)
            {
                if (depth == 24 || !visited.Add(current)) return;
                schemas.Add(Step(current));
                foreach (var property in current.Properties.Values) if (property.Next != null) Collect(property.Next, depth + 1);
            }
            Collect(policy, 0);
            return new InputContract<PropertyStep[]>(InputSchema.Array(InputSchema.Union(schemas.ToArray()), maximum: 24),
                new InputBudget(characters: 32768), steps =>
                {
                    PropertyPathPolicy? current = policy;
                    foreach (var step in steps)
                    {
                        InputGuard.Require(current != null && current.Properties.TryGetValue(step.Property, out _));
                        var member = current!.Properties[step.Property];
                        InputGuard.Require(member.PublicReadable && !member.IsIndexer && InputGuard.Identifier(step.Property)
                            && !BackLinks.Contains(step.Property, StringComparer.OrdinalIgnoreCase));
                        InputGuard.Require(step.Index == null || current.AllowIndex && step.Index <= (current.MaximumIndex ?? int.MaxValue));
                        current = member.Next;
                    }
                    return steps;
                });
        }

        // EngineeringGroupOperations.cs:17-23. Hosts call this as they consume each
        // native collection, before accepting the next item; no native enumeration here.
        public static Error? ValidateTraversal(int visitedItems, string parameter)
        {
            if (visitedItems < 0) return InputGuard.Invalid(parameter);
            return visitedItems <= 10000 ? null : new Error("Traversal exceeds its declared budget.", new LimitExceededDetails(parameter, 10000, visitedItems));
        }
    }
}

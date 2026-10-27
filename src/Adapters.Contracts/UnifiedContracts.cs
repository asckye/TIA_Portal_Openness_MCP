using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class UnifiedValue
    {
        public string Kind { get; set; } = "null";
        public string Text { get; set; } = "";
        public string? Error { get; set; }
        public Dictionary<string, UnifiedValue> Properties { get; set; } = new Dictionary<string, UnifiedValue>(StringComparer.Ordinal);
        public List<UnifiedValue> Items { get; set; } = new List<UnifiedValue>();
    }

    // Pure managed validation shared by host admission and native adapter navigation.
    public static class EngineeringInputValidation
    {
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        public const string ObjectPathExample = "[{\"property\":\"Screens\",\"name\":\"Overview\"},{\"property\":\"ScreenItems\",\"name\":\"StartButton\"}]";
        public const string OptionalObjectPathExample = "[]";
        public const string BranchPathExample = "[{\"property\":\"ScreenItems\"},{\"name\":\"Overview\"}]";

        public static object? Value(UnifiedValue value)
        {
            if (value.Error != null) throw new ArgumentException("Invalid JSON input: " + value.Error);
            switch (value.Kind)
            {
                case "object": return value.Properties.ToDictionary(p => p.Key, p => Value(p.Value), StringComparer.Ordinal);
                case "array": return value.Items.Select(Value).ToArray();
                case "string": return value.Text;
                case "number": return int.TryParse(value.Text, out var number) ? (object)number
                    : double.TryParse(value.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fractional) ? fractional : double.NaN;
                case "boolean": return bool.Parse(value.Text);
                default: return null;
            }
        }

        public static void Path(string json, object? value, bool branch = false, bool optional = false)
        {
            string expected = branch
                ? "Expected an array of {property}, {attribute}, {index} or {name,key?} steps, e.g. " + BranchPathExample + "."
                : "Expected an array of {\"property\":string,\"name\"?:string} steps, e.g. "
                    + (optional ? OptionalObjectPathExample + " (the typed DCC/SiVArc root)" : ObjectPathExample) + ".";
            ArgumentException Invalid(string reason) => new ArgumentException(reason + " " + expected);
            if (json == null || json.Length > 32768) throw Invalid("Object path exceeds 32 KiB.");
            if (value is not IList steps) throw Invalid("Path is " + Kind(value) + "; it must be a JSON array.");
            if (steps.Count > (branch ? 64 : 24)) throw Invalid("Too many path steps (maximum " + (branch ? 64 : 24) + ").");
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] is not IDictionary<string, object?> step) throw Invalid("step " + i + " is " + Kind(steps[i]) + ".");
                string prefix = "step " + i + ": ";
                if (!branch)
                {
                    if (step.Keys.Any(k => k != "property" && k != "name")) throw Invalid(prefix + "unknown key.");
                    if (!step.TryGetValue("property", out var property) || !Member(property)) throw Invalid(prefix + "invalid or backward property.");
                    if (step.TryGetValue("name", out var name) && !Name(name)) throw Invalid(prefix + "name must be a nonempty string.");
                }
                else if (step.Count == 1 && (step.TryGetValue("property", out var member) || step.TryGetValue("attribute", out member)))
                {
                    if (!BranchMember(member)) throw Invalid(prefix + "invalid or backward property/attribute.");
                }
                else if (step.Count == 1 && step.TryGetValue("index", out var index))
                {
                    if (index is not int number || number < 0) throw Invalid(prefix + "index must be a nonnegative integer.");
                }
                else if (step.TryGetValue("name", out var name) && step.Keys.All(k => k == "name" || k == "key"))
                {
                    if (!Name(name) || step.TryGetValue("key", out var key) && !BranchMember(key)) throw Invalid(prefix + "invalid exact name or backward key.");
                }
                else throw Invalid(prefix + "unknown key or invalid branch step.");
            }
        }

        private static bool Name(object? value) => value is string text && !string.IsNullOrWhiteSpace(text);
        private static bool BranchMember(object? value) => value is string text && Name(text) && !BackLinks.Contains(text, StringComparer.OrdinalIgnoreCase);
        private static bool Member(object? value) => value is string text && !string.IsNullOrWhiteSpace(text)
            && text.All(c => char.IsLetterOrDigit(c) || c == '_') && !BackLinks.Contains(text, StringComparer.OrdinalIgnoreCase);
        private static string Kind(object? value) => value == null ? "null" : value is string ? "a string"
            : value is IDictionary<string, object?> ? "an object" : value is IList ? "an array" : value is bool ? "a boolean" : "a number";

        public static void NameOrAbsolutePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An exact name or absolute path is required; pass /Group/Sub/Table.");
            if (value.IndexOfAny(new[] { '/', '\\' }) < 0) return;
            AbsoluteHmiPath(value);
        }

        public static string[] AbsoluteHmiPath(string value, bool rootAllowed = false, int maximumSegments = 65)
        {
            if (rootAllowed && value == "/") return Array.Empty<string>();
            var corrected = "/" + string.Join("/", (value ?? "").Trim().Replace('\\', '/').Split('/').Where(p => !string.IsNullOrWhiteSpace(p)));
            if (!rootAllowed && corrected == "/") corrected = "/Group/Sub/Table";
            var guidance = " Use absolute /Group/Sub/Table (or /Group/Sub for folders) with URI-escaped segments. Corrected slash form: " + corrected + ".";
            if (string.IsNullOrWhiteSpace(value) || !value!.StartsWith("/", StringComparison.Ordinal) || value.IndexOf('\\') >= 0)
                throw new ArgumentException("Relative and backslash HMI paths are refused." + guidance);
            var parts = value.Substring(1).Split('/');
            if (parts.Length > maximumSegments || parts.Any(p => string.IsNullOrWhiteSpace(Uri.UnescapeDataString(p))
                || Uri.UnescapeDataString(p).IndexOfAny(new[] { '/', '\\' }) >= 0))
                throw new ArgumentException("Invalid HMI path: empty segments, encoded separators and trailing / are refused; at most " + maximumSegments + " segments are allowed." + guidance);
            return parts;
        }
    }
    public sealed class UnifiedRequest
    {
        public string Operation { get; set; } = "";
        public Dictionary<string, UnifiedValue> Inputs { get; set; } = new Dictionary<string, UnifiedValue>(StringComparer.Ordinal);
        public string SoftwarePath { get; set; } = "";
        public string ObjectPathJson { get; set; } = "";
        public int Offset { get; set; }
        public int Limit { get; set; }
        public string PropertiesJson { get; set; } = "";
        public bool Overwrite { get; set; }
        public bool DryRun { get; set; }
        public string Property { get; set; } = "";
        public string Culture { get; set; } = "";
        public string RawText { get; set; } = "";
        public string Filter { get; set; } = "";
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string DestinationDirectory { get; set; } = "";
        public string TagPathJson { get; set; } = "";
        public string Action { get; set; } = "";
        public string DurationPathJson { get; set; } = "";
        public string Kind { get; set; } = "";
        public uint Days { get; set; }
        public uint Hours { get; set; }
        public uint Minutes { get; set; }
        public uint Seconds { get; set; }
        public uint HundredNanoseconds { get; set; }
        public string NodeId { get; set; } = "";
        public string Connection { get; set; } = "";
        public string PlantPath { get; set; } = "";
        public string PlantObjectType { get; set; } = "";
        public string CollectionProperty { get; set; } = "";
        public string PartName { get; set; } = "";
        public int PartIndex { get; set; }
        public string PartKind { get; set; } = "";
        public bool ConfirmDelete { get; set; }
        public string PropertyName { get; set; } = "";
        public string DynamizationKind { get; set; } = "";
        public string MappingEntriesJson { get; set; } = "";
        public string ListName { get; set; } = "";
        public string EntryKey { get; set; } = "";
        public string EntryJson { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ExpectedNamesJson { get; set; } = "";
        public string EventType { get; set; } = "";
        public string ScriptPropertiesJson { get; set; } = "";
        public string ExpectedToken { get; set; } = "";
        public string Directory { get; set; } = "";
        public string TagTable { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ExpectedTagNamesJson { get; set; } = "";
        public string ModuleName { get; set; } = "";
        public string ConnectionName { get; set; } = "";
        public string XmlPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ExpectedProject { get; set; } = "";
        public string ScriptCode { get; set; } = "";
        public string ScreenPath { get; set; } = "";
        public string ItemNamesJson { get; set; } = "";
        public string Cursor { get; set; } = "";
        public int PageSize { get; set; }
        public int BudgetMs { get; set; }
        public string BeforePagesJson { get; set; } = "";
        public string AfterPagesJson { get; set; } = "";
        public string FieldsJson { get; set; } = "";
        public string ChangesJson { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string BranchJson { get; set; } = "";
        public string TypePath { get; set; } = "";
        public string Version { get; set; } = "";
        public string FolderPath { get; set; } = "";
    }
}

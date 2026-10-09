using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationNamingEngine
    {
        private readonly Dictionary<string, NamingPartRulesItem> rules;
        private readonly string language;
        public GenerationNamingEngine(NamingPart naming, string language)
        {
            GenerationDocuments.Canonical(naming);
            rules = naming.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
            this.language = language;
        }

        public string Expand(string template, IReadOnlyDictionary<string, JsonElement> context,
            Func<string, IReadOnlyList<JsonElement>, JsonElement>? builtIn = null)
            => Expand(template, context, builtIn, 0);

        private string Expand(string template, IReadOnlyDictionary<string, JsonElement> context,
            Func<string, IReadOnlyList<JsonElement>, JsonElement>? builtIn, int depth)
        {
            if (depth > 8) throw CanonicalJson.Failure("", "placeholder-depth", "Naming rules are recursive or exceed eight calls.");
            RestrictedPlaceholders.Validate(template, "", (p, r, m) => throw CanonicalJson.Failure(p, r, m), rules.ContainsKey);
            var result = new StringBuilder();
            var position = 0;
            while (position < template.Length)
            {
                var open = template.IndexOf("{{", position, StringComparison.Ordinal);
                if (open < 0) { result.Append(template.Substring(position)); break; }
                result.Append(template.Substring(position, open - position));
                var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
                result.Append(Value(Evaluate(RestrictedPlaceholders.Parse(template.Substring(open + 2, close - open - 2)))));
                position = close + 2;
                if (result.Length > 4096) throw CanonicalJson.Failure("", "name-length", "Expanded text exceeds 4096 characters.");
            }
            if (result.Length > 4096) throw CanonicalJson.Failure("", "name-length", "Expanded text exceeds 4096 characters.");
            return result.ToString();

            JsonElement Evaluate(RestrictedPlaceholders.Expression expression)
            {
                if (expression.Literal != null) return JsonSerializer.SerializeToElement(expression.Literal);
                if (expression.Arguments == null)
                {
                    if (!TryResolve(context, expression.Name, out var field))
                        throw CanonicalJson.Failure("/" + expression.Name, "reference", "Placeholder field does not exist.");
                    return field;
                }
                var args = expression.Arguments.Select(Evaluate).ToArray();
                if (expression.Name.StartsWith("naming.", StringComparison.Ordinal))
                {
                    var rule = rules[expression.Name.Substring(7)];
                    var nested = context.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    for (var i = 0; i < args.Length; i++)
                    {
                        nested["arg" + (i + 1).ToString(CultureInfo.InvariantCulture)] = args[i];
                        var argument = expression.Arguments[i];
                        if (argument.Arguments == null && argument.Literal == null && argument.Name.IndexOf('.') < 0) nested[argument.Name] = args[i];
                    }
                    var name = Expand(rule.Template, nested, builtIn, depth + 1);
                    CheckRule(rule, name);
                    return JsonSerializer.SerializeToElement(name);
                }
                switch (expression.Name)
                {
                    case "upper": return JsonSerializer.SerializeToElement(Value(args[0]).ToUpperInvariant());
                    case "lower": return JsonSerializer.SerializeToElement(Value(args[0]).ToLowerInvariant());
                    case "pad":
                        if (!args[1].TryGetInt32(out var width) || width < 1 || width > 256)
                            throw CanonicalJson.Failure("", "pad", "Pad width must be 1..256.");
                        return JsonSerializer.SerializeToElement(Value(args[0]).PadLeft(width, '0'));
                    case "text":
                        if (args[0].ValueKind == JsonValueKind.String) return args[0];
                        if (args[0].ValueKind != JsonValueKind.Object || !args[0].TryGetProperty(language, out var text))
                            throw CanonicalJson.Failure("", "language", "Required localized text is missing: " + language);
                        return text;
                    default:
                        if (builtIn == null) throw CanonicalJson.Failure("", "placeholder", "This expansion has no allocation/tag context.");
                        return builtIn(expression.Name, args);
                }
            }
        }

        public void Check(string kind, string name)
        {
            var matching = rules.Values.Where(r => r.Kind == kind).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            if (matching.Length == 0) throw CanonicalJson.Failure("/names/" + CanonicalJson.Pointer(name), "naming-rule", "No naming rule for kind " + kind + ".");
            // Multiple rules for a kind are alternatives; a naming.* call additionally checks its exact rule.
            if (!matching.Any(r => Passes(r, name))) throw CanonicalJson.Failure("/names/" + CanonicalJson.Pointer(name), "naming", "Generated name fails the package rules for " + kind + ".");
        }

        private static void CheckRule(NamingPartRulesItem rule, string name)
        {
            if (!Passes(rule, name)) throw CanonicalJson.Failure("/naming/" + rule.Id, "naming", "Expanded name violates its naming rule: " + name);
        }

        private static bool Passes(NamingPartRulesItem rule, string name)
        {
            if (name.Length < (rule.MinLength ?? 1) || name.Length > (rule.MaxLength ?? 4096) || name.Any(c => char.IsControl(c) || c == '"')) return false;
            try
            {
                if (!Regex.IsMatch(name, rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
            }
            catch (RegexMatchTimeoutException) { throw CanonicalJson.Failure("/naming/" + rule.Id, "pattern-timeout", "Naming pattern exceeded its time budget."); }
            return rule.Case switch
            {
                "upper" => name == name.ToUpperInvariant(), "lower" => name == name.ToLowerInvariant(),
                "pascal" => !char.IsLetter(name[0]) || char.IsUpper(name[0]), "camel" => !char.IsLetter(name[0]) || char.IsLower(name[0]), _ => true
            };
        }

        internal static bool TryResolve(IReadOnlyDictionary<string, JsonElement> context, string path, out JsonElement value)
        {
            var segments = path.Split('.');
            if (!context.TryGetValue(segments[0], out value)) return false;
            foreach (var segment in segments.Skip(1))
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
            return true;
        }

        internal static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!
            : value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? Encoding.UTF8.GetString(CanonicalJson.Encode(value)) : throw CanonicalJson.Failure("", "placeholder-type", "Placeholder result must be a string, number or boolean.");
    }
}

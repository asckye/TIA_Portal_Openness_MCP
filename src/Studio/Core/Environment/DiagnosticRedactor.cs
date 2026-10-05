using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaOpenness.Core.Environment
{
    public sealed class DiagnosticRedaction
    {
        public string Text { get; }
        public IReadOnlyDictionary<string, int> Rules { get; }
        internal DiagnosticRedaction(string text, Dictionary<string, int> rules) { Text = text; Rules = rules; }
    }

    // Used for logs, configurations, check evidence and manifest paths. Never record secret values.
    public sealed class DiagnosticRedactor
    {
        public const string Mask = "[REDACTED]";
        private readonly HashSet<string> knownSecrets = new HashSet<string>(StringComparer.Ordinal);
        private const string SecretName = @"[\w.-]*(?:key|token|password|passwd|pwd|passphrase|secret|credential|authorization|cookie)[\w.-]*";
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        public static bool SecretField(string name)
        {
            string normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return new[] { "key", "token", "password", "passwd", "passphrase", "secret", "credential", "authorization", "cookie" }
                .Any(normalized.Contains) || normalized == "pwd";
        }

        public DiagnosticRedaction Redact(string text, string format = "text")
        {
            var rules = new Dictionary<string, int>(StringComparer.Ordinal);
            void Count(string rule) { rules[rule] = rules.TryGetValue(rule, out var count) ? count + 1 : 1; }
            void Remember(string value) { if (!string.IsNullOrWhiteSpace(value) && value != Mask) knownSecrets.Add(value); }
            void RememberNode(JsonNode node)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var secret)) Remember(secret);
                else if (node is JsonArray array) foreach (var child in array) { if (child != null) RememberNode(child); }
                else if (node is JsonObject obj) foreach (var child in obj) { if (child.Value != null) RememberNode(child.Value); }
            }
            string Replace(string value, string pattern, string rule, Func<Match, string> replacement)
            {
                return Regex.Replace(value, pattern, match => { Count(rule); return replacement(match); },
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            void Scrub(JsonNode node)
            {
                if (node is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                    {
                        if (SecretField(pair.Key))
                        {
                            if (pair.Value != null) RememberNode(pair.Value);
                            obj[pair.Key] = Mask; Count("secret-field");
                        }
                        else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var content)) obj[pair.Key] = Plain(content);
                        else if (pair.Value != null) Scrub(pair.Value);
                    }
                else if (node is JsonArray array)
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is JsonValue value && value.TryGetValue<string>(out var content)) array[i] = Plain(content);
                        else if (array[i] != null) Scrub(array[i]);
            }
            string Plain(string value)
            {
                value = Replace(value, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|\z)", "private-key", _ => Mask);
                value = Replace(value, @"\b(?:Bearer|Basic)\s+[^\s""'<>\\,;]+", "auth-scheme", m => (m.Value.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) ? "Basic " : "Bearer ") + Mask);
                value = Replace(value, @"(?<prefix>https?://)[^\s/@]+:[^\s/@]+@", "url-userinfo", m => m.Groups["prefix"].Value + Mask + "@");
                value = Replace(value, @"(?<prefix>[?&]" + SecretName + @"=)[^&\s""'<>]+", "query-secret", m => m.Groups["prefix"].Value + Mask);
                // Quotes include escaped quotes; unquoted values are conservatively removed to the end of the line.
                value = Replace(value, @"(?<prefix>(?:[""']?" + SecretName + @"[""']?\s*[:=]\s*|--" + SecretName + @"\s+))(?<value>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\r\n]+)",
                    "text-secret", m => m.Groups["prefix"].Value + "\"" + Mask + "\"");
                foreach (string secret in knownSecrets.OrderByDescending(s => s.Length))
                    if (value.Contains(secret)) { value = value.Replace(secret, Mask); Count("known-secret"); }
                return value;
            }
            try
            {
                if (format == "json")
                {
                    var node = JsonNode.Parse(text);
                    if (node != null) Scrub(node);
                    return new DiagnosticRedaction(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", rules);
                }
                if (format == "xml")
                {
                    var xml = XDocument.Parse(text);
                    foreach (var element in xml.Descendants())
                    {
                        if (!element.HasElements && SecretField(element.Name.LocalName))
                        { Remember(element.Value); element.Value = Mask; Count("xml-secret"); }
                        bool secretSetting = element.Attributes().Any(a => (a.Name.LocalName == "key" || a.Name.LocalName == "name") && SecretField(a.Value));
                        foreach (var attribute in element.Attributes().ToArray())
                            if (SecretField(attribute.Name.LocalName) || (secretSetting && attribute.Name.LocalName == "value"))
                            { Remember(attribute.Value); attribute.Value = Mask; Count("xml-secret"); }
                    }
                    return new DiagnosticRedaction(Plain(xml.ToString()), rules);
                }
                // JSONL logs retain readable fields when complete; malformed fragments use the conservative text rules.
                text = Replace(text, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|\z)", "private-key", _ => Mask);
                text = Replace(text, @"\b(?:Bearer|Basic)\s+[^\s""'<>\\,;]+", "auth-scheme", m => (m.Value.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) ? "Basic " : "Bearer ") + Mask);
                var lines = text.Split('\n').Select(line =>
                {
                    if (!line.TrimStart().StartsWith("{")) return Plain(line);
                    try { var node = JsonNode.Parse(line); if (node != null) Scrub(node); return Plain(node?.ToJsonString() ?? "null"); }
                    catch (JsonException) /* swallow(privacy): malformed JSON log lines are hidden entirely instead of exposing partial credentials */ { Count("unparseable-hidden"); return Mask; }
                });
                return new DiagnosticRedaction(string.Join("\n", lines), rules);
            }
            catch (Exception ex) when (ex is JsonException || ex is System.Xml.XmlException || ex is RegexMatchTimeoutException)
            {
                Count("unparseable-hidden");
                return new DiagnosticRedaction(Mask, rules);
            }
        }
    }
}

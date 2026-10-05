using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaOpenness.Shared
{
    // Linked by producers and the reader; sanitization precedes every size limit.
    public static class CallJournalPayload
    {
        public const int Limit = 4096;
        public const string Hidden = "••••";
        public const string Truncated = "\n… [truncated]";
        private static readonly JsonSerializerOptions DisplayOptions = new JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string Redact(string json)
            => Bound(Sanitize(json));

        internal static string Sanitize(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                var secrets = new HashSet<string>(StringComparer.Ordinal);
                Collect(node, secrets, 0);
                if (node is JsonValue value && value.TryGetValue<string>(out var text)) node = JsonValue.Create(Text(text, secrets, 0));
                else Scrub(node, secrets, 0);
                return node?.ToJsonString(DisplayOptions) ?? "null";
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is RegexMatchTimeoutException)
            { return Hidden; }
        }

        public static string Bound(string text)
        {
            if (text.Length <= Limit) return text;
            int count = Limit - Truncated.Length;
            if (char.IsHighSurrogate(text[count - 1])) count--;
            return text.Substring(0, count) + Truncated;
        }

        internal static bool Sensitive(string key)
        {
            string name = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (name == "releasekey") return false;
            return name.Contains("password") || name.Contains("passwd") || name.Contains("secret") || name.Contains("token")
                || name.EndsWith("key", StringComparison.Ordinal) || name.EndsWith("keys", StringComparison.Ordinal)
                || name.Contains("credential") || name.Contains("authorization") || name == "pwd" || name == "sk";
        }

        private static bool NamedSecret(JsonObject obj) => obj.Any(p =>
            (p.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("key", StringComparison.OrdinalIgnoreCase))
            && p.Value is JsonValue value && value.TryGetValue<string>(out var name) && Sensitive(name));

        private static void Collect(JsonNode? node, HashSet<string> secrets, int depth)
        {
            if (depth > 16) return;
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj);
                foreach (var pair in obj)
                    if (Sensitive(pair.Key) || named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase)) Values(pair.Value, secrets);
                    else Collect(pair.Value, secrets, depth + 1);
            }
            else if (node is JsonArray array) foreach (var child in array) Collect(child, secrets, depth + 1);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                FindSecrets(text, secrets);
                if (Embedded(text, out var embedded)) Collect(embedded, secrets, depth + 1);
            }
        }

        private static void Values(JsonNode? node, HashSet<string> secrets)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text != Hidden)
            { secrets.Add(text); FindSecrets(text, secrets); }
            else if (node is JsonValue scalar) secrets.Add(scalar.ToJsonString());
            else if (node is JsonArray array) foreach (var child in array) Values(child, secrets);
            else if (node is JsonObject obj) foreach (var pair in obj) Values(pair.Value, secrets);
        }

        private static void FindSecrets(string text, HashSet<string> secrets)
        {
            foreach (Match match in Regex.Matches(text, @"(?i)\b(?:Bearer|Basic)\s+([^\s""'<>\\,;]+)", RegexOptions.None, TimeSpan.FromSeconds(1)))
                if (match.Groups[1].Value != Hidden) secrets.Add(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(text, @"(?i)\b[\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*([^\s&;,""'<>]+)", RegexOptions.None, TimeSpan.FromSeconds(1)))
                if (match.Groups[1].Value != Hidden) secrets.Add(match.Groups[1].Value);
        }

        private static void Scrub(JsonNode? node, HashSet<string> secrets, int depth)
        {
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj);
                foreach (string key in obj.Select(p => p.Key).ToArray())
                {
                    if (Sensitive(key) || named && key.Equals("value", StringComparison.OrdinalIgnoreCase))
                        obj[key] = key.EndsWith("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " + Hidden : Hidden;
                    else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Text(text, secrets, depth);
                    else if (depth >= 16) obj[key] = Hidden;
                    else Scrub(obj[key], secrets, depth + 1);
                }
            }
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Text(text, secrets, depth);
                    else if (depth >= 16) array[i] = Hidden;
                    else Scrub(array[i], secrets, depth + 1);
        }

        private static bool Embedded(string text, out JsonNode? node)
        {
            node = null;
            if (!text.TrimStart().StartsWith("{") && !text.TrimStart().StartsWith("[")) return false;
            try { node = JsonNode.Parse(text); return true; }
            catch (JsonException) /* swallow(privacy): malformed embedded JSON is hidden by the text boundary */ { return false; }
        }

        private static string Text(string text, HashSet<string> secrets, int depth)
        {
            if (text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("["))
            {
                if (depth >= 16 || !Embedded(text, out var embedded)) return Hidden;
                Scrub(embedded, secrets, depth + 1);
                text = embedded?.ToJsonString(DisplayOptions) ?? "null";
            }
            foreach (var secret in secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, Hidden);
            text = Regex.Replace(text, @"(?i)\b(Bearer|Basic)\s+[^\s""'<>\\,;]+", "$1 " + Hidden, RegexOptions.None, TimeSpan.FromSeconds(1));
            text = Regex.Replace(text, @"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*)[^\s&;,""'<>]+", "$1" + Hidden, RegexOptions.None, TimeSpan.FromSeconds(1));
            text = Regex.Replace(text, @"(?i)(https?://)[^/\s@]+@", "$1" + Hidden + "@", RegexOptions.None, TimeSpan.FromSeconds(1));
            return Regex.Replace(text, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", Hidden, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
    }
}

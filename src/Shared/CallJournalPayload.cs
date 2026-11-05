using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaOpenness.Shared
{
    // Linked by producers and the reader; sanitization precedes every size limit.
#if TIA_APPROVAL_PRIVATE_JOURNAL
    internal static class ApprovalJournalPayload
#else
    public static class CallJournalPayload
#endif
    {
        public const int Limit = 4096;
        // Digest/display text scans are limited to 4096 UTF-16 code units per string.
        // Larger free text is retained verbatim for hashing (and summarized by Display).
        // Sensitive keys, including keys in embedded JSON, have no size exemption.
        internal const int ArgumentTextLimit = 4096;
        public const string Hidden = "••••";
        public const string Truncated = "\n… [truncated]";
        private static readonly string[] PairMarkers = { "password", "passwd", "pwd", "secret", "token", "key", "credential" };
        internal static bool IsCredentialPairName(string name)
            => PairMarkers.Any(marker => name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        private static readonly JsonSerializerOptions DisplayOptions = new JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string Redact(string json)
            => Bound(Sanitize(json));

        // Hash inputs have no display limits. Do not substitute secrets collected from
        // other positions: that would make an ordinary argument depend on the secret.
        public static JsonNode? RedactArguments(JsonNode? node)
            => TransformArguments(node, _ => JsonValue.Create(Hidden));

        // A single immutable process key protects secrets and invalidates previews on restart.
        // Each call owns its HMAC instance, so concurrent previews share no mutable crypto state.
        private static readonly byte[] PlanSecretKey = CreatePlanSecretKey();
        private static byte[] CreatePlanSecretKey()
        {
            var key = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            return key;
        }
        internal static JsonNode? ProtectPlanArguments(JsonNode? node) => TransformArguments(node, SecretDigest);
        internal static string PlanHostLifetime => SecretDigest(JsonValue.Create("host-lifetime")).GetValue<string>();
        private static JsonNode SecretDigest(JsonNode? node)
        {
            using var hmac = new HMACSHA256(PlanSecretKey);
            return JsonValue.Create("hmac-sha256:" + BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null")))
                .Replace("-", "").ToLowerInvariant())!;
        }
        private static JsonNode? TransformArguments(JsonNode? node, Func<JsonNode?, JsonNode?> secret)
        {
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj);
                var result = new JsonObject();
                foreach (var pair in obj)
                    result[pair.Key] = Sensitive(pair.Key) || named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase)
                        ? secret(pair.Value) : TransformArguments(pair.Value, secret);
                return result;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(item => TransformArguments(item, secret)).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (Embedded(text, out var embedded))
                {
                    var transformed = TransformArguments(embedded, secret);
                    return JsonNode.DeepEquals(embedded, transformed) ? node.DeepClone() : JsonValue.Create(transformed?.ToJsonString());
                }
                return JsonValue.Create(Text(text, new HashSet<string>(StringComparer.Ordinal), 0, boundedText: true,
                    replaceSecret: value => secret(JsonValue.Create(value))!.GetValue<string>()));
            }
            return node?.DeepClone();
        }

        public static string Sanitize(string json) => Sanitize(json, boundedText: false);

        public static string Sanitize(string json, bool boundedText)
        {
            try
            {
                var node = JsonNode.Parse(json);
                var secrets = new HashSet<string>(StringComparer.Ordinal);
                Collect(node, secrets, 0, boundedText);
                if (node is JsonValue value && value.TryGetValue<string>(out var text)) node = JsonValue.Create(Text(text, secrets, 0, boundedText));
                else Scrub(node, secrets, 0, boundedText);
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

        private static void Collect(JsonNode? node, HashSet<string> secrets, int depth, bool boundedText)
        {
            if (depth > 16) return;
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj);
                foreach (var pair in obj)
                    if (Sensitive(pair.Key) || named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase)) Values(pair.Value, secrets, boundedText);
                    else Collect(pair.Value, secrets, depth + 1, boundedText);
            }
            else if (node is JsonArray array) foreach (var child in array) Collect(child, secrets, depth + 1, boundedText);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (boundedText && text.Length > ArgumentTextLimit) return;
                FindSecrets(text, secrets, boundedText);
                if (Embedded(text, out var embedded)) Collect(embedded, secrets, depth + 1, boundedText);
            }
        }

        private static void Values(JsonNode? node, HashSet<string> secrets, bool boundedText)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text != Hidden)
            {
                if (boundedText && text.Length > ArgumentTextLimit) return;
                secrets.Add(text); FindSecrets(text, secrets, boundedText);
            }
            else if (node is JsonValue scalar) secrets.Add(scalar.ToJsonString());
            else if (node is JsonArray array) foreach (var child in array) Values(child, secrets, boundedText);
            else if (node is JsonObject obj) foreach (var pair in obj) Values(pair.Value, secrets, boundedText);
        }

        private static void FindSecrets(string text, HashSet<string> secrets, bool boundedText)
        {
            var timeout = boundedText ? Regex.InfiniteMatchTimeout : TimeSpan.FromSeconds(1);
            var options = boundedText ? RegexOptions.CultureInvariant : RegexOptions.None;
            foreach (Match match in Regex.Matches(text, @"(?i)\b(?:Bearer|Basic)\s+([^\s""'<>\\,;]+)", options, timeout))
                if (match.Groups[1].Value != Hidden) secrets.Add(match.Groups[1].Value);
            if (boundedText) { RedactPairs(text, secrets); return; }
            foreach (Match match in Regex
                    .Matches(text, @"(?i)\b[\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*([^\s&;,""'<>]+)", RegexOptions.None,
                        TimeSpan.FromSeconds(1)))
                if (match.Groups[1].Value != Hidden) secrets.Add(match.Groups[1].Value);
        }

        private static void Scrub(JsonNode? node, HashSet<string> secrets, int depth, bool boundedText)
        {
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj);
                foreach (string key in obj.Select(p => p.Key).ToArray())
                {
                    if (Sensitive(key) || named && key.Equals("value", StringComparison.OrdinalIgnoreCase))
                        obj[key] = key.EndsWith("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " + Hidden : Hidden;
                    else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Text(text, secrets, depth, boundedText);
                    else if (depth >= 16) obj[key] = Hidden;
                    else Scrub(obj[key], secrets, depth + 1, boundedText);
                }
            }
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Text(text, secrets, depth, boundedText);
                    else if (depth >= 16) array[i] = Hidden;
                    else Scrub(array[i], secrets, depth + 1, boundedText);
        }

        private static bool Embedded(string text, out JsonNode? node)
        {
            node = null;
            int start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start == text.Length || text[start] != '{' && text[start] != '[') return false;
            try { node = JsonNode.Parse(text); return true; }
            catch (JsonException) /* swallow(privacy): malformed embedded JSON is hidden by the text boundary */ { return false; }
        }

        private static string Text(string text, HashSet<string> secrets, int depth, bool boundedText, Func<string, string>? replaceSecret = null)
        {
            if (boundedText && text.Length > ArgumentTextLimit) return text;
            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal) || text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                if (depth >= 16 || !Embedded(text, out var embedded)) return replaceSecret == null ? Hidden : replaceSecret(text);
                Scrub(embedded, secrets, depth + 1, boundedText);
                text = embedded?.ToJsonString(DisplayOptions) ?? "null";
            }
            foreach (var secret in secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, Hidden);
            replaceSecret ??= _ => Hidden;
            // Only the bounded approval path uses timeout-free scans. Journal rows keep
            // their existing regexes, timeout and output. Pair names are scanned once:
            // the journal regex retries a whole dotted name at every word boundary.
            var timeout = boundedText ? Regex.InfiniteMatchTimeout : TimeSpan.FromSeconds(1);
            var options = boundedText ? RegexOptions.CultureInvariant : RegexOptions.None;
            text = Regex.Replace(text, @"(?i)\b(Bearer|Basic)\s+[^\s""'<>\\,;]+",
                match => match.Groups[1].Value + " " + replaceSecret(match.Value.Substring(match.Groups[1].Length).TrimStart()), options, timeout);
            text = boundedText ? RedactPairs(text, null, replaceSecret)
                : Regex
                    .Replace(text, @"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*)[^\s&;,""'<>]+",
                        match => match.Groups[1].Value + replaceSecret(match.Value.Substring(match.Groups[1].Length)),
                        RegexOptions.None, timeout);
            text = Regex.Replace(text, @"(?i)(https?://)[^/\s@]+@",
                match => match.Groups[1].Value + replaceSecret(match.Value.Substring(match.Groups[1].Length).TrimEnd('@')) + "@", options, timeout);
            return Regex.Replace(text, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", match => replaceSecret(match.Value), options, timeout);
        }

        private static string RedactPairs(string text, HashSet<string>? secrets, Func<string, string>? replaceSecret = null)
        {
            StringBuilder? output = null;
            int copied = 0, index = 0;
            while (index < text.Length)
            {
                if (!NameCharacter(text[index])) { index++; continue; }
                int start = index;
                while (index < text.Length && NameCharacter(text[index])) index++;
                string name = text.Substring(start, index - start);
                if (!IsCredentialPairName(name)) continue;
                int delimiter = index;
                while (delimiter < text.Length && char.IsWhiteSpace(text[delimiter])) delimiter++;
                if (delimiter == text.Length || text[delimiter] != '=' && text[delimiter] != ':') continue;
                int value = delimiter + 1;
                while (value < text.Length && char.IsWhiteSpace(text[value])) value++;
                int end = value;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && "&;,\"'<>".IndexOf(text[end]) < 0) end++;
                if (end == value) continue;
                string secret = text.Substring(value, end - value);
                if (secrets != null)
                { if (secret != Hidden) secrets.Add(secret); }
                else
                {
                    output ??= new StringBuilder(text.Length);
                    output.Append(text, copied, value - copied).Append(replaceSecret == null ? Hidden : replaceSecret(secret));
                    copied = end;
                }
                index = end;
            }
            return output == null ? text : output.Append(text, copied, text.Length - copied).ToString();
        }

        private static bool NameCharacter(char value)
        {
            if (value == '.' || value == '-') return true;
            switch (char.GetUnicodeCategory(value))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.ConnectorPunctuation: return true;
                default: return false;
            }
        }
    }
}

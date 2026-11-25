using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

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
            => Bound(Sanitize(json, boundedText: false, forDisplay: true));

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
        public static JsonObject ProtectApprovalArguments(JsonObject arguments) => ProtectPlanArguments(arguments)!.AsObject();
        internal static string PlanHostLifetime => SecretDigest(JsonValue.Create("host-lifetime")).GetValue<string>();
        private static JsonNode SecretDigest(JsonNode? node)
        {
            using var hmac = new HMACSHA256(PlanSecretKey);
            return JsonValue.Create("hmac-sha256:" + BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null")))
                .Replace("-", "").ToLowerInvariant())!;
        }
        private static JsonNode? TransformArguments(JsonNode? node, Func<JsonNode?, JsonNode?> secret)
            => TransformArguments(node, secret, Sensitive, guard: false);

        internal static JsonNode? RedactGuardArguments(JsonNode? node)
            => TransformArguments(node, _ => JsonValue.Create("<redacted>"),
                name => IsCredentialName(name) || name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0, guard: true);

        private static string RedactGuardText(string prefix, bool truncated)
            => LooksLikeJson(prefix) ? "<redacted>" : RedactFreeText(prefix, _ => "<redacted>", linearPairs: true, guard: true, truncated: truncated);

        private static JsonNode? TransformArguments(JsonNode? node, Func<JsonNode?, JsonNode?> secret, Func<string, bool> sensitive,
            bool guard)
        {
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj, sensitive);
                var result = new JsonObject();
                foreach (var pair in obj)
                {
                    bool namedValue = named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase);
                    result[pair.Key] = sensitive(pair.Key) || namedValue ? secret(pair.Value) : TransformArguments(pair.Value, secret, sensitive, guard);
                }
                return result;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(item => TransformArguments(item, secret, sensitive, guard)).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (guard && text.Length > ArgumentTextLimit)
                    return JsonValue.Create(RedactGuardText(text.Substring(0, ArgumentTextLimit), truncated: true) + "...");
                if (Embedded(text, out var embedded))
                {
                    var transformed = TransformArguments(embedded, secret, sensitive, guard);
                    return JsonNode.DeepEquals(embedded, transformed) ? node.DeepClone() : JsonValue.Create(transformed?.ToJsonString());
                }
                return JsonValue.Create(guard ? RedactGuardText(text, truncated: false)
                    : Text(text, new HashSet<string>(StringComparer.Ordinal), 0, boundedText: true,
                        replaceSecret: match => secret(JsonValue.Create(match))!.GetValue<string>()));
            }
            return node?.DeepClone();
        }

        public static string Sanitize(string json) => Sanitize(json, boundedText: false);

        public static string Sanitize(string json, bool boundedText) => Sanitize(json, boundedText, forDisplay: false);

        public static string Sanitize(string json, bool boundedText, bool forDisplay)
        {
            try
            {
                var node = JsonNode.Parse(json);
                var secrets = new HashSet<string>(StringComparer.Ordinal);
                Collect(node, secrets, 0, boundedText);
                if (node is JsonValue value && value.TryGetValue<string>(out var text)) node = JsonValue.Create(Text(text, secrets, 0, boundedText));
                else Scrub(node, secrets, 0, boundedText);
                return (forDisplay ? CleanDisplay(node) : node)?.ToJsonString(DisplayOptions) ?? "null";
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is RegexMatchTimeoutException)
            { return Hidden; }
        }

        private static JsonNode? CleanDisplay(JsonNode? node, bool sourceLines = false)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(DisplayText(text, sourceLines));
            if (node is JsonArray array) return new JsonArray(array.Select(item => CleanDisplay(item)).ToArray());
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var pair in obj)
                {
                    string name = DisplayText(pair.Key);
                    string unique = name;
                    for (int suffix = 2; result.ContainsKey(unique); suffix++)
                        unique = name + " [" + suffix.ToString(CultureInfo.InvariantCulture) + "]";
                    // The source comparison renders before/after one line at a time.
                    result.Add(unique, CleanDisplay(pair.Value, pair.Key is "before" or "after"));
                }
                return result;
            }
            return node?.DeepClone();
        }

        public static string DisplayText(string text, bool sourceLines = false)
        {
            StringBuilder? result = null;
            int copied = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (sourceLines && c is '\r' or '\n') continue;
                int code = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : c;
                if (!char.IsControl(c) && code is not (0x061c or 0x200e or 0x200f or >= 0x202a and <= 0x202e
                    or >= 0x2066 and <= 0x206f or >= 0xe0000 and <= 0xe007f)) continue;
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, i - copied).Append("(U+").Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append(')');
                if (code > char.MaxValue) i++;
                copied = i + 1;
            }
            return result == null ? text : result.Append(text, copied, text.Length - copied).ToString();
        }

        public static string Bound(string text)
        {
            if (text.Length <= Limit) return text;
            int count = Limit - Truncated.Length;
            if (char.IsHighSurrogate(text[count - 1])) count--;
            return text.Substring(0, count) + Truncated;
        }

        private static string NormalizeName(string key) => new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static bool PasswordName(string name) => name.Contains("password") || name.Contains("passwd") || name == "pwd" || name == "sk";

        private static bool BroadCredentialName(string name) => name.Contains("secret") || name.Contains("credential") || name.Contains("authorization");

        private static bool ResponseCredentialName(string name)
            => PasswordName(name) || name.Contains("passphrase") || name.EndsWith("apikey", StringComparison.Ordinal)
                || name.EndsWith("accesskey", StringComparison.Ordinal) || name.EndsWith("privatekey", StringComparison.Ordinal)
                || name.EndsWith("secretkey", StringComparison.Ordinal) || name.EndsWith("signingkey", StringComparison.Ordinal)
                || name.EndsWith("clientsecret", StringComparison.Ordinal);

        internal static bool IsResponseCredentialName(string key) => ResponseCredentialName(NormalizeName(key));

        internal static bool IsCredentialName(string key)
        {
            string name = NormalizeName(key);
            return ResponseCredentialName(name) || BroadCredentialName(name);
        }

        internal static bool Sensitive(string key)
        {
            string name = NormalizeName(key);
            if (name == "releasekey") return false;
            return PasswordName(name) || BroadCredentialName(name) || name.Contains("token")
                || name.EndsWith("key", StringComparison.Ordinal) || name.EndsWith("keys", StringComparison.Ordinal);
        }

        private static bool NamedSecret(JsonObject obj, Func<string, bool> sensitive) => obj.Any(p =>
            (p.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("key", StringComparison.OrdinalIgnoreCase))
            && p.Value is JsonValue value && value.TryGetValue<string>(out var name) && sensitive(name));

        private static void Collect(JsonNode? node, HashSet<string> secrets, int depth, bool boundedText)
        {
            if (depth > 16) return;
            if (node is JsonObject obj)
            {
                bool named = NamedSecret(obj, Sensitive);
                foreach (var pair in obj)
                    if (Sensitive(pair.Key) || named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase))
                        Values(pair.Value, secrets, boundedText, named && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase));
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

        private static void Values(JsonNode? node, HashSet<string> secrets, bool boundedText, bool literal = false)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text != Hidden)
            {
                if (boundedText && text.Length > ArgumentTextLimit) return;
                secrets.Add(text); FindSecrets(text, secrets, boundedText);
            }
            else if (node is JsonValue scalar) secrets.Add(scalar.ToJsonString());
            else if (node is JsonArray array) foreach (var child in array) Values(child, secrets, boundedText, literal);
            else if (node is JsonObject obj) foreach (var pair in obj) Values(pair.Value, secrets, boundedText, literal);
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
                bool named = NamedSecret(obj, Sensitive);
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

        private static bool LooksLikeJson(string text)
        {
            int start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            return start < text.Length && text[start] is '{' or '[';
        }

        private static bool Embedded(string text, out JsonNode? node)
        {
            node = null;
            if (!LooksLikeJson(text)) return false;
            try { node = JsonNode.Parse(text); return true; }
            catch (JsonException) /* swallow(privacy): malformed embedded JSON is hidden by the text boundary */ { return false; }
        }

        private static string Text(string text, HashSet<string> secrets, int depth, bool boundedText, Func<string, string>? replaceSecret = null)
        {
            if (boundedText && text.Length > ArgumentTextLimit) return text;
            if (LooksLikeJson(text))
            {
                if (depth >= 16 || !Embedded(text, out var embedded)) return replaceSecret == null ? Hidden : replaceSecret(text);
                Scrub(embedded, secrets, depth + 1, boundedText);
                text = embedded?.ToJsonString(DisplayOptions) ?? "null";
            }
            foreach (var secret in secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, Hidden);
            return RedactFreeText(text, replaceSecret ?? (_ => Hidden), boundedText);
        }

        private static string RedactFreeText(string text, Func<string, string> replaceSecret, bool linearPairs,
            bool guard = false, bool truncated = false)
        {
            // The guard and bounded approval path scan pair names once. Journal rows keep their existing regex and timeout.
            var timeout = linearPairs && !guard ? Regex.InfiniteMatchTimeout : TimeSpan.FromSeconds(1);
            var options = linearPairs ? RegexOptions.CultureInvariant : RegexOptions.None;
#if NET7_0_OR_GREATER
            if (guard) options |= RegexOptions.NonBacktracking;
#endif
            text = Regex.Replace(text, @"(?i)\b(Bearer|Basic)\s+[^\s""'<>\\,;]+",
                match => match.Groups[1].Value + " " + replaceSecret(match.Value.Substring(match.Groups[1].Length).TrimStart()), options, timeout);
            text = linearPairs ? RedactPairs(text, null, replaceSecret, guard)
                : Regex
                    .Replace(text, @"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*)[^\s&;,""'<>]+",
                        match => match.Groups[1].Value + replaceSecret(match.Value.Substring(match.Groups[1].Length)),
                        RegexOptions.None, timeout);
            // A prefix boundary may conceal the closing @ or PEM footer.
            string userInfo = truncated ? @"(?i)(https?://)[^/\s@]+(?:@|\z)" : @"(?i)(https?://)[^/\s@]+@";
            text = Regex.Replace(text, userInfo, match => match.Groups[1].Value
                + replaceSecret(match.Value.Substring(match.Groups[1].Length).TrimEnd('@')) + (match.Value.EndsWith("@", StringComparison.Ordinal) ? "@" : ""),
                options, timeout);
            string privateKey = truncated ? @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|\z)"
                : @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----";
            return Regex
                .Replace(text, privateKey, match => replaceSecret(match.Value), options, timeout);
        }

        internal static int PairValueStart(string text, int offset)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
            if (offset == text.Length || text[offset] != '=' && text[offset] != ':') return -1;
            do { offset++; } while (offset < text.Length && char.IsWhiteSpace(text[offset]));
            return offset;
        }

        private static string RedactPairs(string text, HashSet<string>? secrets, Func<string, string>? replaceSecret = null, bool guard = false)
        {
            StringBuilder? output = null;
            int copied = 0, index = 0;
            while (index < text.Length)
            {
                if (!NameCharacter(text[index])) { index++; continue; }
                int start = index;
                while (index < text.Length && NameCharacter(text[index])) index++;
                string name = text.Substring(start, index - start);
                if (!IsCredentialPairName(name) && !(guard && IsCredentialName(name))) continue;
                int value, end;
                if (guard && start > 0 && text[start - 1] == '<' && index < text.Length && text[index] == '>'
                    && name.Equals("Password", StringComparison.OrdinalIgnoreCase))
                {
                    value = index + 1;
                    end = text.IndexOf("</Password>", value, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) end = text.Length;
                }
                else
                {
                    // A note can contain a JSON member after ordinary prose.
                    bool quotedName = guard && start > 0 && text[start - 1] == '"' && index < text.Length && text[index] == '"';
                    value = PairValueStart(text, quotedName ? index + 1 : index);
                    if (value < 0) continue;
                    if (guard && value < text.Length && text[value] is '"' or '\'')
                    {
                        char quote = text[value++];
                        end = value;
                        while (end < text.Length && text[end] != quote)
                        {
                            if (text[end] == '\\' && end + 1 < text.Length) end++;
                            end++;
                        }
                    }
                    else if (quotedName && value < text.Length && text[value] is '{' or '[')
                    {
                        // A container in an inline fragment can span lines. Hide the rest of this bounded note.
                        end = text.Length;
                    }
                    else
                    {
                        end = value;
                        while (end < text.Length && !char.IsWhiteSpace(text[end]) && "&;,\"'<>".IndexOf(text[end]) < 0) end++;
                        if (guard && end < text.Length && (text[end] == ',' || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                            && text.Substring(value, end - value).Equals("Digest", StringComparison.OrdinalIgnoreCase)))
                            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
                    }
                }
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

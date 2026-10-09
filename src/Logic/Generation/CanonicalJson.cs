using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public static class CanonicalJson
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public const int MaximumDepth = 64;

        public static JsonElement Parse(byte[] utf8, string path = "")
        {
            try
            {
                if (utf8.Length >= 3 && utf8[0] == 0xef && utf8[1] == 0xbb && utf8[2] == 0xbf)
                    throw Failure(path, "encoding", "UTF-8 BOM is not allowed.");
                var json = Utf8.GetString(utf8);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
                var value = document.RootElement.Clone();
                Write(value, new StringBuilder(), path, 0);
                return value;
            }
            catch (Exception ex) when (ex is DecoderFallbackException || ex is JsonException || ex is InvalidOperationException)
            {
                throw Failure(path, "json", ex.Message);
            }
        }

        public static JsonElement Parse(string json, string path = "")
        {
            try { return Parse(Utf8.GetBytes(json), path); }
            catch (EncoderFallbackException ex) { throw Failure(path, "encoding", ex.Message); }
        }

        public static byte[] Encode(JsonElement value)
        {
            var output = new StringBuilder();
            Write(value, output, "", 0);
            return Utf8.GetBytes(output.ToString());
        }

        public static string Normalize(string json) => Utf8.GetString(Encode(Parse(json)));
        public static string Hash(JsonElement value) => "sha256:" + HashBytes(Encode(value));

        public static string HashBytes(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string Pointer(string name) => name.Replace("~", "~0").Replace("/", "~1");
        internal static GenerationValidationException Failure(string path, string rule, string message) =>
            new GenerationValidationException(new[] { new GenerationValidationError(path, rule, message) });

        private static void Write(JsonElement value, StringBuilder output, string path, int depth)
        {
            if (depth > MaximumDepth) throw Failure(path, "depth", "JSON nesting exceeds 64.");
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    output.Append('{');
                    foreach (var property in properties)
                    {
                        var child = path + "/" + Pointer(property.Name);
                        if (!names.Add(property.Name)) throw Failure(child, "duplicate-property", "Duplicate JSON property.");
                        if (names.Count > 1) output.Append(',');
                        WriteString(property.Name, output, child);
                        output.Append(':');
                        Write(property.Value, output, child, depth + 1);
                    }
                    output.Append('}');
                    break;
                case JsonValueKind.Array:
                    output.Append('[');
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (index > 0) output.Append(',');
                        Write(item, output, path + "/" + index.ToString(CultureInfo.InvariantCulture), depth + 1);
                        index++;
                    }
                    output.Append(']');
                    break;
                case JsonValueKind.String: WriteString(value.GetString()!, output, path); break;
                case JsonValueKind.Number: output.Append(Number(value.GetRawText(), path)); break;
                case JsonValueKind.True: output.Append("true"); break;
                case JsonValueKind.False: output.Append("false"); break;
                case JsonValueKind.Null: output.Append("null"); break;
                default: throw Failure(path, "json", "Undefined JSON value.");
            }
        }

        private static void WriteString(string value, StringBuilder output, string path)
        {
            output.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var ch = value[index];
                if (ch == '"' || ch == '\\') output.Append('\\').Append(ch);
                else if (ch < 32) output.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else if (char.IsHighSurrogate(ch))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                        throw Failure(path, "unicode", "Unpaired Unicode surrogate.");
                    output.Append(ch).Append(value[++index]);
                }
                else if (char.IsLowSurrogate(ch)) throw Failure(path, "unicode", "Unpaired Unicode surrogate.");
                else output.Append(ch);
            }
            output.Append('"');
        }

        // Exact decimal normalization avoids floating point and runtime formatting differences.
        internal static string Number(string raw, string path = "")
        {
            var negative = raw[0] == '-';
            var parts = raw.TrimStart('-').Split('e', 'E');
            long exponent = 0;
            if (parts.Length == 2 && (!long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent < -1000000 || exponent > 1000000))
                throw Failure(path, "number", "Decimal exponent must be between -1000000 and 1000000.");
            var point = parts[0].IndexOf('.');
            if (point >= 0) exponent -= parts[0].Length - point - 1;
            var digits = parts[0].Replace(".", "").TrimStart('0');
            if (digits.Length == 0) return "0";
            var trimmed = digits.TrimEnd('0');
            exponent += digits.Length - trimmed.Length;
            if (exponent >= 0 && trimmed.Length + exponent <= 21)
                return (negative ? "-" : "") + trimmed + new string('0', (int)exponent);
            return (negative ? "-" : "") + trimmed + (exponent == 0 ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
        }

        internal static int CompareNumbers(string first, string second)
        {
            var a = Number(first);
            var b = Number(second);
            if (a == b) return 0;
            var aSign = a == "0" ? 0 : a[0] == '-' ? -1 : 1;
            var bSign = b == "0" ? 0 : b[0] == '-' ? -1 : 1;
            if (aSign != bSign) return aSign.CompareTo(bSign);
            var ap = a.TrimStart('-').Split('e');
            var bp = b.TrimStart('-').Split('e');
            var ae = ap.Length == 1 ? 0 : long.Parse(ap[1], CultureInfo.InvariantCulture);
            var be = bp.Length == 1 ? 0 : long.Parse(bp[1], CultureInfo.InvariantCulture);
            var magnitude = (ap[0].Length + ae).CompareTo(bp[0].Length + be);
            if (magnitude != 0) return aSign * magnitude;
            var length = Math.Max(ap[0].Length, bp[0].Length);
            return aSign * string.CompareOrdinal(ap[0].PadRight(length, '0'), bp[0].PadRight(length, '0'));
        }
    }
}

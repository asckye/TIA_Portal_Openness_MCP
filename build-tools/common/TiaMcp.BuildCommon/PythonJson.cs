using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.BuildCommon;

// The baseline fingerprints use Python json.dumps, including its spaces, escapes and float notation.
public static class PythonJson
{
    public static string Dumps(object? value, bool ensureAscii = true, int? indent = null,
        bool compactSeparators = false, bool sortKeys = false)
    {
        var output = new StringBuilder();
        var itemSeparator = compactSeparators || indent.HasValue ? "," : ", ";
        var keySeparator = compactSeparators ? ":" : ": ";
        var width = Math.Max(0, indent ?? 0);
        void Newline(int depth)
        {
            if (indent.HasValue) output.Append('\n').Append(' ', depth * width);
        }
        void Sequence(IEnumerable<object?> items, int depth, char opening, char closing)
        {
            output.Append(opening);
            var first = true;
            foreach (var item in items)
            {
                if (!first) output.Append(itemSeparator);
                Newline(depth + 1);
                Write(item, depth + 1);
                first = false;
            }
            if (!first) Newline(depth);
            output.Append(closing);
        }
        void Mapping(IEnumerable<KeyValuePair<string, object?>> items, int depth)
        {
            output.Append('{');
            var first = true;
            if (sortKeys) items = items.OrderBy(item => item.Key, PythonStringComparer.Instance);
            foreach (var (key, item) in items)
            {
                if (!first) output.Append(itemSeparator);
                Newline(depth + 1);
                Quote(output, key, ensureAscii);
                output.Append(keySeparator);
                Write(item, depth + 1);
                first = false;
            }
            if (!first) Newline(depth);
            output.Append('}');
        }
        void Element(JsonElement element, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Mapping(element.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), depth);
                    break;
                case JsonValueKind.Array: Sequence(element.EnumerateArray().Select(e => (object?)e), depth, '[', ']'); break;
                case JsonValueKind.String: Quote(output, element.GetString()!, ensureAscii); break;
                case JsonValueKind.True: output.Append("true"); break;
                case JsonValueKind.False: output.Append("false"); break;
                case JsonValueKind.Null: output.Append("null"); break;
                case JsonValueKind.Number:
                    var raw = element.GetRawText();
                    output.Append(raw.IndexOfAny(['.', 'e', 'E']) >= 0 ? Float(element.GetDouble()) : BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                default: throw new ArgumentException("Unsupported JSON value");
            }
        }
        void Write(object? item, int depth)
        {
            switch (item)
            {
                case null: output.Append("null"); break;
                case string text: Quote(output, text, ensureAscii); break;
                case bool flag: output.Append(flag ? "true" : "false"); break;
                case double number: output.Append(Float(number)); break;
                case float number: output.Append(Float(number)); break;
                case BigInteger number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    output.Append(Convert.ToString(item, CultureInfo.InvariantCulture)); break;
                case JsonElement element: Element(element, depth); break;
                case JsonObject obj: Mapping(obj.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)), depth); break;
                case JsonArray array: Sequence(array.Select(e => (object?)e), depth, '[', ']'); break;
                case JsonValue value:
                    if (value.TryGetValue<JsonElement>(out var parsedElement)) Element(parsedElement, depth);
                    else if (value.TryGetValue<double>(out var number)) Write(number, depth);
                    else if (value.TryGetValue<float>(out var single)) Write(single, depth);
                    else if (value.TryGetValue<string>(out var text)) Write(text, depth);
                    else if (value.TryGetValue<bool>(out var flag)) Write(flag, depth);
                    else Element(JsonSerializer.SerializeToElement(value), depth);
                    break;
                case IDictionary mapping:
                    Mapping(mapping.Keys.Cast<object>().Select(key => new KeyValuePair<string, object?>(
                        key as string ?? throw new ArgumentException("JSON object keys must be strings"), mapping[key])), depth);
                    break;
                case IEnumerable sequence: Sequence(sequence.Cast<object?>(), depth, '[', ']'); break;
                default: throw new ArgumentException("Unsupported Python JSON type: " + item.GetType().Name);
            }
        }
        Write(value, 0);
        return output.ToString();
    }

    private static void Quote(StringBuilder output, string value, bool ascii)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 32 || ascii && character > 126)
                        output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }

    private static string Float(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        var text = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        var parts = text.Split('e');
        var point = parts[0].IndexOf('.');
        var digits = parts[0].Replace(".", "", StringComparison.Ordinal);
        var exponent = (parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0)
            + (point < 0 ? parts[0].Length : point) - 1;
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; exponent--; }
        digits = digits.TrimEnd('0');
        if (exponent is < -4 or >= 16)
            text = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        else if (exponent < 0) text = "0." + new string('0', -exponent - 1) + digits;
        else if (digits.Length <= exponent + 1) text = digits + new string('0', exponent + 1 - digits.Length) + ".0";
        else text = digits.Insert(exponent + 1, ".");
        return (negative ? "-" : "") + text;
    }
}

// Python compares Unicode scalar values, not UTF-16 code units.
public sealed class PythonStringComparer : IComparer<string>
{
    public static PythonStringComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return x is null ? y is null ? 0 : -1 : 1;
        var a = 0;
        var b = 0;
        while (a < x.Length && b < y.Length)
        {
            var first = Next(x, ref a);
            var second = Next(y, ref b);
            if (first != second) return first.CompareTo(second);
        }
        return (x.Length - a).CompareTo(y.Length - b);
    }
    private static int Next(string value, ref int offset)
    {
        var first = value[offset++];
        if (char.IsHighSurrogate(first) && offset < value.Length && char.IsLowSurrogate(value[offset]))
            return char.ConvertToUtf32(first, value[offset++]);
        return first;
    }
}

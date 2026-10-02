using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using TiaMcp.WorkerProtocol;

namespace TiaMcp.WorkerProtocol.JsonLegacy;

public enum PayloadKind { Object, Array, String, Number, True, False, Null }
public sealed class PayloadProperty
{
    readonly string name;
    public string Name { get { JsonPayload.ValidateScalars(name); return name; } }
    public JsonPayload Value { get; }
    internal PayloadProperty(string name, JsonPayload value) { this.name = name; Value = value; }
}
// Immutable, dependency-neutral interchange seam. Modern adapters can parse ToJson()
// into their own DOM; no JsonElement crosses the net461 API boundary.
public sealed class JsonPayload
{
    public PayloadKind ValueKind { get; }
    readonly string? scalar;
    readonly List<PayloadProperty>? properties;
    readonly List<JsonPayload>? items;
    JsonPayload(PayloadKind kind, string? scalar = null, List<PayloadProperty>? properties = null, List<JsonPayload>? items = null)
    { ValueKind = kind; this.scalar = scalar; this.properties = properties; this.items = items; }
    public bool GetBoolean() { if (ValueKind != PayloadKind.True && ValueKind != PayloadKind.False) throw new InvalidOperationException(); return ValueKind == PayloadKind.True; }
    public JsonPayload Clone() => this;
    public string? GetString() { if (ValueKind == PayloadKind.Null) return null; Kind(PayloadKind.String); ValidateScalars(scalar!); return scalar; }
    public bool TryGetInt64(out long value) { value = 0; return ValueKind == PayloadKind.Number && long.TryParse(scalar, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value); }
    public bool TryGetInt32(out int value) { value = 0; return ValueKind == PayloadKind.Number && int.TryParse(scalar, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value); }
    public long GetInt64() { if (!TryGetInt64(out long value)) throw new FormatException(); return value; }
    public bool TryGetProperty(string key, out JsonPayload value)
    { Kind(PayloadKind.Object); foreach (var p in properties!) if (p.Name == key) { value = p.Value; return true; } value = null!; return false; }
    public JsonPayload GetProperty(string key) => TryGetProperty(key, out var value) ? value : throw new KeyNotFoundException();
    public IEnumerable<PayloadProperty> EnumerateObject() { Kind(PayloadKind.Object); return properties!.ToArray(); }
    public IEnumerable<JsonPayload> EnumerateArray() { Kind(PayloadKind.Array); return items!.ToArray(); }
    void Kind(PayloadKind kind) { if (ValueKind != kind) throw new InvalidOperationException(); }
    public static JsonPayload Parse(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (new UTF8Encoding(false, true).GetByteCount(json) > StrictCodec.MaxFrameBytes) throw new IdentityViolation("FrameSizeInvalid");
        var parser = new Parser(json); var result = parser.Value(0); parser.Space();
        if (!parser.End) throw new JsonReaderException(); return result;
    }
    public string ToJson() { var b = new StringBuilder(); Write(b); return b.ToString(); }
    void Write(StringBuilder b)
    {
        switch (ValueKind)
        {
            case PayloadKind.Object:
                b.Append('{'); bool first = true;
                foreach (var p in properties!) { if (!first) b.Append(','); first = false; Quote(b, p.Name); b.Append(':'); p.Value.Write(b); } b.Append('}'); break;
            case PayloadKind.Array:
                b.Append('['); for (int i = 0; i < items!.Count; i++) { if (i != 0) b.Append(','); items[i].Write(b); } b.Append(']'); break;
            case PayloadKind.String: Quote(b, scalar!); break;
            default: b.Append(scalar); break;
        }
    }
    internal static void ValidateScalars(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new InvalidOperationException(); }
            else if (char.IsLowSurrogate(value[i])) throw new InvalidOperationException();
        }
    }
    static void Quote(StringBuilder b, string text)
    {
        ValidateScalars(text);
        b.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e || c == '"' || c == '\'' || c == '&' || c == '<' || c == '>' || c == '+' || c == '`')
                        b.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }
    sealed class Parser
    {
        readonly string text; int at;
        public Parser(string text) { this.text = text; }
        public bool End => at == text.Length;
        public void Space() { while (!End && (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n')) at++; }
        bool Take(char c) { if (!End && text[at] == c) { at++; return true; } return false; }
        void Need(char c) { if (!Take(c)) throw new JsonReaderException(); }
        public JsonPayload Value(int depth)
        {
            Space(); if (End) throw new JsonReaderException();
            if (text[at] == '{' || text[at] == '[')
            {
                if (depth >= 32) throw new JsonReaderException();
                if (Take('{'))
                {
                    var list = new List<PayloadProperty>(); Space();
                    if (!Take('}')) { do { Space(); string name = String(); Space(); Need(':'); list.Add(new PayloadProperty(name, Value(depth + 1))); Space(); if (Take('}')) break; Need(','); } while (true); }
                    return new JsonPayload(PayloadKind.Object, properties: list);
                }
                Need('['); var array = new List<JsonPayload>(); Space();
                if (!Take(']')) { do { array.Add(Value(depth + 1)); Space(); if (Take(']')) break; Need(','); } while (true); }
                return new JsonPayload(PayloadKind.Array, items: array);
            }
            if (text[at] == '"') return new JsonPayload(PayloadKind.String, String());
            foreach (var pair in new[] { new KeyValuePair<string, PayloadKind>("null", PayloadKind.Null), new KeyValuePair<string, PayloadKind>("true", PayloadKind.True), new KeyValuePair<string, PayloadKind>("false", PayloadKind.False) })
                if (text.Length - at >= pair.Key.Length && string.CompareOrdinal(text, at, pair.Key, 0, pair.Key.Length) == 0) { at += pair.Key.Length; return new JsonPayload(pair.Value, pair.Key); }
            int start = at; Take('-');
            if (!Take('0')) { if (End || text[at] < '1' || text[at] > '9') throw new JsonReaderException(); Digits(); }
            if (Take('.')) { int p = at; Digits(); if (p == at) throw new JsonReaderException(); }
            if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); int p = at; Digits(); if (p == at) throw new JsonReaderException(); }
            return new JsonPayload(PayloadKind.Number, text.Substring(start, at - start));
        }
        void Digits() { while (!End && text[at] >= '0' && text[at] <= '9') at++; }
        string String()
        {
            int start = at; Need('"'); var decoded = new StringBuilder();
            while (!End)
            {
                char c = text[at++]; if (c == '"')
                {
                    // Preserve invalid scalar text until traversal, matching the modern
                    // DOM's deferred validation and duplicate-field error precedence.
                    string value = decoded.ToString();
                    try { ValidateScalars(value); } catch (InvalidOperationException) { return value; }
                    using var input = new StringReader(text.Substring(start, at - start));
                    using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
                    if (!reader.Read() || reader.TokenType != JsonToken.String) throw new JsonReaderException();
                    return (string)reader.Value!;
                }
                if (c < 0x20) throw new JsonReaderException();
                if (c == '\\')
                {
                    if (End) throw new JsonReaderException(); c = text[at++];
                    switch (c)
                    {
                        case 'u':
                            int value = 0;
                            for (int j = 0; j < 4; j++) { if (End) throw new JsonReaderException(); char h = text[at++]; int n = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1; if (n < 0) throw new JsonReaderException(); value = value * 16 + n; }
                            c = (char)value; break;
                        case '"': case '\\': case '/': break;
                        case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                        default: throw new JsonReaderException();
                    }
                }
                decoded.Append(c);
            }
            throw new JsonReaderException();
        }
    }
}
internal sealed class PayloadConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(JsonPayload);
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => writer.WriteRawValue(((JsonPayload)value!).ToJson());
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) => throw new NotSupportedException();
    public override bool CanRead => false;
}

internal static class NewtonsoftBridge
{
    internal static string Serialize(object body)
    {
        // Create, rather than CreateDefault/JsonConvert: process-wide defaults must
        // never opt this protocol into type metadata, custom converters or dates.
        var serializer = JsonSerializer.Create(new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, DateParseHandling = DateParseHandling.None,
            Culture = CultureInfo.InvariantCulture, Formatting = Formatting.None,
            Converters = new List<JsonConverter> { new PayloadConverter() }
        });
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var writer = new JsonTextWriter(output);
        serializer.Serialize(writer, body); writer.Flush(); return output.ToString();
    }
}

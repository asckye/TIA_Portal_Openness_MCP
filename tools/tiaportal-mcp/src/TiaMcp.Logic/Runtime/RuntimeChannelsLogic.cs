using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Runtime
{
    // Pure request-building / response-parsing logic for the two runtime channels
    // that do NOT go through TIA Openness:
    //   A. SIMATIC S7 Web server API (HTTPS JSON-RPC on the CPU, via the official
    //      Siemens.Simatic.S7.Webserver.API client)
    //   B. WinCC Unified Open Pipe (local named pipe \\.\pipe\HmiRuntime, line-based JSON)
    // Nothing in here touches the network, so it can be linked into the offline test
    // project. Every refusal (bad input, unsupported value shape) is thrown here, so
    // the channel implementations only ever see validated inputs.

    public sealed class RuntimeWriteItem
    {
        public string Name = "";
        public JsonNode? Value;          // JSON as given by the caller (bool/number/string)
        public object ClrValue = "";     // bool / long / double / string for the S7 Web API client
        public string ValueKind = "";    // bool / integer / number / string
    }

    public sealed class OpenPipeTagResult
    {
        public string Name = "";
        public string? Value;
        public string? Quality;
        public string? QualityCode;
        public string? TimeStamp;
        public long ErrorCode;
        public string ErrorDescription = "";
        public bool Ok => ErrorCode == 0;
    }

    public sealed class OpenPipeResponse
    {
        public string Message = "";
        public string? ClientCookie;
        public bool IsError;             // Message starts with "Error"
        public long ErrorCode;
        public string ErrorDescription = "";
        public List<OpenPipeTagResult> Tags = new List<OpenPipeTagResult>();
        public JsonNode? Params;         // raw Params/params node for non-tag responses (alarms, browse)
        public JsonNode? Raw;
    }

    public static class RuntimeChannelsLogic
    {
        public const string DefaultOpenPipeName = @"\\.\pipe\HmiRuntime";
        public const int MaxItemsPerCall = 500;

        // Requests go out as literal UTF-8 (the pipe protocol is UTF-8 by definition);
        // the default encoder would turn Chinese tag names and quotes into \uXXXX escapes.
        private static readonly JsonWriterOptions WireOptions = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        };

        public static string ToWireJson(JsonNode node)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WireOptions)) node.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        // --- shared ---------------------------------------------------------------

        // JSON array of strings, or a comma/semicolon/newline separated list. Empty
        // entries are refused, duplicates are refused (a duplicate in a write list is
        // almost always a copy/paste mistake and would make readback ambiguous).
        public static List<string> ParseNameList(string? json, string paramName)
        {
            var list = new List<string>();
            string s = (json ?? "").Trim();
            if (s.Length == 0) throw new ArgumentException($"{paramName} is empty; give a JSON array of names, e.g. [\"\\\"DB1\\\".\\\"Speed\\\"\"].");
            if (s.StartsWith("["))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(s); }
                catch (JsonException ex) { throw new ArgumentException($"{paramName} is not valid JSON: {ex.Message}"); }
                if (node is not JsonArray arr) throw new ArgumentException($"{paramName} must be a JSON array of strings.");
                foreach (var n in arr)
                {
                    if (n is not JsonValue v || !v.TryGetValue<string>(out var str))
                        throw new ArgumentException($"{paramName}: every entry must be a JSON string (got '{n?.ToJsonString() ?? "null"}').");
                    list.Add(str.Trim());
                }
            }
            else if (s.StartsWith("{")) throw new ArgumentException($"{paramName} must be a JSON array of strings, not an object.");
            else
            {
                foreach (var part in s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    list.Add(part.Trim());
            }
            if (list.Count == 0) throw new ArgumentException($"{paramName} contains no names.");
            if (list.Any(x => x.Length == 0)) throw new ArgumentException($"{paramName} contains an empty name.");
            if (list.Count > MaxItemsPerCall) throw new ArgumentException($"{paramName} has {list.Count} entries; at most {MaxItemsPerCall} per call.");
            var dup = list.GroupBy(x => x, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"{paramName} lists '{dup.Key}' more than once.");
            return list;
        }

        // Writes: JSON array of {"name":..., "value":...} objects, or a JSON object
        // {"name": value, ...}. Values must be JSON bool / number / string; null, arrays
        // and objects are refused (structs/arrays are not supported by these tools).
        public static List<RuntimeWriteItem> ParseWriteList(string? json, string paramName)
        {
            string s = (json ?? "").Trim();
            if (s.Length == 0) throw new ArgumentException($"{paramName} is empty; give [{{\"name\":\"...\",\"value\":...}}] or {{\"name\":value}}.");
            JsonNode? node;
            try { node = JsonNode.Parse(s); }
            catch (JsonException ex) { throw new ArgumentException($"{paramName} is not valid JSON: {ex.Message}"); }

            var items = new List<RuntimeWriteItem>();
            if (node is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) throw new ArgumentException($"{paramName}: every array entry must be an object with 'name' and 'value'.");
                    var nameNode = o["name"] ?? o["Name"];
                    if (nameNode is not JsonValue nv || !nv.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException($"{paramName}: entry without a non-empty string 'name'.");
                    if (!o.ContainsKey("value") && !o.ContainsKey("Value"))
                        throw new ArgumentException($"{paramName}: entry '{name}' has no 'value'.");
                    items.Add(MakeWriteItem(name.Trim(), o["value"] ?? o["Value"], paramName));
                }
            }
            else if (node is JsonObject map)
            {
                foreach (var kv in map)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) throw new ArgumentException($"{paramName}: empty tag name.");
                    items.Add(MakeWriteItem(kv.Key.Trim(), kv.Value, paramName));
                }
            }
            else throw new ArgumentException($"{paramName} must be a JSON array of {{name,value}} objects or a JSON object.");

            if (items.Count == 0) throw new ArgumentException($"{paramName} contains no writes.");
            if (items.Count > MaxItemsPerCall) throw new ArgumentException($"{paramName} has {items.Count} entries; at most {MaxItemsPerCall} per call.");
            var dup = items.GroupBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"{paramName} writes '{dup.Key}' more than once; readback would be ambiguous.");
            return items;
        }

        private static RuntimeWriteItem MakeWriteItem(string name, JsonNode? value, string paramName)
        {
            if (value == null) throw new ArgumentException($"{paramName}: '{name}' has a null value; only bool, number or string values can be written.");
            if (value is not JsonValue v) throw new ArgumentException($"{paramName}: '{name}' has an array/object value; structs and arrays are not supported by this tool, write the members individually.");
            var item = new RuntimeWriteItem { Name = name, Value = value.DeepClone() };
            if (v.TryGetValue<bool>(out var b)) { item.ClrValue = b; item.ValueKind = "bool"; }
            else if (v.TryGetValue<long>(out var l)) { item.ClrValue = l; item.ValueKind = "integer"; }
            else if (v.TryGetValue<double>(out var d)) { item.ClrValue = d; item.ValueKind = "number"; }
            else if (v.TryGetValue<string>(out var str)) { item.ClrValue = str; item.ValueKind = "string"; }
            else throw new ArgumentException($"{paramName}: '{name}' has an unsupported value '{v.ToJsonString()}'.");
            return item;
        }

        // Lenient value comparison used for post-write readback: bool vs bool, numbers
        // with a relative tolerance (REAL round-trips are not bit-exact through JSON),
        // strings ordinal, and "TRUE"/"1"-style strings from Open Pipe against JSON scalars.
        public static bool ValuesMatch(JsonNode? requested, JsonNode? actual)
        {
            if (requested == null || actual == null) return false;
            if (requested is not JsonValue r || actual is not JsonValue a) return requested.ToJsonString() == actual.ToJsonString();
            string rs = ScalarText(r), @as = ScalarText(a);
            if (bool.TryParse(rs, out var rb) && bool.TryParse(@as, out var ab)) return rb == ab;
            if (double.TryParse(rs, NumberStyles.Float, CultureInfo.InvariantCulture, out var rd) &&
                double.TryParse(@as, NumberStyles.Float, CultureInfo.InvariantCulture, out var ad))
            {
                if (bool.TryParse(rs, out _) || bool.TryParse(@as, out _)) return false;
                double tol = Math.Max(Math.Abs(rd), Math.Abs(ad)) * 1e-6;
                return Math.Abs(rd - ad) <= Math.Max(tol, 1e-9);
            }
            // Open Pipe reports BOOL as "TRUE"/"FALSE" or "1"/"0"; treat those as equivalent to a JSON bool.
            if (bool.TryParse(rs, out rb) && (@as == "1" || @as == "0")) return rb == (@as == "1");
            if (bool.TryParse(@as, out ab) && (rs == "1" || rs == "0")) return ab == (rs == "1");
            return string.Equals(rs, @as, StringComparison.Ordinal);
        }

        private static string ScalarText(JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
            return v.ToJsonString();
        }

        // Convert a CLR scalar coming back from the S7 Web API client (Newtonsoft primitive)
        // into a System.Text.Json node. Non-scalars are re-serialised via their JSON text.
        public static JsonNode? ToJsonNode(object? value, Func<object, string>? serializeComplex = null)
        {
            switch (value)
            {
                case null: return null;
                case bool b: return JsonValue.Create(b);
                case string s: return JsonValue.Create(s);
                case sbyte or byte or short or ushort or int or uint or long:
                    return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case ulong ul: return JsonValue.Create(ul);
                case float f: return JsonValue.Create((double)f);
                case double d: return JsonValue.Create(d);
                case decimal m: return JsonValue.Create((double)m);
                case JsonNode n: return n.DeepClone();
                default:
                    if (serializeComplex != null)
                    {
                        try { return JsonNode.Parse(serializeComplex(value)); } catch { /* fall through */ }
                    }
                    return JsonValue.Create(value.ToString());
            }
        }

        // --- A. S7 Web server API --------------------------------------------------

        // Host may be an IP/DNS name with optional :port. A scheme is refused (the client
        // always uses https), as is any path. Returns the normalised "host[:port]".
        public static string NormalizeS7WebHost(string? host)
        {
            string h = (host ?? "").Trim();
            if (h.Length == 0) throw new ArgumentException("host is empty; give the CPU web server address, e.g. '192.168.0.1' or 'plc.local:443'.");
            if (h.Contains("://")) throw new ArgumentException("host must not contain a scheme; the S7 Web API is always https. Give '192.168.0.1' or 'host:port'.");
            if (h.Contains("/") || h.Contains("\\") || h.Contains(" ") || h.Contains("?") || h.Contains("#"))
                throw new ArgumentException("host must be a bare host name or IP with optional :port (no path, no spaces).");
            int colon = h.LastIndexOf(':');
            if (colon > 0 && !h.StartsWith("["))
            {
                if (!int.TryParse(h.Substring(colon + 1), out int port) || port < 1 || port > 65535)
                    throw new ArgumentException("host port must be 1..65535.");
            }
            return h;
        }

        public static string S7WebSessionKey(string host, string username, bool ignoreCertificateErrors)
            => host.ToLowerInvariant() + "|" + username + "|" + (ignoreCertificateErrors ? "insecure" : "verified");

        // Only run/stop can be requested through Plc.RequestChangeOperatingMode.
        public static string NormalizeOperatingModeRequest(string? mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            if (m == "run" || m == "stop") return m;
            throw new ArgumentException("mode must be 'run' or 'stop' (the Web API can only request these two operating modes).");
        }

        // Does the observed mode satisfy the request? "run" is also satisfied by the
        // redundant variants; "stop" only by stop (a firmware-update stop is not a plain stop).
        public static bool OperatingModeSatisfies(string requested, string? observed)
        {
            string o = (observed ?? "").Trim().ToLowerInvariant();
            return requested == "run" ? (o == "run" || o == "run_redundant") : o == "stop";
        }

        public static int ClampTimeout(int timeoutMs, int min = 500, int max = 120000, int fallback = 5000)
        {
            if (timeoutMs <= 0) return fallback;
            return Math.Min(Math.Max(timeoutMs, min), max);
        }

        // --- B. WinCC Unified Open Pipe --------------------------------------------

        // Accepts "\\.\pipe\HmiRuntime" (Windows form from the manual) or the bare pipe
        // name "HmiRuntime". Open Pipe is local-only by design, so a remote server in
        // the path (\\host\pipe\x) is refused rather than silently connected.
        public static (string server, string name) ParsePipeName(string? pipeName)
        {
            string p = (pipeName ?? "").Trim();
            if (p.Length == 0) p = DefaultOpenPipeName;
            if (p.StartsWith(@"\\"))
            {
                var parts = p.Substring(2).Split(new[] { '\\' }, StringSplitOptions.None);
                if (parts.Length != 3 || !parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) || parts[2].Length == 0)
                    throw new ArgumentException(@"pipeName must look like \\.\pipe\HmiRuntime or just HmiRuntime.");
                if (parts[0] != ".")
                    throw new ArgumentException("pipeName refers to a remote machine; WinCC Unified Open Pipe is a local interface only (run this MCP server on the Runtime PC).");
                return (".", parts[2]);
            }
            if (p.Contains("\\") || p.Contains("/")) throw new ArgumentException(@"pipeName must look like \\.\pipe\HmiRuntime or just HmiRuntime.");
            return (".", p);
        }

        public static string NewClientCookie(string purpose) => "tiamcp-" + purpose + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        // {"Message":"ReadTag","Params":{"Tags":["Tag_0","Tag_1"]},"ClientCookie":"..."}  (manual 3.2.3 / 5.2.3)
        public static string BuildReadTagRequest(IEnumerable<string> tags, string clientCookie)
        {
            var arr = new JsonArray();
            foreach (var t in tags) arr.Add(t);
            var req = new JsonObject
            {
                ["Message"] = "ReadTag",
                ["Params"] = new JsonObject { ["Tags"] = arr },
                ["ClientCookie"] = clientCookie
            };
            return ToWireJson(req);
        }

        // {"Message":"WriteTag","Params":{"Tags":[{"Name":"Tag_0","Value":"50"}]},"ClientCookie":"..."}  (manual 5.2.4, 11/2023;
        // the 2019 edition printed "TagName" instead of "Name" — the current manual and the Siemens Node-RED nodes use "Name").
        public static string BuildWriteTagRequest(IEnumerable<RuntimeWriteItem> writes, string clientCookie)
        {
            var arr = new JsonArray();
            foreach (var w in writes)
                arr.Add(new JsonObject { ["Name"] = w.Name, ["Value"] = w.Value?.DeepClone() });
            var req = new JsonObject
            {
                ["Message"] = "WriteTag",
                ["Params"] = new JsonObject { ["Tags"] = arr },
                ["ClientCookie"] = clientCookie
            };
            return ToWireJson(req);
        }

        // {"Message":"ReadAlarm","Params":{"SystemNames":[...],"Filter":"...","LanguageId":1033},"ClientCookie":"..."}  (manual 5.2.7)
        public static string BuildReadAlarmRequest(IEnumerable<string> systemNames, string filter, int languageId, string clientCookie)
        {
            var arr = new JsonArray();
            foreach (var s in systemNames) arr.Add(s);
            var req = new JsonObject
            {
                ["Message"] = "ReadAlarm",
                ["Params"] = new JsonObject { ["SystemNames"] = arr, ["Filter"] = filter ?? "", ["LanguageId"] = languageId },
                ["ClientCookie"] = clientCookie
            };
            return ToWireJson(req);
        }

        // Validates a caller-supplied raw Open Pipe request and returns the single line to send
        // (ClientCookie is added when missing, since the expert syntax requires one).
        // Subscribe*/Unsubscribe* are refused: they stream notifications for the life of the
        // pipe, which a one-shot request/response tool cannot represent honestly.
        public static (string line, string message, string cookie, bool isReadOnly) PrepareRawRequest(string? requestJson)
        {
            string s = (requestJson ?? "").Trim();
            if (s.Length == 0) throw new ArgumentException("requestJson is empty.");
            if (s.Contains("\n") || s.Contains("\r")) throw new ArgumentException("requestJson must be a single-line JSON object (Open Pipe messages are newline-terminated).");
            JsonNode? node;
            try { node = JsonNode.Parse(s); }
            catch (JsonException ex) { throw new ArgumentException("requestJson is not valid JSON: " + ex.Message); }
            if (node is not JsonObject o) throw new ArgumentException("requestJson must be a JSON object with a 'Message' property.");
            var msgNode = o["Message"];
            if (msgNode is not JsonValue mv || !mv.TryGetValue<string>(out var message) || string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("requestJson needs a non-empty string 'Message' (e.g. ReadTag, WriteTag, ReadAlarm, BrowseTags).");
            if (message.StartsWith("Subscribe", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Unsubscribe", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"'{message}' opens a streaming subscription; this one-shot tool does not support subscriptions.");
            string cookie;
            if (o["ClientCookie"] is JsonValue cv && cv.TryGetValue<string>(out var given) && !string.IsNullOrWhiteSpace(given)) cookie = given;
            else { cookie = NewClientCookie("raw"); o["ClientCookie"] = cookie; }
            return (ToWireJson(o), message, cookie, IsReadOnlyOpenPipeMessage(message));
        }

        public static bool IsReadOnlyOpenPipeMessage(string message)
        {
            var readOnly = new[] { "ReadTag", "ReadAlarm", "ReadConfig", "BrowseTags", "BrowseConfiguredAlarms", "BrowseAlarmClasses" };
            return readOnly.Any(m => string.Equals(m, message, StringComparison.OrdinalIgnoreCase));
        }

        // Parses one response line. Returns null when the line belongs to another cookie
        // (a stray notification), so the transport keeps reading.
        public static OpenPipeResponse? ParseResponseLine(string line, string expectedCookie)
        {
            string s = (line ?? "").Trim();
            if (s.Length == 0) return null;
            JsonNode? node;
            try { node = JsonNode.Parse(s); }
            catch (JsonException ex) { throw new FormatException("Open Pipe returned a line that is not JSON: " + ex.Message + " | " + Truncate(s, 200)); }
            if (node is not JsonObject o) throw new FormatException("Open Pipe returned a non-object JSON line: " + Truncate(s, 200));
            var resp = new OpenPipeResponse { Raw = o };
            resp.Message = Str(o["Message"]) ?? "";
            resp.ClientCookie = Str(o["ClientCookie"]) ?? Str(o["clientCookie"]);
            if (resp.ClientCookie != null && !string.Equals(resp.ClientCookie, expectedCookie, StringComparison.Ordinal)) return null;
            resp.IsError = resp.Message.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
            resp.ErrorCode = Num(o["ErrorCode"]);
            resp.ErrorDescription = Str(o["ErrorDescription"]) ?? "";
            // The manual shows "Params" for tag responses and "params" for alarm responses.
            resp.Params = o["Params"] ?? o["params"];
            if (resp.Params is JsonObject p && p["Tags"] is JsonArray tags)
            {
                foreach (var t in tags)
                {
                    if (t is not JsonObject to) continue;
                    resp.Tags.Add(new OpenPipeTagResult
                    {
                        Name = Str(to["Name"]) ?? Str(to["TagName"]) ?? "",
                        Value = Str(to["Value"]),
                        Quality = Str(to["Quality"]),
                        QualityCode = Str(to["QualityCode"]),
                        TimeStamp = Str(to["TimeStamp"]),
                        ErrorCode = Num(to["ErrorCode"]),
                        ErrorDescription = Str(to["ErrorDescription"]) ?? ""
                    });
                }
            }
            return resp;
        }

        // Whether the response is the one we asked for ("Notify<Cmd>" or "Error<Cmd>").
        public static bool IsResponseFor(OpenPipeResponse resp, string command)
            => string.Equals(resp.Message, "Notify" + command, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resp.Message, "Error" + command, StringComparison.OrdinalIgnoreCase);

        private static string? Str(JsonNode? n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "TRUE" : "FALSE";
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
            return v.ToJsonString();
        }

        private static long Num(JsonNode? n)
        {
            if (n is not JsonValue v) return 0;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
            return 0;
        }

        public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}

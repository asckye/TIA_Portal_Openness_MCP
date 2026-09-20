using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // The escape hatch that lets the lite roster be the default without losing anything.
    //
    // Shipping all 212 tools costs ~40k tokens of JSON schema in every single turn and
    // exceeds what Copilot (128) and Windsurf (100) will even load. Shipping only the ~48
    // lite tools fixes that but used to be a dead end: a model in lite could not reach
    // ExportPlcWatchTable at all, and had no way to find out it existed.
    //
    // FindTools + CallTool close that gap: two tools (~700 tokens) buy on-demand access to
    // the entire roster. The model searches when it needs something the roster lacks, reads
    // just that one signature, and calls it. This is the progressive-disclosure / tool-search
    // pattern that Anthropic, VS Code and the agent gateways all converged on during 2025-26.
    public static partial class McpServer
    {
        // name -> the static method carrying [McpServerTool]. Built once; ~212 entries.
        private static Dictionary<string, MethodInfo>? _allToolMethods;

        private static Dictionary<string, MethodInfo> AllToolMethods()
        {
            if (_allToolMethods != null) return _allToolMethods;
            var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                map[attr.Name ?? m.Name] = m;
            }
            _allToolMethods = map;
            return map;
        }

        private static string ToolDescription(MethodInfo m)
        {
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            return d == null ? "" : d.Description;
        }

        /// <summary>Renders one tool's signature the way the model needs to call it through CallTool.</summary>
        // Name-based on purpose: the offline test project links this file without the MCP SDK.
        internal static bool IsInfrastructureParameter(Type type)
            => type.Name == "IMcpServer" || (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("RequestContext", StringComparison.Ordinal))
               || (type.Namespace != null && type.Namespace.StartsWith("ModelContextProtocol", StringComparison.Ordinal));

        private static string RenderSignature(string name, MethodInfo m)
        {
            var parts = new List<string>();
            foreach (var p in m.GetParameters())
            {
                string t = FriendlyTypeName(p.ParameterType);
                // Optional params are what a model most often gets wrong, so show the actual
                // default rather than a bare "?".
                if (!p.HasDefaultValue) { parts.Add(p.Name + ": " + t); continue; }
                string def;
                if (p.DefaultValue == null) def = "null";
                else if (p.DefaultValue is bool) def = ((bool)p.DefaultValue) ? "true" : "false";
                else if (p.DefaultValue is string) def = "\"" + p.DefaultValue + "\"";
                else def = Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                parts.Add(p.Name + "?: " + t + " = " + def);
            }
            return name + "(" + string.Join(", ", parts) + ")";
        }

        private static string FriendlyTypeName(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return "string";
            if (u == typeof(bool)) return "boolean";
            if (u == typeof(int) || u == typeof(long)) return "integer";
            if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return "number";
            if (u.IsArray) return FriendlyTypeName(u.GetElementType()!) + "[]";
            return u.Name;
        }

        [McpServerTool(Name = "ListToolCategories"), Description(
            "[L0][Meta][READ] The tool taxonomy: 7 categories (session, project, plc, plc-online, hardware, hmi, runtime), " +
            "their domains (the [L?][Domain] tag every tool description starts with), the meaning of layers L0/L1/L2, and live tool counts per category/domain/operation. " +
            "Call this first to orient, then FindTools(category=… or domain=…) to browse one area.")]
        public static ResponseStringList ListToolCategories()
        {
            try
            {
                var all = AllToolMethods();
                var parsed = all.Select(kv => (Name: kv.Key, Tag: ToolTaxonomy.Parse(ToolDescription(kv.Value)), Op: ToolTaxonomy.OperationOf(kv.Key, ToolDescription(kv.Value)))).ToList();
                var lines = new List<string>();
                var categories = new JsonArray();
                foreach (var c in ToolTaxonomy.Categories)
                {
                    var inCategory = parsed.Where(p => ToolTaxonomy.CategoryOf(p.Tag.Domain) == c.Key).ToList();
                    lines.Add($"{c.Key} — {c.NameZh} / {c.NameEn} ({inCategory.Count} tools): {c.Description}");
                    var domains = new JsonArray();
                    foreach (var d in c.Domains)
                    {
                        var inDomain = inCategory.Where(p => string.Equals(p.Tag.Domain, d, StringComparison.OrdinalIgnoreCase)).ToList();
                        lines.Add($"    [{d}] {inDomain.Count} tools; layers " + string.Join("/", inDomain.GroupBy(p => p.Tag.Layer).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count())));
                        domains.Add(new JsonObject { ["domain"] = d, ["tools"] = inDomain.Count,
                            ["operations"] = new JsonObject(inDomain.GroupBy(p => p.Op.Operation).OrderBy(g => g.Key).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))), ["operationsInferred"] = inDomain.Count(p => p.Op.Inferred) });
                    }
                    categories.Add(new JsonObject { ["key"] = c.Key, ["nameZh"] = c.NameZh, ["nameEn"] = c.NameEn, ["description"] = c.Description, ["tools"] = inCategory.Count, ["domains"] = domains });
                }
                var unknown = parsed.Where(p => !ToolTaxonomy.IsKnownDomain(p.Tag.Domain)).Select(p => p.Name + "(" + p.Tag.Domain + ")").ToList();
                var meta = BridgeMeta(true);
                meta["categories"] = categories;
                meta["layers"] = new JsonObject(ToolTaxonomy.LayerMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["operations"] = new JsonObject(ToolTaxonomy.OperationMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["uncategorized"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
                meta["toolCount"] = all.Count;
                return new ResponseStringList
                {
                    Message = $"{all.Count} tools in {ToolTaxonomy.Categories.Count} categories" + (unknown.Count > 0 ? $"; {unknown.Count} tool(s) carry an unregistered domain tag: {string.Join(", ", unknown)}" : "") + ". Use FindTools(category=…) or FindTools(domain=…) to list one area.",
                    Items = lines,
                    Meta = meta,
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "ListToolCategories failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [McpServerTool(Name = "FindTools"), Description(
            "[L0][Meta][READ] Search the FULL tool roster, including tools not listed in this session. " +
            "The server ships a small 'lite' roster by default so every host can load it; everything else is reached through this tool plus CallTool. " +
            "USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. " +
            "Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. " +
            "Optionally restrict to one category (session/project/plc/plc-online/hardware/hmi/runtime) or one domain tag (e.g. HMI-Unified, PLC-Online) — see ListToolCategories. " +
            "Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool.")]
        public static ResponseStringList FindTools(
            [Description("query: space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster (or the whole category/domain when one is given).")] string query = "",
            [Description("limit: max tools to return (default 12). Raise it for a broad survey.")] int limit = 12,
            [Description("category: optional category key from ListToolCategories (session, project, plc, plc-online, hardware, hmi, runtime).")] string category = "",
            [Description("domain: optional exact domain tag from ListToolCategories, e.g. 'HMI-Unified' or 'PLC-Software'. Case-insensitive.")] string domain = "")
        {
            try
            {
                var all = AllToolMethods();
                if (limit <= 0) limit = 12;
                if (!string.IsNullOrWhiteSpace(category) && ToolTaxonomy.FindCategory(category) == null)
                    return new ResponseStringList { Message = "Unknown category '" + category + "'. Valid keys: " + string.Join(", ", ToolTaxonomy.Categories.Select(c => c.Key)) + ".", Meta = BridgeMeta(false) };
                if (!string.IsNullOrWhiteSpace(domain) && !ToolTaxonomy.IsKnownDomain(domain))
                    return new ResponseStringList { Message = "Unknown domain '" + domain + "'. Call ListToolCategories for the registered domain tags.", Meta = BridgeMeta(false) };
                if (!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(domain))
                {
                    var filtered = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in all)
                    {
                        var tag = ToolTaxonomy.Parse(ToolDescription(kv.Value));
                        if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(tag.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(category) && !string.Equals(ToolTaxonomy.CategoryOf(tag.Domain), category.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        filtered[kv.Key] = kv.Value;
                    }
                    all = filtered;
                }

                var terms = (query ?? "")
                    .Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .ToArray();

                var scored = new List<KeyValuePair<int, string>>();
                foreach (var kv in all)
                {
                    string lname = kv.Key.ToLowerInvariant();
                    string desc = ToolDescription(kv.Value).ToLowerInvariant();
                    int score = 0;
                    if (terms.Length == 0) score = 1;
                    foreach (var t in terms)
                    {
                        // Name hits outrank description hits: a model searching "watch table"
                        // wants ExportPlcWatchTable ahead of every tool that merely mentions it.
                        if (lname == t) score += 100;
                        else if (lname.Contains(t)) score += 20;
                        if (desc.Contains(t)) score += 3;
                    }
                    if (score > 0) scored.Add(new KeyValuePair<int, string>(score, kv.Key));
                }

                if (scored.Count == 0)
                {
                    return new ResponseStringList
                    {
                        Message = "No tool matches '" + query + "'. Try fewer or more general words " +
                                  "(e.g. 'watch table' instead of 'ExportPlcWatchTableToCsv'), " +
                                  "or call FindTools with an empty query to list everything.",
                        Meta = BridgeMeta(true),
                    };
                }

                var hits = scored
                    .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                    .Take(limit).ToList();

                bool lite = IsLiteProfile();
                var lines = new List<string>();
                foreach (var h in hits)
                {
                    var m = all[h.Value];
                    bool listed = !lite || LiteToolNames.Contains(h.Value);
                    lines.Add(RenderSignature(h.Value, m)
                              + (listed ? "  [already listed - call it directly]" : "  [call via CallTool]"));
                    lines.Add("    " + ToolDescription(m));
                }

                return new ResponseStringList
                {
                    Message = hits.Count + " of " + scored.Count + " matching tools (roster: " + all.Count + " total). " +
                              "Tools marked [call via CallTool] are not in this session's tool list - " +
                              "invoke them with CallTool(name, argumentsJson).",
                    Items = lines,
                    Meta = BridgeMeta(true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "FindTools failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [McpServerTool(Name = "CallTool"), Description(
            "[L0][Meta] Invoke ANY tool in the full roster by name, including ones not listed in this session. " +
            "Use FindTools first to get the exact name and parameter signature. " +
            "Message contains the direct tool JSON. Meta.bridgeSuccess only confirms dispatch/serialization; Meta.operationSuccess reflects inner Meta.success, or null if unknown. Never infer business success from MCP isError=false. " +
            "Example: name='ExportPlcWatchTable', argumentsJson='{\"softwarePath\":\"PLC_1\",\"watchTableName\":\"WT1\"}'.")]
        public static ResponseMessage CallTool(
            [Description("name: exact tool name from FindTools, e.g. 'ExportPlcWatchTable'.")] string name,
            [Description("argumentsJson: the tool's arguments as a JSON object - either the object itself ({\"softwarePath\":\"PLC_1\"}) or that object as a JSON string. Omit for a no-argument tool. Parameters ending in Json (devicePathJson, propertiesJson, ...) may likewise be given as the object/array itself; enum-like values (action, kind, ...) are matched case-insensitively; numbers and booleans are accepted as strings.")] JsonElement? argumentsJson = null)
        {
            // 2.7.47: AI callers routinely send the arguments as an object instead of a string (a binding error before this
            // overload existed) - both forms are accepted. Nullable on purpose: the tool factory cannot serialize default(JsonElement)
            // as a parameter default (the stdio host died in AIFunctionFactory.Build during the 2.7.47 build).
            var element = argumentsJson ?? default;
            string text = argumentsJson == null || element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null ? ""
                : element.ValueKind == JsonValueKind.String ? (element.GetString() ?? "") : element.GetRawText();
            return CallTool(name, text);
        }

        public static ResponseMessage CallTool(string name, string argumentsJson)
        {
            string target = (name ?? "").Trim();
            try
            {
                if (target.Length == 0)
                    return new ResponseMessage { Message = "CallTool: 'name' is required. Call FindTools to look up a tool name.", Meta = BridgeMeta(false) };

                // Self-recursion would be a loop with no purpose; refuse it explicitly.
                if (string.Equals(target, "CallTool", StringComparison.OrdinalIgnoreCase))
                    return new ResponseMessage { Message = "CallTool cannot invoke itself. Pass the target tool's own name.", Meta = BridgeMeta(false) };

                var all = AllToolMethods();
                MethodInfo? method;
                if (!all.TryGetValue(target, out method))
                {
                    // A wrong name is the likeliest failure, so spend the message on the fix
                    // rather than on restating the problem.
                    var near = all.Keys
                        .Where(k => k.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
                                 || target.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(k => k, StringComparer.Ordinal).Take(8).ToList();
                    // Containment misses the commonest case of all - a typo in the middle of an
                    // otherwise correct name ("ExportPlcWatchTabel"). Fall back to shared prefix.
                    if (near.Count == 0)
                        near = all.Keys
                            .Select(k => new KeyValuePair<int, string>(CommonPrefixLength(k, target), k))
                            .Where(x => x.Key >= 6)
                            .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                            .Take(5).Select(x => x.Value).ToList();
                    return new ResponseMessage
                    {
                        Message = "No tool named '" + target + "'." + (near.Count > 0
                            ? " Did you mean: " + string.Join(", ", near) + "?"
                            : " Call FindTools with a capability keyword to find the right name."),
                        Meta = BridgeMeta(false),
                    };
                }

                JsonObject args;
                if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}")
                {
                    args = new JsonObject();
                }
                else
                {
                    JsonNode? parsed;
                    try { parsed = JsonNode.Parse(argumentsJson); }
                    catch (JsonException jx)
                    {
                        return new ResponseMessage
                        {
                            Message = "argumentsJson is not valid JSON (" + jx.Message + "). It must be a JSON OBJECT of the " +
                                      "tool's parameters, e.g. {\"softwarePath\":\"PLC_1\"} - not a bare value, not the tool name.",
                            Meta = BridgeMeta(false),
                        };
                    }
                    JsonObject? obj = parsed as JsonObject;
                    if (obj == null)
                        return new ResponseMessage
                        {
                            Message = "argumentsJson must be a JSON object, e.g. {\"softwarePath\":\"PLC_1\"}. " +
                                      "Expected signature: " + RenderSignature(target, method!),
                            Meta = BridgeMeta(false),
                        };
                    args = obj;
                }

                var ps = method.GetParameters();
                var call = new object?[ps.Length];
                var missing = new List<string>();
                JsonObject? normalized = null;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    // 2.7.46: infrastructure parameters of the async export tools (IMcpServer, RequestContext<...>) are not tool
                    // arguments; the bridge has no request context of its own, so they are passed as null and the tools only send
                    // progress notifications when a progress token exists (real project: ExportBlocks / ExportTypes were uncallable
                    // through CallTool - "missing required argument(s): server, context").
                    if (IsInfrastructureParameter(p.ParameterType)) { call[i] = null; continue; }
                    // Match case-insensitively: models routinely send PascalCase for a camelCase param.
                    JsonNode? value = null;
                    bool found = false;
                    foreach (var kv in args)
                    {
                        if (!string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        value = kv.Value; found = kv.Value != null; break;
                    }
                    if (!found)
                    {
                        if (p.HasDefaultValue) { call[i] = p.DefaultValue; continue; }
                        missing.Add(p.Name!);
                        call[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                        continue;
                    }
                    // 2.7.46: an empty string for a parameter whose documented default is a non-empty keyword (unitKind="all",
                    // kind="all", action="read") means "the default" - callers routinely pass "" for "not specified".
                    if (p.ParameterType == typeof(string) && p.HasDefaultValue && p.DefaultValue is string defaultText && defaultText.Length > 0
                        && value is JsonValue emptyCandidate && emptyCandidate.TryGetValue<string>(out var candidateText) && candidateText.Length == 0)
                    { call[i] = defaultText; continue; }
                    // 2.7.46: an array parameter (InvokeObject / InvokeService args: JsonElement[]) given as a JSON-encoded string.
                    if (p.ParameterType.IsArray && value is JsonValue encodedArray && encodedArray.TryGetValue<string>(out var encodedText) && encodedText.TrimStart().StartsWith("["))
                    {
                        try { value = JsonNode.Parse(encodedText); } catch (JsonException) { }
                    }
                    // 2.7.47: lenient coercion - the recurring "format errors" of AI callers. A *Json / string parameter given as the
                    // object or array itself becomes its JSON text; numbers and booleans given as strings (or 0/1) are parsed; a
                    // string parameter given as a number / boolean takes its text.
                    var coerced = CoerceArgument(value!, p.ParameterType);
                    if (coerced != null) { value = coerced; normalized ??= new JsonObject(); normalized[p.Name!] = value.DeepClone(); }
                    try { call[i] = value!.Deserialize(p.ParameterType, BridgeJson); }
                    catch (Exception cx)
                    {
                        return new ResponseMessage
                        {
                            Message = "Argument '" + p.Name + "' of " + target + " could not be read as " +
                                      FriendlyTypeName(p.ParameterType) + ": " + cx.Message +
                                      ". Expected signature: " + RenderSignature(target, method!),
                            Meta = BridgeMeta(false),
                        };
                    }
                }

                if (missing.Count > 0)
                {
                    return new ResponseMessage
                    {
                        Message = target + " is missing required argument(s): " + string.Join(", ", missing) +
                                  ". Expected signature: " + RenderSignature(target, method!),
                        Meta = BridgeMeta(false),
                    };
                }

                object? result = InvokeToolMethod(method!, call);
                // 2.7.47: a refusal such as "action must be one of: read/create/delete (case-sensitive)" whose given value matches one of
                // the alternatives except for casing is retried once with the canonical spelling; the normalization is reported.
                var refusal = (result as ResponseMessage)?.Message;
                if (refusal != null && TryCanonicalizeEnumArgument(refusal, ps, call, out var canonicalName, out var canonicalValue))
                {
                    normalized ??= new JsonObject(); normalized[canonicalName] = canonicalValue;
                    result = InvokeToolMethod(method!, call);
                }
                // Tools return their own strongly-typed response objects; hand that JSON through
                // unchanged so the model sees exactly what a direct call would have produced.
                string payload = result == null
                    ? "null"
                    : JsonSerializer.Serialize(result, result.GetType(), BridgeJson);

                var bridgeMeta = ToolBridgeStatus.Create(true, (result as ResponseMessage)?.Meta);
                if (normalized != null) bridgeMeta["bridgeNormalizedArguments"] = normalized;
                return new ResponseMessage
                {
                    Message = payload,
                    Meta = bridgeMeta,
                };
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return new ResponseMessage { Message = target + " failed: " + inner.Message, Meta = BridgeMeta(false) };
            }
            catch (Exception ex)
            {
                return new ResponseMessage { Message = "CallTool('" + target + "') failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        private static object? InvokeToolMethod(MethodInfo method, object?[] call)
        {
            object? result = method.Invoke(null, call);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                var resultProperty = task.GetType().GetProperty("Result");
                result = resultProperty != null && resultProperty.PropertyType.Name != "VoidTaskResult" ? resultProperty.GetValue(task) : null;
            }
            return result;
        }

        private static readonly Regex EnumRefusal = new Regex(@"(?<name>[A-Za-z][A-Za-z0-9]*) must be (?:one of:?\s*)?(?<values>[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)+)", RegexOptions.Compiled);

        internal static bool TryCanonicalizeEnumArgument(string refusal, ParameterInfo[] ps, object?[] call, out string name, out string canonical)
        {
            name = ""; canonical = "";
            var match = EnumRefusal.Match(refusal ?? "");
            if (!match.Success) return false;
            var parameterName = match.Groups["name"].Value;
            var alternatives = match.Groups["values"].Value.Split('/');
            for (int i = 0; i < ps.Length; i++)
            {
                if (!string.Equals(ps[i].Name, parameterName, StringComparison.OrdinalIgnoreCase) || ps[i].ParameterType != typeof(string)) continue;
                var given = call[i] as string;
                if (string.IsNullOrEmpty(given)) return false;
                var hit = alternatives.FirstOrDefault(a => string.Equals(a, given, StringComparison.OrdinalIgnoreCase) && !string.Equals(a, given, StringComparison.Ordinal));
                if (hit == null) return false;
                call[i] = hit; name = ps[i].Name!; canonical = hit;
                return true;
            }
            return false;
        }

        // Returns the coerced node, or null when the value already fits (or cannot be coerced safely).
        internal static JsonNode? CoerceArgument(JsonNode value, Type type)
        {
            if (value == null) return null;
            if (type == typeof(string))
            {
                if (value is JsonObject || value is JsonArray) return JsonValue.Create(value.ToJsonString());
                if (value is JsonValue v && !v.TryGetValue<string>(out _)) return JsonValue.Create(v.ToJsonString());
                return null;
            }
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                var t = text.Trim();
                if (type == typeof(bool)) return bool.TryParse(t, out var b) ? JsonValue.Create(b) : t == "1" ? JsonValue.Create(true) : t == "0" ? JsonValue.Create(false) : null;
                if (type == typeof(int) || type == typeof(ushort) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint))
                    return long.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : null;
                if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
                    return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null;
                return null;
            }
            if (type == typeof(bool) && value is JsonValue num && num.TryGetValue<int>(out var n) && (n == 0 || n == 1)) return JsonValue.Create(n == 1);
            return null;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        private static JsonObject BridgeMeta(bool success)
        {
            return success ? new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                : ToolBridgeStatus.Create(false);
        }

        private static readonly JsonSerializerOptions BridgeJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // Chinese project/block names must survive the round trip unescaped.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}

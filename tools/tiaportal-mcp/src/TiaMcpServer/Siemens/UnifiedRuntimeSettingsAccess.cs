using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class UnifiedRuntimeSettingsAccess
    {
        // Explicit root properties verified against V21 PublicAPI. Never replace
        // nested settings objects or accept arbitrary property/method paths.
        internal static readonly string[] Allowed = { "StartScreen", "ScreenResolution", "CentralInputHint", "CentralPanning", "CentralZooming",
            "BitSelection", "BitSelectionStrategyForResourceLists", "BitSelectionStrategyForTagDynamization", "EnableLanguageCompatibleFontFamilies",
            "AutoLogOffURL", "GMPEnabled", "GeneralESIGCommentsStrategy" };
        internal const string DefaultFields = "[\"StartScreen\",\"ScreenResolution\"]";
        private static void CheckJson(string json)
        {
            if (json == null || json.Length > 16384) throw new ArgumentException("Settings JSON must be at most 16 KiB.");
        }
        internal static string[] Fields(string json)
        {
            CheckJson(json);
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("fieldsJson must be a string array.");
            var fields = array.Select(v => v?.GetValue<string>() ?? "").ToArray();
            if (fields.Length == 0 || fields.Length > Allowed.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
                throw new ArgumentException("Select 1..12 unique root settings.");
            return fields;
        }
        internal static JsonObject Changes(string json)
        {
            CheckJson(json);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("changesJson must be a JSON object.");
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length == 0 || names.Length > Allowed.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Supply 1..12 distinct setting names; duplicate JSON keys are invalid.");
            foreach (var name in names) if (!Allowed.Contains(name, StringComparer.Ordinal)) throw new NotSupportedException("Unsupported root setting: " + name);
            return (JsonObject)JsonNode.Parse(json)!;
        }
        private static PropertyInfo Property(object settings, string name, bool write)
        {
            if (!Allowed.Contains(name, StringComparer.Ordinal)) throw new NotSupportedException("Setting is outside the supported root field list: " + name);
            var p = settings.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p?.GetMethod?.IsPublic != true || p.GetIndexParameters().Length != 0 || (write && p.SetMethod?.IsPublic != true)
                || !(p.PropertyType == typeof(string) || p.PropertyType == typeof(bool) || p.PropertyType.IsEnum))
                throw new NotSupportedException("Public " + (write ? "read/write" : "read") + " scalar API unavailable for " + name + " on " + settings.GetType().FullName);
            return p;
        }
        private static object? Call(HmiReadTrace trace, string path, Func<object?> action)
        {
            trace.Step("before", path);
            try { var value = action(); trace.Step("after", path); return value; }
            catch (Exception ex) { trace.Step("failed", path, ex); throw; }
        }
        private static object Settings(object hmi, HmiReadTrace trace) => Call(trace, "/RuntimeSettings", () => MigrationRead.Get(hmi, "RuntimeSettings"))
            ?? throw new NotSupportedException("RuntimeSettings is unavailable on this HMI/API version.");
        private static JsonNode? Json(object? value) => value is bool b ? JsonValue.Create(b) : value == null ? null : JsonValue.Create(value.ToString());
        private static JsonObject Evidence(object settings, PropertyInfo p, HmiReadTrace trace)
        {
            var path = "/RuntimeSettings/" + p.Name;
            var value = Call(trace, path, () => p.GetValue(settings));
            return new JsonObject { ["path"] = path, ["type"] = p.PropertyType.FullName, ["value"] = Json(value), ["status"] = "ok", ["publicSetter"] = p.SetMethod?.IsPublic == true };
        }
        private static JsonArray Capabilities(object settings)
        {
            var result = new JsonArray();
            foreach (var field in Allowed)
            {
                try
                {
                    var p = Property(settings, field, false);
                    result.Add(new JsonObject { ["field"] = field, ["type"] = p.PropertyType.FullName, ["readable"] = true,
                        ["writable"] = p.SetMethod?.IsPublic == true, ["allowedValues"] = p.PropertyType.IsEnum ? new JsonArray(Enum.GetNames(p.PropertyType).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) : null });
                }
                catch (NotSupportedException ex) { result.Add(new JsonObject { ["field"] = field, ["readable"] = false, ["writable"] = false, ["reason"] = ex.Message }); }
            }
            return result;
        }
        internal static void Read(object hmi, string[] fields, JsonObject meta, HmiReadTrace trace)
        {
            meta["readOnly"] = true; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
            meta["expectedCount"] = fields.Length; meta["actualCount"] = 0; meta["truncated"] = false; meta["nextCursor"] = null;
            var rows = new JsonObject(); var failures = new JsonArray(); meta["settings"] = rows; meta["failures"] = failures;
            var settings = Settings(hmi, trace); meta["capabilities"] = Capabilities(settings);
            foreach (var field in fields)
            {
                try { rows[field] = Evidence(settings, Property(settings, field, false), trace); meta["actualCount"] = meta["actualCount"]!.GetValue<int>() + 1; }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex))
                { var failure = MigrationRead.Failure("/RuntimeSettings/" + field, ex is NotSupportedException ? "Unsupported" : "ReadFailed", ex); rows[field] = failure; failures.Add(failure.DeepClone()); }
            }
            meta["failureCount"] = failures.Count; meta["apiCallSuccess"] = true; meta["dataComplete"] = failures.Count == 0;
            meta["operationSuccess"] = failures.Count == 0;
            meta["coverage"] = "Requested root fields only. Nested settings and Windows/Runtime Manager startup configuration are outside this interface.";
        }
        private static object ConvertValue(JsonNode? node, Type type, string field)
        {
            if (node is not JsonValue value) throw new ArgumentException(field + " requires a non-null scalar.");
            if (type == typeof(bool) && value.TryGetValue<bool>(out var b)) return b;
            if (type == typeof(string) && value.TryGetValue<string>(out var s))
            {
                if (s.Length > 4096) throw new ArgumentException(field + " exceeds 4096 characters.");
                return s;
            }
            if (type.IsEnum && value.TryGetValue<string>(out var enumName) && Enum.GetNames(type).Contains(enumName, StringComparer.Ordinal))
                return Enum.Parse(type, enumName, false);
            throw new ArgumentException(field + " requires " + type.FullName + (type.IsEnum ? "; use an exact advertised enum name, not an integer." : "; no implicit conversion is performed."));
        }
        private static string StartScreenName(object hmi, string path, HmiReadTrace trace, JsonObject meta)
        {
            if (path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)
                || path.Substring(1).Split('/').Any(string.IsNullOrEmpty) || path.Count(c => c == '/') > 65)
                throw new ArgumentException("StartScreen requires an exact absolute URI-escaped screen path, not a bare name or an empty value.");
            var screen = Call(trace, path, () => MigrationRead.Screen(hmi, path))!;
            var name = (string?)Call(trace, path + "/Name", () => MigrationRead.Get(screen, "Name")) ?? throw new InvalidOperationException("Target screen Name unavailable.");
            // StartScreen stores a NAME, not a folder path. Prove that name is
            // unique using a bounded names-only scan before sending any setter.
            var pending = new Stack<(object owner, bool root, int depth)>(); pending.Push((hmi, true, 0));
            var watch = Stopwatch.StartNew(); int visited = 0, matches = 0;
            void Limit() { if (++visited > 30000 || watch.ElapsedMilliseconds > 15000) throw new NotSupportedException("Screen-name uniqueness scan exceeded 30000 steps/15 seconds; no setting was written."); }
            while (pending.Count > 0)
            {
                Limit(); var entry = pending.Pop();
                if (entry.depth > 64) throw new NotSupportedException("Screen-group depth exceeds 64; uniqueness is unverified.");
                foreach (var collectionName in new[] { "Screens", entry.root ? "ScreenGroups" : "Groups" })
                {
                    if (entry.owner.GetType().GetProperty(collectionName)?.GetMethod?.IsPublic != true)
                        throw new NotSupportedException(collectionName + " collection unavailable; screen-name uniqueness is unverified.");
                    var collection = Call(trace, "/$screenNameCheck/" + collectionName, () => MigrationRead.Get(entry.owner, collectionName))
                        ?? throw new NotSupportedException("Null screen/group collection; uniqueness is unverified.");
                    int count = (int)Call(trace, "/$screenNameCheck/" + collectionName + "/Count", () => MigrationRead.Count(collection))!;
                    if (count < 0 || count > 10000) throw new NotSupportedException("Screen/group collection exceeds 10000 objects.");
                    for (int i = 0; i < count; i++)
                    {
                        Limit(); var item = Call(trace, "/$screenNameCheck/" + collectionName + "/" + i, () => MigrationRead.At(collection, i))!;
                        if (collectionName != "Screens") { pending.Push((item, false, entry.depth + 1)); continue; }
                        var candidate = Call(trace, "/$screenNameCheck/Name", () => MigrationRead.Get(item, "Name")) as string
                            ?? throw new InvalidOperationException("Screen name could not be read.");
                        if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) && ++matches > 1)
                            throw new InvalidOperationException("Ambiguous StartScreen name across folders: " + name + ". The API stores only a name; no setting was written.");
                    }
                    if (count != (int)Call(trace, "/$screenNameCheck/" + collectionName + "/Count", () => MigrationRead.Count(collection))!)
                        throw new InvalidOperationException("Screen inventory changed during preview; retry an explicit fresh preview.");
                }
            }
            if (matches != 1) throw new InvalidOperationException("Target screen name uniqueness could not be established.");
            meta["startScreenTarget"] = new JsonObject { ["screenPath"] = path, ["apiValue"] = name, ["uniqueNameVerified"] = true, ["uniquenessScope"] = "selected HMI software; names only" };
            return name;
        }
        internal static string Update(object hmi, string project, string softwarePath, JsonObject changes, bool dryRun, string expectedToken, JsonObject meta, HmiReadTrace trace)
        {
            meta["readOnly"] = dryRun; meta["dryRun"] = dryRun; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
            meta["verificationSuccess"] = false; meta["dataComplete"] = false; meta["writeAttempted"] = false; meta["mayHaveChanged"] = false;
            meta["expectedCount"] = changes.Count; meta["actualCount"] = 0; meta["nextCursor"] = null; meta["truncated"] = false;
            meta["persistence"] = "No save, compile, download, Runtime restart or TIA close. Applied changes remain in the engineering project until separately saved/deployed.";
            meta["atomicity"] = "Multiple settings are not an atomic transaction. No automatic rollback or retry after a write failure.";
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new ArgumentException("Preview expectedToken is required before writing.");
            var settings = Settings(hmi, trace);
            var before = new JsonObject(); var desired = new JsonObject(); var requested = new JsonObject();
            var writes = new List<(PropertyInfo p, object value)>();
            foreach (var entry in changes.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var p = Property(settings, entry.Key, true); var value = ConvertValue(entry.Value, p.PropertyType, entry.Key);
                if (entry.Key == "StartScreen") value = StartScreenName(hmi, (string)value, trace, meta);
                before[entry.Key] = Evidence(settings, p, trace);
                requested[entry.Key] = entry.Value?.DeepClone(); desired[entry.Key] = Json(value);
                writes.Add((p, value));
            }
            meta["before"] = before; meta["proposed"] = desired; meta["requested"] = requested;
            var scope = new JsonObject { ["expectedProject"] = project, ["softwarePath"] = softwarePath, ["before"] = before.DeepClone(), ["requested"] = requested.DeepClone(), ["proposed"] = desired.DeepClone() };
            var token = HmiExactAccess.Token("UpdateUnifiedRuntimeSettings/v1", scope); meta["token"] = token;
            meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["actualCount"] = writes.Count;
            if (dryRun) { meta["operationSuccess"] = true; meta["status"] = "Preview"; return "Review before/proposed; apply the same changes with dryRun=false and expectedToken. No setting was written."; }
            HmiExactAccess.RequireToken(expectedToken, token);
            var applied = new JsonArray(); var readback = new JsonObject(); meta["appliedFields"] = applied; meta["readback"] = readback;
            meta["dataComplete"] = false; meta["actualCount"] = 0;
            foreach (var write in writes)
            {
                var field = write.p.Name;
                if (JsonNode.DeepEquals(before[field]!["value"], desired[field])) continue;
                meta["phase"] = "write:" + field; meta["writeAttempted"] = true; meta["mayHaveChanged"] = true; meta["apiCallSuccess"] = false;
                Call(trace, "/RuntimeSettings/" + field + "/$set", () => { write.p.SetValue(settings, write.value); return null; });
                meta["apiCallSuccess"] = true; applied.Add(field);
                readback[field] = Evidence(settings, write.p, trace);
                if (!JsonNode.DeepEquals(readback[field]!["value"], desired[field]))
                {
                    meta["status"] = "ReadbackMismatch"; meta["failureCount"] = 1;
                    meta["failures"] = new JsonArray(MigrationRead.Failure("/RuntimeSettings/" + field, "ReadbackMismatch", reason: "Setter returned but readback differs; remaining writes were not attempted."));
                    return "Write returned but readback mismatched. Partial changes may exist; inspect before/proposed/readback. No retry or rollback.";
                }
            }
            // Re-read all requested fields after the last setter to detect any
            // coupled change, rather than relying solely on intermediate reads.
            var failures = new JsonArray();
            foreach (var write in writes)
            {
                meta["phase"] = "verify:" + write.p.Name;
                readback[write.p.Name] = Evidence(settings, write.p, trace); meta["actualCount"] = meta["actualCount"]!.GetValue<int>() + 1;
                if (!JsonNode.DeepEquals(readback[write.p.Name]!["value"], desired[write.p.Name])) failures.Add(MigrationRead.Failure("/RuntimeSettings/" + write.p.Name, "ReadbackMismatch"));
            }
            meta["failures"] = failures; meta["failureCount"] = failures.Count; meta["dataComplete"] = true;
            meta["verificationSuccess"] = failures.Count == 0; meta["operationSuccess"] = failures.Count == 0;
            meta["status"] = failures.Count > 0 ? "ReadbackMismatch" : applied.Count == 0 ? "Unchanged" : "Verified";
            return failures.Count == 0 ? "Requested runtime settings readback verified; no save or deployment performed." : "Final readback detected coupled or rejected changes. Inspect evidence; no automatic rollback.";
        }
    }
}

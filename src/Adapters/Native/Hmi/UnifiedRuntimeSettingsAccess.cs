using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedRuntimeSettingsAccess
    {
        // Explicit scalar properties verified against V19-V21 PublicAPI. Never replace
        // nested settings objects or accept arbitrary property/method paths.
        private static readonly string[] Allowed = UnifiedRuntimeSettingsFields.Root.Concat(UnifiedRuntimeSettingsFields.Nested).ToArray();
        internal const int MaxFieldsPerCall = UnifiedRuntimeSettingsFields.MaxFieldsPerCall;
        internal const string DefaultFields = "[\"StartScreen\",\"ScreenResolution\"]";
        private static void CheckJson(string json)
        {
            if (json == null || json.Length > 16384) throw new ArgumentException("Settings JSON must be at most 16 KiB.");
        }
        internal static string[] Fields(string json)
        {
            CheckJson(json);
            var array = UnifiedRequestValues.Parse(json) as HmiArray ?? throw new ArgumentException("fieldsJson must be a string array.");
            var fields = array.Select(v => v?.GetValue<string>() ?? "").ToArray();
            if (UnifiedRuntimeSettingsFields.Invalid(fields) is string reason) throw new ArgumentException(reason, nameof(json));
            return fields;
        }
        internal static HmiObject Changes(string json)
        {
            CheckJson(json);
            var changes = UnifiedRequestValues.Parse(json) as HmiObject ?? throw new ArgumentException("changesJson must be a JSON object.");
            var names = changes.Keys.ToArray();
            if (UnifiedRuntimeSettingsFields.Invalid(names) is string reason) throw new ArgumentException(reason, nameof(json));
            return changes;
        }
        private static void CheckField(string name)
        {
            if (!Allowed.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException("Unknown runtime setting name.");
        }
        private static string Path(string field) => "/RuntimeSettings/" + field.Replace('.', '/');
        private static (object owner, PropertyInfo p) Property(object settings, string name, bool write, HmiReadTrace trace)
        {
            CheckField(name);
            var parts = name.Split('.');
            var owner = parts.Length == 1 ? settings : Call(trace, Path(parts[0]), () => MigrationRead.Get(settings, parts[0]))
                ?? throw new NotSupportedException("Nested settings object unavailable: " + parts[0]);
            return (owner, ScalarProperty(owner.GetType(), parts[parts.Length - 1], name, write));
        }
        private static PropertyInfo ScalarProperty(Type ownerType, string leaf, string name, bool write)
        {
            var p = ownerType.GetProperty(leaf, BindingFlags.Instance | BindingFlags.Public);
            if (p?.GetMethod?.IsPublic != true || p.GetIndexParameters().Length != 0 || (write && p.SetMethod?.IsPublic != true)
                || !(p.PropertyType == typeof(string) || p.PropertyType == typeof(bool) || p.PropertyType.IsEnum
                    || p.PropertyType == typeof(int) || p.PropertyType == typeof(uint)))
                throw new NotSupportedException("Public " + (write ? "read/write" : "read") + " scalar API unavailable for " + name + " on "
                    + ownerType.FullName);
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
        private static object? Json(object? value) => value is bool || value is int || value is uint ? value
            : value == null ? null : HmiValues.Value(value.ToString());
        private static HmiObject Evidence(object owner, PropertyInfo p, string field, HmiReadTrace trace)
        {
            var path = Path(field);
            var value = Call(trace, path, () => p.GetValue(owner));
            return new HmiObject {
                ["path"] = path, ["type"] = p.PropertyType.FullName, ["value"] = Json(value), ["status"] = "ok", ["publicSetter"] = p.SetMethod?
                    .IsPublic == true
            };
        }
        private static HmiArray Capabilities(Type settingsType)
        {
            var result = new HmiArray();
            foreach (var field in Allowed)
            {
                try
                {
                    var parts = field.Split('.');
                    var ownerType = settingsType;
                    if (parts.Length > 1)
                    {
                        var owner = settingsType.GetProperty(parts[0], BindingFlags.Instance | BindingFlags.Public);
                        if (owner?.GetMethod?.IsPublic != true || owner.GetIndexParameters().Length != 0)
                            throw new NotSupportedException("Nested settings API unavailable: " + parts[0]);
                        ownerType = owner.PropertyType;
                    }
                    var p = ScalarProperty(ownerType, parts[parts.Length - 1], field, false);
                    result.Add(new HmiObject { ["field"] = field, ["type"] = p.PropertyType.FullName, ["readable"] = true,
                        ["writable"] = p.SetMethod?.IsPublic == true, ["allowedValues"] = p.PropertyType.IsEnum
                            ? new HmiArray(Enum.GetNames(p.PropertyType).Select(n => (object)HmiValues.Value(n)!).ToArray()) : null });
                }
                catch (NotSupportedException ex) {
                    result.Add(new HmiObject { ["field"] = field, ["readable"] = false, ["writable"] = false, ["reason"] = ex.Message }); }
            }
            return result;
        }
        internal static void Read(object hmi, string[] fields, HmiObject meta, HmiReadTrace trace)
        {
            meta["readOnly"] = true; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
            meta["expectedCount"] = fields.Length; meta["actualCount"] = 0; meta["truncated"] = false; meta["nextCursor"] = null;
            var rows = new HmiObject(); var failures = new HmiArray(); meta["settings"] = rows; meta["failures"] = failures;
            var settings = Settings(hmi, trace); meta["capabilities"] = Capabilities(settings.GetType());
            foreach (var field in fields)
            {
                try {
                    var property = Property(settings, field, false, trace);
                    rows[field] = Evidence(property.owner, property.p, field, trace);
                    meta["actualCount"] = meta["actualCount"]!.GetValue<int>() + 1; }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex))
                {
                    var failure = MigrationRead.Failure(Path(field), ex is NotSupportedException ? "Unsupported" : "ReadFailed", ex);
                    rows[field] = failure; failures.Add(failure.DeepClone());
                }
            }
            meta["failureCount"] = failures.Count; meta["apiCallSuccess"] = true; meta["dataComplete"] = failures.Count == 0;
            meta["operationSuccess"] = failures.Count == 0;
            meta["coverage"] = "Requested root or dotted nested scalar fields only. V20+: Telemetry, UPSS and tag settings; " +
                "V21: TagOptimizationActive and exclusive operation. LanguageAndFonts, Windows auto-start and Runtime Manager are not covered.";
        }
        private static object ConvertValue(object? node, Type type, string field)
        {
            if (node is not object value) throw new ArgumentException(field + " requires a non-null scalar.");
            if (type == typeof(bool) && value.TryGetValue<bool>(out var b)) return b;
            if (type == typeof(string) && value.TryGetValue<string>(out var s))
            {
                if (s.Length > 4096) throw new ArgumentException(field + " exceeds 4096 characters.");
                return s;
            }
            if (type.IsEnum && value.TryGetValue<string>(out var enumName) && Enum.GetNames(type).Contains(enumName, StringComparer.Ordinal))
                return Enum.Parse(type, enumName, false);
            if (type == typeof(int) || type == typeof(uint))
            {
                long integer;
                if (value is int i) integer = i;
                else if (value is long l) integer = l;
                else throw new ArgumentException(field + " requires a JSON integer in the range of " + type.FullName + ".");
                if (type == typeof(int) && integer >= int.MinValue && integer <= int.MaxValue) return (int)integer;
                if (type == typeof(uint) && integer >= uint.MinValue && integer <= uint.MaxValue) return (uint)integer;
                throw new ArgumentException(field + " requires a JSON integer in the range of " + type.FullName + ".");
            }
            throw new ArgumentException(field + " requires " + type.FullName
                    + (type.IsEnum ? "; use an exact advertised enum name, not an integer." : "; no implicit conversion is performed."));
        }
        private static string StartScreenName(object hmi, string path, HmiReadTrace trace, HmiObject meta)
        {
            if (path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)
                || path.Substring(1).Split('/').Any(string.IsNullOrEmpty) || path.Count(c => c == '/') > 65)
                throw new ArgumentException("StartScreen requires an exact absolute readable screen path (old encoded input accepted), not a bare name or an " +
                    "empty value.");
            var screen = Call(trace, path, () => MigrationRead.Screen(hmi, path))!;
            var name = (string?)Call(trace, path + "/Name", () => MigrationRead.Get(screen, "Name"))
                ?? throw new InvalidOperationException("Target screen Name unavailable.");
            // StartScreen stores a NAME, not a folder path. Prove that name is
            // unique using a bounded names-only scan before sending any setter.
            var pending = new Stack<(object owner, bool root, int depth)>(); pending.Push((hmi, true, 0));
            var watch = Stopwatch.StartNew(); int visited = 0, matches = 0;
            void Limit() {
                if (++visited > 30000 || watch.ElapsedMilliseconds > 15000)
                    throw new NotSupportedException("Screen-name uniqueness scan exceeded 30000 steps/15 seconds; no setting was written.");
            }
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
                            throw new InvalidOperationException("Ambiguous StartScreen name across folders: " + name
                                    + ". The API stores only a name; no setting was written.");
                    }
                    if (count != (int)Call(trace, "/$screenNameCheck/" + collectionName + "/Count", () => MigrationRead.Count(collection))!)
                        throw new InvalidOperationException("Screen inventory changed during preview; retry an explicit fresh preview.");
                }
            }
            if (matches != 1) throw new InvalidOperationException("Target screen name uniqueness could not be established.");
            meta["startScreenTarget"] = new HmiObject {
                ["screenPath"] = path, ["apiValue"] = name, ["uniqueNameVerified"] = true, ["uniquenessScope"] = "selected HMI software; names only" };
            return name;
        }
        internal static string Update(object hmi, string project, string softwarePath, HmiObject changes, bool dryRun, string expectedToken, HmiObject meta,
                HmiReadTrace trace)
        {
            meta["readOnly"] = dryRun; meta["dryRun"] = dryRun; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
            meta["verificationSuccess"] = false; meta["dataComplete"] = false; meta["writeAttempted"] = false; meta["mayHaveChanged"] = false;
            meta["expectedCount"] = changes.Count; meta["actualCount"] = 0; meta["nextCursor"] = null; meta["truncated"] = false;
            meta["persistence"] =
                "No save, compile, download, Runtime restart or TIA close. Applied changes remain in the engineering project until separately saved/deployed.";
            meta["atomicity"] = "Captured public values are restored on failure; inspect rollbackCompleted and cleanupRequired.";
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new ArgumentException("Preview previewToken is required before writing.");
            var settings = Settings(hmi, trace);
            var before = new HmiObject(); var desired = new HmiObject(); var requested = new HmiObject();
            var writes = new List<(string field, object owner, PropertyInfo p, object value)>();
            foreach (var entry in changes.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var property = Property(settings, entry.Key, true, trace); var p = property.p;
                var value = ConvertValue(entry.Value, p.PropertyType, entry.Key);
                if (entry.Key == "StartScreen") value = StartScreenName(hmi, (string)value, trace, meta);
                before[entry.Key] = Evidence(property.owner, p, entry.Key, trace);
                requested[entry.Key] = entry.Value?.DeepClone(); desired[entry.Key] = Json(value);
                writes.Add((entry.Key, property.owner, p, value));
            }
            meta["before"] = before; meta["proposed"] = desired; meta["requested"] = requested;
            var scope = new HmiObject {
                ["expectedProject"] = project, ["softwarePath"] = softwarePath, ["before"] = before.DeepClone(), ["requested"] = requested.DeepClone(),
                    ["proposed"] = desired.DeepClone()
            };
            var token = HmiExactAccess.Token("UpdateUnifiedRuntimeSettings/v1", scope); meta["token"] = token;
            meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["actualCount"] = writes.Count;
            if (dryRun) {
                meta["operationSuccess"] = true; meta["status"] = "Preview";
                return "Review before/proposed; apply the same changes with dryRun=false and previewToken. No setting was written.";
            }
            HmiExactAccess.RequireToken(expectedToken, token);
            var applied = new HmiArray(); var readback = new HmiObject(); meta["appliedFields"] = applied; meta["readback"] = readback;
            meta["dataComplete"] = false; meta["actualCount"] = 0;
            // A setter can change another requested field even when that field was initially unchanged.
            foreach (var write in writes) EngineeringWriteJournal.Property(write.owner, write.p);
            foreach (var write in writes)
            {
                var field = write.field;
                if (UnifiedRequestValues.Same(before[field]!.Field("value"), desired[field])) continue;
                meta["phase"] = "write:" + field; meta["writeAttempted"] = true; meta["mayHaveChanged"] = true; meta["apiCallSuccess"] = false;
                Call(trace, Path(field) + "/$set", () => { write.p.SetValue(write.owner, write.value); return null; });
                meta["apiCallSuccess"] = true; applied.Add(field);
                readback[field] = Evidence(write.owner, write.p, field, trace);
                if (!UnifiedRequestValues.Same(readback[field]!.Field("value"), desired[field]))
                {
                    meta["status"] = "ReadbackMismatch"; meta["failureCount"] = 1;
                    meta["failures"] =
                        new HmiArray(MigrationRead
                            .Failure(Path(field), "ReadbackMismatch", reason
                                : "Setter returned but readback differs; remaining writes were not attempted."));
                    return "Write returned but readback mismatched. Partial changes may exist; inspect before/proposed/readback. Inspect rollbackCompleted " +
                        "and cleanupRequired.";
                }
            }
            // Re-read all requested fields after the last setter to detect any
            // coupled change, rather than relying solely on intermediate reads.
            var failures = new HmiArray();
            foreach (var write in writes)
            {
                meta["phase"] = "verify:" + write.field;
                readback[write.field] = Evidence(write.owner, write.p, write.field, trace);
                meta["actualCount"] = meta["actualCount"]!.GetValue<int>() + 1;
                if (!UnifiedRequestValues.Same(readback[write.field]!.Field("value"), desired[write.field])) failures
                    .Add(MigrationRead.Failure(Path(write.field), "ReadbackMismatch"));
            }
            meta["failures"] = failures; meta["failureCount"] = failures.Count; meta["dataComplete"] = true;
            meta["verificationSuccess"] = failures.Count == 0; meta["operationSuccess"] = failures.Count == 0;
            meta["status"] = failures.Count > 0 ? "ReadbackMismatch" : applied.Count == 0 ? "Unchanged" : "Verified";
            return failures.Count == 0 ? "Requested runtime settings readback verified; no save or deployment performed."
                : "Final readback detected coupled or rejected changes. Inspect evidence; inspect rollbackCompleted and cleanupRequired.";
        }
    }
}

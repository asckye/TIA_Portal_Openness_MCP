using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // A caller-defined selection is useful even when Openness does not expose
    // the editor's graphical group. Never turn the selection into native membership.
    internal static class UnifiedGraphicSelection
    {
        internal static readonly string[] Geometry = { "Left", "Top", "Width", "Height" };
        internal static string[] Names(string json)
        {
            if (json == null || json.Length > 65536) throw new ArgumentException("itemNamesJson exceeds 64 KiB.");
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("itemNamesJson must be an array of exact object names.");
            if (array.Count == 0 || array.Count > 128) throw new ArgumentException("Select 1..128 objects; split larger selections explicitly.");
            var names = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 1024) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Names must be nonempty, unique and at most 1024 characters. Names are case sensitive and are not normalized.");
            return names;
        }
        private static object? Read(HmiReadTrace trace, string path, Func<object?> get)
        {
            trace.Step("before", path);
            try { var value = get(); trace.Step("after", path); return value; }
            catch (Exception ex) { trace.Step("failed", path, ex); throw; }
        }
        private static PropertyInfo? PublicGetter(Type type, string name)
        {
            var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return p?.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0 ? p : null;
        }
        private static MethodInfo? Official(object item, string name)
            => item.GetType().GetInterface("Siemens.Engineering.IEngineeringObject")?.GetMethod(name);
        private static bool RelationName(string name) => new[] { "Group", "Parent", "Container", "Layout", "Contained" }
            .Any(part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        private static JsonObject Value(string path, object? value, string access)
        {
            if (!MigrationRead.IsScalar(value)) throw new NotSupportedException("Only scalar evidence is read; nested object traversal was not attempted.");
            var row = MigrationRead.Scalar(path, value);
            row["access"] = access;
            return row;
        }
        private static JsonObject TryValue(HmiReadTrace trace, string path, string access, Func<object?> read)
        {
            try { return Value(path, Read(trace, path, read), access); }
            catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { return MigrationRead.Failure(path, "FieldUnavailable", ex); }
        }
        internal static IEnumerable<JsonObject> Capture(object hmi, string screenPath, string[] names, HmiReadTrace trace)
        {
            var screen = Read(trace, screenPath, () => MigrationRead.Screen(hmi, screenPath))!;
            var items = Read(trace, screenPath + "/ScreenItems", () => MigrationRead.Get(screen, "ScreenItems"))!;
            var completed = new List<JsonObject>();
            foreach (var name in names)
            {
                var path = "/Screens" + screenPath + "/ScreenItems/" + MigrationRead.Segment(name);
                JsonObject row;
                try
                {
                    var item = Read(trace, path, () => MigrationRead.Named(items, name))!;
                    row = CaptureItem(item, name, path, trace);
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex))
                {
                    row = MigrationRead.Failure(path, "ObjectReadFailed", ex);
                    row["kind"] = "graphicObject"; row["name"] = name; row["geometryComplete"] = false;
                }
                completed.Add(row);
                yield return row;
            }
            var summary = new JsonObject { ["kind"] = "graphicSelectionSummary", ["path"] = "/Screens" + screenPath,
                ["status"] = "unsupported", ["code"] = "NativeGraphicGroupUnverified",
                ["reason"] = "This is a caller-defined exact-name selection. Native graphical-group membership, origin and transformed bounds are not established by the available engineering API adapter. ScreenGroups are screen folders. Empty or unavailable group metadata does not mean the objects are ungrouped.",
                ["expectedObjectCount"] = names.Length, ["actualObjectCount"] = completed.Count(r => r["type"] != null),
                ["geometryComplete"] = completed.All(r => r["geometryComplete"]?.GetValue<bool>() == true),
                ["nativeGroupVerified"] = false, ["nativeGroup"] = null, ["nativeGroupBounds"] = null,
                ["rawCoordinateEnvelope"] = Envelope(completed),
                ["coordinateCaveat"] = "Envelope of returned Left/Top/Width/Height only; coordinate-frame equivalence, transforms and rotation are unverified. This is not the editor's group bounding box." };
            yield return summary;
        }
        private static JsonObject CaptureItem(object item, string name, string path, HmiReadTrace trace)
        {
            var started = DateTimeOffset.UtcNow.ToString("o");
            var type = item.GetType();
            var fields = new JsonObject(); var relations = new JsonArray(); var gaps = new JsonArray();
            var attributes = new Dictionary<string, bool>(StringComparer.Ordinal);
            var capabilities = new JsonArray();
            var infos = Official(item, "GetAttributeInfos");
            if (infos != null)
            {
                try
                {
                    // GetAttributeInfos returns local descriptors, never recurse into engineering objects.
                    var descriptors = Read(trace, path + "/$attributeInfos", () => infos.Invoke(item, null)) as IEnumerable
                        ?? throw new NotSupportedException("GetAttributeInfos did not return descriptors.");
                    int count = 0;
                    foreach (var info in descriptors)
                    {
                        if (++count > 512) throw new NotSupportedException("Attribute metadata exceeds 512 descriptors.");
                        var key = MigrationRead.Get(info!, "Name")?.ToString() ?? "";
                        var access = MigrationRead.Get(info!, "AccessMode")?.ToString() ?? "";
                        attributes[key] = access.IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (RelationName(key)) capabilities.Add(new JsonObject { ["name"] = key, ["accessMode"] = access, ["source"] = "IEngineeringObject.GetAttributeInfos" });
                    }
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { gaps.Add(MigrationRead.Failure(path + "/$attributeInfos", "MetadataReadFailed", ex)); }
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => RelationName(p.Name)))
                capabilities.Add(new JsonObject { ["name"] = p.Name, ["type"] = p.PropertyType.FullName, ["readable"] = PublicGetter(type, p.Name) != null, ["source"] = "public CLR property metadata" });
            var compositions = Official(item, "GetCompositionInfos");
            if (compositions != null)
            {
                try
                {
                    var descriptors = Read(trace, path + "/$compositionInfos", () => compositions.Invoke(item, null)) as IEnumerable
                        ?? throw new NotSupportedException("GetCompositionInfos did not return descriptors.");
                    int count = 0;
                    foreach (var info in descriptors)
                    {
                        if (++count > 128) throw new NotSupportedException("Composition metadata exceeds 128 descriptors.");
                        var key = MigrationRead.Get(info!, "Name")?.ToString() ?? "";
                        // List names only: don't invoke a collection getter or walk
                        // contained controls without a verified group API contract.
                        capabilities.Add(new JsonObject { ["name"] = key, ["source"] = "IEngineeringObject.GetCompositionInfos", ["contentsRead"] = false });
                    }
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { gaps.Add(MigrationRead.Failure(path + "/$compositionInfos", "MetadataReadFailed", ex)); }
            }
            foreach (var field in Geometry)
            {
                var p = PublicGetter(type, field);
                var fpath = path + "/" + field;
                fields[field] = p != null ? TryValue(trace, fpath, "public property", () => p.GetValue(item))
                    : attributes.TryGetValue(field, out var readable) && readable
                        ? TryValue(trace, fpath, "IEngineeringObject.GetAttribute", () => MigrationRead.Attribute(item, field))
                        : MigrationRead.Failure(fpath, "Unsupported", reason: "No public getter or advertised readable attribute.");
            }
            // Read only explicitly advertised scalar relationship attributes. Names
            // are evidence, not a contract establishing native group membership.
            foreach (var entry in attributes.Where(a => a.Value && RelationName(a.Key)).Take(32))
                relations.Add(TryValue(trace, path + "/$attributes/" + MigrationRead.Segment(entry.Key), "IEngineeringObject.GetAttribute", () => MigrationRead.Attribute(item, entry.Key)));
            if (attributes.Count(a => a.Value && RelationName(a.Key)) > 32)
                gaps.Add(MigrationRead.Failure(path, "RelationMetadataLimit", reason: "More than 32 relation attributes; only the first 32 were read."));
            // Parent is a deliberate ONE-hop ownership probe, not a general graph
            // escape. Never read a parent's properties other than Name, or its Parent.
            JsonObject owner;
            var parent = PublicGetter(type, "Parent");
            if (parent == null) owner = MigrationRead.Failure(path + "/Parent", "Unsupported", reason: "No public Parent getter.");
            else
            {
                try
                {
                    var value = Read(trace, path + "/Parent", () => parent.GetValue(item));
                    owner = new JsonObject { ["status"] = "ok", ["evidence"] = path + "/Parent", ["type"] = value?.GetType().FullName,
                        ["meaning"] = "Engineering owner only; not proof of graphical-group membership." };
                    if (value != null && PublicGetter(value.GetType(), "Name") is PropertyInfo np)
                        owner["name"] = TryValue(trace, path + "/Parent/Name", "public property", () => np.GetValue(value));
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { owner = MigrationRead.Failure(path + "/Parent", "OwnerReadFailed", ex); }
            }
            var row = new JsonObject { ["kind"] = "graphicObject", ["name"] = name, ["path"] = path, ["type"] = type.FullName,
                ["sampleStartedUtc"] = started, ["sampleFinishedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["fields"] = fields, ["owner"] = owner, ["relationEvidence"] = relations, ["relationCapabilities"] = capabilities,
                ["gaps"] = gaps, ["nativeGroupVerified"] = false, ["coordinateFrame"] = "unverified raw Openness geometry",
                ["attributeMetadataApiAvailable"] = infos != null, ["compositionMetadataApiAvailable"] = compositions != null };
            bool complete = Geometry.All(f => Number(row, f, out _));
            row["geometryComplete"] = complete;
            bool evidenceComplete = gaps.Count == 0 && owner["status"]?.ToString() == "ok"
                && (owner["name"] == null || owner["name"]!["status"]?.ToString() == "ok")
                && relations.All(r => r?["status"]?.ToString() == "ok");
            row["status"] = complete && evidenceComplete ? "ok" : "failed";
            if (row["status"]!.ToString() != "ok") { row["code"] = "SelectionEvidenceIncomplete"; row["reason"] = "Inspect fields, owner, relationEvidence and gaps. Missing evidence is not a zero coordinate or an ungrouped object."; }
            return row;
        }
        private static bool Number(JsonObject row, string field, out decimal number)
        {
            number = 0;
            var evidence = row["fields"]?[field];
            if (evidence?["status"]?.ToString() != "ok" || !decimal.TryParse(evidence["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
            return (field != "Width" && field != "Height") || number >= 0;
        }
        private static JsonObject? Envelope(IReadOnlyList<JsonObject> rows)
        {
            if (rows.Count == 0 || rows.Any(r => !Geometry.All(f => Number(r, f, out _)))) return null;
            try
            {
                var rectangles = rows.Select(r => { Number(r, "Left", out var l); Number(r, "Top", out var t); Number(r, "Width", out var w); Number(r, "Height", out var h); return new[] { l, t, checked(l + w), checked(t + h) }; }).ToArray();
                var left = rectangles.Min(r => r[0]); var top = rectangles.Min(r => r[1]);
                return new JsonObject { ["left"] = left, ["top"] = top, ["width"] = checked(rectangles.Max(r => r[2]) - left), ["height"] = checked(rectangles.Max(r => r[3]) - top), ["nativeGroupBounds"] = false };
            }
            catch (OverflowException) { return null; }
        }
        // Offline comparison accepts the full array of persisted page Meta objects.
        // Require a contiguous, completed collection so missing pages cannot look unchanged.
        private static (JsonObject scope, Dictionary<string, JsonObject> items) Decode(string json)
        {
            if (json == null || json.Length > 4 * 1024 * 1024) throw new ArgumentException("Snapshot pages exceed 4 MiB.");
            var pages = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("Supply an array of saved page Meta objects, in pageIndex order.");
            if (pages.Count == 0 || pages.Count > 1024) throw new ArgumentException("Expected 1..1024 pages.");
            JsonObject? scope = null; string? id = null; var records = new List<JsonObject>();
            for (int j = 0; j < pages.Count; j++)
            {
                var page = pages[j] as JsonObject ?? throw new ArgumentException("Each page must be a Meta object.");
                var currentScope = page["scope"] as JsonObject;
                if (j == 0) { scope = currentScope; id = page["collectionId"]?.ToString(); }
                if (scope == null || scope["tool"]?.ToString() != "ReadUnifiedGraphicSelection" || string.IsNullOrEmpty(id)
                    || !JsonNode.DeepEquals(scope, currentScope) || id != page["collectionId"]?.ToString()
                    || page["pageIndex"]?.GetValue<int>() != j || page["apiCallSuccess"]?.GetValue<bool>() != true)
                    throw new ArgumentException("Pages must be contiguous, successful and from one graphical-selection collection/scope.");
                bool final = j == pages.Count - 1;
                if (final ? page["traversalComplete"]?.GetValue<bool>() != true || page["nextCursor"] != null || page["truncated"]?.GetValue<bool>() != false
                    : page["nextCursor"] == null || page["traversalComplete"]?.GetValue<bool>() == true)
                    throw new ArgumentException("Missing, out-of-order or unfinished pages; comparison refused.");
                foreach (var row in page["records"] as JsonArray ?? throw new ArgumentException("Missing records."))
                    records.Add(row as JsonObject ?? throw new ArgumentException("Invalid record."));
            }
            var names = Names(scope!["itemNames"]!.ToJsonString());
            var summary = records.Where(r => r["kind"]?.ToString() == "graphicSelectionSummary").ToArray();
            var items = records.Where(r => r["kind"]?.ToString() == "graphicObject").ToArray();
            if (summary.Length != 1 || records.Last() != summary[0] || items.Length != names.Length || records.Count != items.Length + 1
                || items.Any(r => r["geometryComplete"]?.GetValue<bool>() != true || !Geometry.All(f => Number(r, f, out _))))
                throw new ArgumentException("Complete geometry and a final summary are required; incomplete samples cannot prove unchanged coordinates.");
            var map = items.ToDictionary(r => r["name"]!.GetValue<string>(), StringComparer.Ordinal);
            if (names.Any(n => !map.ContainsKey(n))) throw new ArgumentException("Object selection does not match records.");
            return (scope, map);
        }
        internal static JsonObject Compare(string beforePagesJson, string afterPagesJson)
        {
            var before = Decode(beforePagesJson); var after = Decode(afterPagesJson);
            if (!JsonNode.DeepEquals(before.scope, after.scope)) throw new ArgumentException("Both snapshots must have exactly the same project, HMI, screen and itemNames scope.");
            var changes = new JsonArray(); int changed = 0;
            foreach (var entry in before.items)
            {
                var next = after.items[entry.Key]; var previous = entry.Value;
                if (previous["type"] == null || !JsonNode.DeepEquals(previous["type"], next["type"]) || !JsonNode.DeepEquals(previous["path"], next["path"]))
                    throw new ArgumentException("Object path/type changed; geometry comparison refused: " + entry.Key);
                var fields = new JsonObject(); bool moved = false;
                foreach (var field in Geometry)
                {
                    Number(previous, field, out var a); Number(next, field, out var b);
                    fields[field] = new JsonObject { ["before"] = a, ["after"] = b, ["delta"] = checked(b - a), ["changed"] = a != b };
                    moved |= a != b;
                }
                if (moved) changed++;
                changes.Add(new JsonObject { ["name"] = entry.Key, ["path"] = previous["path"]?.DeepClone(), ["changed"] = moved, ["fields"] = fields });
            }
            return new JsonObject { ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = true,
                ["scope"] = before.scope.DeepClone(), ["readOnly"] = true, ["offline"] = true,
                ["coverage"] = "raw Left/Top/Width/Height comparison only", ["expectedCount"] = before.items.Count, ["actualCount"] = changes.Count,
                ["changedObjectCount"] = changed, ["truncated"] = false, ["nextCursor"] = null, ["failureCount"] = 0, ["failures"] = new JsonArray(),
                ["nativeGroupVerified"] = false, ["causalityEstablished"] = false,
                ["limitation"] = "Matching names/paths/types do not prove persistent object identity across deletion/recreation. Coordinate changes alone do not establish grouping, layout constraints or who caused a change.", ["objects"] = changes };
        }
    }
}

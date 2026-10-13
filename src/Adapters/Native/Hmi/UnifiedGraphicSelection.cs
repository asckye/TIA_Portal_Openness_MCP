using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace TiaMcp.Adapters.Native.Hmi
{
    // A caller-defined selection is useful even when Openness does not expose
    // the editor's graphical group. Never turn the selection into native membership.
    internal static class UnifiedGraphicSelection
    {
        internal static readonly string[] Geometry = { "Left", "Top", "Width", "Height" };
        internal static string[] Names(string json)
        {
            if (json == null || json.Length > 65536) throw new ArgumentException("itemNamesJson exceeds 64 KiB.");
            var array = UnifiedRequestValues.Parse(json) as HmiArray ?? throw new ArgumentException("itemNamesJson must be an array of exact object names.");
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
        private static HmiObject Value(string path, object? value, string access)
        {
            if (!MigrationRead.IsScalar(value)) throw new NotSupportedException("Only scalar evidence is read; nested object traversal was not attempted.");
            var row = MigrationRead.Scalar(path, value);
            row["access"] = access;
            return row;
        }
        private static HmiObject TryValue(HmiReadTrace trace, string path, string access, Func<object?> read)
        {
            try { return Value(path, Read(trace, path, read), access); }
            catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { return MigrationRead.Failure(path, "FieldUnavailable", ex); }
        }
        internal static IEnumerable<HmiObject> Capture(object hmi, string screenPath, string[] names, HmiReadTrace trace)
        {
            var screen = Read(trace, screenPath, () => MigrationRead.Screen(hmi, screenPath))!;
            var items = Read(trace, screenPath + "/ScreenItems", () => MigrationRead.Get(screen, "ScreenItems"))!;
            var completed = new List<HmiObject>();
            foreach (var name in names)
            {
                var path = "/Screens" + screenPath + "/ScreenItems/" + MigrationRead.Segment(name);
                HmiObject row;
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
            var summary = new HmiObject { ["kind"] = "graphicSelectionSummary", ["path"] = "/Screens" + screenPath,
                ["status"] = "unsupported", ["code"] = "NativeGraphicGroupUnverified",
                ["reason"] = "This is a caller-defined exact-name selection. Native graphical-group membership, origin and transformed bounds are not established by the available engineering API adapter. ScreenGroups are screen folders. Empty or unavailable group metadata does not mean the objects are ungrouped.",
                ["expectedObjectCount"] = names.Length, ["actualObjectCount"] = completed.Count(r => r["type"] != null),
                ["geometryComplete"] = completed.All(r => r["geometryComplete"]?.GetValue<bool>() == true),
                ["nativeGroupVerified"] = false, ["nativeGroup"] = null, ["nativeGroupBounds"] = null,
                ["rawCoordinateEnvelope"] = Envelope(completed),
                ["coordinateCaveat"] = "Envelope of returned Left/Top/Width/Height only; coordinate-frame equivalence, transforms and rotation are unverified. This is not the editor's group bounding box." };
            yield return summary;
        }
        private static HmiObject CaptureItem(object item, string name, string path, HmiReadTrace trace)
        {
            var started = DateTimeOffset.UtcNow.ToString("o");
            var type = item.GetType();
            var fields = new HmiObject(); var relations = new HmiArray(); var gaps = new HmiArray();
            var attributes = new Dictionary<string, bool>(StringComparer.Ordinal);
            var capabilities = new HmiArray();
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
                        if (RelationName(key)) capabilities.Add(new HmiObject { ["name"] = key, ["accessMode"] = access, ["source"] = "IEngineeringObject.GetAttributeInfos" });
                    }
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { gaps.Add(MigrationRead.Failure(path + "/$attributeInfos", "MetadataReadFailed", ex)); }
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => RelationName(p.Name)))
                capabilities.Add(new HmiObject { ["name"] = p.Name, ["type"] = p.PropertyType.FullName, ["readable"] = PublicGetter(type, p.Name) != null, ["source"] = "public CLR property metadata" });
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
                        capabilities.Add(new HmiObject { ["name"] = key, ["source"] = "IEngineeringObject.GetCompositionInfos", ["contentsRead"] = false });
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
            HmiObject owner;
            var parent = PublicGetter(type, "Parent");
            if (parent == null) owner = MigrationRead.Failure(path + "/Parent", "Unsupported", reason: "No public Parent getter.");
            else
            {
                try
                {
                    var value = Read(trace, path + "/Parent", () => parent.GetValue(item));
                    owner = new HmiObject { ["status"] = "ok", ["evidence"] = path + "/Parent", ["type"] = value?.GetType().FullName,
                        ["meaning"] = "Engineering owner only; not proof of graphical-group membership." };
                    if (value != null && PublicGetter(value.GetType(), "Name") is PropertyInfo np)
                        owner["name"] = TryValue(trace, path + "/Parent/Name", "public property", () => np.GetValue(value));
                }
                catch (Exception ex) when (!HmiReadSafety.ConnectionUnavailable(ex)) { owner = MigrationRead.Failure(path + "/Parent", "OwnerReadFailed", ex); }
            }
            var row = new HmiObject { ["kind"] = "graphicObject", ["name"] = name, ["path"] = path, ["type"] = type.FullName,
                ["sampleStartedUtc"] = started, ["sampleFinishedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["fields"] = fields, ["owner"] = owner, ["relationEvidence"] = relations, ["relationCapabilities"] = capabilities,
                ["gaps"] = gaps, ["nativeGroupVerified"] = false, ["coordinateFrame"] = "unverified raw Openness geometry",
                ["attributeMetadataApiAvailable"] = infos != null, ["compositionMetadataApiAvailable"] = compositions != null };
            bool complete = Geometry.All(f => Number(row, f, out _));
            row["geometryComplete"] = complete;
            bool evidenceComplete = gaps.Count == 0 && owner["status"]?.ToString() == "ok"
                && (owner["name"] == null || owner["name"]!.Field("status")?.ToString() == "ok")
                && relations.All(r => r?.Field("status")?.ToString() == "ok");
            row["status"] = complete && evidenceComplete ? "ok" : "failed";
            if (row["status"]!.ToString() != "ok") { row["code"] = "SelectionEvidenceIncomplete"; row["reason"] = "Inspect fields, owner, relationEvidence and gaps. Missing evidence is not a zero coordinate or an ungrouped object."; }
            return row;
        }
        private static bool Number(HmiObject row, string field, out decimal number)
        {
            number = 0;
            var evidence = row["fields"]?.Field(field);
            if (evidence?.Field("status")?.ToString() != "ok" || !decimal.TryParse(evidence!.Field("value")?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
            return (field != "Width" && field != "Height") || number >= 0;
        }
        private static HmiObject? Envelope(IReadOnlyList<HmiObject> rows)
        {
            if (rows.Count == 0 || rows.Any(r => !Geometry.All(f => Number(r, f, out _)))) return null;
            try
            {
                var rectangles = rows.Select(r => { Number(r, "Left", out var l); Number(r, "Top", out var t); Number(r, "Width", out var w); Number(r, "Height", out var h); return new[] { l, t, checked(l + w), checked(t + h) }; }).ToArray();
                var left = rectangles.Min(r => r[0]); var top = rectangles.Min(r => r[1]);
                return new HmiObject { ["left"] = left, ["top"] = top, ["width"] = checked(rectangles.Max(r => r[2]) - left), ["height"] = checked(rectangles.Max(r => r[3]) - top), ["nativeGroupBounds"] = false };
            }
            catch (OverflowException) /* swallow(parse-fallback): overflowed persisted coordinates cannot provide approximate selection bounds */ { return null; }
        }
    }
}

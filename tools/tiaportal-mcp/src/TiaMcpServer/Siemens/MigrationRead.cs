using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // The only invocations in this adapter are documented read APIs. It cannot
    // invoke caller-supplied methods, create a composition item or write a value.
    internal static class MigrationRead
    {
        internal static string Segment(string value) => Uri.EscapeDataString(value);
        internal static Exception Cause(Exception ex) => ex is TargetInvocationException tie && tie.InnerException != null ? Cause(tie.InnerException) : ex;
        internal static JsonObject Failure(string path, string code, Exception? ex = null, string? reason = null)
            => new JsonObject { ["path"] = path, ["kind"] = "gap", ["status"] = code == "Unsupported" ? "unsupported" : "failed",
                ["code"] = code, ["reason"] = reason ?? (ex == null ? code : Cause(ex).GetType().Name + ": " + Cause(ex).Message) };
        internal static object? Get(object target, string name)
        {
            HmiReadSafety.RequireReadable(target.GetType(), name);
            if (name == "Parent") throw new ArgumentException("Parent navigation is outside the collection scope.");
            var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.GetIndexParameters().Length != 0 || p.GetMethod == null)
                throw new NotSupportedException(target.GetType().FullName + "." + name + " is not a public readable property.");
            return p.GetValue(target);
        }
        internal static object? Attribute(object target, string name)
        {
            HmiReadSafety.RequireReadable(target.GetType(), name);
            var i = target.GetType().GetInterface("Siemens.Engineering.IEngineeringObject");
            var m = i?.GetMethod("GetAttribute", new[] { typeof(string) }) ?? target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
            if (m == null) throw new NotSupportedException("Official GetAttribute API is unavailable.");
            return m.Invoke(target, new object[] { name });
        }
        internal static JsonObject Scalar(string path, object? value, string kind = "value")
        {
            JsonNode? json = null;
            if (value is string str) json = JsonValue.Create(str);
            else if (value is bool b) json = JsonValue.Create(b);
            else if (value != null) json = JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
            return new JsonObject { ["path"] = path, ["kind"] = kind, ["status"] = "ok", ["type"] = value?.GetType().FullName,
                ["value"] = json, ["evidence"] = path };
        }
        internal static bool IsScalar(object? v) => v == null || v is string || v is decimal || v is DateTime || v is Guid || v is Version || v.GetType().IsPrimitive || v.GetType().IsEnum;
        internal static int Count(object collection) => Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        internal static object At(object collection, int index)
        {
            if (collection is IList list) return list[index]!;
            var p = collection.GetType().GetProperty("Item", new[] { typeof(int) })
                ?? collection.GetType().GetInterfaces().Select(t => t.GetProperty("Item", new[] { typeof(int) })).FirstOrDefault(x => x != null);
            if (p == null) throw new NotSupportedException("Collection has no indexed read API; no unbounded enumeration was attempted.");
            return p.GetValue(collection, new object[] { index }) ?? throw new InvalidOperationException("Null collection item.");
        }
        internal static object Named(object collection, string name, string key = "Name", int max = 10000)
        {
            // Find is an official non-mutating composition read, with ordinal
            // verification so Siemens' case-insensitive lookup cannot select a near match.
            if (key == "Name")
            {
                var find = collection.GetType().GetMethod("Find", new[] { typeof(string) });
                if (find != null)
                {
                    var value = find.Invoke(collection, new object[] { name });
                    if (value != null && string.Equals(Get(value, key)?.ToString(), name, StringComparison.Ordinal)) return value;
                    throw new KeyNotFoundException("Exact name not found: " + name);
                }
            }
            int count = Count(collection); if (count > max) throw new InvalidOperationException("Lookup exceeds " + max + " items; use an exact indexed branch.");
            object? match = null;
            for (int j = 0; j < count; j++) { var v = At(collection, j); if (Get(v, key)?.ToString() != name) continue;
                if (match != null) throw new InvalidOperationException("Ambiguous " + key + ": " + name); match = v; }
            return match ?? throw new KeyNotFoundException("Exact " + key + " not found: " + name);
        }
        internal static object Screen(object hmi, string screenPath)
        {
            if (!screenPath.StartsWith("/") || screenPath.EndsWith("/")) throw new ArgumentException("screenPath must be an absolute URI-escaped path from ListHmiScreenPaths.");
            var parts = screenPath.Substring(1).Split('/'); object owner = hmi;
            for (int j = 0; j < parts.Length - 1; j++) owner = Named(Get(owner, j == 0 ? "ScreenGroups" : "Groups")!, Uri.UnescapeDataString(parts[j]));
            return Named(Get(owner, "Screens")!, Uri.UnescapeDataString(parts[parts.Length - 1]));
        }
        internal static object? Branch(object? root, string branchJson)
        {
            var steps = JsonNode.Parse(branchJson) as JsonArray ?? throw new ArgumentException("branchJson must be a JSON array.");
            if (steps.Count > 64) throw new ArgumentException("At most 64 branch segments.");
            foreach (var node in steps)
            {
                if (root == null) throw new InvalidOperationException("Null intermediate branch.");
                var step = node as JsonObject ?? throw new ArgumentException("Each branch segment must be an object.");
                if (step.ContainsKey("property") && step.Count == 1) root = Get(root, step["property"]!.GetValue<string>());
                else if (step.ContainsKey("attribute") && step.Count == 1) root = Attribute(root, step["attribute"]!.GetValue<string>());
                else if (step.ContainsKey("index") && step.Count == 1) root = At(root, step["index"]!.GetValue<int>());
                else if (step.ContainsKey("name") && step.All(k => k.Key == "name" || k.Key == "key"))
                    root = Named(root, step["name"]!.GetValue<string>(), step["key"]?.GetValue<string>() ?? "Name");
                else throw new ArgumentException("Allowed segments: {property}, {attribute}, {index}, {name,key?}. Methods and writes are forbidden.");
            }
            return root;
        }
        internal static IEnumerable<JsonObject> Collection(object collection, string path, Func<object, string, IEnumerable<JsonObject>> visit)
        {
            int count = 0; JsonObject? error = null;
            try { count = Count(collection); } catch (Exception ex) { error = Failure(path, "CountFailed", ex); }
            if (error != null) { yield return error; yield break; }
            yield return new JsonObject { ["path"] = path, ["kind"] = "collection", ["status"] = "ok", ["expectedCount"] = count, ["type"] = collection.GetType().FullName };
            for (int j = 0; j < count; j++)
            {
                object? value = null; error = null;
                try { if (Count(collection) != count) throw new InvalidOperationException("CollectionChanged: start a new collection after edits stop."); value = At(collection, j); }
                catch (Exception ex) { error = Failure(path + "/[" + j + "]", "ItemReadFailed", ex); }
                if (error != null) { yield return error; if (value == null) continue; }
                var itemPath = path + "/[" + j + "]";
                foreach (var row in Safe(() => visit(value!, itemPath), itemPath)) yield return row;
            }
            try { if (Count(collection) != count) error = Failure(path, "CollectionChanged"); } catch (Exception ex) { error = Failure(path, "CountFailed", ex); }
            if (error != null) yield return error;
            yield return new JsonObject { ["path"] = path, ["kind"] = "collectionEnd", ["status"] = "ok", ["visitedCount"] = count };
        }
        internal static IEnumerable<JsonObject> Safe(Func<IEnumerable<JsonObject>> read, string path)
        {
            IEnumerator<JsonObject>? iterator = null; JsonObject? error = null;
            try { iterator = read().GetEnumerator(); } catch (Exception ex) { error = Failure(path, "ReadFailed", ex); }
            if (error != null) { yield return error; yield break; }
            using (iterator)
            {
                while (true)
                {
                    JsonObject? row = null; bool more = false;
                    try { more = iterator!.MoveNext(); if (more) row = iterator.Current; } catch (Exception ex) { error = Failure(path, "ReadFailed", ex); }
                    if (error != null) { yield return error; yield break; }
                    if (!more) yield break;
                    yield return row!;
                }
            }
        }
        internal static IEnumerable<JsonObject> Property(object owner, string name, string path, Func<object?, string, IEnumerable<JsonObject>> visit)
        {
            object? value = null; JsonObject? error = null;
            try { value = Get(owner, name); } catch (Exception ex) { error = Failure(path, Cause(ex) is NotSupportedException ? "Unsupported" : "ReadFailed", ex); }
            if (error != null) { yield return error; yield break; }
            foreach (var row in visit(value, path)) yield return row;
        }
        internal static IEnumerable<JsonObject> Graph(object? root, string path, int depth = 0, HashSet<object>? ancestors = null)
        {
            if (IsScalar(root))
            {
                yield return Scalar(path, root);
                if (root is string code && (path.EndsWith("/ScriptCode", StringComparison.Ordinal) || path.EndsWith("/GlobalDefinitionAreaScriptCode", StringComparison.Ordinal)))
                    yield return JavaScriptEvidence.Analyze(code, path);
                yield break;
            }
            if (depth >= 128) { yield return Failure(path, "DepthLimit", reason: "Select this exact branch to continue; maximum depth 128."); yield break; }
            ancestors ??= new HashSet<object>();
            if (!ancestors.Add(root!)) { yield return Failure(path, "Cycle", reason: "Ancestor cycle; select an exact branch if this is a meaningful association."); yield break; }
            try
            {
                if (root is IEnumerable)
                { foreach (var row in Collection(root, path, (v, p) => Graph(v, p, depth + 1, ancestors))) yield return row; yield break; }
                yield return new JsonObject { ["path"] = path, ["kind"] = "object", ["status"] = "ok", ["type"] = root!.GetType().FullName };
                var properties = root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetMethod != null && p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                foreach (var p in properties)
                {
                    if (p.Name == "Parent") continue; // Official owner backlink, outside the selected subtree.
                    foreach (var row in Property(root, p.Name, path + "/" + Segment(p.Name), (v, q) => Graph(v, q, depth + 1, ancestors))) yield return row;
                }
                // Attributes may exist only in Openness self-description, not as CLR properties.
                object? infos = null; JsonObject? error = null;
                var api = root.GetType().GetInterface("Siemens.Engineering.IEngineeringObject");
                if (api != null)
                {
                    try { infos = api.GetMethod("GetAttributeInfos")!.Invoke(root, Array.Empty<object>()); } catch (Exception ex) { error = Failure(path, "AttributeDiscoveryFailed", ex); }
                    if (error != null) yield return error;
                    if (infos is IEnumerable list) foreach (var info in list)
                    {
                        var name = Get(info, "Name")?.ToString(); if (name == null || name == "Parent" || properties.Any(p => p.Name == name)) continue;
                        object? value = null; error = null;
                        try { value = Attribute(root, name); } catch (Exception ex) { error = Failure(path + "/@" + Segment(name), "AttributeReadFailed", ex); }
                        if (error != null) yield return error;
                        else foreach (var row in Graph(value, path + "/@" + Segment(name), depth + 1, ancestors)) yield return row;
                    }
                }
            }
            finally { ancestors.Remove(root!); }
        }
    }
}

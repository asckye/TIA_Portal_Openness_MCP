using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiExactAccess
    {
        internal static object? Get(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
        internal static List<object> Items(object? value)
        {
            var result = new List<object>();
            if (value is not IEnumerable list || value is string) throw new PortalException(PortalErrorCode.InvalidParams, "Expected collection.");
            foreach (var item in list) { if (item != null) result.Add(item); if (result.Count > 10000) throw new InvalidOperationException("Collection limit exceeded."); }
            return result;
        }
        internal static object Named(object collection, string name)
        {
            var found = Items(collection).Where(x => string.Equals(Get(x,"Name") as string, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return Unique(found, name);
        }
        private static object Unique(List<object> found, string key)
        {
            if (found.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "Object not found: " + key);
            if (found.Count != 1) throw new PortalException(PortalErrorCode.InvalidParams, "Ambiguous object: " + key + "; use a complete path.");
            return found[0];
        }
        internal static List<(string Path, object Value)> Screens(object software)
        {
            var result = new List<(string,object)>();
            var ancestors = new HashSet<object>();
            Walk(software,"",0);
            return result;
            void Walk(object group, string path, int depth)
            {
                if (depth > 64 || result.Count > 10000 || !ancestors.Add(group)) throw new InvalidOperationException("HMI group cycle or traversal limit.");
                var screens = Get(group,"Screens");
                if (screens != null) foreach(var screen in Items(screens)) result.Add((path + "/" + Segment(screen), screen));
                var classic = Get(group,"ScreenFolder");
                if (classic != null) Walk(classic,path,depth+1);
                foreach(var key in new[]{"ScreenGroups","Groups","Folders"})
                {
                    var groups = Get(group,key);
                    if (groups != null) foreach(var child in Items(groups)) Walk(child,path+"/"+Segment(child),depth+1);
                }
                ancestors.Remove(group);
            }
        }
        private static string Segment(object value) => Uri.EscapeDataString(Get(value,"Name") as string
            ?? throw new InvalidOperationException("Object name unavailable."));
        internal static object Screen(object software, string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                throw new PortalException(PortalErrorCode.InvalidParams, "Screen name/path is required.");
            if (!nameOrPath.StartsWith("/"))
                return Unique(Screens(software).Where(x => string.Equals(Get(x.Value,"Name") as string,
                    nameOrPath,StringComparison.OrdinalIgnoreCase)).Select(x=>x.Value).ToList(),nameOrPath);

            // An exact path must not enumerate every screen/subtree before selecting one.
            var parts = nameOrPath.Substring(1).Split('/');
            if (parts.Length > 65 || parts.Any(string.IsNullOrEmpty))
                throw new PortalException(PortalErrorCode.InvalidParams, "Invalid absolute screen path.");
            object current = Get(software, "ScreenFolder") ?? software;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var groups = Get(current, "ScreenGroups") ?? Get(current, "Groups") ?? Get(current, "Folders")
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Screen group collection unavailable: " + nameOrPath);
                current = Named(groups, Uri.UnescapeDataString(parts[i]));
            }
            return Named(Get(current, "Screens")
                ?? throw new PortalException(PortalErrorCode.NotFound, "Screens unavailable: " + nameOrPath),
                Uri.UnescapeDataString(parts[parts.Length - 1]));
        }
        internal static object Group(object software,string path)
        {
            if (!path.StartsWith("/") || path == "/" || path.EndsWith("/")) throw new PortalException(PortalErrorCode.InvalidParams,"Use an absolute group path such as /Folder/Subfolder; root cannot be deleted.");
            object current = software;
            foreach(var part in path.Substring(1).Split('/'))
            {
                if (string.IsNullOrEmpty(part)) throw new PortalException(PortalErrorCode.InvalidParams,"Empty group path segment.");
                var collection = Get(current,"ScreenGroups") ?? Get(current,"Groups")
                    ?? throw new PortalException(PortalErrorCode.NotFound,"Group collection not found.");
                current = Named(collection,Uri.UnescapeDataString(part));
            }
            return current;
        }
        internal static object Event(object button,string type)
            => HmiEventAccess.Resolve(Get(button,"EventHandlers") ?? throw new PortalException(PortalErrorCode.NotFound,"EventHandlers unavailable."),type);
        internal static JsonObject EventDto(object handler)
        {
            var scriptRead = PropertyPathReader.Read(handler,"Script");
            if (!scriptRead.Success) throw new InvalidOperationException("Script: "+scriptRead.Status+" "+scriptRead.Error);
            var script = scriptRead.Value;
            var result = new JsonObject { ["eventType"] = Get(handler,"EventType")?.ToString(), ["scriptExists"] = script != null };
            foreach(var name in new[]{"ScriptCode","GlobalDefinitionAreaScriptCode","Async"})
            {
                var value = script == null ? null : PropertyPathReader.Read(script,name);
                if (value != null && !value.Success) throw new InvalidOperationException(name+": "+value.Status+" "+value.Error);
                if (value?.Value is bool flag) result[name] = flag;
                else if(value?.Value is string str) result[name] = str;
                else if(value?.Value == null) result[name] = null;
                else throw new InvalidOperationException("Unexpected event script property type: "+name);
            }
            return result;
        }
        internal static string Token(string scope, JsonNode data)
        {
            // Inspection timing is diagnostic metadata, not guarded engineering content.
            var content = data.DeepClone();
            if (content is JsonObject snapshot && snapshot["kind"]?.ToString() == "InspectionSnapshot")
                snapshot.Remove("elapsedMs");
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope+"\n"+content.ToJsonString()))).Replace("-", "").ToLowerInvariant();
        }
        internal static void Delete(object value)
        {
            var method = value.GetType().GetMethod("Delete",Type.EmptyTypes)
                ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion,"Delete() unavailable.");
            method.Invoke(value,Array.Empty<object>());
        }
        internal static void RequireToken(string? expected,string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected,actual,StringComparison.Ordinal))
                throw new PortalException(PortalErrorCode.InvalidState,"Content changed or expectedToken missing; read/preview again.");
        }
    }
}

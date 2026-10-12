using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class HmiExactAccess
    {
        // Both worker restart and explicit project rebinding invalidate old preview hashes.

        internal static object? Get(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
        internal static List<object> Items(object? value)
        {
            var result = new List<object>();
            if (value is not IEnumerable list || value is string) throw new PlcSoftwareException("InvalidParams", "Expected collection.");
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
            if (found.Count == 0) throw new PlcSoftwareException("NotFound", "Object not found: " + key);
            if (found.Count != 1) throw new PlcSoftwareException("InvalidParams", "Ambiguous object: " + key + "; use a complete path.");
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
                throw new PlcSoftwareException("InvalidParams", "Screen name/path is required.");
            if (!nameOrPath.StartsWith("/", StringComparison.Ordinal))
                return Unique(Screens(software).Where(x => string.Equals(Get(x.Value,"Name") as string,
                    nameOrPath,StringComparison.OrdinalIgnoreCase)).Select(x=>x.Value).ToList(),nameOrPath);

            // An exact path must not enumerate every screen/subtree before selecting one.
            var parts = nameOrPath.Substring(1).Split('/');
            if (parts.Length > 65 || parts.Any(string.IsNullOrEmpty))
                throw new PlcSoftwareException("InvalidParams", "Invalid absolute screen path.");
            object current = Get(software, "ScreenFolder") ?? software;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var groups = Get(current, "ScreenGroups") ?? Get(current, "Groups") ?? Get(current, "Folders")
                    ?? throw new PlcSoftwareException("NotFound", "Screen group collection unavailable: " + nameOrPath);
                current = Named(groups, Uri.UnescapeDataString(parts[i]));
            }
            return Named(Get(current, "Screens")
                ?? throw new PlcSoftwareException("NotFound", "Screens unavailable: " + nameOrPath),
                Uri.UnescapeDataString(parts[parts.Length - 1]));
        }
        internal static object Group(object software,string path)
        {
            if (!path.StartsWith("/", StringComparison.Ordinal) || path == "/" || path.EndsWith("/", StringComparison.Ordinal)) throw new PlcSoftwareException("InvalidParams","Use an absolute group path such as /Folder/Subfolder; root cannot be deleted.");
            object current = software;
            foreach(var part in path.Substring(1).Split('/'))
            {
                if (string.IsNullOrEmpty(part)) throw new PlcSoftwareException("InvalidParams","Empty group path segment.");
                var collection = Get(current,"ScreenGroups") ?? Get(current,"Groups")
                    ?? throw new PlcSoftwareException("NotFound","Group collection not found.");
                current = Named(collection,Uri.UnescapeDataString(part));
            }
            return current;
        }



        internal static void Delete(object value)
        {
            var method = value.GetType().GetMethod("Delete",Type.EmptyTypes)
                ?? throw new PlcSoftwareException("NotSupportedOnVersion","Delete() unavailable.");
            method.Invoke(value,Array.Empty<object>());
        }

    }
}

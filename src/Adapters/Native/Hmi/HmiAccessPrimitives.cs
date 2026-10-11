using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Native.Hmi
{
    public static class HmiAccessPrimitives
    {
        public static object? Get(object value, string name) => HmiExactAccess.Get(value, name);
        public static List<object> Items(object? value) => HmiExactAccess.Items(value);
        public static object Named(object collection, string name) => HmiExactAccess.Named(collection, name);
        public static List<(string Path, object Value)> Screens(object software) => HmiExactAccess.Screens(software);
        public static object Screen(object software, string path) => HmiExactAccess.Screen(software, path);
        public static object Group(object software, string path) => HmiExactAccess.Group(software, path);
        public static void Delete(object value) => HmiExactAccess.Delete(value);
        public static Dictionary<string, object?> Capture(object root, int maxDepth = 6, int maxNodes = 2000, int maxString = 65536,
            Action<string, string, Exception?>? trace = null) => HmiSnapshot.Capture(root, maxDepth, maxNodes, maxString, trace);
        public static List<string> ListNames(object root) => HmiScreenTraversal.ListNames(root);
        public static object? FindByName(object root, string name) => HmiScreenTraversal.FindByName(root, name);
        public static string[] ValidateTagDeletion(string path, string name, bool dryRun, bool confirm) => HmiTagDeletion.Validate(path, name, dryRun, confirm);
        public static string DeleteTag(Dictionary<string, object?> values, bool dryRun, Func<object?> find, Action<object> delete)
        {
            var meta = new HmiObject(values);
            try { return HmiTagDeletion.Execute(meta, dryRun, find, delete); }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }
    }
}

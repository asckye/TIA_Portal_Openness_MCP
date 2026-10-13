using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class MigrationRead
    {

        internal static string Segment(string value)
            => UnifiedPrimitiveCompatibility.Call<string>("MigrationRead", "Segment", value);
        internal static Exception Cause(Exception ex)
            => UnifiedPrimitiveCompatibility.Call<Exception>("MigrationRead", "Cause", ex);
        internal static JsonObject Failure(string path, string code, Exception? ex = null, string? reason = null)
            => UnifiedPrimitiveCompatibility.Call<JsonObject>("MigrationRead", "Failure", path, code, ex, reason);
        internal static object? Get(object target, string name)
            => UnifiedPrimitiveCompatibility.Call<object?>("MigrationRead", "Get", target, name);
        internal static object? Attribute(object target, string name)
            => UnifiedPrimitiveCompatibility.Call<object?>("MigrationRead", "Attribute", target, name);
        internal static JsonObject Scalar(string path, object? value, string kind = "value")
            => UnifiedPrimitiveCompatibility.Call<JsonObject>("MigrationRead", "Scalar", path, value, kind);
        internal static bool IsScalar(object? v)
            => UnifiedPrimitiveCompatibility.Call<bool>("MigrationRead", "IsScalar", v);
        internal static int Count(object collection)
            => UnifiedPrimitiveCompatibility.Call<int>("MigrationRead", "Count", collection);
        internal static object At(object collection, int index)
            => UnifiedPrimitiveCompatibility.Call<object>("MigrationRead", "At", collection, index);
        internal static object Named(object collection, string name, string key = "Name", int max = 10000)
            => UnifiedPrimitiveCompatibility.Call<object>("MigrationRead", "Named", collection, name, key, max);
        internal static object Screen(object hmi, string screenPath)
            => UnifiedPrimitiveCompatibility.Call<object>("MigrationRead", "Screen", hmi, screenPath);
        internal static object? Branch(object? root, string branchJson)
            => UnifiedPrimitiveCompatibility.Call<object?>("MigrationRead", "Branch", root, branchJson);
        internal static IEnumerable<JsonObject> Collection(object collection, string path, Func<object, string, IEnumerable<JsonObject>> visit)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("MigrationRead", "Collection", collection, path, visit);
        internal static IEnumerable<JsonObject> Safe(Func<IEnumerable<JsonObject>> read, string path)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("MigrationRead", "Safe", read, path);
        internal static IEnumerable<JsonObject> Property(object owner, string name, string path, Func<object?, string, IEnumerable<JsonObject>> visit)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("MigrationRead", "Property", owner, name, path, visit);
        internal static IEnumerable<JsonObject> Graph(object? root, string path, int depth = 0, HashSet<object>? ancestors = null)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("MigrationRead", "Graph", root, path, depth, ancestors);
    }
}

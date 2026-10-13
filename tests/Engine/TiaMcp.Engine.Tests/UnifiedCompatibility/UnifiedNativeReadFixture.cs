using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedNativeRead
    {

        internal static object ExactType(object projectLibrary, string typePath)
            => UnifiedPrimitiveCompatibility.Call<object>("UnifiedNativeRead", "ExactType", projectLibrary, typePath);
        internal static object ExactVersion(object type, string version)
            => UnifiedPrimitiveCompatibility.Call<object>("UnifiedNativeRead", "ExactVersion", type, version);
        internal static IEnumerable<JsonObject> ListScripts(object hmi)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedNativeRead", "ListScripts", hmi);
        internal static IEnumerable<JsonObject> Script(object hmi, string moduleName)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedNativeRead", "Script", hmi, moduleName);
        internal static JsonObject InstanceLink(object item, string path)
            => UnifiedPrimitiveCompatibility.Call<JsonObject>("UnifiedNativeRead", "InstanceLink", item, path);
        internal static IEnumerable<JsonObject> LibraryType(object library, string typePath, string versionNumber)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedNativeRead", "LibraryType", library, typePath, versionNumber);
        internal static IEnumerable<JsonObject> Export(object target, string scope, bool libraryVersion)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedNativeRead", "Export", target, scope, libraryVersion);
        internal static IEnumerable<JsonObject> Document(string text, string path, string extension)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedNativeRead", "Document", text, path, extension);
    }
}

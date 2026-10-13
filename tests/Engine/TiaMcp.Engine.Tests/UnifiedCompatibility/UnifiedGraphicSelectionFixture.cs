using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedGraphicSelection
    {
        internal static string[] Geometry => UnifiedPrimitiveCompatibility.Field<string[]>(null, "UnifiedGraphicSelection", "Geometry");
        internal static string[] Names(string json)
            => UnifiedPrimitiveCompatibility.Call<string[]>("UnifiedGraphicSelection", "Names", json);
        internal static IEnumerable<JsonObject> Capture(object hmi, string screenPath, string[] names, HmiReadTrace trace)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedGraphicSelection", "Capture", hmi, screenPath, names, trace);
        internal static JsonObject Compare(string beforePagesJson, string afterPagesJson)
            => Services.UnifiedGraphicComparison.Compare(beforePagesJson, afterPagesJson);
    }
}

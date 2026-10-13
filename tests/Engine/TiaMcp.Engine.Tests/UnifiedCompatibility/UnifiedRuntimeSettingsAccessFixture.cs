using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedRuntimeSettingsAccess
    {
        internal const string DefaultFields = "[\"StartScreen\",\"ScreenResolution\"]";
        internal static string[] Allowed => UnifiedPrimitiveCompatibility.Field<string[]>(null, "UnifiedRuntimeSettingsAccess", "Allowed");
        internal static string[] Fields(string json)
            => UnifiedPrimitiveCompatibility.Call<string[]>("UnifiedRuntimeSettingsAccess", "Fields", json);
        internal static JsonObject Changes(string json)
            => UnifiedPrimitiveCompatibility.Call<JsonObject>("UnifiedRuntimeSettingsAccess", "Changes", json);
        internal static void Read(object hmi, string[] fields, JsonObject meta, HmiReadTrace trace)
            => UnifiedPrimitiveCompatibility.Call("UnifiedRuntimeSettingsAccess", "Read", hmi, fields, meta, trace);
        internal static string Update(object hmi, string project, string softwarePath, JsonObject changes, bool dryRun, string expectedToken, JsonObject meta, HmiReadTrace trace)
            => UnifiedPrimitiveCompatibility.Call<string>("UnifiedRuntimeSettingsAccess", "Update", hmi, project, softwarePath, changes, dryRun, expectedToken, meta, trace);
    }
}

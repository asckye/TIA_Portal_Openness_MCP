using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedTagDefinitions
    {

        internal static IEnumerable<JsonObject> Read(object hmi)
            => UnifiedPrimitiveCompatibility.Call<IEnumerable<JsonObject>>("UnifiedTagDefinitions", "Read", hmi);
        internal static JsonObject Bounds(string dataType)
            => UnifiedPrimitiveCompatibility.Call<JsonObject>("UnifiedTagDefinitions", "Bounds", dataType);
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedScriptAccess
    {

        internal static object Scripts(object hmi)
            => UnifiedPrimitiveCompatibility.Call<object>("UnifiedScriptAccess", "Scripts", hmi);
        internal static object Module(object hmi, string moduleName)
            => UnifiedPrimitiveCompatibility.Call<object>("UnifiedScriptAccess", "Module", hmi, moduleName);
        internal static object Resolve(Func<string, object> resolveHmi, string objectPath, string softwarePath, bool collection)
            => UnifiedPrimitiveCompatibility.Call<object>("UnifiedScriptAccess", "Resolve", resolveHmi, objectPath, softwarePath, collection);
    }
}

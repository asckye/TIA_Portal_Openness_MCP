using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedGlobalScriptEdit
    {

        internal static string Execute(Func<object> resolveHmi, string project, string softwarePath, string moduleName,
            string scriptCode, bool dryRun, string expectedToken, JsonObject meta)
            => UnifiedPrimitiveCompatibility.Call<string>("UnifiedGlobalScriptEdit", "Execute", resolveHmi, project, softwarePath, moduleName, scriptCode, dryRun, expectedToken, meta);
        internal static TiaMcp.Adapters.Contracts.UnifiedScriptPair ReadPair(string directory, string moduleName) => Services.UnifiedScriptFileCodec.ReadPair(directory, moduleName);
    }
}

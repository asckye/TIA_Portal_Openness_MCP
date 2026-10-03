using System;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // Runtime-specific journal serialization; call tracking is shared.
    internal static partial class NativeCallDiagnostics
    {
        private static void Write(Span span, string phase, Exception? error)
        {
            var exceptions = new JArray();
            for (var current = error; current != null && exceptions.Count < 8; current = current.InnerException)
                exceptions.Add(new JObject { ["type"] = current.GetType().FullName, ["hresult"] = current.HResult });
            InvocationJournal.Write(span.Correlation, "native:" + span.Member, phase, span.Type, span.Path, new JObject {
                ["nativeCallId"] = span.Id, ["parentNativeCallId"] = span.Parent?.Id, ["callSite"] = span.Site,
                ["member"] = span.Member, ["dispatch"] = span.Category, ["objectId"] = span.ObjectId,
                ["pathKind"] = "observed-access-lineage", ["exceptionType"] = error?.GetType().FullName,
                ["hresult"] = error == null ? (int?)null : error.HResult, ["exceptionChain"] = exceptions });
        }
    }
}

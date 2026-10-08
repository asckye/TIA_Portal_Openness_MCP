using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

// The linked offline builder source has unused native namespace imports. These
// anchors supply names only; no Siemens API is referenced by the net10 host.
namespace Siemens.Engineering.SW.Blocks { internal sealed class OfflineNamespaceAnchor { } }
namespace TiaMcpServer.Siemens.Services { internal sealed class OfflineNamespaceAnchor { } }

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal static string ReleaseKey => "21";
        internal static Error InvalidInput(string parameter) => new("Input does not satisfy the declared contract.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        internal static Paging OffsetPage(int offset, int limit, int total) => new(PagingMode.Offset, offset, limit,
            (long)offset + limit < total ? offset + limit : null, null, null, total, (long)offset + limit >= total);
    }
    // Only ImportOrderTools' offline helper is referenced by the linked contract file.
    // It is not advertised by this slice.
    internal static class OfflineToolExecution
    {
        internal static ResponseMessage RunOfflineAnalysisTool(string tool, Func<JsonObject, string> action)
        {
            var meta = ResponseMeta.Step(tool, ("offlineOnly", true));
            string message = action(meta);
            ResponseMeta.Complete(meta, true);
            return new ResponseMessage { Message = message, Meta = meta };
        }
    }
    public static class McpHints
    {
        public static string Recovery(Exception? error) => error is ArgumentException
            ? TiaMcp.Logic.ModelContextProtocol.RecoveryHints.RecoveryCode("INVALID_ARGUMENT") : "";
    }
}

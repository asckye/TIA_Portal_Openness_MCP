// Minimal SDK/response boundary stand-ins. The production CallTool source is linked unchanged.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ModelContextProtocol.Server
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class McpServerToolAttribute : Attribute { public string? Name { get; set; } }
}
namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseMessage { public string? Message { get; set; } public JsonObject? Meta { get; set; } }
    public class ResponseStringList : ResponseMessage { public IEnumerable<string>? Items { get; set; } }
    public static partial class McpServer
    {
        private static bool IsLiteProfile() => false;
        private static readonly HashSet<string> LiteToolNames = new HashSet<string>();
        [global::ModelContextProtocol.Server.McpServerTool]
        public static ResponseMessage ProbeResult(bool success) => new ResponseMessage { Message = "probe", Meta = new JsonObject { ["success"] = success } };
        [global::ModelContextProtocol.Server.McpServerTool]
        public static ResponseMessage ProbeThrow() => throw new InvalidOperationException("probe exception");
        public sealed class Cycle { public Cycle Self => this; }
        [global::ModelContextProtocol.Server.McpServerTool]
        public static Cycle ProbeCycle() => new Cycle();
    }
}

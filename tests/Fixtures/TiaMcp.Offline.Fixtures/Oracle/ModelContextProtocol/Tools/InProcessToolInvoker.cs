using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class InProcessToolInvoker : IToolInvoker
    {
        private readonly ToolCatalog catalog;
        internal InProcessToolInvoker(ToolCatalog catalog) { this.catalog = catalog; }

        public Error? Bind(string name, ToolArguments arguments, out IBoundToolCall? call)
        {
            var error = McpServer.BindV4Call(name, arguments, out var method, out var values);
            call = error == null ? new BoundCall(method!, values!) : null;
            return error;
        }

        public ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview)
        {
            var error = Bind(name, arguments, out var call);
            return error == null ? call!.Invoke(preview) : new ToolInvocationResult(McpServer.V4Reject(name, error), false);
        }

        public Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false)
        {
            var method = catalog.Methods.First(pair => pair.Key == tool.Name).Value;
            return typedFamiliesOnly ? McpServer.ValidateReflectedTypedFamilies(method, arguments)
                : McpServer.ValidateV4Arguments(method, arguments, schema);
        }

        public McpServerTool CreateTool(ToolDescriptor tool) => catalog.RuntimeTool(tool.Name);

        private sealed class BoundCall : IBoundToolCall
        {
            private readonly MethodInfo method;
            private readonly object?[] arguments;
            internal BoundCall(MethodInfo method, object?[] arguments) { this.method = method; this.arguments = arguments; }
            public ToolInvocationResult Invoke(bool preview)
            {
                using var previewScope = preview ? McpServer.BeginReadOnlyApprovalPreview() : null;
                // NativeCallStarted also records evidence in every parent dispatch scope.
                using var native = InvocationJournal.BeginNativeCallScope();
                var result = McpServer.ToolResult(McpServer.InvokeToolMethod(method, arguments));
                return new ToolInvocationResult(result, native.NativeCallIssued);
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class McpPromptRegistration
    {
        internal static void Configure(IMcpServerBuilder builder)
        {
            // Do not scan the server assembly: unrelated optional engineering types
            // can require Startdrive/DCC assemblies that are not installed.
            // Keep this complete list in sync with every [McpServerPromptType].
            builder.WithPrompts(new[] { typeof(McpPrompts) });
        }
    }
}

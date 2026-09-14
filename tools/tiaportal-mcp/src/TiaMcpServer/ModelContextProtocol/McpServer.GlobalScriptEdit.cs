using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "UpdateUnifiedGlobalScript"), Description("[L2][HMI-Unified][WRITE]Update one existing global Scripts module (e.g. Navigation) using official native JS/YAML export and import. Default dryRun=true returns complete before/after text, backup path and token. Apply the SAME full scriptCode with dryRun=false and expectedToken; current content is rechecked, then re-exported for exact verification. Never uses event ScriptCode setters, creates/deletes modules, saves, compiles or deletes pages. Import false/exception/readback mismatch is NOT success; partial changes may exist. No automatic retry after connection failure.")]
        public static ResponseMessage UpdateUnifiedGlobalScript(
            [Description("Explicit HMI software path, as used by ReadUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact existing global module name, e.g. Navigation; case-sensitive.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string expectedToken = "")
            => Portal.UpdateUnifiedGlobalScript(softwarePath, expectedProject, moduleName, scriptCode, dryRun, expectedToken);
    }
}

using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class GlobalScriptEditTools
    {
        private readonly GlobalScriptEditService _globalScriptEdit;

        public GlobalScriptEditTools(GlobalScriptEditService service) => _globalScriptEdit = service;

        [McpServerTool(Name = "SetUnifiedGlobalScript"), Description("[L2][HMI-Unified][WRITE]Update one existing global Scripts module (e.g. Navigation) using official native JS/YAML export and import. Default dryRun=true returns complete before/after text, backup path and token. Apply the SAME full scriptCode with dryRun=false and expectedToken; current content is rechecked, then re-exported for exact verification. Never uses event ScriptCode setters, creates/deletes modules, saves, compiles or deletes pages. Import false/exception/readback mismatch is NOT success; partial changes may exist. No automatic retry after connection failure. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedGlobalScriptV4(
            [Description("Explicit HMI software path, as used by GetUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact existing global module name, e.g. Navigation; case-sensitive.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string expectedToken = "")
            => HmiInspectionContract.Run("SetUnifiedGlobalScript", !dryRun, true, () => UpdateUnifiedGlobalScript(softwarePath, expectedProject, moduleName, scriptCode, dryRun, expectedToken));

        public ResponseMessage UpdateUnifiedGlobalScript(
            [Description("Explicit HMI software path, as used by GetUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact existing global module name, e.g. Navigation; case-sensitive.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string expectedToken = "")
        => _globalScriptEdit.UpdateUnifiedGlobalScript(softwarePath, expectedProject, moduleName, scriptCode, dryRun, expectedToken);
    }
}

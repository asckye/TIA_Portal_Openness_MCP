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

        [McpServerTool(Name = "SetUnifiedGlobalScript"),
                Description("[L2][HMI-Unified][WRITE]Update an existing global Scripts module (default action=update), or create one new module from " +
                "complete scriptCode using official native JS/YAML import (action=create). Create refuses an existing name, including case variants. " +
                "No Openness delete exists for script modules; remove an unwanted create manually in TIA. Default dryRun=true returns proposed text, " +
                "hashes and " +
                "previewToken; updates also retain the native backup and before text. Execution requires the SAME arguments, previewToken, expectedPlanHash " +
                "and expectedProjectFile. Current content/absence is rechecked and exact full text is verified by export. No save/compile. Import false, " +
                "exception or readback mismatch is NOT success; partial changes may exist. No automatic retry after connection failure. Native " +
                "behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedGlobalScriptV4(
            [Description("Explicit HMI software path, as used by GetUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact global module name, e.g. Navigation; create requires an absent name, including case variants.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 " +
                "MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string previewToken = "",
            [Description("update | create; default update.")] string action = "update",
            [Description("planHash from this tool's preview for the same arguments and bound project; required for execution.")] string expectedPlanHash = "",
            [Description("Absolute bound project file from the preview target identity; required for execution.")] string expectedProjectFile = "")
            => McpServer.RunNativeWritePreview("SetUnifiedGlobalScript", dryRun,
                new { softwarePath, expectedProject, moduleName, scriptCode, action },
                () => Run(softwarePath, expectedProject, moduleName, scriptCode, true, previewToken, action),
                () => Run(softwarePath, expectedProject, moduleName, scriptCode, false, previewToken, action), expectedPlanHash, expectedProjectFile);

        private CallToolResult Run(string softwarePath, string expectedProject, string moduleName, string scriptCode,
            bool dryRun, string previewToken, string action)
            => HmiInspectionContract.Run("SetUnifiedGlobalScript", !dryRun, true, () =>
                UpdateUnifiedGlobalScript(softwarePath, expectedProject, moduleName, scriptCode, dryRun, previewToken, action));

        public ResponseMessage UpdateUnifiedGlobalScript(
            [Description("Explicit HMI software path, as used by GetUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact existing global module name, e.g. Navigation; case-sensitive.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 " +
                "MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string expectedToken = "",
            string action = "update")
        => _globalScriptEdit.UpdateUnifiedGlobalScript(softwarePath, expectedProject, moduleName, scriptCode, dryRun, expectedToken, action);
    }
}

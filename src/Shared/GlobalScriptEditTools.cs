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
                Description("[L2][HMI-Unified][WRITE]Update an existing global Scripts module with complete scriptCode. " +
                "Create execution is unavailable pending real TIA verification. Create the module in TIA, then use action=update. " +
                "Default dryRun=true previews proposed text and hashes. Updates retain the native backup and before text. " +
                "Apply the same arguments with previewToken, expectedPlanHash and expectedProjectFile. Exact full text is verified by export. " +
                "No save or compile. Inspect failed imports or readback mismatches before any further write. Native behaviorPolicy=current.")]
        public CallToolResult UpdateUnifiedGlobalScriptV4(
            [Description("Explicit HMI software path, as used by GetUnifiedGlobalScript.")] string softwarePath,
            [Description("Exact currently open project name; mismatch refuses the operation.")] string expectedProject,
            [Description("Exact global module name, e.g. Navigation; create requires an absent name, including case variants.")] string moduleName,
            [Description("COMPLETE modified native .hmi.js source including imports, global definitions and functions; not a single function body. Maximum 1 " +
                "MiB.")] string scriptCode,
            [Description("Preview and back up only by default. Set false only to apply the reviewed text.")] bool dryRun = true,
            [Description("Token from this tool's preview for the same project, HMI, module, original and proposed content.")] string previewToken = "",
            [Description("Default update. Create supports preview only pending real TIA verification.")] string action = "update",
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

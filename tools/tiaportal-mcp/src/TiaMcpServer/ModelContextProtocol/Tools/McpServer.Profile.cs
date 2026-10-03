using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    // Tool roster size. DEFAULT = lite: ~48 essentials instead of ~200, so a small /
    // non-expert model is not drowned in choices, hosts with a tool cap (Copilot 128,
    // Windsurf 100) can load the server at all, and every turn carries ~8k instead of
    // ~40k tokens of schema. Opt out per session with --profile full / TIA_MCP_PROFILE=full;
    // reach any individual non-lite tool without opting out via FindTools + CallTool.
    public static partial class McpServer
    {
        // Explicit allowlist (tool Name, not method name). Kept explicit on purpose:
        // membership must not silently change when a [Lx] description prefix is edited.
        // Include all [L0]/[L1] tools and the golden-path tools named by ServerInstructions/GetAuthoringGuide,
        // so every profile exposes the tools its instructions ask the model to call.
        private static readonly HashSet<string> LiteToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // L0 — discovery and invocation for every tool outside the advertised lite roster.
            "FindTools", "CallTool", "ListToolCategories",
            // Preflight belongs next to the bridge so callers can check a call before making it in every profile.
            // Update checking is available alongside the other diagnostics.
            "PreflightToolCall", "CheckForUpdate",
            // The verified sequences belong next to the guide.
            "GetRecipe", "GetToolUsage",
            // L0 — orientation / diagnostics
            "Bootstrap", "Doctor", "GetState", "GetAuthoringGuide",
            "GenerateAcceptanceReport", "GenerateErrorReport",
            "RunCapabilitySelfTest", "RunOnlineMonitoringSafetySelfTest",
            // L1 — session / project lifecycle
            "Connect", "ConnectToProject", "Disconnect", "ListPortalProcessProjects", "EnsureOpennessUserGroup",
            // 用户在博途界面里开着工程时，Connect 会接管那个实例、OpenProject 又拒绝动它，
            // 整台服务器就用不了了。这条出口必须在默认档里看得见 —— 挡掉它等于让
            // 「用户正在用博途」变成一个无解的死局。
            "ConnectIsolated",
            "OpenProject", "AttachToOpenProject", "CreateProject", "SaveProject", "CloseProject",
            "GetProject", "GetProjectTree", "ValidateAutomationContext",
            // L1 — read / understand
            "GetSoftwareInfo", "GetSoftwareTree", "GetDevices", "DescribeBlockLogic",
            // L1 — build / import / compile
            "ScaffoldProject", "PlcBuildAndImport", "ImportBlock", "ImportType",
            "ImportPlcTagTable", "WritePlcSclSourceFile",
            "CompileSoftware", "CompileAndDiagnosePlc",
            // The HMI counterpart. Without it a lite session can generate Unified screens but
            // cannot read its own HMI compile errors, so it has to hand the project back to the
            // engineer to compile in the UI (#24).
            "CompileAndDiagnoseHmi",
            // L1 — hardware
            "AddDeviceWithFallback", "SearchHardwareCatalog", "ConnectDeviceNodesToProfinetSubnet",
            // Golden-path tools referenced by ServerInstructions / GetAuthoringGuide
            // (required in lite so the roster agrees with those instructions)
            "ImportFromDocuments", "GenerateBlocksFromExternalSource",
            // Batch SD import/export are the "PREFERRED on V21+" batch path in the same
            // instructions; tag tables and cross-references are what a model needs to read a
            // project it did not write.
            "ImportBlocksFromDocuments", "ExportBlocksAsDocuments",
            "GetPlcTagTables", "GetCrossReferences",
            "GetBlocks", "GetBlocksWithHierarchy", "GetBlockInfo",
            "ExportAsDocuments", "GoOffline",
            // 大响应寄存与分页。**任何档都必须能翻页** —— 超过阈值的响应会被寄存，
            // 挡掉这几个出口等于内容直接丢：真实工程上 GetBlocks 的首页只装得下十几个块，
            // 剩下的拿不回来。它们只碰引擎自己内存里的那份副本，一个都不动 TIA 工程。
            "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports",
            "ReadOpennessWorkerStatus", "RestartOpennessWorker",
        };

        public static IList<McpServerTool> GetLiteTools()
        {
            var tools = new List<McpServerTool>();
            foreach (var entry in ToolCatalog.Engine.Methods)
            {
                var name = entry.Key;
                if (LiteToolNames.Contains(name) && VersionToolProblem(name).Length == 0)
                {
                    tools.Add(CreateTool(name, entry.Value));
                }
            }
            return tools;
        }

        /// <summary>
        /// 全量工具表。注册前先取得列表，才能统一包装每个工具，接入参数诊断和大响应分页。
        /// </summary>
        public static IList<McpServerTool> GetAllTools()
        {
            var tools = new List<McpServerTool>();
            foreach (var entry in ToolCatalog.Engine.Methods)
            {
                var name = entry.Key;
                if (VersionToolProblem(name).Length == 0) tools.Add(CreateTool(name, entry.Value));
            }
            return tools;
        }

        // The protocol description carries the worked example from ToolExamples (one table, validated at build
        // time), so the model sees a correct call next to every listed tool without duplicating examples in attributes.
        private static McpServerTool CreateTool(string name, MethodInfo method)
        {
            var attribute = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            var description = attribute?.Description ?? "";
            var decorated = ToolExamples.Decorate(name, description) + TiaOpenness.Shared.ToolUsageCatalog.Hint(name);
            var tool = ReferenceEquals(decorated, description) || decorated == description
                ? ToolCatalog.CreateTool(method)
                : ToolCatalog.CreateTool(method, new McpServerToolCreateOptions { Name = name, Description = decorated });
            // Enum / default / examples hints in the input schema (McpServer.CallDiscipline.cs).
            return WithSchemaHints(tool, name, method);
        }

        // ---- Profile resolution -----------------------------------------------------------------
        // Lite is the default to limit schema size per turn and stay within host tool-count limits.
        // FindTools / CallTool (McpServer.ToolBridge.cs) reach every other tool on demand.
        // Precedence: --profile flag > TIA_MCP_PROFILE env > lite.
        private static string? _profileOverride;

        static partial void ConfigureToolBridgeProfile()
        {
            _bridgeIsLiteProfile = IsLiteProfile;
            _bridgeLiteToolNames = LiteToolNames;
        }

        /// <summary>Applies the CLI --profile flag. Wins over TIA_MCP_PROFILE. Call before building the host.</summary>
        public static void SetProfileOverride(string? profile)
        {
            _profileOverride = string.IsNullOrWhiteSpace(profile) ? null : profile!.Trim();
        }

        /// <summary>Resolved profile name, always lowercase: "lite" or "full".</summary>
        public static string ResolvedProfile()
        {
            string? p = _profileOverride;
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("TIA_MCP_PROFILE");
            p = p?.Trim();
            if (string.IsNullOrEmpty(p)) return "lite";
            // Only "full" (and the historical "all") opts out; anything else — including a
            // typo — stays on the safe, host-compatible lite roster rather than silently
            // blowing past a host's tool cap.
            if (string.Equals(p, "full", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, "all", StringComparison.OrdinalIgnoreCase)) return "full";
            return "lite";
        }

        public static bool IsLiteProfile()
        {
            return ResolvedProfile() == "lite";
        }
    }
}

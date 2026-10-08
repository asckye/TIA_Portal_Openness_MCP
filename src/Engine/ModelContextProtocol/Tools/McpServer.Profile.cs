using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // Tool roster size. DEFAULT = lite: ~48 essentials instead of ~200, so a small /
    // non-expert model is not drowned in choices, hosts with a tool cap (Copilot 128,
    // Windsurf 100) can load the server at all, and every turn carries ~8k instead of
    // ~40k tokens of schema. Opt out per session with --profile full / TIA_MCP_PROFILE=full;
    // reach any individual non-lite tool without opting out via FindTools + CallTool.
    public static partial class McpServer
    {
        private static readonly HashSet<string> LiteToolNames = new HashSet<string>(
            RuntimeProfileEntries()
                .Where(row => row!["profiles"]!.AsArray().Any(p => (string?)p == "lite"))
                .Select(row => (string)row!["currentName"]!), StringComparer.Ordinal);

        public static IList<McpServerTool> GetLiteTools()
        {
            return CatalogView.Lite.Select(tool => ToolInvoker.CreateTool(tool)).ToList();
        }

        /// <summary>
        /// 全量工具表。注册前先取得列表，才能统一包装每个工具，接入参数诊断和大响应分页。
        /// </summary>
        public static IList<McpServerTool> GetAllTools()
        {
            return CatalogView.All.Values.Select(tool => ToolInvoker.CreateTool(tool)).ToList();
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public static partial class McpServer
    {
        private static IServiceProvider? _services => EngineServices.Host;
        private static Portal? _portal;

        public static ILogger? Logger { get; set; }

        public static Portal Portal
        {
            get
            {
                if (_services !=null)
                {
                    return _services.GetRequiredService<Portal>();
                }
                else
                {
                    if (_portal == null)
                    {
                        _portal = (Portal)EngineServices.Get(typeof(Portal));
                    }
                    return _portal;
                }
            }
            set
            {
                _portal = value ?? throw new ArgumentNullException(nameof(value), "Portal cannot be null");
            }
        }

        public static void SetServiceProvider(IServiceProvider services)
        {
            EngineServices.SetServiceProvider(services);
        }

        #region portal

        public static ResponseConnect Connect(
            string projectName = "",
            bool allowStart = false)
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).Connect(projectName, allowStart);

        public static ResponseConnect ConnectIsolated()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).ConnectIsolated();

        public static ResponseStringList ListPortalProcessProjects()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).ListPortalProcessProjects();

        public static Task<ResponseMessage> EnsureOpennessUserGroup()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).EnsureOpennessUserGroup();

        public static ResponseDisconnect Disconnect()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).Disconnect();

        #endregion

        #region state

        public static ResponseState GetState()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).GetState();

        #endregion

        #region bootstrap

        public static Task<ResponseBootstrap> Bootstrap()
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).Bootstrap();

        #endregion

        #region capability self-test

        public static Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(
            bool connectIfNeeded = false,
            bool includeProjectTree = false,
            bool inspectPortalProcesses = false,
            string expectedPlcSoftwarePath = "PLC_1",
            string expectedHmiSoftwarePath = "HMI_RT_1")
            => ((DiagnosticsTools)EngineServices.Get(typeof(DiagnosticsTools))).RunCapabilitySelfTest(connectIfNeeded, includeProjectTree, inspectPortalProcesses, expectedPlcSoftwarePath, expectedHmiSoftwarePath);

        public static ResponseSafetySelfTest RunOnlineMonitoringSafetySelfTest()
            => ((DiagnosticsTools)EngineServices.Get(typeof(DiagnosticsTools))).RunOnlineMonitoringSafetySelfTest();

        private static List<string> GetMcpToolNames()
        {
            return ToolCatalog.Engine.Methods.Select(entry => entry.Key).ToList();
        }

        private static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
        {
            return new[]
            {
                "在线监视只允许读取变量当前状态/当前值。",
                "在线模式不允许修改监控表、监视表或表内对象。",
                "不允许通过 MCP 暴露、调用或绕过任何强制表/强制相关操作。",
                "通用反射入口必须拦截强制相关服务，并拦截在线/监视/监控表面的写入、创建、删除、下载、启停和上下线切换动作。",
                "新增监视能力必须先探测 API 形状，再用最小实例读回验证；未验证前只能标记为探测能力。"
            };
        }

        #endregion

        #region acceptance report

        public static Task<ResponseAcceptanceReport> GenerateAcceptanceReport(
            string outputDirectory = "",
            bool connectIfNeeded = false,
            bool includeProjectTree = false,
            bool inspectPortalProcesses = false,
            string title = "TIA MCP Acceptance Report")
            => ((DiagnosticsTools)EngineServices.Get(typeof(DiagnosticsTools))).GenerateAcceptanceReport(outputDirectory, connectIfNeeded, includeProjectTree, inspectPortalProcesses, title);

        #endregion

        #region error report

        public static ResponseErrorReport GenerateErrorReport(
            string errorCode,
            string summary,
            string detail = "",
            string recommendedNextActions = "",
            string severity = "error",
            string outputDirectory = "")
            => ((DiagnosticsTools)EngineServices.Get(typeof(DiagnosticsTools))).GenerateErrorReport(errorCode, summary, detail, recommendedNextActions, severity, outputDirectory);

        // Best-effort "Did you mean …?" suffix for a not-found block name. Only fires for a
        // bare name (no '/'), where a typo is the likely cause. Returns "" on any failure.
        private static string BuildBlockDidYouMean(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                    blocks = Portal.GetBlocks(softwarePath, escaped);

                var candidates = blocks
                    .Take(10)
                    .Select(b => Portal.GetBlockPath(b))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch /* swallow(enumerate-optional): failure to enumerate block suggestions must preserve the original not-found error */
            {
                return string.Empty;
            }
        }

        // Best-effort "Did you mean …?" suffix for a not-found type name. See BuildBlockDidYouMean.
        private static string BuildTypeDidYouMean(string softwarePath, string typePath)
        {
            if (string.IsNullOrEmpty(typePath) || typePath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(typePath);
                var types = Portal.GetTypes(softwarePath, $"^{escaped}$");
                if (types == null || types.Count == 0)
                    types = Portal.GetTypes(softwarePath, escaped);

                var candidates = types
                    .Take(10)
                    .Select(t => t.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch /* swallow(enumerate-optional): failure to enumerate type suggestions must preserve the original not-found error */
            {
                return string.Empty;
            }
        }

        #endregion

    }
}

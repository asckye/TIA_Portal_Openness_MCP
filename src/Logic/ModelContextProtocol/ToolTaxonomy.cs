using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 工具分类的唯一事实来源。分类由 ToolMetadata 显式声明；Description 展示前缀为 "[层][域][操作]" 开头：
    ///   层  L0 = 会话/引导，L1 = 常用工程动作，L2 = 专用深度工具（默认 lite 配置只列出部分，其余经 FindTools + CallTool）；
    ///   域  下表 Domains 中的 PascalCase 标签，一个工具只属于一个域；
    ///   操作 READ / WRITE / FILE / EXECUTE / ONLINE / ONLINE-WRITE 等，可省略。
    /// 域再归入 7 个大类。生成清单、工具矩阵和 ListToolCategories 都从这里取分类，避免文档与二进制漂移。
    /// </summary>
    public static class ToolTaxonomy
    {
        // Reviewed admission and dispatch policy. Catalog descriptions cannot make a
        // new tool local; unknown tools always use the exclusive Openness lane.
        private static readonly HashSet<string> WithoutTia = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "InitializeEnvironment", "GetEnvironmentDiagnostics", "GetOpennessWorkerStatus",
            "GetNativeInvocationLog", "GetOpennessCompatibility", "InspectSimaticSdCompatibility", "FindTools",
            "ListToolCategories", "GetToolUsage", "PreviewToolCall",
            "StageImportFiles", "ListStagedImportFiles", "CleanupStagedImportFiles",
            "GetOpennessGuidance", "GetV21EcosystemCatalog", "PlanArtifactImportOrder",
            "BuildClassicHmiMinimalPackage", "BuildClassicHmiScreen", "BuildClassicHmiTagTable", "BuildFlgNetCall",
            "BuildPlcAliasAlarmLad", "BuildPlcFbBlock", "BuildPlcFcBlock", "BuildPlcGlobalDb",
            "BuildPlcLadFcBlock", "BuildPlcSymbolManifestFromPath", "BuildPlcTagTable", "BuildPlcUdt",
            "BuildReleaseDiagnosticReport", "BuildReleaseManifest", "BuildReleaseRunbook", "BuildStructuredText",
            "DecodePlcSimaticMl", "ValidatePlcDocumentSchemas", "RenderPlcBlock", "RenderPlcProgramAtlas", "RenderPlcVisualDiff",
            "ScanPlcSourceAnnotations", "GetExportContent", "ListExportHandles", "SaveExportContent", "DeleteExportHandle", "ClearExportHandles"
        };
        private static readonly HashSet<string> SessionReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "InitializeEnvironment", "GetEnvironmentDiagnostics", "PreviewToolCall" };

        public static bool IsSafeWithoutTia(string name) => WithoutTia.Contains(name);
        // File analysis, update discovery and SDK type inspection do not borrow a portal.
        private static readonly HashSet<string> WithoutPortal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ComparePlcBlockDocuments", "ExtractPlcBlockMetrics", "GeneratePlcDocumentation", "AuditEngineeringExports",
            "CheckProductUpdate", "RunPlcCompanionTool", "ListUnifiedHmiApiTypes", "DescribeUnifiedScreenItemType",
            "RunHmiActionScriptRecipeSafetySelfTest", "RunClassicHmiTemporaryImportPreflight"
        };
        public static bool MayCallOpenness(string name)
        {
            var row = For(name);
            return !IsSafeWithoutTia(name) && !WithoutPortal.Contains(name) && row.Operation != "OFFLINE" && CategoryOf(row.Domain) != "runtime";
        }
        public static bool RequiresConnectedPortal(string name)
        {
            return MayCallOpenness(name) && !DispatchesTargets(name) && name != "ConnectPortal" && name != "ConnectProject"
                && name != "ConnectIsolatedPortal" && name != "DisconnectPortal" && name != "RestartOpennessWorker"
                && name != "GetSessionState" && name != "GetPortalInfo" && name != "ListPortalProcessProjects"
                && name != "DiagnosePortalConnectReadiness" && name != "EnsureOpennessUserGroup";
        }
        public static bool UsesOpennessLane(string name) => !WithoutTia.Contains(name) || SessionReaders.Contains(name);
        public static bool DispatchesTargets(string name) => string.Equals(name, "CallTool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "RunReadOnlyToolBatch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "PreviewToolBatch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ApplyToolBatch", StringComparison.OrdinalIgnoreCase);

        public sealed class Category
        {
            public string Key { get; }
            public string NameZh { get; }
            public string NameEn { get; }
            public string Description { get; }
            public IReadOnlyList<string> Domains { get; }
            public Category(string key, string nameZh, string nameEn, string description, params string[] domains)
            { Key = key; NameZh = nameZh; NameEn = nameEn; Description = description; Domains = domains; }
        }

        public static readonly IReadOnlyList<Category> Categories = new[]
        {
            new Category("session", "会话与基础设施", "Session & infrastructure",
                "连接 TIA 进程、引导与自检、工具发现（FindTools/CallTool）、通用反射访问、导出寄存与报告生成。",
                "Bootstrap", "Guide", "Meta", "Portal", "Diagnostics", "Reflection", "Exports", "Reports"),
            new Category("project", "工程与协作", "Project & collaboration",
                "工程打开/保存/归档、多语言文本、库与主副本、版本控制接口、用户管理与证书、离线校验与文档分析。",
                "Project", "Library", "VersionControl", "Security", "Validation"),
            new Category("plc", "PLC 软件", "PLC software",
                "块/UDT/标签表/外部源/软件单元、块构建器与 SCL/LAD 生成、PLC 报警文本、工艺对象、OPC UA 服务器配置、Safety 离线工程。",
                "PLC-Software", "PLC-Builders", "PLC-Alarms", "PLC-TechnologyObjects", "PLC-OpcUA", "Safety"),
            new Category("plc-online", "PLC 在线与传输", "PLC online & transfer",
                "经 Openness 的在线/离线切换、下载、存储卡镜像、站上载、可达设备扫描、在线比较与监视表读取。",
                "PLC-Online"),
            new Category("hardware", "硬件与网络", "Hardware & network",
                "设备/模块增删移动、硬件目录、子网与 IO 系统、通信连接、系统诊断设置、AML 交换、驱动与 DCC 图表。",
                "Hardware"),
            new Category("hmi", "HMI 人机界面", "HMI",
                "WinCC Unified 画面/变量/报警/归档/脚本/事件/动态化，经典 HMI 画面/脚本/周期/列表，两者共用的读取与导入导出，库模板分析，SiVArc。",
                "HMI", "HMI-Unified", "HMI-Classic", "HMI-Library"),
            new Category("runtime", "运行时监视与仿真", "Runtime monitoring & simulation",
                "不经 Openness 的运行时通道：S7 协议、OPC UA、S7 Web 服务器 API、Unified Open Pipe 的读值、报警与受确认的写入；PLCSIM Advanced 虚拟 CPU 的实例管理、读写与闭环测试场景。",
                "Online-Monitoring", "Simulation"),
        };

        private static readonly Dictionary<string, Category> DomainIndex = Categories
            .SelectMany(c => c.Domains.Select(d => new KeyValuePair<string, Category>(d, c)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        public static readonly IReadOnlyDictionary<string, string> LayerMeaning = new Dictionary<string, string>
        {
            ["L0"] = "会话与引导：连接前后必经的入口、自检与工具发现",
            ["L1"] = "常用工程动作：打开/编译/下载/导入导出等高频操作",
            ["L2"] = "专用深度工具：按对象精确寻址的读写，默认 lite 配置不全部列出，经 FindTools + CallTool 调用",
        };

        public static (string Layer, string Domain, string Operation) For(string name)
        {
            var row = ToolMetadata.Find(name);
            return row == null ? ("L2", "", "") : (row.Layer, row.Domain, row.Operation);
        }

        public static Category? CategoryOfDomain(string? domain)
            => !string.IsNullOrWhiteSpace(domain) && DomainIndex.TryGetValue(domain!.Trim(), out var c) ? c : null;

        /// <summary>供生成脚本经反射调用：域 → 大类键；未登记的域返回 "uncategorized"，让清单校验能发现漏登记。</summary>
        public static string CategoryOf(string? domain) => CategoryOfDomain(domain)?.Key ?? "uncategorized";

        public static bool IsKnownDomain(string? domain) => CategoryOfDomain(domain) != null;

        public static Category? FindCategory(string? key)
            => string.IsNullOrWhiteSpace(key) ? null : Categories.FirstOrDefault(c => string.Equals(c.Key, key!.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>规范操作类型。描述里第三个方括号使用这些值；其它写法映射到这些规范值。</summary>
        public static readonly IReadOnlyDictionary<string, string> OperationMeaning = new Dictionary<string, string>
        {
            ["SESSION"] = "会话与发现：连接/断开/状态/引导/工具查找，不改工程",
            ["READ"] = "读取已打开工程的数据，不改动",
            ["WRITE"] = "修改已打开工程（离线工程数据），默认预览，不自动保存/编译/下载",
            ["FILE"] = "导出或导入文件、生成离线产物",
            ["OFFLINE"] = "纯离线计算（XML/JSON 构造、文档分析），不需要 TIA 会话",
            ["ONLINE"] = "联系 PLC/设备/运行时但只读（扫描、在线读值、在线比较）",
            ["ONLINE-WRITE"] = "改变真实设备或运行时（下载、上载进工程、运行模式、运行时写值）",
            ["EXECUTE"] = "执行编译、测试或自检并返回结果",
        };

        private static readonly string[] SessionNames = { "InitializeEnvironment", "ConnectPortal", "ConnectIsolatedPortal", "DisconnectPortal", "GetSessionState", "GetEnvironmentDiagnostics", "EnsureOpennessUserGroup", "ListPortalProcessProjects", "FindTools", "CallTool", "ListToolCategories", "GetToolUsage", "PreviewToolCall", "AttachOpenProject", "OpenProject", "CloseProject", "SaveProject", "OpenSession", "CloseSession" };

        /// <summary>
        /// 描述未标注操作类型时按工具名推断。只用于清单/分类展示（标记 inferred），不改变工具行为；
        /// 已登记的工具以 ToolMetadata 的显式分类为准。
        /// </summary>
        public static (string Operation, bool Inferred) OperationOf(string name, string? description)
        {
            var row = ToolMetadata.Find(name);
            if (row != null) return (row.Operation, row.Inferred);
            bool Starts(params string[] prefixes) => prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
            bool Has(params string[] parts) => parts.Any(p => name.IndexOf(p, StringComparison.Ordinal) >= 0);
            if (SessionNames.Contains(name)) return ("SESSION", true);
            // Preserve operations when V4 names cross a general verb rule.
            if (Starts("BuildAndImportPlcArtifact", "BuildProjectScaffold")) return ("WRITE", true);
            if (Starts("BuildReleaseHandoffArtifacts")) return ("FILE", true);
            if (Starts("Compile", "Run") || Has("SelfTest", "ValidationSuite", "PrecheckSuite")) return ("EXECUTE", true);
            if (Starts("Download", "Upload", "SetPlcWebOperatingMode", "WritePlcWebVars", "WriteUnifiedRuntimeTags", "WritePlcSimAdvanced", "ManagePlcSimAdvanced")) return ("ONLINE-WRITE", true);
            if (Starts("ConnectOnlinePlc", "DisconnectOnlinePlc", "ScanAccessible", "MonitorPlcWatchTableS7") || Has("Online", "Live", "WebVars", "WebDiagnostics", "RuntimeTags", "RuntimeAlarms", "OpenPipe", "PlcSimAdvanced")) return ("ONLINE", true);
            if (Starts("Build", "Compose", "Plan", "Analyze", "Compare", "Scan", "Extract", "Render", "Lint")) return ("OFFLINE", true);
            if (Starts("Export", "Archive", "Write", "Generate", "Save", "Rebuild", "GetExportContent", "ListExportHandles", "ClearExportHandles", "DeleteExportHandle")) return ("FILE", true);
            if (Starts("Import", "Ensure", "Apply", "Create", "Set", "Update", "Manage", "Delete", "Remove", "Add", "Plug", "Bind", "Rename", "Move", "Copy", "Assign", "Protect", "Release", "Exchange", "Configure", "Retrieve", "Sync", "Clear", "Invoke", "Migrate", "Restore", "Register", "Normalize", "Enable", "Disable", "Attach", "PlcBuild", "Repair", "Connect", "Seed", "Scaffold")) return ("WRITE", true);
            if (Starts("Get", "List", "Read", "Describe", "Probe", "Validate", "Check", "Dump", "Inspect", "Find", "Search", "Preview", "Resolve", "Diff", "Verify", "Count", "Enumerate", "Show", "Query", "Fetch", "Is", "Has", "Lookup", "Match", "Test", "Audit", "Trace")) return ("READ", true);
            return ("UNSPECIFIED", true);
        }
    }
}

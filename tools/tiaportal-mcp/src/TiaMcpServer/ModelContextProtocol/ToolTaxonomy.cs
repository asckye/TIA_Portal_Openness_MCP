using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 工具分类的唯一事实来源。每个工具的 Description 以 "[层][域][操作]" 开头：
    ///   层  L0 = 会话/引导，L1 = 常用工程动作，L2 = 专用深度工具（默认 lite 配置只列出部分，其余经 FindTools + CallTool）；
    ///   域  下表 Domains 中的 PascalCase 标签，一个工具只属于一个域；
    ///   操作 READ / WRITE / FILE / EXECUTE / ONLINE / ONLINE-WRITE 等，可省略。
    /// 域再归入 7 个大类。生成清单、工具矩阵和 ListToolCategories 都从这里取分类，避免文档与二进制漂移。
    /// </summary>
    public static class ToolTaxonomy
    {
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
            new Category("runtime", "运行时监视", "Runtime monitoring",
                "不经 Openness 的运行时通道：S7 协议、OPC UA、S7 Web 服务器 API、Unified Open Pipe 的读值、报警与受确认的写入。",
                "Online-Monitoring"),
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

        private static readonly Regex TagPattern = new Regex(@"^\s*\[(?<layer>L\d)\]\[(?:Category:)?(?<domain>[^\]]+)\](?:\[(?<op>[A-Za-z-]+)\])?", RegexOptions.Compiled);

        /// <summary>解析描述前缀。没有前缀时层为 L2、域为空。</summary>
        public static (string Layer, string Domain, string Operation) Parse(string? description)
        {
            var m = TagPattern.Match(description ?? "");
            if (!m.Success) return ("L2", "", "");
            return (m.Groups["layer"].Value, m.Groups["domain"].Value.Trim(), m.Groups["op"].Success ? m.Groups["op"].Value : "");
        }

        public static Category? CategoryOfDomain(string? domain)
            => !string.IsNullOrWhiteSpace(domain) && DomainIndex.TryGetValue(domain!.Trim(), out var c) ? c : null;

        /// <summary>供生成脚本经反射调用：域 → 大类键；未登记的域返回 "uncategorized"，让清单校验能发现漏登记。</summary>
        public static string CategoryOf(string? domain) => CategoryOfDomain(domain)?.Key ?? "uncategorized";

        public static bool IsKnownDomain(string? domain) => CategoryOfDomain(domain) != null;

        public static Category? FindCategory(string? key)
            => string.IsNullOrWhiteSpace(key) ? null : Categories.FirstOrDefault(c => string.Equals(c.Key, key!.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>规范操作类型。描述里第三个方括号使用这些值；其它历史写法已统一映射。</summary>
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

        private static readonly string[] SessionNames = { "Bootstrap", "Connect", "ConnectIsolated", "Disconnect", "GetState", "Doctor", "EnsureOpennessUserGroup", "ListPortalProcessProjects", "FindTools", "CallTool", "ListToolCategories", "GetAuthoringGuide", "AttachToOpenProject", "OpenProject", "CloseProject", "SaveProject", "OpenSession", "CloseSession" };

        /// <summary>
        /// 描述未标注操作类型时按工具名推断。只用于清单/分类展示（标记 inferred），不改变工具行为；
        /// 与描述前缀冲突时以描述前缀为准。
        /// </summary>
        public static (string Operation, bool Inferred) OperationOf(string name, string? description)
        {
            var tagged = Parse(description).Operation;
            if (tagged.Length > 0) return (tagged.ToUpperInvariant(), false);
            bool Starts(params string[] prefixes) => prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
            bool Has(params string[] parts) => parts.Any(p => name.IndexOf(p, StringComparison.Ordinal) >= 0);
            if (SessionNames.Contains(name)) return ("SESSION", true);
            if (Starts("Compile", "Run") || Has("SelfTest", "ValidationSuite", "PrecheckSuite")) return ("EXECUTE", true);
            if (Starts("Download", "Upload", "SetPlcWebOperatingMode", "WritePlcWebVars", "WriteUnifiedRuntimeTags")) return ("ONLINE-WRITE", true);
            if (Starts("GoOnline", "GoOffline", "ScanAccessible") || Has("Online", "Live", "WebVars", "WebDiagnostics", "RuntimeTags", "RuntimeAlarms", "OpenPipe")) return ("ONLINE", true);
            if (Starts("Build", "Compose", "Plan", "Analyze", "Compare", "Scan", "Extract")) return ("OFFLINE", true);
            if (Starts("Export", "Archive", "Write", "Generate", "Save", "Rebuild", "GetExport", "ListExports", "ClearExports", "DeleteExport")) return ("FILE", true);
            if (Starts("Import", "Ensure", "Apply", "Create", "Set", "Update", "Manage", "Delete", "Remove", "Add", "Plug", "Bind", "Rename", "Move", "Copy", "Assign", "Protect", "Release", "Exchange", "Configure", "Retrieve", "Sync", "Clear", "Invoke", "Migrate", "Restore", "Register", "Normalize", "Enable", "Disable", "Attach", "PlcBuild", "Repair", "Connect", "Seed", "Scaffold")) return ("WRITE", true);
            if (Starts("Get", "List", "Read", "Describe", "Probe", "Validate", "Check", "Dump", "Inspect", "Find", "Search", "Preview", "Resolve", "Diff", "Verify", "Count", "Enumerate", "Show", "Query", "Fetch", "Is", "Has", "Lookup", "Match", "Test", "Audit", "Trace")) return ("READ", true);
            return ("UNSPECIFIED", true);
        }
    }
}

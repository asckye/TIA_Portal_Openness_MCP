namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    // Workbench shell navigation and page sections.
    internal static (string Key, string En, string Zh)[] LayoutCatalogue =>
    [
        ("Shell.Approvals", "Approvals", "审批"),
        ("Shell.Logs", "Logs & diagnostics", "日志与诊断"),
        ("Shell.NoProject", "Open a project first.", "请先打开项目。"),
        ("Shell.OfflineUnavailable", "Unavailable in offline demo mode.", "离线演示模式下不可用。"),
        ("Shell.VersionControlUnavailable", "Version control is unavailable for this TIA Portal release.", "此 TIA Portal 版本不支持版本控制。"),
        ("Shell.PageUnavailable", "Page unavailable", "页面不可用"),
        ("Shell.ProjectStatus", "TIA Portal {0} · {1}", "TIA Portal {0} · 项目：{1}"),
        ("Shell.ApprovalOnStatus", "Write approval on", "写操作审批已开"),
        ("Shell.ApprovalOffStatus", "Write approval off", "写操作审批已关"),
        ("Settings.Sections", "Settings sections", "设置分区"),
        ("Settings.HumanOnly", "Human only", "仅限人工操作"),
        ("Settings.Folders", "Folders", "目录"),
        ("Settings.DataDirectory", "Data folder", "数据目录"),
        ("Shell.GroupEngineering", "Project", "项目"),
        ("Shell.GroupAi", "AI collaboration", "AI 协作"),
        ("Shell.GroupOperations", "Operations", "运行"),
        ("Shell.GroupConnection", "Connection & settings", "连接与设置"),
        ("Shell.CollapseMenu", "Collapse menu", "收起菜单"),
        ("Shell.ExpandMenu", "Expand menu", "展开菜单"),
        ("Shell.Language", "Switch language", "切换语言"),
        ("Config.StepInstallationHint", "Detect the version and path", "检测版本与路径"),
        ("Config.StepServiceHint", "Address, port and start", "地址、端口、启动"),
        ("Config.StepClientsHint", "Write the connection info", "写入连接信息"),
        ("Calls.Subtitle", "Recent tool calls; select a row for the full arguments and result.", "最近的工具调用；点击一行查看完整参数与返回。"),
        ("Calls.All", "All", "全部"),
        ("Calls.AllReleases", "All releases", "全部版本"),
        ("Calls.ReleaseScope", "TIA release", "TIA 版本"),
        ("Calls.StatTotal", "Calls", "调用"),
        ("Calls.StatPending", "Pending", "待批"),
        ("Calls.StatFailed", "Failed / unknown", "失败 / 未知"),
        ("Calls.StatAverage", "Average duration", "平均耗时"),
        ("Calls.Milliseconds", "ms", "毫秒"),
        ("Vc.Subtitle", "Map the project to a TIA VCI workspace and synchronise it in either direction.", "将项目映射到 TIA VCI 工作区，并按任一方向同步。"),
        ("Pages.NoSelection", "No blocks selected: compile and inspect apply to the whole PLC software; select blocks to export or import.",
                "未选块：编译与检查作用于整个 PLC 软件，导出导入需先选块"),
    ];
}

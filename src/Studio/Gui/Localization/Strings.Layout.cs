namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    // The Ant layout (P8-80): side menu groups and trigger, header actions and the 项目 selection bar.
    internal static (string Key, string En, string Zh)[] LayoutCatalogue =>
    [
        ("Shell.GroupEngineering", "Engineering", "工程"),
        ("Shell.GroupAi", "AI collaboration", "AI 协作"),
        ("Shell.GroupOperations", "Operations", "运维"),
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
        ("Pages.NoSelection", "No blocks selected: compile and inspect apply to the whole PLC software; select blocks to export or import.", "未选块：编译与检查作用于整个 PLC 软件，导出导入需先选块"),
    ];
}

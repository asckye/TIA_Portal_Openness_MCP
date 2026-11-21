namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    internal static (string Key, string En, string Zh)[] OverlaysCatalogue =>
    [
        ("Overlay.Results", "Search results", "搜索结果"),
        ("Overlay.SearchHint", "Jump to a page, find a block, open a panel or switch states…", "跳转页面、搜索程序块、打开弹层或切换状态…"),
        ("Overlay.NoResults", "No matching pages, blocks or commands", "没有匹配的页面、程序块或命令"),
        ("Overlay.Close", "Close (Esc)", "关闭（Esc）"),
        ("Overlay.Session", "Current session", "当前会话"),
        ("Overlay.SharedSession", "Shared MCP sessions and version", "共享 MCP 会话与版本"),
        ("Overlay.Connected", "Connected", "已连接"),
        ("Overlay.Disconnect", "Disconnect project…", "断开项目…"),
        ("Overlay.SwitchVersion", "Switch version", "切换版本"),
        ("Overlay.Release", "Target TIA version", "目标 TIA 版本"),
        ("Overlay.Sessions", "Discovered active sessions ({0})", "已发现的活跃会话 ({0})"),
        ("Overlay.Panels", "Workbench panels ({0})", "{0} 个工作台面板"),
        ("Overlay.Discover", "Discover sessions", "发现会话"),
        ("Overlay.Attach", "Attach panel", "附着面板"),
        ("Overlay.Create", "Create own session", "创建自有会话"),
        ("Overlay.Detach", "Detach panel only", "仅释放面板"),
        ("Overlay.EmptySessions", "No sessions listed", "暂无会话"),
        ("Overlay.Picker", "Open TIA process picker", "打开 TIA 进程选择器"),
        ("Overlay.Rules", "Open inspection rules", "打开检查规则设置"),
        ("Overlay.Transfer", "Open transfer options", "打开传输选项面板"),
        ("Overlay.Modal", "Dialog", "对话框"),
        ("Overlay.Drawer", "Drawer", "选项面板"),
        ("Overlay.PickerTitle", "Choose a TIA Portal process to attach to", "选择要附着的 TIA Portal 进程"),
        ("Overlay.ProcessCount", "Running TIA Portal instances found locally ({0})", "本机检测到 {0} 个正在运行的 TIA Portal 实例。"),
        ("Overlay.Rescan", "_Rescan", "重新扫描(_R)"),
        ("Overlay.AttachPid", "_Attach to PID {0}", "附着到 PID {0}(_O)"),
        ("Overlay.Lease", "Lease status", "租约状态"),
        ("Overlay.Project", "Target project", "目标项目"),
        ("Overlay.Changes", "Pending changes", "待保存改动"),
        ("Overlay.SaveTitle", "Save project {0}?", "保存项目 {0}？"),
        ("Overlay.SaveText", "Save the current changes to the TIA Portal project file on disk. This cannot be undone.",
            "将当前修改写入 TIA Portal 磁盘项目文件。此操作不可撤销。"),
        ("Overlay.EndTitle", "Disconnect and end the current shared session?", "断开并结束当前共享会话？"),
        ("Overlay.AffectedClient", "Affected client", "受影响客户端"),
        ("Overlay.Process", "TIA process", "TIA 进程"),
        ("Overlay.ProcessKept", "PID {0} (process stays open)", "PID {0}（进程本身不会被关闭）"),
    ];
}

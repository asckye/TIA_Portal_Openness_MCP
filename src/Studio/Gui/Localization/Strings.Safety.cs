namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    private static (string Key, string En, string Zh)[] SafetyCatalogue =>
    [
        ("Settings.SafetyTitle", "Safety & AI", "安全与 AI 控制"),
        ("Settings.AppearanceTitle", "Appearance", "外观与语言"),
        ("Settings.StorageTitle", "Storage & about", "存储、更新与关于"),
        ("Settings.SafetySubtitle", "Only editable in the local workbench", "仅允许在本机工作台修改，AI 客户端与远程调用无权改动"),
        ("Settings.AppearanceSubtitle", "Light and dark themes, accent and language", "浅色 / 深色主题、精密钴蓝 / 西门子青碧主色与语言"),
        ("Settings.StorageSubtitle", "Log retention, diagnostics, data paths and updates", "日志轮转保留、脱敏诊断包、数据目录与版本更新"),
        ("Settings.ApprovalRule", "Require approval for MCP writes", "普通写操作默认工作台人工审批"),
        ("Settings.ApprovalRuleHint", "Changes immediately. Physical PLC writes still require confirmation.", "切换后立即生效，真实 PLC 写入仍须人工确认。"),
        ("Settings.ExpiryHint", "Auto-deny on expiry", "倒计时归零自动拒绝"),
        ("Settings.PlanHint", "Expired plans require a new preview", "过期后须重新预检"),
        ("Settings.DurationCustom", "Custom", "自定义"),
        ("Settings.TimeoutSeconds", "Timeout in seconds (1–3600)", "超时秒数（1–3600）"),
        ("Settings.TimeoutInvalid", "Enter a whole number of seconds from 1 to 3600.", "请输入 1–3600 的整数秒数。"),
        ("Settings.PlanDuration", "Online plan lifetime", "在线预检计划有效期"),
        ("Settings.Plan5", "5 min", "5 分钟"),
        ("Settings.Plan15", "15 min", "15 分钟"),
        ("Settings.Plan30", "30 min", "30 分钟"),
        ("Settings.ApplySeconds", "Apply", "应用"),
        ("Settings.AddAndSave", "Add & save", "添加并保存"),
        ("Settings.PolicyDetails", "Approval policy details", "查看审批策略详情"),
        ("Settings.AlwaysConfirmed", "Even with approval off, the Workbench requires confirmation to execute CloseProject with discardChanges=true " +
            "or to write to a physical online target. ManagePlcDataBlockSnapshot read/exportSnapshot and InvokeUnifiedOpenPipe read/browse messages " +
            "can proceed. The createSnapshot action, verified PLCSIM downloads and the three PLCSIM-only tools follow the ordinary approval switch.",
            "关闭普通审批后，执行 CloseProject 且 discardChanges=true 或向真实设备在线写入，仍须工作台人工确认。" +
            "ManagePlcDataBlockSnapshot 的 read、exportSnapshot 及 InvokeUnifiedOpenPipe 的读取、浏览消息可直接执行。" +
            "创建快照、已验证的 PLCSIM 下载和三个 PLCSIM 专用工具沿用普通审批开关。"),
        ("Settings.DownloadSafety", "Download safety", "下载安全"),
        ("Settings.DownloadSafetyHelp", "Compile and save before downloading. Physical targets require explicit confirmation. " +
            "Expired online write plans require a new preview.", "下载前须编译并保存。真实目标还须显式确认。在线写入计划过期后须重新预览。"),
        ("Settings.PlanLifetime", "Online write plan lifetime (seconds, 1–86400)", "在线写入计划有效期（秒，1–86400）"),
        ("Settings.SavePlanLifetime", "Save plan lifetime", "保存计划有效期"),
        ("Settings.PlanLifetimeInvalid", "Enter a whole number of seconds from 1 to 86400.", "请输入 1–86400 的整数秒数。"),
        ("Settings.OnlineTargets", "Online target IPv4 allowlist", "在线目标 IPv4 白名单"),
        ("Settings.OnlineTargetsHelp", "Add and save IPv4 addresses or CIDR subnets. The list covers Openness, S7, Web API and OPC UA. " +
            "Saving an empty list denies all targets. Reload discards unsaved edits.",
            "添加并保存 IPv4 地址或 CIDR 子网。列表覆盖 Openness、S7、Web API 和 OPC UA。保存空列表会拒绝全部目标。重新加载会放弃未保存的编辑。"),
        ("Settings.TargetsUnconfigured", "No list configured; address restrictions are off.", "未配置列表；尚未限制地址。"),
        ("Settings.TargetsEmpty", "Empty list saved; all online targets are refused.", "已保存空列表；将拒绝全部在线目标。"),
        ("Settings.TargetsConfigured", "The saved list limits online targets.", "已保存列表；在线目标受此列表限制。"),
        ("Settings.TargetsUnsaved", "Unsaved changes; save to apply the list.", "有未保存的修改；保存后列表生效。"),
        ("Settings.TargetFileInvalid", "Invalid list file: {0}. Editing is locked until you confirm replacement.",
            "列表文件无效：{0}。确认替换前禁止编辑。"),
        ("Settings.TargetFileUnreadable", "Cannot read list file: {0}. Editing is locked until you confirm replacement.",
            "无法读取列表文件：{0}。确认替换前禁止编辑。"),
        ("Settings.ReplaceTargets", "Replace file", "替换文件"),
        ("Settings.ConfirmReplaceTargets", "Replace the invalid or unreadable list file {0}? Its existing entries will be replaced when you save.",
            "是否替换无效或不可读的列表文件 {0}？保存时将替换其现有条目。"),
        ("Settings.TargetsReplacementReady", "Replacement confirmed. Add the intended entries, then save; an empty list denies all targets.",
            "已确认替换。请添加目标条目后保存；空列表会拒绝全部目标。"),
        ("Settings.TargetsChanged", "The list file changed after loading. Reload the lists before saving; your save was refused.",
            "列表文件在加载后已改变。此次保存已拒绝，请重新加载列表后再保存。"),
        ("Settings.TargetAddress", "IPv4 address or CIDR subnet", "IPv4 地址或 CIDR 子网"),
        ("Settings.RemoveTarget", "Remove selected target", "移除所选目标"),
        ("Settings.SaveTargets", "Save online targets", "保存在线目标"),
        ("Settings.ReloadSafety", "Reload lists", "重新加载列表"),
        ("Settings.TargetInvalid", "Enter one complete IPv4 address or CIDR subnet (prefix 0–32).", "请输入一个完整 IPv4 地址或 CIDR 子网（前缀 0–32）。"),
        ("Settings.SafetyLoadFailed", "Could not load the online target list. Check file access, then reload.",
            "无法加载在线目标列表。请检查文件访问权限，然后重新加载。"),
        ("Settings.SafetySaveFailed", "Could not complete the save and audit. The file may already have changed; reload and check the audit log.",
            "未能完成保存和审计。文件可能已经改变；请重新加载并检查审计日志。"),
    ];
}

namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    // Configuration owns these messages; they join the existing Loc catalogue.
    static Strings()
    {
        CommonCatalogue = [.. CommonCatalogue, .. ConfigurationCatalogue];
        English = Build(static entry => entry.En);
        Chinese = Build(static entry => entry.Zh);
    }

    private static readonly (string Key, string En, string Zh)[] ConfigurationCatalogue =
    [
        ("Config.McpService", "MCP Server", "MCP 服务配置"),
        ("Config.GoClients", "AI Clients →", "配置 AI 客户端 →"),
        ("Config.Detect", "Auto-detect", "检测安装"),
        ("Config.LocalStatus", "Local STDIO · {0}", "本机 STDIO · {0}"),
        ("Config.ConnectionTitle", "Connection & Endpoint", "连接方式与服务地址"),
        ("Config.ConnectionHint", "Single source of truth for MCP transport — synced to AI Clients.", "在此统一配置本机或远程 MCP 目标，「AI 客户端」页将自动同步读取。"),
        ("Config.LocalTag", "Zero-config IPC", "零网络配置"),
        ("Config.RemoteTag", "Key-free LAN", "免密钥局域网直连"),
        ("Config.LocalTile", "Local Direct", "本机直连"),
        ("Config.RemoteTile", "VM / Remote", "虚拟机 / 远程"),
        ("Config.LocalTileHint", "TIA Portal & AI client on this PC — no IP or port.", "TIA Portal 与 AI 客户端在同一台电脑，无需 IP 与端口。"),
        ("Config.RemoteTileHint", "TIA runs in a VM or remote host over LAN (key-free).", "TIA 运行在虚拟机或另一台电脑，通过局域网免密钥直连。"),
        ("Config.ReleaseTitle", "Target TIA Portal Release", "目标 TIA Portal 版本"),
        ("Config.LiteCount", "{0} lite", "lite {0} 项"),
        ("Config.ToolCount", "{0} tools", "{0} 个工具"),
        ("Config.OneRelease", "{0} release detected locally (dashed = not installed)", "本机已检测到 {0} 个版本（虚线项为未安装，仍可预选生成配置）"),
        ("Config.ReleaseCount", "{0} releases detected locally (dashed = not installed)", "本机已检测到 {0} 个版本（虚线项为未安装，仍可预选生成配置）"),
        ("Config.Found", "Found", "已装"),
        ("Config.Missing", "—", "未装"),
        ("Config.AssembliesReady", "✓ Assemblies verified", "✓ Openness 程序集就绪"),
        ("Config.AssembliesMissing", "Assemblies not found", "本机未检测到"),
        ("Config.EditTarget", "Edit target in MCP Server", "前往 MCP 服务修改目标"),
        ("Config.Edit", "Edit →", "修改 →"),
        ("Config.SelectDetected", "Select detected ({0})", "全选已装 ({0})"),
        ("Config.DetectedCount", "{0} detected locally", "本机已检测到 {0} 个"),
        ("Config.ChooseTargets", "Select AI Clients to Configure", "选择要写入 MCP 配置的客户端"),
        ("Config.BackupWrite", "Back up existing files before writing", "写入前备份已有文件"),
        ("Config.WriteCount", "Backup & write ({0})", "备份并写入 ({0})"),
        ("Config.SelectForWrite", "Select clients", "请选择客户端"),
        ("Config.SelectionBackup", "{0} selected · Automatic .bak backup", "已选 {0} 个客户端 · 写入前自动生成 .bak 备份"),
        ("Config.EmptySelection", "Select at least one client above", "请在上方勾选至少 1 个客户端"),
        ("Config.PreviewFormat", "Preview {0}", "预览 {0}"),
        ("Config.HideFormat", "Hide {0}", "收起 {0}"),
        ("Config.CopyFormat", "Copy {0}", "复制 {0}"),
        ("Config.FormatCopied", "Copied the configuration {0}.", "已复制配置 {0}。"),
        ("Config.PreviewTarget", "Client", "客户端"),
        ("Config.RelayLabel", "Remote clients connect through the local stdio relay on your LAN.", "远程客户端通过本地 stdio 中继直连局域网 / 虚拟机。"),
        ("Config.RelayMode",
                "Remote clients use the bundled local stdio relay without bearer keys. Private and loopback addresses bypass " +
                "proxies; public addresses follow proxy and NO_PROXY settings.",
                "远程客户端使用随包本地 stdio 中继免密钥直连。私网和回环地址绕过代理；公网地址遵守代理及 NO_PROXY 设置。"),
        ("Config.RelayNeedsConnection", "Save the remote address here first, then write the selected clients using relay mode.",
                "请先在此保存远程地址，再以中继模式写入所选客户端。"),
        ("Config.MigrationConfirmationRequired", "Confirm the client configuration change in the Workbench before writing an existing file.",
                "请先在工作台确认客户端配置变更，再写入已有文件。"),
        ("Config.TargetEngineUnavailable", "The selected service is missing. Client configuration was not changed.", "所选服务缺失，客户端配置未改动。"),
        ("Config.ClientChangedBeforeWrite", "The client configuration changed after preview. Review it again before saving.", "预览后客户端配置已变化，请重新审查后保存。"),
        ("Config.MigrateClientsPrompt",
                "Migrate these entries to the bundled launcher or remote relay? A timestamped backup will be saved first. Relay migration removes the target " +
                "entry's HTTP authentication fields. Other servers are preserved.",
                "将这些条目迁移到随包启动器或远程中继？将先保存时间戳备份。中继迁移会移除目标条目的 HTTP 鉴权字段，保留其他服务器。"),
        ("Config.ClientBackupSaved", "Backup saved: {0}", "备份已保存：{0}"),
        ("Config.ClientRollbackFailed", "Unable to restore the client configuration. Restore the backup at {0} before retrying.",
                "无法恢复客户端配置。请先从 {0} 的备份恢复，再重试。"),
    ];
}

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
        ("Config.RelayLabel", "Remote clients use a local stdio relay; access keys stay protected.", "远程客户端通过本地 stdio 中继连接，访问密钥受当前用户保护。"),
        ("Config.RelayMode",
                "Remote clients use the bundled local stdio relay. The key stays in your protected Workbench store. Private and loopback addresses bypass " +
                "proxies; public addresses follow proxy and NO_PROXY settings.",
                "远程客户端使用随包本地 stdio 中继。密钥保留在工作台的用户保护存储中。私网和回环地址绕过代理；公网地址遵守代理及 NO_PROXY 设置。"),
        ("Config.RelayNeedsConnection", "Save the remote address and access key here first, then write the selected clients using relay mode.",
                "请先在此保存远程地址和访问密钥，再以中继模式写入所选客户端。"),
        ("Config.MigrationConfirmationRequired", "Confirm the client configuration change in the Workbench before writing an existing file.",
                "请先在工作台确认客户端配置变更，再写入已有文件。"),
        ("Config.TargetEngineUnavailable", "The selected engine is missing. Client configuration was not changed.", "所选引擎缺失，客户端配置未改动。"),
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

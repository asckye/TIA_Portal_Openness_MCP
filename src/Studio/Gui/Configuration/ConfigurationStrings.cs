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
        ("Config.MigrationConfirmationRequired", "Confirm the client configuration change in the Workbench before writing an existing file.", "请先在工作台确认客户端配置变更，再写入已有文件。"),
        ("Config.TargetEngineUnavailable", "The selected engine is missing. Client configuration was not changed.", "所选引擎缺失，客户端配置未改动。"),
        ("Config.ClientChangedBeforeWrite", "The client configuration changed after preview. Review it again before saving.", "预览后客户端配置已变化，请重新审查后保存。"),
        ("Config.MigrateClientsPrompt", "Migrate the local launch command/arguments to 4.0? A timestamped backup will be saved beside each existing file first. URLs, auth fields and other servers are preserved.", "将本地启动命令和参数迁移到 4.0？每个已有文件会先在旁边保存时间戳备份。URL、鉴权字段和其他服务器保持不变。"),
        ("Config.ClientBackupSaved", "Backup saved: {0}", "备份已保存：{0}"),
        ("Config.ClientRollbackFailed", "Unable to restore the client configuration. Restore the backup at {0} before retrying.", "无法恢复客户端配置。请先从 {0} 的备份恢复，再重试。"),
    ];
}

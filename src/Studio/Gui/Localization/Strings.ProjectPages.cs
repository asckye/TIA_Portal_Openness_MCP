namespace TiaOpenness.Gui.Localization;

internal static partial class Strings
{
    internal static (string Key, string En, string Zh)[] ProjectPagesCatalogue =>
    [
        ("Pages.ConnectDescription", "Connect to a running TIA Portal, or enter an .ap project path and connect.", "连接到运行中的博途，或填写 .ap 工程路径后连接。"),
        ("Pages.ConnectHint", "After connecting: blocks, compile, import/export, inspect, version control.", "连接后可用：程序块、编译、导入导出、检查、版本控制。"),
        ("Pages.Options", "Options…", "选项…"),
        ("Pages.Rules", "Rules…", "规则…"),
        ("Pages.Save", "Save project", "保存工程"),
        ("Pages.Disconnecting", "Disconnecting from TIA Portal", "正在断开 TIA Portal"),
        ("Pages.Disconnect", "Disconnect", "断开"),
        ("Pages.RealProject", "Real project", "真实工程"),
        ("Pages.CompileDescription", "Compile the selected PLC software and return errors, warnings and diagnostics.", "编译所选 PLC 软件，返回错误、警告与诊断。"),
        ("Pages.TransferDescription", "Export selected blocks as SimaticML XML, or import and overwrite.", "以 SimaticML XML 导出所选块，或导入并覆盖。"),
        ("Pages.InspectDescription", "Check all blocks for naming, author, consistency and know-how protection.", "按命名、作者、一致性与专有技术保护检查全部块。"),
        ("Pages.EnvironmentDescription", "Check Openness, user group and engine whitelist item by item.", "逐项检查 Openness、用户组、引擎白名单。"),
        ("Pages.NeedConnection", "Connect a project first", "需先连接工程"),
        ("Pages.Ready", "Ready", "就绪"),
        ("Pages.Done", "Done", "已完成"),
        ("Pages.Running", "Running", "进行中"),
        ("Pages.NotRun", "Not run yet", "尚未执行"),
        ("Pages.Run", "Run", "执行"),
        ("Pages.Import", "Import XML (overwrite)", "导入 XML（覆盖）"),
        ("Pages.Filter", "Filter by name or group path…", "按名称或组路径筛选…"),
        ("Pages.Selected", "{0} blocks selected", "{0} 个块已选"),
        ("Pages.ViewLadder", "View ladder", "查看梯形图"),
        ("Pages.GenerateAtlas", "Generate atlas", "生成程序图册"),
        ("Pages.AtlasRunning", "Generating {0}/{1}", "生成中 {0}/{1}"),
        ("Pages.AtlasDone", "Atlas generated", "图册已生成"),
        ("Pages.AtlasFailed", "Generation failed", "生成失败"),
        ("Pages.AtlasMissingPath", "The generated HTML file is missing.", "找不到生成的 HTML 文件。"),
        ("Pages.LogCount", "{0} entries", "{0} 条记录"),
        ("Pages.CompileHint", "Run compile from Program blocks or Project operations.", "在程序块页或工程操作页执行编译。"),
        ("Pages.NotCompiled", "Not compiled", "尚未编译"),
        ("Pages.NotInspected", "Not inspected yet.", "尚未检查。"),
        ("Pages.OpenWorkspace", "Open workspace folder", "打开工作区目录"),
        ("Pages.GitTools", "Git tools…", "Git 工具…"),
        ("Pages.GitHint", "Open the workspace folder in your preferred Git tool. This page shows staged, unstaged and untracked changes.", "在你使用的 Git 工具中打开工作区目录。本页显示已暂存、未暂存与未跟踪的变更。"),
        ("Pages.NoDiff", "Select a mapped object to read its local Git changes.", "选择已映射对象以读取本地 Git 差异。"),
        ("Pages.MappedMeta", "{0} objects · {1} differ", "{0} 个 · 差异 {1}"),
    ];
}

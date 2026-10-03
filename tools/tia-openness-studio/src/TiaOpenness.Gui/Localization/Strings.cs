using System;
using System.Collections.Generic;

namespace TiaOpenness.Gui.Localization;

/// <summary>
/// Every visible string, in both languages.
///
/// The two languages are held as one table of triples rather than two parallel files: a key
/// cannot exist in English and be missing in Chinese, because there is only one place to add
/// it. <c>StringsTests</c> checks the rest - no duplicates, nothing blank, and the same
/// <c>{0}</c> placeholders on both sides.
/// </summary>
internal static class Strings
{
    /// <summary>key, English, Chinese.</summary>
    internal static readonly (string Key, string En, string Zh)[] Catalogue =
    [
        // ---- shell -----------------------------------------------------------------
        ("App.Title",                  "Openness Studio", "Openness Studio"),
        ("App.Monogram",               "OS", "OS"),
        ("Badge.NoVersion",            "TIA —", "TIA —"),
        ("Badge.Version",              "TIA {0}", "TIA {0}"),
        ("Theme.Light",                "Light", "浅色"),
        ("Theme.Dark",                 "Dark", "深色"),
        ("Theme.Auto",                 "Auto", "自动"),
        ("Theme.Switch",               "Appearance", "外观"),
        ("Lang.Switch",                "Language", "语言"),
        ("Lang.English",               "EN", "EN"),
        ("Lang.Chinese",               "中文", "中文"),

        // ---- header actions --------------------------------------------------------
        ("Toolbar.Mock",               "Mock", "模拟"),
        ("Toolbar.Mock.Tip",           "Run against a synthetic project; no TIA Portal needed",
                                       "使用合成项目运行,无需安装 TIA Portal"),
        ("Toolbar.Headless",           "Headless", "无界面"),
        ("Toolbar.Headless.Tip",       "Start TIA Portal without its window. It cannot show the first-connect trust dialog.",
                                       "以无窗口方式启动 TIA Portal。它无法显示首次连接的信任对话框。"),
        ("Toolbar.Doctor",             "Doctor", "环境检查"),
        ("Toolbar.Doctor.Tip",         "Check every Openness precondition on this machine",
                                       "检查本机是否满足 Openness 的全部前置条件"),
        ("Toolbar.Connect",            "Connect", "连接"),
        ("Toolbar.Connect.Tip",        "Start TIA Portal, or attach to one that is already running",
                                       "启动 TIA Portal,或附加到已在运行的实例"),
        ("Toolbar.Compile",            "Compile", "编译"),
        ("Toolbar.Inspect",            "Inspect", "审查"),
        ("Toolbar.Import",             "Import…", "导入…"),
        ("Toolbar.Save",               "Save project", "保存项目"),
        ("Toolbar.Save.Tip",           "Write the project to disk. Cannot be undone.",
                                       "把项目写入磁盘,此操作不可撤销。"),

        // ---- project row -----------------------------------------------------------
        ("Project.Label",              "Project", "项目"),
        ("Project.Placeholder",        "Path to an .ap15_1 … .ap21 project file",
                                       ".ap15_1 / .ap21 项目文件的路径"),
        ("Common.Browse",              "Browse…", "浏览…"),
        ("Common.Open",                "Open", "打开"),
        ("Common.All",                 "All", "全选"),
        ("Common.None",                "None", "清空"),
        ("Common.Reload",              "Reload", "刷新"),

        // ---- sidebar ---------------------------------------------------------------
        ("Sidebar.Devices",            "Devices", "设备"),
        ("Sidebar.Empty",              "Devices will be listed here once you connect and open a project.",
                                       "连接并打开项目后\n此处将列出设备"),

        // ---- tabs ------------------------------------------------------------------
        ("Tab.Blocks",                 "Blocks", "程序块"),
        ("Tab.VersionControl",         "Version control", "版本控制"),
        ("Tab.Log",                    "Log", "日志"),

        // ---- blocks ----------------------------------------------------------------
        ("Blocks.Filter",              "Filter by path", "按路径筛选"),
        ("Blocks.Count",               "{0} block(s)", "共 {0} 个程序块"),
        ("Blocks.Selected",            "{0} of {1} selected", "已选择 {0}/{1}"),
        ("Blocks.Empty.Title",         "No project open", "尚未打开项目"),
        ("Blocks.Empty.Body",          "Press Connect. If TIA Portal is already running with a project open, that project is taken over and its blocks appear here — no path needed. Otherwise give a project path above and open it.",
                                       "点击\"连接\"。如果博途已在运行并且已打开项目,会直接接管那个项目,程序块随即出现在这里——不需要填路径。否则在上方填写项目路径再打开。"),
        ("Blocks.ExportTo",            "Export to", "导出到"),
        ("Blocks.SourceFormat",        "SIMATIC SD text", "SIMATIC SD 文本"),
        ("Blocks.SourceFormat.Tip",    "Export SIMATIC SD documents through the MCP engine. Mock mode uses synthetic source text.",
                                      "通过 MCP 引擎导出 SIMATIC SD 文档。模拟模式生成示例源文本。"),
        ("Blocks.Export",              "Export", "导出"),
        ("Block.Protected",            "know-how protected", "专有技术保护"),
        ("Block.NeedsCompiling",       "needs compiling", "需要先编译"),

        // The categories TIA splits a PLC's software into, named as TIA names them.
        ("Tree.ProgramBlocks",         "Program blocks", "程序块"),
        ("Tree.DataTypes",             "PLC data types", "PLC 数据类型"),
        ("Tree.Tags",                  "PLC tags", "PLC 变量"),
        ("Tree.HmiScreens",            "Screens", "画面"),

        // ---- version control -------------------------------------------------------
        ("Vc.Workspace",               "Workspace", "工作区"),
        ("Vc.NoWorkspace",             "No workspace selected. Create one over your Git working tree below.",
                                       "尚未选择工作区。请在下方基于 Git 工作树创建一个。"),
        ("Vc.New",                     "New", "新建"),
        ("Vc.In",                      "in", "位于"),
        ("Vc.Create",                  "Create", "创建"),
        ("Vc.Folder.Tip",              "An existing folder — normally your Git working tree",
                                       "一个已存在的文件夹 — 通常就是你的 Git 工作树"),
        ("Vc.DryRun",                  "Dry run", "预演"),
        ("Vc.DryRun.Tip",              "Report what would happen without changing anything. Clear it to apply.",
                                       "只报告将会发生什么,不做任何改动。取消勾选才会实际执行。"),
        ("Vc.ShowAll",                 "Show in-sync objects", "显示已同步的对象"),
        ("Vc.Map",                     "Map project", "映射项目"),
        ("Vc.Map.Tip",                 "Map the project's objects into the workspace so they can be exported as text",
                                       "把项目对象映射进工作区,使其能够导出为文本"),
        ("Vc.Status",                  "Status", "状态"),
        ("Vc.Push",                    "Push →", "推送 →"),
        ("Vc.Push.Tip",                "Write the project out as text files, ready to commit",
                                       "把项目写出为文本文件,可直接提交"),
        ("Vc.Pull",                    "← Pull", "← 拉取"),
        ("Vc.Pull.Tip",                "Read the text files back into the project. Overwrites blocks.",
                                       "把文本文件读回项目,会覆盖程序块。"),
        ("Vc.Empty.Title",             "No workspace yet", "尚无工作区"),
        ("Vc.Empty.Body",              "The Version Control Interface maps the project onto a folder, one text file per object. It needs TIA Portal V21 or later.",
                                       "版本控制接口会把项目映射到一个文件夹,每个对象一个文本文件。该功能需要 TIA Portal V21 及以上。"),
        ("Col.State",                  "State", "状态"),
        ("Col.Object",                 "Object", "对象"),
        ("Col.Format",                 "Format", "格式"),
        ("Vc.Diff",                    "Change", "改动"),
        ("Vc.Diff.Empty",              "Select a changed object to see what it changed. The comparison is against the files, so push first.",
                                       "选中一个有差异的对象即可查看改动内容。比较的是文件,所以要先推送。"),
        ("Vc.Diff.Unchanged",          "{0}: no uncommitted change in the workspace file.",
                                       "{0}:工作区文件没有未提交的改动。"),

        // ---- log -------------------------------------------------------------------
        ("Log.Title",                  "Log", "日志"),
        ("Log.Copy",                   "Copy", "复制"),
        ("Log.Clear",                  "Clear", "清空"),
        ("Log.Empty",                  "No log yet.", "暂无日志。"),

        // ---- status bar ------------------------------------------------------------
        ("Status.NotConnected",        "Not connected.", "未连接"),
        ("Status.HeadlessMode",        "Headless mode", "无界面模式"),
        ("Status.MockMode",            "Mock mode", "模拟模式"),
        ("Status.Working",             "{0}…", "{0}…"),
        ("Status.CheckingEnvironment", "Checking environment", "正在检查环境"),
        ("Status.Connecting",          "Connecting to TIA Portal", "正在连接 TIA Portal"),
        ("Status.OpeningProject",      "Opening project", "正在打开项目"),
        ("Status.ReadingBlocks",       "Reading blocks of {0}", "正在读取 {0} 的程序块"),
        ("Status.Exporting",           "Exporting", "正在导出"),
        ("Status.Importing",           "Importing", "正在导入"),
        ("Status.Compiling",           "Compiling", "正在编译"),
        ("Status.Inspecting",          "Inspecting", "正在审查"),
        ("Status.SavingProject",       "Saving project", "正在保存项目"),
        ("Status.ReadingVc",           "Reading version control", "正在读取版本控制"),
        ("Status.CreatingWorkspace",   "Creating workspace", "正在创建工作区"),
        ("Status.MappingProject",      "Mapping project", "正在映射项目"),
        ("Status.ComparingWorkspace",  "Comparing with workspace", "正在与工作区比较"),
        ("Status.Synchronizing",       "Synchronizing {0}", "正在同步 {0}"),
        ("Status.EnvOk",               "Environment OK.", "环境正常。"),
        ("Status.EnvNotReady",         "Environment not ready — see the log.", "环境未就绪 — 详见日志。"),
        ("Status.Connected",           "Connected ({0}, Openness {1}).", "已连接({0},Openness {1})。"),
        ("Status.ProjectOpen",         "Project {0} open.", "项目 {0} 已打开。"),
        ("Status.BlocksIn",            "{0} block(s) in {1}.", "{1} 中共 {0} 个程序块。"),
        ("Status.Exported",            "Exported {0}/{1} to {2}", "已导出 {0}/{1} 到 {2}"),
        ("Status.ExportedFailed",      "Exported {0}/{1} to {2} ({3} failed)", "已导出 {0}/{1} 到 {2}({3} 个失败)"),
        ("Status.Imported",            "Imported {0}/{1} file(s). Not saved yet.", "已导入 {0}/{1} 个文件。尚未保存。"),
        ("Status.CompileResult",       "{0}: {1} error(s), {2} warning(s) in {3}s",
                                       "{0}:{1} 个错误,{2} 个警告,耗时 {3} 秒"),
        ("Status.InspectResult",       "{0} finding(s) over {1} block(s).", "在 {1} 个程序块中发现 {0} 处问题。"),
        ("Status.ProjectSaved",        "Project saved.", "项目已保存。"),
        ("Status.VcUnsupported",       "This project has no Version Control Interface (needs TIA Portal V21+).",
                                       "该项目没有版本控制接口(需要 TIA Portal V21 及以上)。"),
        ("Status.VcNoWorkspace",       "No workspace yet. Point one at your Git working tree and create it.",
                                       "还没有工作区。请指向你的 Git 工作树并创建一个。"),
        ("Status.VcWorkspaces",        "{0} workspace(s).", "共 {0} 个工作区。"),
        ("Status.VcInSync",            "{0} mapped object(s), all in sync — nothing to commit.",
                                       "已映射 {0} 个对象,全部同步 — 无需提交。"),
        ("Status.VcDiffer",            "{0} mapped object(s), {1} differ.", "已映射 {0} 个对象,其中 {1} 个存在差异。"),
        ("Status.VcCreated",           "Workspace created. Now map the project into it.",
                                       "工作区已创建。接下来把项目映射进去。"),
        ("Status.VcMapDry",            "Dry run: {0} would be mapped, {1} already, {2} unsupported. Clear Dry run to apply.",
                                       "预演:将映射 {0} 个,已映射 {1} 个,{2} 个不支持。取消勾选“预演”才会实际执行。"),
        ("Status.VcMapApplied",        "Mapped {0}, already {1}, unsupported {2}, failed {3}.",
                                       "已映射 {0} 个,原已映射 {1} 个,不支持 {2} 个,失败 {3} 个。"),
        ("Status.VcSyncDry",           "Dry run: {0} would sync {1}, {2} already equal. Clear Dry run to apply.",
                                       "预演:将同步 {0} 个({1}),{2} 个已一致。取消勾选“预演”才会实际执行。"),
        ("Status.VcSyncApplied",       "{0} synchronized, {1} failed, {2} already equal.",
                                       "已同步 {0} 个,失败 {1} 个,{2} 个已一致。"),

        // ---- log lines -------------------------------------------------------------
        ("Log.BridgeStarted",          "bridge started ({0}).", "bridge 已启动({0})。"),
        ("Log.BridgeExited",           "bridge process exited.", "bridge 进程已退出。"),
        ("Log.EnvironmentHeader",      "--- environment ---", "--- 运行环境 ---"),
        ("Log.Installed",              "installed: V{0} {1}", "已安装:V{0} {1}"),
        ("Log.Attached",               "attached to an open project: {0}", "已附加到打开的项目:{0}"),
        ("Log.Opened",                 "opened {0} ({1})", "已打开 {0}({1})"),
        ("Log.DeviceCount",            "{0} device(s).", "共 {0} 个设备。"),
        ("Log.Failed",                 "FAILED {0}: {1}", "失败 {0}:{1}"),
        ("Log.Error",                  "error: {0}", "错误:{0}"),
        ("Log.InspectionHeader",       "--- inspection of {0} ---", "--- {0} 的审查结果 ---"),
        ("Log.WorkspaceCreated",       "created workspace {0} at {1}", "已创建工作区 {0},位置 {1}"),
        ("Log.CommitHint",             "Commit from {0}:  git add -A && git commit",
                                       "在 {0} 中提交:git add -A && git commit"),
        ("Log.PullHint",               "The project now holds the workspace's version — compile and save it.",
                                       "项目现在是工作区中的版本 — 请编译并保存。"),

        // ---- dialogs ---------------------------------------------------------------
        ("Dialog.OpenProject.Title",   "Open a TIA Portal project", "打开 TIA Portal 项目"),
        ("Dialog.OpenProject.Filter",  "TIA Portal projects|*.ap21;*.ap20;*.ap19;*.ap18;*.ap17;*.ap16;*.ap15_1|All files|*.*",
                                       "TIA Portal 项目|*.ap21;*.ap20;*.ap19;*.ap18;*.ap17;*.ap16;*.ap15_1|所有文件|*.*"),
        ("Dialog.Export.Title",        "Choose the export folder", "选择导出文件夹"),
        ("Dialog.Workspace.Title",     "Choose the workspace folder (your Git working tree)",
                                       "选择工作区文件夹(你的 Git 工作树)"),
        ("Dialog.Import.Title",        "Select blocks to import", "选择要导入的程序块"),
        ("Dialog.Import.Filter",       "SimaticML (*.xml)|*.xml",
                                      "SimaticML XML 文件 (*.xml)|*.xml"),
        ("Dialog.Import.Caption",      "Import", "导入"),
        ("Dialog.Import.Text",         "Replace blocks that already exist in the project?\n\nNo  = fail on blocks that already exist (safer)\nYes = overwrite them",
                                       "是否替换项目中已存在的程序块?\n\n否 = 遇到已存在的块即失败(更安全)\n是 = 直接覆盖"),
        ("Dialog.Save.Caption",        "Save", "保存"),
        ("Dialog.Save.Text",           "Save the project to disk? This cannot be undone.",
                                       "确定把项目保存到磁盘吗?此操作不可撤销。"),
        ("Dialog.Pull.Caption",        "Restore from workspace", "从工作区恢复"),
        ("Dialog.Pull.Text",           "Read the workspace's text files back INTO the project?\n\nThis OVERWRITES blocks in the open project and cannot be undone.\nCompile and save afterwards.",
                                       "确定把工作区的文本文件读回项目吗?\n\n这会覆盖已打开项目中的程序块,且不可撤销。\n完成后请编译并保存。"),
        ("Dialog.Error.Caption",       "Unexpected error", "未预期的错误"),
        ("Dialog.Error.Details",       "Details written to {0}", "详细信息已写入 {0}"),
    ];

    public static readonly IReadOnlyDictionary<string, string> English = Build(static e => e.En);

    public static readonly IReadOnlyDictionary<string, string> Chinese = Build(static e => e.Zh);

    private static IReadOnlyDictionary<string, string> Build(Func<(string Key, string En, string Zh), string> pick)
    {
        var table = new Dictionary<string, string>(Catalogue.Length, StringComparer.Ordinal);
        foreach (var entry in Catalogue) table[entry.Key] = pick(entry);
        return table;
    }
}

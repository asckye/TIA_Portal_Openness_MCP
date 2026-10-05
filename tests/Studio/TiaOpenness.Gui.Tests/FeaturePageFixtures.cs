using System;
using System.Collections.Generic;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;

namespace TiaOpenness.Gui.Tests;

internal static class FeaturePageFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 4, 20, 41, 26, TimeSpan.FromHours(-7));
    private static LocalizedText Text(string en, string zh) => LocalizedText.Literal(Loc.Current.Language == AppLanguage.Chinese ? zh : en);
    internal static ApprovalRequest Request(string id = "req-f379") => new(id, "Claude Code", "HTTP", "V14 SP1 engine", "14sp1",
        @"D:\Projects\Line04\Conveyor_Line_04.ap14", "PLC_1",
        [new(Text("Delete block", "删除块"), "FC_Legacy_Speed", "DeletePlcBlock"), new(Text("Import block", "导入块"), "FB_AutoSpeedCtrl", "ImportBlock")],
        [Text("Irreversible", "不可撤销"), Text("Overwrite", "覆盖已有对象"), Text("Changes project", "会修改工程")],
        "abece9", 120, Now.AddSeconds(118), ApprovalState.Pending,
        "{\"requestId\":\"" + id + "\",\"project\":\"Conveyor_Line_04\",\"plc\":\"PLC_1\",\"operations\":[{\"tool\":\"DeletePlcBlock\",\"target\":\"FC_Legacy_Speed\"},{\"tool\":\"ImportBlock\",\"target\":\"FB_AutoSpeedCtrl\"}],\"secret\":\"fixture-private-key\"}");

    internal static ApprovalServiceStub Approvals(bool populated = true)
    {
        var service = new ApprovalServiceStub(() => Now);
        if (populated)
        {
            service.Receive(Request("req-previous"));
            service.Approve("req-previous"); service.Transition("req-previous", ApprovalState.Executing); service.Transition("req-previous", ApprovalState.Unknown);
            service.Receive(Request());
        }
        return service;
    }

    internal sealed class Journal : ObservableObject, ICallJournalService
    {
        public IReadOnlyList<CallRecord> Calls { get; private set; } =
        [
            new("call-project", Now.AddMinutes(-2), "V14 SP1 engine", "14sp1", "GetProjectInfo", false, CallResult.Success, 210,
                "Conveyor_Line_04", "{}", Text("Project info returned.", "已返回工程信息。"), "", LocalizedText.Empty, Text("No approval needed (read)", "无需审批（只读）")),
            new("call-list", Now.AddMinutes(-2), "V14 SP1 engine", "14sp1", "ListPlcBlocks", false, CallResult.Success, 340,
                "PLC_1 / Software", "{\"plcName\":\"PLC_1\"}", Text("9 blocks returned.", "已返回 9 个程序块。"), "", LocalizedText.Empty, Text("No approval needed (read)", "无需审批（只读）")),
            new("call-export", Now.AddMinutes(-2), "V14 SP1 engine", "14sp1", "ExportBlock", false, CallResult.Failed, 1020,
                "Safety_Door_FB", "{\"blockName\":\"Safety_Door_FB\",\"headers\":{\"Authorization\":\"Bearer fixture-private-key\"}}",
                Text("The protected block could not be exported.", "受保护的程序块无法导出。"), "KNOW_HOW_PROTECTED", Text("Export is not available for this block.", "此程序块不可导出。"), Text("No approval needed (read)", "无需审批（只读）")),
            new("req-f379", Now.AddSeconds(-2), "V14 SP1 engine", "14sp1", "batch · 2", true, CallResult.Pending, null,
                "FC_Legacy_Speed…", Request().ParametersJson, Text("Waiting for approval.", "等待批准。"), "", LocalizedText.Empty, Text("20:41:24 · Awaiting user decision", "20:41:24 · 等待人工审批")),
        ];
        public ConnectionInfo Connection { get; } = new("192.168.86.131:8765", "HTTP",
            "{\"mcpServers\":{\"tia-portal-vm\":{\"type\":\"http\",\"url\":\"http://192.168.86.131:8765/mcp\",\"headers\":{\"Authorization\":\"Bearer fixture-private-key\"}}}}");
        internal void Replace(IReadOnlyList<CallRecord> calls) { Calls = calls; Raise(nameof(Calls)); }
    }

    internal sealed class Audit : ObservableObject, IAuditLogService
    {
        public IReadOnlyList<AuditEvent> Events { get; set; } =
        [
            new(1239, Now.AddSeconds(28), AuditEventType.End, "req-f379", "done", "4e3ec6"),
            new(1238, Now.AddSeconds(27), AuditEventType.Start, "req-f379", "—", "6bf87b"),
            new(1237, Now.AddSeconds(25), AuditEventType.Approve, "req-f379", "user", "2e86f6"),
            new(1236, Now.AddSeconds(-2), AuditEventType.Request, "batch · 2", "2 ops", "220289"),
            new(532, Now.AddMinutes(-2), AuditEventType.Toggle, "approval", "on", "7c1e9a"),
        ];
        private int _size = 10, _copies = 5;
        public int TotalCount => 1239;
        public int FileSizeMb { get => _size; set => Set(ref _size, value); }
        public int Copies { get => _copies; set => Set(ref _copies, value); }
        public LocalizedText Coverage => Text("≈ last 3 days", "约保留最近 3 天");
        internal bool Broken { get; set; }
        public AuditVerification Verify() => new(!Broken, TotalCount, Broken ? 532 : null);
        internal int OpenCount;
        public LocalizedText OpenFolder() { OpenCount++; return LocalizedText.Empty; }
    }

    internal sealed class Environment : ObservableObject, IEnvironmentCheckService
    {
        private static EnvironmentCheck Row(string id, CheckStatus status, LocalizedText name, string en, string zh,
            string fixEn = "", string fixZh = "", string detailEn = "Offline fixture details.", string detailZh = "离线夹具详情。")
            => new(id, status, name, Text(en, zh), Text(fixEn, fixZh), Text(detailEn, detailZh));
        public IReadOnlyList<EnvironmentGroup> Groups { get; private set; } =
        [
            new(LocalizedText.Literal("TIA Portal"),
            [
                Row("v14", CheckStatus.Pass, LocalizedText.Literal("TIA Portal V14 SP1"), "Installed · Openness ready", "已安装 · Openness 可用"),
                Row("v17", CheckStatus.Pass, LocalizedText.Literal("TIA Portal V17"), "Installed · Openness ready", "已安装 · Openness 可用"),
                Row("v21", CheckStatus.Unchecked, LocalizedText.Literal("TIA Portal V21"), "Not installed", "未安装"),
                Row("membership", CheckStatus.Warn, LocalizedText.Key("Env.Membership"), "User is not in Siemens TIA Openness", "当前用户不在 Siemens TIA Openness 组", "Join group", "加入用户组", "Sign out and sign in again after joining the group.", "加入用户组后需注销并重新登录。"),
            ]),
            new(LocalizedText.Key("Env.Runtime"),
            [Row("framework", CheckStatus.Pass, LocalizedText.Literal(".NET Framework 4.8"), "4.8.09032", "4.8.09032"), Row("runtime", CheckStatus.Pass, LocalizedText.Literal(".NET Runtime"), "Bundled .NET runtime 10.0.12", "随包 .NET 运行时 10.0.12")]),
            new(LocalizedText.Key("Env.Application"),
            [Row("engines", CheckStatus.Pass, LocalizedText.Literal("Engine · worker"), "v14 / v17 workers ready", "v14 / v17 worker 就绪"), Row("data", CheckStatus.Pass, LocalizedText.Key("Env.Data"), @"<bundle>\data is writable", @"<bundle>\data 可写")]),
            new(LocalizedText.Key("Env.Network"),
            [Row("port", CheckStatus.Pass, LocalizedText.Key("Env.Port"), "8765 available", "8765 空闲"), Row("url", CheckStatus.Warn, LocalizedText.Key("Env.Url"), "URL reservation missing", "尚未配置 URL 保留", "Configure network", "配置网络"), Row("firewall", CheckStatus.Fail, LocalizedText.Key("Env.Firewall"), "Inbound rule missing", "缺少入站规则", "View instructions", "查看说明")]),
        ];
        public IReadOnlyList<string> LogTail { get; } = ["20:41:21  Mapping done · 9 mapped", "20:41:23  → AI calls", "20:41:24  Write request · Claude Code", "20:41:31  Approved · req-f379", "20:41:33  → Audit log", "20:41:34  Execution done · req-f379", "20:41:35  Audit · hash chain intact", "20:41:37  → Environment check"];
        internal int RecheckCount;
        internal string? FixedId;
        public LocalizedText Recheck() { RecheckCount++; return LocalizedText.Empty; }
        public LocalizedText Fix(string checkId) { FixedId = checkId; return LocalizedText.Empty; }
        internal void Replace(IReadOnlyList<EnvironmentGroup> groups) { Groups = groups; Raise(nameof(Groups)); }
    }

    internal sealed class Diagnostics : ObservableObject, IDiagnosticBundleService
    {
        private DiagnosticProgress _progress = new(DiagnosticState.Idle, LocalizedText.Empty);
        public DiagnosticProgress Progress { get => _progress; set => Set(ref _progress, value); }
        internal int OpenCount;
        public string Export() { Progress = new(DiagnosticState.Running, Text("Collecting logs", "收集日志"), 35); return "Env.Checking"; }
        public LocalizedText OpenFolder() { OpenCount++; return LocalizedText.Empty; }
        internal void Complete() => Progress = new(DiagnosticState.Done, LocalizedText.Empty, 100, @"C:\Users\Engineer\Desktop\TIA-Diagnostics-20261004.zip");
    }

    internal sealed class Notification : IApprovalNotification
    {
        internal int Count;
        internal Action? Activate;
        public void Show(string title, string message, Action activate) { Count++; Activate = activate; }
        public void Dispose() { Activate = null; }
    }
}

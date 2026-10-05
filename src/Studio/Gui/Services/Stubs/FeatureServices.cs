using System;
using System.Collections.Generic;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services.Stubs;

// Stub until P6-45: no journal or MCP connection is attached.
public sealed class CallJournalServiceStub : ObservableObject, ICallJournalService
{
    public IReadOnlyList<CallRecord> Calls => Array.Empty<CallRecord>();
    public ConnectionInfo Connection { get; } = new("", "", "{\"mcpServers\":{\"tia-portal-vm\":{\"type\":\"http\",\"url\":\"<MCP address>\",\"headers\":{\"Authorization\":\"Bearer ••••\"}}}}");
}

// Stub until P6-46: retention is memory-only; no files or integrity results are fabricated.
public sealed class AuditLogServiceStub : ObservableObject, IAuditLogService
{
    private int _size = 10;
    private int _copies = 5;
    public IReadOnlyList<AuditEvent> Events => Array.Empty<AuditEvent>();
    public int TotalCount => 0;
    public int FileSizeMb
    {
        get => _size;
        set { if (value is not (5 or 10 or 50)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _size, value); }
    }
    public int Copies
    {
        get => _copies;
        set { if (value is not (3 or 5 or 10)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _copies, value); }
    }
    public LocalizedText Coverage => LocalizedText.Key("Feature.NotConnected");
    public AuditVerification Verify() => new(null);
    public LocalizedText OpenFolder() => LocalizedText.Key("Feature.NotConnected");
}

// Stub until P6-47: only the check inventory is shown, without probing the machine or network.
public sealed class EnvironmentCheckServiceStub : ObservableObject, IEnvironmentCheckService
{
    public IReadOnlyList<EnvironmentGroup> Groups { get; } =
    [
        new(LocalizedText.Literal("TIA Portal"),
        [
            Row("installations", LocalizedText.Key("Env.Installations")),
            Row("membership", LocalizedText.Key("Env.Membership")),
        ]),
        new(LocalizedText.Key("Env.Runtime"),
        [Row("framework", LocalizedText.Literal(".NET Framework 4.8")), Row("runtime", LocalizedText.Key("Env.BundledRuntime"))]),
        new(LocalizedText.Key("Env.Application"),
        [Row("engines", LocalizedText.Key("Env.Engines")), Row("data", LocalizedText.Key("Env.Data"))]),
        new(LocalizedText.Key("Env.Network"),
        [Row("port", LocalizedText.Key("Env.Port")), Row("url", LocalizedText.Key("Env.Url")), Row("firewall", LocalizedText.Key("Env.Firewall"))]),
    ];
    private static EnvironmentCheck Row(string id, LocalizedText name) => new(id, CheckStatus.Unchecked, name,
        LocalizedText.Key("Env.Unchecked"), LocalizedText.Empty, LocalizedText.Key("Feature.NotConnected"));
    public IReadOnlyList<string> LogTail => Array.Empty<string>();
    public LocalizedText Recheck() => LocalizedText.Key("Feature.NotConnected");
    public LocalizedText Fix(string checkId) => LocalizedText.Key("Feature.NotConnected");
}

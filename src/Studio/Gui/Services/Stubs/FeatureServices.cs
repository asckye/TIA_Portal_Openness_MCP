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

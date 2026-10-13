using System;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Online;

internal sealed class OnlineLog
{
    private readonly IPlcOrganisationSession _session;
    private readonly Action<int, Exception?, string, object?[]>? _write;
    internal OnlineLog(IPlcOrganisationSession session, Action<int, Exception?, string, object?[]>? write)
    {
        _session = session;
        _write = write;
    }
    internal void Error(Exception error, string message, params object?[] values)
    {
        if (_write != null) _write(3, error, message, values);
        else _session.LogEngineeringFailure(error, message);
    }
    internal void Warning(Exception error, string message, params object?[] values) => _write?.Invoke(2, error, message, values);
    internal void Warning(string message, params object?[] values) => _write?.Invoke(2, null, message, values);
    internal void Information(string message, params object?[] values) => _write?.Invoke(1, null, message, values);
    internal void Debug(string message, params object?[] values) => _write?.Invoke(0, null, message, values);
}

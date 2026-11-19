using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public static class ReadbackStep
{
    public const string Message = "The write completed but its readback failed.";

    public static bool TryRead<T>(IDictionary<string, object?> meta, string phase, Func<T> read, out T value)
    {
        try { value = read(); return true; }
        catch (Exception error) when (AfterWrite(meta) && (!HmiReadPolicy.ConnectionUnavailable(error) || HmiReadPolicy.DisposedObjectOnly(error)))
        {
            Failed(meta, phase, error.GetBaseException().Message);
            value = default!;
            return false;
        }
    }

    private static bool AfterWrite(IDictionary<string, object?> meta) =>
        meta.TryGetValue("writeReturned", out var returned) && returned is true && !meta.ContainsKey("rollbackOutcome")
        && !meta.ContainsKey("connectionUnavailable") && !meta.ContainsKey("portalLost")
        && !True("writeOutcomeUnknown", meta) && !True("rollbackCompleted", meta) && !True("unrestoredWrite", meta);

    private static bool True(string key, IDictionary<string, object?> meta) => meta.TryGetValue(key, out var value) && value is true;

    public static void Failed(IDictionary<string, object?> meta, string phase, string message)
    {
        if (!AfterWrite(meta)) throw new InvalidOperationException(message);
        meta["phase"] = phase;
        meta["readbackFailed"] = true;
        meta["nativeMessage"] = NativeFailureText.Compose(message, null, Array.Empty<string>());
        meta["dataComplete"] = false;
    }
}

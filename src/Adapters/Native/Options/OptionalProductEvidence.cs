using System;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Options;

internal static class OptionalProductEvidence
{
    internal static bool Issued(HmiObject meta)
        => new[] { "mayHaveChanged", "mayHaveWrittenFiles", "mayHaveExternalEffects", "operationStarted" }.Any(key => meta[key] is true);
    internal static void Safe(HmiObject row, string key, Func<object?> read)
    {
        try { row[key] = read(); }
        catch (Exception error)
        {
            row[key] = null;
            row[key + "Error"] = error.GetBaseException().Message;
        }
    }
    internal static string CategoryLabel(string category) => category == "styleGuide" ? "rule set" : category == "application" ? "test case" : "system test case";
    internal static void Page(object?[] all, int offset, int limit, HmiObject meta)
    {
        var rows = all.Skip(offset).Take(limit).ToArray();
        meta["records"] = new HmiArray(rows);
        meta["expectedCount"] = all.Length;
        meta["actualCount"] = rows.Length;
        meta["offset"] = offset;
        meta["limit"] = limit;
        meta["truncated"] = offset + rows.Length < all.Length;
        meta["nextOffset"] = offset + rows.Length < all.Length ? offset + rows.Length : (int?)null;
    }
    internal static void RecordNativeState(HmiObject meta, string state)
    {
        meta["nativeResultReturned"] = true;
        meta["nativeState"] = state;
        meta["mayHaveChanged"] = true;
        meta["logFilePath"] = null;
        meta["nativeMessages"] = null;
    }
    internal static void RecordFailure(HmiObject meta, Exception exception)
    {
        var cause = exception.GetBaseException();
        meta["failureType"] = cause is PlcSoftwareException ? "PortalException" : cause.GetType().Name;
        if (Issued(meta)) return;
        meta["operationNotStarted"] = true;
        string? code = cause is PlcSoftwareException portal ? portal.Code switch
        {
            "InvalidParams" => "INVALID_ARGUMENT",
            "NotFound" => "NOT_FOUND",
            "NotSupportedOnVersion" => "UNSUPPORTED_CAPABILITY",
            "InvalidState" => "PRECONDITION_FAILED",
            _ => null
        } : cause is ArgumentException ? "INVALID_ARGUMENT"
            : cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
            : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
            : cause is IOException ? "IO_FAILED" : cause is InvalidOperationException ? "PRECONDITION_FAILED" : null;
        if (code != null) meta["rejectionCode"] = code;
    }
}

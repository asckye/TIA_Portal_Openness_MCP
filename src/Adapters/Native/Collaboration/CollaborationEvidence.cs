using System;
using System.Security;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Collaboration;

internal static class CollaborationEvidence
{
    internal static void Safe(HmiObject row, string key, Func<object?> read)
    {
        try { row[key] = read(); }
        catch (Exception error)
        {
            row[key] = null;
            row[key + "Error"] = error.GetBaseException().Message;
        }
    }

    internal static SecureString ToSecureString(string value)
        => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(value);
}

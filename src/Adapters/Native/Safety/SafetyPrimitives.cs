using System;
using System.Security;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Safety;

internal static class SafetyPrimitives
{
    internal static SecureString ToSecureString(string password) => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(password);
    internal static (int Skip, int Take) Paginate(HmiObject meta, int count, int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
        int take = Math.Min(limit, Math.Max(0, count - offset));
        meta["offset"] = offset;
        meta["limit"] = limit;
        meta["dataComplete"] = offset == 0 && !(offset + limit < count);
        meta["expectedCount"] = count;
        meta["actualCount"] = take;
        meta["nextOffset"] = offset + limit < count ? offset + limit : (int?)null;
        meta["truncated"] = offset + limit < count;
        return (offset, limit);
    }
}

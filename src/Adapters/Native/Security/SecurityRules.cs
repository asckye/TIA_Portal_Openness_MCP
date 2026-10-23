using System;
using System.Collections.Generic;
using System.Security;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Security;

internal static class SecurityRules
{
    internal static void RequireApprovedAttribute(string name, IEnumerable<string> approved)
    {
        try { SecurityAttributePolicy.RequireApproved(name, approved); }
        catch (NotSupportedException error) { throw new PlcSoftwareException("NotSupportedOnVersion", error.Message); }
    }
    internal static readonly string[] DeviceCategories = { "deviceRights", "roleDeviceRights" };
    internal static SecureString Secure(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Empty password refused.");
        return EngineeringCredentialRules.ToSecureString(password);
    }
    internal static void PageMeta(HmiObject meta, int total, int offset, int limit, int pageCount)
    {
        if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
        if (pageCount < 0 || pageCount > limit || offset + pageCount > total) throw new ArgumentException("Page slice inconsistent with total.");
        meta["expectedCount"] = total; meta["actualCount"] = pageCount; meta["offset"] = offset; meta["limit"] = limit;
        meta["nextOffset"] = offset + pageCount < total ? offset + pageCount : (int?)null;
        meta["truncated"] = offset + pageCount < total;
    }
}

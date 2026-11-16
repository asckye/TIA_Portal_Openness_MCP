using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Hardware;

#if TIA_ADAPTER_INTERNAL_VERSIONING
internal static class SubnetTypePolicy
#else
public static class SubnetTypePolicy
#endif
{
    public static readonly IReadOnlyList<string> AllowedValues = Array.AsReadOnly(
        new[] { "PROFINET", "PN", "IndustrialEthernet", "Industrial Ethernet", "PN/IE" });
    public static readonly string Message = "subnetType must be one of: " + string.Join(", ", AllowedValues) + ".";

    public static bool IsSupported(string? subnetType)
    {
        var value = (subnetType ?? string.Empty).Trim();
        return value.Length == 0 || AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

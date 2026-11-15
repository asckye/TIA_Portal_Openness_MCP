using System;

namespace TiaMcp.Logic.Siemens;

public static class SubnetTypePolicy
{
    public static bool IsSupported(string? subnetType)
    {
        var value = (subnetType ?? string.Empty).Trim();
        return value.Length == 0
            || value.Equals("PROFINET", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("IndustrialEthernet", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Industrial Ethernet", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PN/IE", StringComparison.OrdinalIgnoreCase);
    }
}

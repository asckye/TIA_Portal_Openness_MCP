using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
#if PLC_SAFETY
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif
#if SECURITY_UMAC
using Siemens.Engineering.Umac;
#endif
namespace TiaMcp.Adapters.Native.Security;
public static class ProjectSecurityPrimitives
{
    public static object? FindOrdinal(object collection, string name, string kind)
    {
        var matches = HardwareGroupOperations.Items(collection).Where(x => string.Equals(HardwareGroupOperations.Get(x, "Name").ToString(), name, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact " + kind + " name: " + name);
        return matches.SingleOrDefault();
    }
#if SECURITY_UMAC
    public static UmacConfigurator RequireUmac(EngineeringProject project)
        => project.GetService<UmacConfigurator>() ?? throw new NotSupportedException("UmacConfigurator service unavailable on this project: TIA returns it only for a protected project opened with UMAC credentials. Enabling protection is intentionally not exposed.");
    public static Role ExactUmacRole(UmacConfigurator umac, string name)
    {
        var custom = FindOrdinal(umac.CustomRoles, name, "custom role"); var system = FindOrdinal(umac.SystemRoles, name, "system role");
        if (custom != null && system != null) throw new InvalidOperationException("Role name exists as both custom and system role: " + name);
        return (Role?)custom ?? (Role?)system ?? throw new PortalException(PortalErrorCode.NotFound, "Exact role not found: " + name);
    }
    public static Dictionary<string, object?> UmacRow(object item) => SecurityEvidence.UmacRow(item);
#endif
}

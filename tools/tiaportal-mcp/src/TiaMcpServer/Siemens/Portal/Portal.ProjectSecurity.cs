using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.AdvancedProtection;
using Siemens.Engineering.CustomIdentity;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Settings;
using Siemens.Engineering.SW;
using Siemens.Engineering.Umac;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private UmacConfigurator RequireUmac()
            => _project!.GetService<UmacConfigurator>() ?? throw new NotSupportedException("UmacConfigurator service unavailable on this project: TIA returns it only for a protected project opened with UMAC credentials. Enabling protection is intentionally not exposed.");

        private static object? FindOrdinal(object collection, string name, string kind)
        {
            var matches = EngineeringGroupOperations.Items(collection).Where(x => string.Equals(EngineeringGroupOperations.Get(x, "Name").ToString(), name, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact " + kind + " name: " + name);
            return matches.SingleOrDefault();
        }

        private static Role ExactUmacRole(UmacConfigurator umac, string name)
        {
            var custom = FindOrdinal(umac.CustomRoles, name, "custom role"); var system = FindOrdinal(umac.SystemRoles, name, "system role");
            if (custom != null && system != null) throw new InvalidOperationException("Role name exists as both custom and system role: " + name);
            return (Role?)custom ?? (Role?)system ?? throw new PortalException(PortalErrorCode.NotFound, "Exact role not found: " + name);
        }

        private static JsonObject UmacRow(object item)
        {
            var row = EngineeringScalarProperties.Read(item);
            if (item is User user) row["roles"] = Names(user.Roles);
            if (item is UmcUserGroup group) row["roles"] = Names(group.Roles);
            if (item is CustomRole custom) row["assignedEngineeringRights"] = Names(custom.AssignedEngineeringRights);
            if (item is SystemRole system) row["assignedEngineeringRights"] = Names(system.AssignedEngineeringRights);
            // DeviceFunctionRight rows typed (Identifier / Group; Comment on both the system and the custom subclass).
            if (item is DeviceFunctionRight right) { row["identifier"] = right.Identifier; row["group"] = right.Group; row["rightClass"] = right.GetType().Name; }
            if (item is SystemDeviceFunctionRight systemRight) row["comment"] = systemRight.Comment;
            if (item is CustomDeviceFunctionRight customRight) row["comment"] = customRight.Comment;
            return row;
        }
    }
}

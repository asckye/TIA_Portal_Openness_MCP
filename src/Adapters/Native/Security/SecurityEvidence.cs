using System;
using System.Linq;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
#if SECURITY_UMAC
using Siemens.Engineering.Umac;
#endif
namespace TiaMcp.Adapters.Native.Security
{
#if SECURITY_UMAC
    internal static class SecurityEvidence
    {
        internal static HmiObject UmacRow(object item)
        {
            var row = HmiScalar.Read(item);
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
        private static HmiArray Names(object collection)
            => new HmiArray(HardwareGroupOperations.Items(collection).Select(x => (object?)HardwareGroupOperations.Get(x, "Name").ToString()).ToArray());
    }
#endif
}

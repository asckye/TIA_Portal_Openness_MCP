using System;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public static class SecurityCapabilities
{
    public static string? Unsupported(string release, string operation, string action = "", string scope = "", string category = "", string filePath = "")
    {
        int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
        if (operation == "ManagePlcProtection")
        {
            action = PlcProtectionRules.Actions.FirstOrDefault(candidate =>
                string.Equals(candidate, action.Trim(), StringComparison.OrdinalIgnoreCase)) ?? action;
            if (action is "protectAllConfiguration" or "unprotectAllConfiguration" && version < 21)
                return "All-configuration protection requires V21.";
            if (Array.IndexOf(PlcProtectionRules.MasterSecretActions, action) >= 0 && version < 17)
                return "PLC master secret requires V17 or later.";
        }
        if (operation == "ManageDeviceUsers" && scope == "simpleWebserver" && version < 18)
            return "Simple web users require V18 or later.";
        if (operation == "ManageSyslogServers" && scope == "project" && version < 20)
            return "Project-global syslog requires V20 or later.";
        if (operation == "ManageUmcUsers")
        {
            if (action is "createOffline" or "rename" && version < 20)
                return "Offline UMC creation and renaming require V20 or later.";
            if (scope == "server" && action is "checkConsistency" or "synchronize" && version < 18)
                return "UMC consistency/synchronization requires V18 or later.";
        }
        if (operation == "GetProjectUserManagement" && category == "passwordPolicy" && version < 18)
            return "PasswordPolicyConfigurator requires V18 or later.";
        if (operation == "ManageProjectUserManagement" && action == "deleteDeviceRight" && version < 20)
            return "CustomDeviceFunctionRight.Delete requires V20 or later.";
        if (operation == "ManagePlcCertificate" && action == "export" && version < 19
            && !Path.GetExtension(filePath).Equals(".cer", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(filePath).Equals(".der", StringComparison.OrdinalIgnoreCase))
            return "V16-V18 certificate export requires a .cer or .der output file.";
        return null;
    }
}

using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public static class OpennessReflectionPolicy
    {
        // Reflection is not an alternate write route around confirmation/offline/incident guards.
        // CLR getters and the existing read methods remain available.
        public static string? GuardedWriteRefusal(string typeName, string method, int argumentCount)
        {
            if (method.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
                || method.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                || method.StartsWith("Is", StringComparison.OrdinalIgnoreCase)
                || method.StartsWith("Can", StringComparison.OrdinalIgnoreCase)
                || method.Equals("ToString", StringComparison.OrdinalIgnoreCase) || method.Equals("Equals", StringComparison.OrdinalIgnoreCase)
                || method.Equals("CheckValidity", StringComparison.OrdinalIgnoreCase)) return null;
            if (method.Equals("ProtectProject", StringComparison.OrdinalIgnoreCase) || method.Equals("UnprotectProject", StringComparison.OrdinalIgnoreCase))
                return "Project protection writes are not exposed through reflection. Use GetProjectProtection / ManageProjectUserManagement for the guarded project-protection surface.";
            string? tool = null;
            if (typeName.StartsWith("Siemens.Engineering.SafetyValidation.", StringComparison.Ordinal))
                tool = "ManageSafetyActivationTest / ManageSafetyFunction / ManageSafetyFunctionCondition";
            else if (typeName.StartsWith("Siemens.Engineering.Safety.", StringComparison.Ordinal))
            {
                if (typeName == "Siemens.Engineering.Safety.GlobalSettings")
                {
                    if (argumentCount == 0 && (method.Equals("SafetyModificationsPossible", StringComparison.OrdinalIgnoreCase) || method.Equals("GenerationOfDefaultFailsafeProgram", StringComparison.OrdinalIgnoreCase)
                        || method.Equals("ManagementOfFailsafeInSoftwareUnitsEnvironment", StringComparison.OrdinalIgnoreCase) || method.Equals("UsernameForFChangeHistory", StringComparison.OrdinalIgnoreCase))) return null;
                    tool = "ManageSafetyGlobalSettings";
                }
                else tool = typeName == "Siemens.Engineering.Safety.SafetyPrintout" ? "ExportSafetyPrintout" : "ManagePlcSafety";
            }
            else if (typeName.StartsWith("Siemens.Engineering.Umac.", StringComparison.Ordinal))
                tool = "ManageProjectUserManagement / ManageUmcUsers / ManagePasswordPolicy";
            else if (typeName.StartsWith("Siemens.Engineering.Security.", StringComparison.Ordinal))
                tool = "ManagePlcCertificate / ManageSyslogServers / ManagePasswordPolicy";
            else if (typeName.StartsWith("Siemens.Engineering.AdvancedProtection.", StringComparison.Ordinal)) tool = "GetProjectProtection";
            else if (typeName.StartsWith("Siemens.Engineering.HW.DeviceUserGroup", StringComparison.Ordinal)) tool = "ManageDeviceUserGroup";
            else if (typeName == "Siemens.Engineering.MC.Drives.SafetyAcceptanceTestProvider") tool = "ManageDriveSafetyAcceptanceTest";
            else if (typeName == "Siemens.Engineering.MC.Drives.DFI.SafetyCommissioning" || typeName == "Siemens.Engineering.MC.Drives.DFI.FunctionInUse") tool = "ManageDriveFunctions";
            else if (typeName == "Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider") tool = "ManagePlcBlockProtection";
            else if (typeName == "Siemens.Engineering.SW.Blocks.PlcBlockWriteProtectionProvider") tool = "ManagePlcBlockWriteProtection";
            else if (typeName == "Siemens.Engineering.HmiUnified.Library.ScriptModuleType") tool = "ManageLibraryType";
            else if (typeName.StartsWith("Siemens.Engineering.HW.Features.", StringComparison.Ordinal))
            {
                string name = typeName.Substring("Siemens.Engineering.HW.Features.".Length);
                if (name.StartsWith("PlcAccess", StringComparison.Ordinal) || name == "PlcMasterSecretConfigurator") tool = "ManagePlcProtection";
                else if (name.IndexOf("PasswordPolicy", StringComparison.Ordinal) >= 0) tool = "ManagePasswordPolicy";
                else if (name.IndexOf("UserManagement", StringComparison.Ordinal) >= 0 || name.StartsWith("User", StringComparison.Ordinal) || name.StartsWith("Role", StringComparison.Ordinal)) tool = "ManageDeviceUsers / ManageDeviceUserGroup";
                else if (name.IndexOf("SysLog", StringComparison.OrdinalIgnoreCase) >= 0) tool = "ManageSyslogServers";
                else if (name.IndexOf("Certificate", StringComparison.Ordinal) >= 0) tool = "ManagePlcCertificate";
                else if (name.IndexOf("Watch", StringComparison.Ordinal) >= 0 || name.IndexOf("Force", StringComparison.Ordinal) >= 0) tool = "ManageWatchForceTableWebAccess";
            }
            return tool == null ? null : "Reflection writes to " + typeName + " are refused. Use the guarded " + tool + " tool.";
        }

        public static string? ReflectionRefusal(string? service, string? method)
        {
            const string type = "Siemens.Engineering.CrossReference.CrossReferenceService";
            var suffix = (service ?? "").Trim();
            bool matches = suffix.Length > 0 && (type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || suffix.IndexOf("CrossReference", StringComparison.OrdinalIgnoreCase) >= 0);
            return matches || string.Equals(method, "GetCrossReferences", StringComparison.OrdinalIgnoreCase)
                ? "Native PLC cross references cannot be accessed through reflection. Use the guarded GetPlcCrossReferences tool; its default-disabled policy and consistency checks also apply after explicit opt-in."
                : null;
        }
    }

    public sealed class OpennessReflectionRequest
    {
        public string Operation { get; set; } = "";
        public string ObjectKind { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string PropertyPath { get; set; } = "";
        public string ServiceTypeSuffix { get; set; } = "";
        public string MethodName { get; set; } = "";
        public HardwareScalar[]? Arguments { get; set; }
        public int Limit { get; set; } = 200;
        public bool AllowWrite { get; set; }
    }

    public sealed class OpennessMember
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Type { get; set; }
        public string? Signature { get; set; }
    }

    public class OpennessObservation
    {
        public string Message { get; set; } = "";
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? TypeName { get; set; }
        public IEnumerable<OpennessMember>? Members { get; set; }
        public string? ValueType { get; set; }
        public object? Value { get; set; }
        public string? Collection { get; set; }
        public IEnumerable<string>? Items { get; set; }
    }

    public sealed class OpennessReflectionReply : IWorkerOperationReply
    {
        public OpennessObservation? Observation { get; set; }
        public PlcSoftwareFailure? Failure { get; set; }
        public bool MayHaveChanged { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool BlockReadsAfterUncertain => true;
    }
}

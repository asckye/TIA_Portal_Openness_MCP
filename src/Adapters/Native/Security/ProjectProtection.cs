using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Siemens.Engineering;
#if SECURITY_UMAC
using Siemens.Engineering.Umac;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Security;
namespace TiaMcp.Adapters;
public sealed partial class PlcFoundationEngine
{
    public SecurityResult ReadProjectProtection()
    {
        Check();
#if SECURITY_PROJECT_PROTECTION
        return ProjectProtection();
#else
        throw new NotSupportedException("Project protection indicators require V15.1 or later.");
#endif
    }
#if SECURITY_PROJECT_PROTECTION
        private SecurityResult ProjectProtection()
            => RunSecurityStep("GetProjectProtection", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                var boundProject = project!;
                bool complete = true;
                var projectRow = HmiScalar.Read(boundProject); meta["project"] = projectRow; complete &= projectRow["dataComplete"]!.GetValue<bool>();
                meta["protectProjectMethodAvailable"] = boundProject.GetType().GetMethod("ProtectProject", new[] { typeof(string), typeof(SecureString) }) != null;
                meta["projectProtectedFlag"] = null;
                meta["note"] = "The Openness API exposes no project-level IsProtected scalar. The projectProtectedFlag follows UmacConfigurator availability and EngineeringFunctionRights.Any() (reference/siemens-openness/skills/security/SKILL.md). ProtectionProviderBase is for block protection and cannot detect project protection. ProjectBase.ProtectProject is intentionally not exposed.";
#if SECURITY_UMAC
                var umac = boundProject.GetService<UmacConfigurator>();
                meta["umacConfiguratorAvailable"] = umac != null;
                meta["projectProtectedFlag"] = umac != null && umac.EngineeringFunctionRights.Any();
                if (umac != null)
                {
                    meta["counts"] = new HmiObject {
                        ["users"] = HardwareGroupOperations.Items(umac.ProjectUsers).Count(), ["customRoles"] = HardwareGroupOperations.Items(umac.CustomRoles).Count(),
                        ["systemRoles"] = HardwareGroupOperations.Items(umac.SystemRoles).Count(), ["engineeringRights"] = HardwareGroupOperations.Items(umac.EngineeringFunctionRights).Count(),
                        ["customDeviceRights"] = HardwareGroupOperations.Items(umac.CustomDeviceFunctionRights).Count(), ["umcUsers"] = HardwareGroupOperations.Items(umac.UmcUsers).Count(),
                        ["umcUserGroups"] = HardwareGroupOperations.Items(umac.UmcUserGroups).Count() };
                    if (umac.AnonymousUser != null) { var row = SecurityEvidence.UmacRow(umac.AnonymousUser); meta["anonymousUser"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
                }
#else
                meta["umacConfiguratorAvailable"] = false;
#endif
#if SECURITY_POLICY
                var policy = boundProject.GetService<PasswordPolicyConfigurator>();
                if (policy != null) { var row = HmiScalar.Read(policy); meta["passwordPolicy"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
#endif
#if SECURITY_UMAC
                var umcServer = boundProject.GetService<UmcServerConfigurator>();
                meta["umcServerConfiguratorAvailable"] = umcServer != null;
                if (umcServer?.UmcServer != null) { var row = HmiScalar.Read(umcServer.UmcServer); meta["umcServer"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
#else
                meta["umcServerConfiguratorAvailable"] = false;
#endif
                meta["advancedProtectionProviderAvailable"] = null; // No documented project service; do not probe a block-protection base type.
                meta["verificationCertificates"] = "Siemens.Engineering.Online.Security.VerificationCertificate is only reachable via TlsVerificationConfiguration.Certificates inside an online/download configuration callback; there is no project-level read.";
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                return "Project protection indicators read; no protection state changed.";
            });

#endif
    internal SecurityResult RunSecurityStep(string tool, Func<HmiObject, string> action)
    {
        var reply = RunHmiStep(tool, values => {
            var meta = new HmiObject(values);
            try { return action(meta); }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }, requiresProject: true);
        return new SecurityResult { Message = reply.Message, Meta = reply.Meta,
            RequiresSessionReset = reply.RequiresSessionReset
                || reply.Meta.TryGetValue("mayHaveChanged", out var changed) && changed is true
                    && reply.Meta.TryGetValue("operationSuccess", out var success) && success is false };
    }
}

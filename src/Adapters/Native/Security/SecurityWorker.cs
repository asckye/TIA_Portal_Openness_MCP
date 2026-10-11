using System;
using System.Linq;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Security;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
#if PLC_SAFETY
    internal Siemens.Engineering.ProjectBase? SecurityProject => project;
#else
    internal Siemens.Engineering.Project? SecurityProject => project;
#endif
    internal T SecurityHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
        => HardwareRequireHardwareService<T>(owner, parameter);
    internal object? SecurityFindOnFresh(Func<object> composition, string name, Dictionary<string, object?> meta, string phase)
        => HardwareFindOnFresh(composition, name, meta, phase);
    internal int? SecurityCountOnFresh(Func<object> composition, Dictionary<string, object?> meta, string key)
        => HardwareCountOnFresh(composition, meta, key);
    public SecurityResult ExecuteSecurityDeep(SecurityDeepRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting security preview mode.");
#if !SECURITY_UMC_EXTENDED
        if (request.Operation == "ManageUmcUsers" && request.Action is "createOffline" or "rename")
            throw new NotSupportedException("Offline UMC creation and renaming require V20 or later.");
#endif
#if !SECURITY_UMC_SERVER_SYNC
        if (request.Operation == "ManageUmcUsers" && request.Kind == "server" && request.Action is "checkConsistency" or "synchronize")
            throw new NotSupportedException("UMC consistency/synchronization requires V18 or later.");
#endif
        var security = new SecurityDeepAdapter(new SecuritySession(this, request.Faults), request);
        switch (request.Operation)
        {
#if SECURITY_DEVICE_SYSLOG
            case "ManageSyslogServers":
#if !SECURITY_PROJECT_SYSLOG
                if (request.Scope == "project") throw new NotSupportedException("Project-global syslog requires V20 or later.");
#endif
                return security.ManageSyslogServers(request.Scope, request.Action, request.Name, "{}", "{}", request.DevicePath, request.ItemPath, request.ServerAddress, request.ServerPort, request.ConfirmDelete, request.DryRun);
#endif
#if SECURITY_POLICY
            case "ManagePasswordPolicy": return security.ManagePasswordPolicy(request.Action, request.Target, "{}", request.ConfirmChange, request.DryRun);
#endif
#if SECURITY_UMAC
            case "ManageUmcUsers": return security.ManageUmcUsers(request.Kind, request.Action, request.Name, request.NewName, request.RoleName, request.ServerUserName, request.ServerPassword, request.ConfirmChange, request.DryRun, request.Offset, request.Limit);
#endif
            default: throw new NotSupportedException("The requested security operation is unavailable on this release.");
        }
    }
    public SecurityResult ExecuteCertificate(CertificateRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting certificate preview mode.");
#if SECURITY_CERTIFICATE
#if !SECURITY_CERT_EXPORT_IMPLICIT
        if (request.Action == "export" && !System.IO.Path.GetExtension(request.FilePath).Equals(".cer", StringComparison.OrdinalIgnoreCase)
            && !System.IO.Path.GetExtension(request.FilePath).Equals(".der", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("V16-V18 certificate export requires a .cer or .der output file.");
#endif
        var certificates = new CertificateAdapter(new SecuritySession(this, request.Faults), request);
        return certificates.ManagePlcCertificate(request.DevicePath, request.ItemPath, request.Action, request.CertificateId,
            request.FilePath, request.Usage, "{}", request.Assignment, request.DryRun, request.AssignmentItemPath, "[]", request.Password);
#else
        throw new NotSupportedException("Local certificates require V16 or later.");
#endif
    }
    internal IDisposable SecurityEditAccess() => HardwareEditAccess();
    public SecurityResult ExecuteProjectUsers(ProjectUserRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting project user preview mode.");
#if SECURITY_UMAC
        var session = new SecuritySession(this, request);
        session.RequireUserAction();
        var users = new ProjectUsers(session);
        return request.Operation switch {
            "GetProjectUserManagement" => users.ReadProjectUserManagement(request.Category, request.Name, request.DevicePath, request.ItemPath, request.Offset, request.Limit),
            "ManageProjectUserManagement" => users.ManageProjectUserManagement(request.Action, request.Name, request.Password, request.RoleName, request.RightName, request.Comment, request.Group, request.DevicePath, request.ItemPath, request.ConfirmChange, request.DryRun),
            _ => throw new ArgumentException("Unregistered project user operation: " + request.Operation)
        };
#else
        throw new NotSupportedException("UMAC project users require V17 or later.");
#endif
    }
    public SecurityResult ExecuteDeviceUsers(DeviceUserRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting device user preview mode.");
#if SECURITY_DEVICE_USERS
#if !SECURITY_SIMPLE_WEB
        if (request.Family == "simpleWebserver") throw new NotSupportedException("Simple web users require V18 or later.");
#endif
        return new DeviceUsersAdapter(new SecuritySession(this, request.Faults), request).ManageDeviceUsers(request.DevicePath, request.ItemPath, request.Family, request.Action, request.UserName, request.Password, "[]", request.Active, request.NewName, request.ConfirmDelete, request.DryRun);
#else
        throw new NotSupportedException("Device users require V16 or later.");
#endif
    }
    internal Siemens.Engineering.HW.Device? SecurityLegacyDevice(string path) => HardwareLegacyDevice(path);
    internal System.Collections.Generic.IEnumerable<(DeviceItem Item, string Path)> SecurityTraverseDeviceItems(DeviceItem root, string path) => HardwareTraverseDeviceItems(root, path);
    public object ExecuteHardwareSecurity(HardwareSecurityRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting hardware security preview mode.");
        if (request.Operation == "ManagePlcProtection")
        {
#if !SECURITY_MASTER_SECRET
            if (PlcProtectionRules.MasterSecretActions.Contains(request.Action)) throw new NotSupportedException("PLC master secret requires V17 or later.");
#endif
#if !SECURITY_ALL_CONFIGURATION
            if (request.Action is "protectAllConfiguration" or "unprotectAllConfiguration") throw new NotSupportedException("All-configuration protection requires V21.");
#endif
        }
        var security = new HardwareSecurityAdapter(new SecuritySession(this, request.Faults), request);
        switch (request.Operation)
        {
            case "GetPlcPutGetAccess": return security.GetPutGetAccess(request.DeviceName);
            case "SetPlcPutGetAccess": return new SecurityDataResult(security.SetPutGetAccess(request.DeviceName, request.Enable));
#if SECURITY_WATCH_WEB
            case "ManageWatchForceTableWebAccess": return security.ManageWatchForceTableWebAccess(request.DevicePath, request.ItemPath, request.Action, request.SoftwarePath, request.TableKind, request.TablePath, request.Access, request.ConfirmChange, request.DryRun);
#endif
#if SECURITY_PROJECT_PROTECTION
            case "ManagePlcProtection": return security.ManagePlcProtection(request.DevicePath, request.ItemPath, request.Action, request.AccessLevel, request.Password, request.NewPassword, request.ConfirmChange, request.DryRun);
#endif
            default: throw new NotSupportedException("The requested hardware security operation is unavailable on this release.");
        }
    }
}

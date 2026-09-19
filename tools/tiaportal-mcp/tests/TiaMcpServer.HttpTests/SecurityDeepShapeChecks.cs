using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.32 security / UMC family (Portal.SecurityDeep.cs, the certificate-template extension of
// ManagePlcCertificate and the anonymous-user actions of ManageProjectUserManagement), verified member by member against
// the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base) PublicAPI.
internal static class SecurityDeepShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        Type T(string n)=>core.GetType(n,true)!;
        void Method(string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        void Property(string t,string name,string? type=null,bool? writable=null)
        {
            var p=T(t).GetProperty(name);
            check(p!=null && (type==null || p.PropertyType.Name==type) && (writable==null || p.CanWrite==writable),t+"."+name+(type==null?"":" : "+type)+(writable==null?"":writable.Value?" (writable)":" (read-only)"));
        }
        void Enum(string t,params string[] names)
        {
            var type=T(t); var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        var engineeringService=T("Siemens.Engineering.IEngineeringService");
        void Service(string t) => check(engineeringService.IsAssignableFrom(T(t)),t+" is an IEngineeringService");
        const string sec="Siemens.Engineering.Security.", umac="Siemens.Engineering.Umac.", hw="Siemens.Engineering.HW.", features="Siemens.Engineering.HW.Features.";

        // ---- project-global syslog servers (SyslogServerProvider) ----
        Service(sec+"SyslogServerProvider"); Property(sec+"SyslogServerProvider","Servers","SyslogServerComposition");
        Method(sec+"SyslogServerComposition","Create",new[]{typeof(string)},"SyslogServer"); Method(sec+"SyslogServerComposition","Find",new[]{typeof(string)},"SyslogServer");
        Property(sec+"SyslogServer","Name","String",true); Property(sec+"SyslogServer","Address","String",true); Property(sec+"SyslogServer","Port","Int64",true);
        Property(sec+"SyslogServer","Tls","Boolean",true); Property(sec+"SyslogServer","Comment","String",true); Property(sec+"SyslogServer","AssignedModules","DeviceItemAssociation",false);
        Method(sec+"SyslogServer","Delete",Type.EmptyTypes,"Void");
        Method(hw+"DeviceItemAssociation","Add",new[]{T(hw+"DeviceItem")},"Void"); Method(hw+"DeviceItemAssociation","Remove",new[]{T(hw+"DeviceItem")},"Boolean"); Method(hw+"DeviceItemAssociation","Contains",new[]{T(hw+"DeviceItem")},"Boolean");
        // ---- PLC syslog (SysLogConfigurationManager, S7-1500 FW 3.1+) ----
        Service(features+"SysLogConfigurationManager");
        Property(features+"SysLogConfigurationManager","EnableSystemLogging","Boolean",true); Property(features+"SysLogConfigurationManager","TransportProtocol","SysLogTransportProtocolType",true);
        Property(features+"SysLogConfigurationManager","SysLogServerConfiguration","SysLogServerConfigurationComposition",false); Property(features+"SysLogConfigurationManager","OwnedBy","DeviceItem",false);
        Enum(hw+"SysLogTransportProtocolType","None","TLSServerAndClientAuthentication","TLSOnlyServerAuthentication","UDP");
        Method(features+"SysLogServerConfigurationComposition","Create",new[]{typeof(string),typeof(ushort)},"SysLogServerConfiguration"); Method(features+"SysLogServerConfigurationComposition","Find",new[]{typeof(string)},"SysLogServerConfiguration");
        Property(features+"SysLogServerConfiguration","SysLogServerAddress","String",true); Property(features+"SysLogServerConfiguration","SysLogServerPort","UInt16",true); Method(features+"SysLogServerConfiguration","Delete",Type.EmptyTypes,"Void");

        // ---- password policies ----
        Service(umac+"PasswordPolicyConfigurator");
        foreach(var p in new[]{"IncludesLowerCaseAndUpperCaseCharacters","EnablePasswordAging"}) Property(umac+"PasswordPolicyConfigurator",p,"Boolean",true);
        foreach(var p in new[]{"MinimumLength","MinimumNumericCharacterLength","MinimumSpecialCharacterLength","MinimumUserPasswordsBlockedForReuse","PasswordValidity","PasswordValidityPrewarningTime"}) Property(umac+"PasswordPolicyConfigurator",p,"Int16",true);
        Service(sec+"PlcPasswordPolicyService"); Property(sec+"PlcPasswordPolicyService","PasswordPolicyEnabled","Boolean",true);
        Service(sec+"LegacyPlcPasswordPolicyService"); Property(sec+"LegacyPlcPasswordPolicyService","PasswordPolicyEnabled","Boolean",true); Property(sec+"LegacyPlcPasswordPolicyService","IncludesLowerCaseAndUpperCaseCharacters","Boolean",true);
        foreach(var p in new[]{"MinimumLength","MinimumNumericCharacterLength","MinimumSpecialCharacterLength"}) Property(sec+"LegacyPlcPasswordPolicyService",p,"Int16",true);
        check(core.GetType("Siemens.Engineering.PasswordPolicySettingsException")!=null,"PasswordPolicySettingsException exists (raised outside the official ranges)");

        // ---- UMC users / groups / server ----
        Property(umac+"UmacConfigurator","UmcUsers","UmcUserComposition",false); Property(umac+"UmacConfigurator","UmcUserGroups","UmcUserGroupComposition",false);
        Method(umac+"UmcUserComposition","CreateOfflineUmcUser",new[]{typeof(string)},"UmcUser"); Method(umac+"UmcUserComposition","Create",new[]{T(umac+"UmcUserInfo")},"UmcUser"); Method(umac+"UmcUserComposition","Find",new[]{typeof(string)},"UmcUser");
        Method(umac+"UmcUserGroupComposition","CreateOfflineUmcUserGroup",Type.EmptyTypes,"UmcUserGroup"); Method(umac+"UmcUserGroupComposition","Create",new[]{T(umac+"UmcUserGroupInfo")},"UmcUserGroup"); Method(umac+"UmcUserGroupComposition","Find",new[]{typeof(string)},"UmcUserGroup");
        check(T(umac+"UmcUser").IsSubclassOf(T(umac+"User")),"UmcUser derives from User (Name / Roles)");
        Property(umac+"UmcUser","DomainId","String",false); Property(umac+"UmcUser","IsActive","Boolean",false);
        foreach(var m in new[]{"Activate","Deactivate","Delete"}) Method(umac+"UmcUser",m,Type.EmptyTypes,"Void");
        Method(umac+"UmcUser","SetName",new[]{typeof(string)},"Void");
        Property(umac+"UmcUserGroup","Name","String",false); Property(umac+"UmcUserGroup","Description","String",false); Property(umac+"UmcUserGroup","DomainId","String",false); Property(umac+"UmcUserGroup","IsActive","Boolean",false); Property(umac+"UmcUserGroup","Roles","RoleAssociation",false);
        foreach(var m in new[]{"Activate","Deactivate","Delete"}) Method(umac+"UmcUserGroup",m,Type.EmptyTypes,"Void");
        Method(umac+"UmcUserGroup","SetName",new[]{typeof(string)},"Void");
        Property(umac+"UmcUserInfo","Name","String",false); Property(umac+"UmcUserGroupInfo","UserGroupName","String",false); Property(umac+"UmcUserGroupInfo","Description","String",false);
        Service(umac+"UmcServerConfigurator"); Property(umac+"UmcServerConfigurator","UmcServer","UmcServer",false);
        Method(umac+"UmcServerConfigurator","CheckConsistency",Type.EmptyTypes,"Boolean"); Method(umac+"UmcServerConfigurator","Synchronize",Type.EmptyTypes,"Void");
        Method(umac+"UmcServer","GetUserByName",new[]{typeof(string)},"UmcUserInfo"); Method(umac+"UmcServer","GetUserGroupByName",new[]{typeof(string)},"UmcUserGroupInfo");
        var authentication=T(umac+"UmcServer").GetEvent("Authentication");
        check(authentication!=null && authentication.EventHandlerType==typeof(EventHandler<>).MakeGenericType(T(umac+"UmcAuthenticationEventArgs")),"UmcServer.Authentication : EventHandler<UmcAuthenticationEventArgs>");
        Property(umac+"UmcAuthenticationEventArgs","UmcCredentials","UmcCredentials");
        Property(umac+"UmcCredentials","Name","String",true); Method(umac+"UmcCredentials","SetPassword",new[]{typeof(SecureString)},"Void");
        Method(umac+"UmacConfigurator","ActivateAnonymousUser",Type.EmptyTypes,"Void"); Method(umac+"UmacConfigurator","DeactivateAnonymousUser",Type.EmptyTypes,"Void"); Property(umac+"UmacConfigurator","AnonymousUser","ProjectUser",false);
        Property(umac+"SystemDeviceFunctionRight","Comment","String",false); Property(umac+"CustomDeviceFunctionRight","Comment","String",false);
        Property(umac+"DeviceFunctionRight","Identifier","String",false); Property(umac+"DeviceFunctionRight","Group","String",false);

        // ---- certificate templates ----
        Method(sec+"LocalCertificateStore","GetCertificateTemplate",new[]{T(sec+"CertificateUsage")},"CertificateTemplate");
        Property(sec+"CertificateTemplate","Signature","SignatureAlgorithm",true); Property(sec+"CertificateTemplate","SubjectCommonName","String",true); Property(sec+"CertificateTemplate","Usage","CertificateUsage",true);
        Property(sec+"CertificateTemplate","ValidFrom","DateTime",true); Property(sec+"CertificateTemplate","ValidUntil","DateTime",true); Property(sec+"CertificateTemplate","SubjectAlternativeNames","SubjectAlternativeNameComposition",false);
        Method(sec+"SubjectAlternativeNameComposition","Create",new[]{T(sec+"SubjectAlternativeNameType"),typeof(string)},"SubjectAlternativeName");
        Property(sec+"SubjectAlternativeName","Type","SubjectAlternativeNameType",false); Property(sec+"SubjectAlternativeName","Value","String",false); Method(sec+"SubjectAlternativeName","Delete",Type.EmptyTypes,"Void");
        Enum(sec+"CertificateUsage","None","Tls","WebServer","OpcUaServer","OpcUaClient","OpcUaClientServer"); Enum(sec+"SignatureAlgorithm","None","Sha1RSA","Sha256RSA"); Enum(sec+"SubjectAlternativeNameType","None","Dns","Email","IP","Uri");
        Method(sec+"CertificateComposition","Create",new[]{T(sec+"CertificateTemplate")},"Certificate"); Method(sec+"CertificateComposition","Import",new[]{typeof(FileInfo)},"Certificate"); Method(sec+"CertificateComposition","Import",new[]{typeof(FileInfo),typeof(SecureString)},"Certificate");
        Property(sec+"Certificate","HasPrivateKey","Boolean",false); Property(sec+"Certificate","Id","UInt32",false);
    }
}

using System;
using System.Linq;
using System.Reflection;

// Every native type/member the ProjectSecurity family calls, verified against the installed API assemblies.
internal static class ProjectSecurityShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,int count)
            =>check(T(a,t).GetMethods().Any(m=>m.Name==name && m.GetParameters().Length==count),t+"."+name+"/"+count);
        void Property(Assembly a,string t,string name)=>check(T(a,t).GetProperty(name)!=null,t+"."+name);
        void Service(Assembly a,string t)=>check(T(a,t).GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringService"),t+" is an IEngineeringService");

        // UMAC (Siemens.Engineering.Umac)
        Service(core,"Siemens.Engineering.Umac.UmacConfigurator");
        foreach(var p in new[]{"ProjectUsers","AnonymousUser","SystemRoles","CustomRoles","EngineeringFunctionRights","CustomDeviceFunctionRights","UmcUsers","UmcUserGroups"}) Property(core,"Siemens.Engineering.Umac.UmacConfigurator",p);
        Service(core,"Siemens.Engineering.Umac.UmacDevice"); Property(core,"Siemens.Engineering.Umac.UmacDevice","AvailableDeviceFunctionRights");
        Service(core,"Siemens.Engineering.Umac.PasswordPolicyConfigurator");
        foreach(var p in new[]{"EnablePasswordAging","MinimumLength","PasswordValidity"}) Property(core,"Siemens.Engineering.Umac.PasswordPolicyConfigurator",p);
        Service(core,"Siemens.Engineering.Umac.UmcServerConfigurator"); Property(core,"Siemens.Engineering.Umac.UmcServerConfigurator","UmcServer");
        check(T(core,"Siemens.Engineering.Umac.ProjectUserComposition").GetMethod("Create",new[]{typeof(string),typeof(System.Security.SecureString)})!=null,"ProjectUserComposition.Create(string, SecureString)");
        check(T(core,"Siemens.Engineering.Umac.CustomRoleComposition").GetMethod("Create",new[]{typeof(string),typeof(string)})!=null,"CustomRoleComposition.Create(name, comment)");
        check(T(core,"Siemens.Engineering.Umac.CustomDeviceFunctionRightComposition").GetMethod("Create",new[]{typeof(string),typeof(string),typeof(string)})!=null,"CustomDeviceFunctionRightComposition.Create(name, group, comment)");
        Property(core,"Siemens.Engineering.Umac.User","Name"); Property(core,"Siemens.Engineering.Umac.User","Roles");
        foreach(var m in new[]{"Delete","Activate","Deactivate"}) Method(core,"Siemens.Engineering.Umac.ProjectUser",m,0);
        check(T(core,"Siemens.Engineering.Umac.ProjectUser").GetMethod("SetPassword",new[]{typeof(System.Security.SecureString)})!=null,"ProjectUser.SetPassword(SecureString)");
        Property(core,"Siemens.Engineering.Umac.ProjectUser","IsActive");
        Property(core,"Siemens.Engineering.Umac.Role","Name"); Property(core,"Siemens.Engineering.Umac.Role","Identifier");
        Method(core,"Siemens.Engineering.Umac.RoleAssociation","Add",1); Method(core,"Siemens.Engineering.Umac.RoleAssociation","Remove",1);
        Property(core,"Siemens.Engineering.Umac.CustomRole","AssignedEngineeringRights"); Property(core,"Siemens.Engineering.Umac.SystemRole","AssignedEngineeringRights");
        Method(core,"Siemens.Engineering.Umac.CustomRole","Delete",0);
        Method(core,"Siemens.Engineering.Umac.CustomRole","AssignDeviceFunctionRight",2); Method(core,"Siemens.Engineering.Umac.CustomRole","UnAssignDeviceFunctionRight",2);
        Method(core,"Siemens.Engineering.Umac.CustomRole","GetAssignedDeviceFunctionRights",1); Method(core,"Siemens.Engineering.Umac.SystemRole","GetAssignedSystemDeviceFunctionRights",1);
        Method(core,"Siemens.Engineering.Umac.EngineeringFunctionRightAssociation","Add",1); Method(core,"Siemens.Engineering.Umac.EngineeringFunctionRightAssociation","Remove",1);
        Property(core,"Siemens.Engineering.Umac.EngineeringFunctionRight","Name"); Property(core,"Siemens.Engineering.Umac.DeviceFunctionRight","Name");
        Method(core,"Siemens.Engineering.Umac.CustomDeviceFunctionRight","Delete",0);
        Property(core,"Siemens.Engineering.Umac.UmcUserGroup","Roles");
        check(T(core,"Siemens.Engineering.ProjectBase").GetMethod("ProtectProject",new[]{typeof(string),typeof(System.Security.SecureString)})!=null,"ProjectBase.ProtectProject(string, SecureString) exists (deliberately not exposed)");
        Service(core,"Siemens.Engineering.AdvancedProtection.ProtectionProviderBase");
        Method(core,"Siemens.Engineering.AdvancedProtection.ProtectionProviderBase","GetInvalidPasswordCharacters",0);
        check(core.GetType("Siemens.Engineering.Umac.UmacConfigurator")!.GetProperty("IsProtected")==null && core.GetType("Siemens.Engineering.ProjectBase")!.GetProperty("IsProtected")==null,"No native project IsProtected scalar; tool must not fabricate one");
        if(core.GetType("Siemens.Engineering.Online.Security.VerificationCertificate")==null) Console.WriteLine("CAPABILITY Siemens.Engineering.Online.Security.VerificationCertificate absent on this API version; ReadProjectProtection reports it as unreachable on every version (only inside TlsVerificationConfiguration callbacks).");
        else Property(core,"Siemens.Engineering.Online.Security.VerificationCertificate","Certificate");

        // Multiuser
        Property(core,"Siemens.Engineering.TiaPortal","ProjectServers"); Property(core,"Siemens.Engineering.TiaPortal","LocalSessions");
        check(T(core,"Siemens.Engineering.Multiuser.ProjectServerComposition").GetMethod("Create",new[]{typeof(string),T(core,"Siemens.Engineering.Multiuser.Protocol"),typeof(string),typeof(int)})!=null,"ProjectServerComposition.Create(alias, Protocol, host, port)");
        foreach(var p in new[]{"ServerName","Host","Port"}) Property(core,"Siemens.Engineering.Multiuser.ProjectServer",p);
        foreach(var m in new[]{"GetServerProjects","GetProjectServerGroups","DeleteConnection"}) Method(core,"Siemens.Engineering.Multiuser.ProjectServer",m,0);
        foreach(var m in new[]{"GetLockStateProvider","GetLocalSessions"}) Method(core,"Siemens.Engineering.Multiuser.ProjectServer",m,1);
        Method(core,"Siemens.Engineering.Multiuser.LockStateProvider","IsProjectLocked",0); Method(core,"Siemens.Engineering.Multiuser.LockStateProvider","GetLockOwner",0);
        Property(core,"Siemens.Engineering.Multiuser.ServerProjectInfo","ProjectName"); Property(core,"Siemens.Engineering.Multiuser.ServerProjectInfo","ServerAlias");
        Property(core,"Siemens.Engineering.Multiuser.LocalSessionInfo","SessionId"); Property(core,"Siemens.Engineering.Multiuser.LocalSessionInfo","ProjectFileInfo");
        Property(core,"Siemens.Engineering.Multiuser.ProjectServerGroup","Name");
        Property(core,"Siemens.Engineering.Multiuser.LocalSession","Project"); Property(core,"Siemens.Engineering.Multiuser.LocalSession","MarkingService");
        Method(core,"Siemens.Engineering.Multiuser.LocalSession","IsUptoDate",0);
        check(T(core,"Siemens.Engineering.Multiuser.LocalSession").GetMethod("CloseAndCommit",new[]{typeof(string)})?.ReturnType==typeof(int),"LocalSession.CloseAndCommit(string) returns int");
        Method(core,"Siemens.Engineering.Multiuser.MarkingService","GetMarkings",0);
        Property(core,"Siemens.Engineering.Multiuser.Markings","AllMarkings"); Property(core,"Siemens.Engineering.Multiuser.Markings","ConflictedMarkings");
        Property(core,"Siemens.Engineering.Multiuser.Marking","MarkState"); Property(core,"Siemens.Engineering.Multiuser.Marking","MarkedObject");
        check(T(core,"Siemens.Engineering.Multiuser.MultiuserProject").IsSubclassOf(T(core,"Siemens.Engineering.ProjectBase")),"MultiuserProject derives from ProjectBase");

        // Compare (offline)
        Method(core,"Siemens.Engineering.Library.ProjectLibrary","CompareToLibrary",1); Method(core,"Siemens.Engineering.Library.GlobalLibrary","CompareToLibrary",1);
        check(T(core,"Siemens.Engineering.Library.ProjectLibrary").GetMethods().Single(m=>m.Name=="CompareToLibrary").ReturnType.FullName=="Siemens.Engineering.Library.Compare.LibraryCompareResult","CompareToLibrary returns LibraryCompareResult");
        Method(step7,"Siemens.Engineering.SW.PlcSoftware","CompareTo",1);
        var compareTarget=T(step7,"Siemens.Engineering.SW.PlcSoftware").GetMethods().Single(m=>m.Name=="CompareTo" && m.GetParameters().Length==1).GetParameters()[0].ParameterType;
        Console.WriteLine("CAPABILITY ISoftwareCompareTarget lives in "+compareTarget.Namespace+" on this version (V20: Siemens.Engineering.SW, V21: Siemens.Engineering.Compare); the tool binds CompareTo by reflection with a parameter-type check.");
        foreach(var t in new[]{"Siemens.Engineering.Library.ProjectLibrary","Siemens.Engineering.Library.GlobalLibrary","Siemens.Engineering.SW.PlcSoftware"})
            check(compareTarget.IsAssignableFrom((core.GetType(t) ?? step7.GetType(t))!),t+" is a software compare target");
        Method(core,"Siemens.Engineering.HW.HardwareObject","CompareTo",1);
        check(T(core,"Siemens.Engineering.HW.IHardwareCompareTarget").IsAssignableFrom(T(core,"Siemens.Engineering.HW.Device")) && T(core,"Siemens.Engineering.HW.IHardwareCompareTarget").IsAssignableFrom(T(core,"Siemens.Engineering.HW.DeviceItem")),"Device/DeviceItem are hardware compare targets");
        Property(core,"Siemens.Engineering.Compare.CompareResult","RootElement");
        foreach(var p in new[]{"Elements","ComparisonResult","DetailedInformation","LeftName","RightName"}) Property(core,"Siemens.Engineering.Compare.CompareResultElement",p);
        Property(core,"Siemens.Engineering.Library.Compare.LibraryCompareResult","RootElement");
        foreach(var p in new[]{"Elements","ComparisonResult","DetailedInformation","Left","LeftName","Right","RightName"}) Property(core,"Siemens.Engineering.Library.Compare.LibraryCompareResultElement",p);
        foreach(var v in new[]{"ObjectsIdentical","ContainerContentsIdentical","LeftMissing","RightMissing"}) check(Enum.IsDefined(T(core,"Siemens.Engineering.Library.Compare.LibraryCompareResultState"),v),"LibraryCompareResultState."+v);
        foreach(var v in new[]{"ObjectsIdentical","FolderContentsIdentical","CompareIrrelevant","LeftMissing","RightMissing"}) check(Enum.IsDefined(T(core,"Siemens.Engineering.Compare.CompareResultState"),v),"CompareResultState."+v);
        check(core.GetType("Siemens.Engineering.HW.HardwareObject")!.GetMethod("GetService")!=null || v20,"HardwareObject.GetService present on V21 (tool uses Device/DeviceItem.GetService on both)");
        Method(core,"Siemens.Engineering.HW.Device","GetService",0); Method(core,"Siemens.Engineering.HW.DeviceItem","GetService",0);

        // Settings / custom identity
        Property(core,"Siemens.Engineering.TiaPortal","SettingsFolders");
        foreach(var p in new[]{"Name","Folders","Settings"}) Property(core,"Siemens.Engineering.Settings.TiaPortalSettingsFolder",p);
        Property(core,"Siemens.Engineering.Settings.TiaPortalSetting","Name"); Property(core,"Siemens.Engineering.Settings.TiaPortalSetting","Value");
        Service(core,"Siemens.Engineering.CustomIdentity.CustomIdentityProvider");
        check(T(core,"Siemens.Engineering.CustomIdentity.CustomIdentityProvider").GetMethod("Get",new[]{typeof(string)})?.ReturnType==typeof(string),"CustomIdentityProvider.Get(string) returns string");
        check(typeof(Exception).IsAssignableFrom(T(core,"Siemens.Engineering.CustomIdentity.CustomIdentityNotFoundException")),"CustomIdentityNotFoundException is an exception");

        // Tool contract
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManageProjectUserManagement","ManageMultiuserSession"})
        {
            var method=tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
            check(Equals(method.GetParameters().Single(p=>p.Name=="confirmChange").DefaultValue,false),name+" requires explicit confirmChange");
            check(method.GetParameters().Last().Name=="dryRun",name+" has dryRun as last parameter");
        }
        foreach(var name in new[]{"ReadProjectUserManagement","ReadProjectProtection","CompareLibraries","CompareProjects","ReadProjectSettings"})
            check(tools.GetMethod(name)!=null && tools.GetMethod(name)!.GetParameters().All(p=>p.Name!="dryRun"),name+" is read-only (no dryRun)");
        foreach(var name in new[]{"ReadProjectUserManagement","ManageMultiuserSession","CompareLibraries","CompareProjects","ReadProjectSettings"})
            check(tools.GetMethod(name)!.GetParameters().Any(p=>p.Name=="offset") && tools.GetMethod(name)!.GetParameters().Any(p=>p.Name=="limit"),name+" paginates");
        check(tools.GetMethod("ManageProjectUserManagement")!.GetParameters().Single(p=>p.Name=="password").ParameterType==typeof(string),"password accepted as plain parameter, converted to SecureString internally");
    }
}

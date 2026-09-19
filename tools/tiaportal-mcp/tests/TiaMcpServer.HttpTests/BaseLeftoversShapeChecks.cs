using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.33 Base leftovers (Portal.BaseLeftovers.cs, the rebind guard, transactions, credential-bearing
// OpenProject / GoOnline, R/H providers, typed transfer prompts / results, typed cross references / compare / catalog rows),
// verified member by member against the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base) PublicAPI.
internal static class BaseLeftoversShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetType("Siemens.Engineering.TextCategory")==null;
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
        const string se="Siemens.Engineering.", hw="Siemens.Engineering.HW.", features="Siemens.Engineering.HW.Features.", util="Siemens.Engineering.HW.Utilities.", conn="Siemens.Engineering.Connection.";
        var engineeringObject=T(se+"IEngineeringObject"); var secure=typeof(SecureString); var file=typeof(FileInfo);

        // ---- portal diagnostics ----
        Property(se+"TiaPortalProcess","Id","Int32"); Property(se+"TiaPortalProcess","Mode","TiaPortalMode"); Property(se+"TiaPortalProcess","Path","FileInfo"); Property(se+"TiaPortalProcess","ProjectPath","FileInfo");
        Property(se+"TiaPortalProcess","AcquisitionTime","DateTime"); Property(se+"TiaPortalProcess","AttachedSessions"); Property(se+"TiaPortalProcess","InstalledSoftware");
        foreach(var p in new[]{"Id","Version","IsActive","AttachTime","UtilizationTime","AccessLevel","TrustAuthority","ProcessPath","ProcessId"}) Property(se+"TiaPortalSession",p);
        Enum(se+"TiaPortalAccessLevel","None","NoLicense","Trusted","Modify","Published","Pilot","Elevated"); Enum(se+"TiaPortalTrustAuthority","None","Signed","Certified","CertifiedWithExpiration","FeatureTokens");
        Property(se+"TiaPortalProduct","Name","String"); Property(se+"TiaPortalProduct","Version","String"); Property(se+"TiaPortalProduct","Options");
        Method(se+"TiaPortal","GetCurrentProcess",Type.EmptyTypes,"TiaPortalProcess");
        Property(se+"ProjectBase","HwUtilities","HardwareUtilityComposition");
        if(v20) Console.WriteLine("CAPABILITY ProjectBase.TextCategories / TextCategory absent on V20 (V21 only)");
        else { Property(se+"ProjectBase","TextCategories","TextCategoryComposition"); Property(se+"TextCategory","Identifier","String"); Property(se+"TextCategory","Name","String"); Method(se+"TextCategoryComposition","Find",new[]{typeof(string)},"TextCategory"); }
        Property(se+"ExceptionMessageData","Text","String"); Property(se+"ExceptionMessageData","DetailText","String"); Property(se+"EngineeringException","MessageData","ExceptionMessageData"); Property(se+"EngineeringException","DetailMessageData");

        // ---- transactions / exclusive access ----
        Method(se+"ExclusiveAccess","Transaction",new[]{T(se+"ITransactionSupport"),typeof(string)},"Transaction"); Property(se+"ExclusiveAccess","IsCancellationRequested","Boolean");
        Property(se+"Transaction","CanCommit","Boolean",false); Property(se+"Transaction","CommitRequested","Boolean",false); Method(se+"Transaction","CommitOnDispose",Type.EmptyTypes,"Void");
        check(T(se+"ITransactionSupport").IsAssignableFrom(T(se+"ProjectBase")),"ProjectBase implements ITransactionSupport");

        // ---- protected project open / UMAC credentials ----
        Method(se+"ProjectComposition","OpenWithUpgrade",new[]{file,T(se+"UmacDelegate")},"Project"); Method(se+"ProjectComposition","Open",new[]{file,T(se+"UmacDelegate")},"Project");
        Property(se+"UmacCredentials","Name","String",true); Property(se+"UmacCredentials","Type","UmacUserType",true); Method(se+"UmacCredentials","SetPassword",new[]{secure},"Void");
        Enum(se+"UmacUserType","Project","Global");

        // ---- object identifier / show in editor / system object ----
        Service(se+"ObjectIdentifierProvider"); Method(se+"ObjectIdentifierProvider","GetIdentifier",new[]{engineeringObject},"String"); Method(se+"ObjectIdentifierProvider","Find",new[]{typeof(string)},"IEngineeringObject");
        Method(se+"IShowable","ShowInEditor",Type.EmptyTypes,"Void");
        if(v20) Console.WriteLine("CAPABILITY Device does not implement IShowable on V20 (ShowObjectInEditor kind=device answers NotSupported there)"); else check(T(se+"IShowable").IsAssignableFrom(T(hw+"Device")),"Device implements IShowable");
        Property(se+"ISystemObject","IsSystemObject","Boolean");

        // ---- attribute batch writes ----
        Property(se+"AttributeConfiguration","Name","String"); Property(se+"AttributeConfiguration","Message","String"); Property(se+"AttributeConfiguration","CurrentSelection","AttributeChoiceSelection",true);
        Enum(se+"AttributeChoiceSelection","Abort","Ignore");
        foreach(var t in new[]{"AttributeNameUnsupported","AttributeReadOnly","AttributeTypeUnsupported","AttributeValueUnsupported"}) check(T(se+t).IsSubclassOf(T(se+"AttributeConfiguration")),t+" derives from AttributeConfiguration");
        check(T(se+"AttributeDelegate").GetMethod("Invoke")!.GetParameters().Single().ParameterType==T(se+"AttributeConfiguration"),"AttributeDelegate(AttributeConfiguration)");

        // ---- transfer routes, R/H providers, prompts and results ----
        Property(conn+"ConfigurationMode","Name","String"); Property(conn+"ConfigurationMode","PcInterfaces","ConfigurationPcInterfaceComposition");
        Property(conn+"ConfigurationPcInterface","Name","String"); Property(conn+"ConfigurationPcInterface","Number","Int32"); Property(conn+"ConfigurationPcInterface","Addresses","ConfigurationAddressComposition");
        Property(conn+"ConfigurationPcInterface","Subnets","ConfigurationSubnetComposition"); Property(conn+"ConfigurationPcInterface","TargetInterfaces","ConfigurationTargetInterfaceComposition");
        Property(conn+"ConfigurationSubnet","Name","String"); Property(conn+"ConfigurationSubnet","Addresses","ConfigurationAddressComposition"); Property(conn+"ConfigurationSubnet","Gateways","ConfigurationGatewayComposition");
        Property(conn+"ConfigurationGateway","Name","String"); Property(conn+"ConfigurationGateway","Addresses","ConfigurationAddressComposition");
        Property(conn+"ConfigurationAddress","Address","String"); Property(conn+"ConfigurationTargetInterface","Addresses","ConfigurationAddressComposition");
        Service(se+"Download.RHDownloadProvider"); Property(se+"Download.RHDownloadProvider","Configuration","ConnectionConfiguration");
        Method(se+"Download.RHDownloadProvider","DownloadToPrimary",new[]{T(conn+"IConfiguration"),T(se+"Download.DownloadConfigurationDelegate"),T(se+"Download.DownloadConfigurationDelegate"),T(se+"Download.DownloadOptions")},"DownloadResult");
        Method(se+"Download.RHDownloadProvider","DownloadToBackup",new[]{T(conn+"IConfiguration"),T(se+"Download.DownloadConfigurationDelegate"),T(se+"Download.DownloadConfigurationDelegate"),T(se+"Download.DownloadOptions")},"DownloadResult");
        Service(se+"Online.RHOnlineProvider"); Property(se+"Online.RHOnlineProvider","PrimaryState","OnlineState"); Property(se+"Online.RHOnlineProvider","BackupState","OnlineState");
        Method(se+"Online.RHOnlineProvider","GoOnlineToPrimary",Type.EmptyTypes,"OnlineState"); Method(se+"Online.RHOnlineProvider","GoOnlineToBackup",Type.EmptyTypes,"OnlineState");
        if(v20) Console.WriteLine("CAPABILITY RHOnlineProvider.GoOnlineToPrimary(ConfigurationAddress) absent on V20 (V21 only)");
        else { Method(se+"Online.RHOnlineProvider","GoOnlineToPrimary",new[]{T(conn+"ConfigurationAddress")},"OnlineState"); Method(se+"Online.RHOnlineProvider","GoOnlineToBackup",new[]{T(conn+"ConfigurationAddress")},"OnlineState"); }
        Property(se+"Download.Configurations.DownloadConfiguration","Message","String"); Property(se+"Download.Configurations.DownloadCheckConfiguration","Checked","Boolean",true);
        Property(se+"Download.Configurations.DownloadPasswordConfiguration","IsSecureCommunication","Boolean"); Method(se+"Download.Configurations.DownloadPasswordConfiguration","SetPassword",new[]{secure},"Void");
        Property(se+"Download.Configurations.SelectiveDeleteDownload","CurrentSelection","SelectiveDeleteDataSelections",true); Enum(se+"Download.Configurations.SelectiveDeleteDataSelections","AcceptAll","DeleteSelected","DeleteAll");
        Property(se+"Upload.Configurations.UploadConfiguration","Message","String"); Property(se+"Upload.Configurations.UploadPasswordConfiguration","IsSecureCommunication","Boolean"); Method(se+"Upload.Configurations.UploadPasswordConfiguration","SetPassword",new[]{secure},"Void");
        foreach(var p in new[]{"Message","State","ErrorCount","WarningCount","DateTime","Messages"}) { Property(se+"Download.DownloadResultMessage",p); Property(se+"Upload.UploadResultMessage",p); }
        foreach(var p in new[]{"Path","Description","State","ErrorCount","WarningCount","DateTime","Messages"}) Property(se+"Compiler.CompilerResultMessage",p);
        check(!T(se+"Compiler.CompilerResultMessage").Assembly.GetType(se+"Compiler.CompileProvider")!.IsPublic,"CompileProvider is internal in the PublicAPI (ICompilable is the public entry)");
        Property(se+"Online.Configurations.OnlineAuthenticationConfiguration","OnlineCredentials","OnlineCredentials"); Property(se+"Online.Configurations.OnlineAuthenticationConfiguration","IsSecureCommunication","Boolean");
        Method(se+"Online.Configurations.OnlineAuthenticationConfiguration","GetSupportedAuthenticationTypes",Type.EmptyTypes);
        Property(se+"Online.Configurations.OnlineCredentials","Name","String",true); Property(se+"Online.Configurations.OnlineCredentials","Type","UserType",true); Method(se+"Online.Configurations.OnlineCredentials","SetPassword",new[]{secure},"Void");
        Property(se+"Online.Configurations.AuthenticationType","CurrentUserType","UserType"); Enum(se+"Online.Configurations.UserType","None","AnonymousUser","GlobalUser","ProjectUser","SingleSignOnUser","PasswordOnly");

        // ---- hardware utilities ----
        Method(se+"HW.Utilities.HardwareUtilityComposition","Find",new[]{typeof(string)},"HardwareUtility"); Property(util+"HardwareUtility","Identifier","String");
        Method(util+"ModuleInformationProvider","FindModuleTypes",new[]{typeof(string)}); Method(util+"ModuleInformationProvider","FindContainerTypes",new[]{typeof(string)}); Method(util+"ModuleInformationProvider","GetTypeIdentifierNormalized",new[]{typeof(string)},"String");
        Method(util+"OpcUaExportProvider","Export",new[]{T(hw+"DeviceItem"),file},"Void"); Method(util+"CardReaderPscProvider","Export",new[]{T(hw+"Device"),file},"Void"); Method(util+"CardReaderPscProvider","Export",new[]{T(hw+"Device"),file,secure},"Void");
        foreach(var t in new[]{"ModuleInformationProvider","OpcUaExportProvider","CardReaderPscProvider"}) check(T(util+t).IsSubclassOf(T(util+"HardwareUtility")),t+" derives from HardwareUtility");

        // ---- device service objects ----
        Service(features+"CertificateManagementConfiguration"); Property(features+"CertificateManagementConfiguration","Usage","CertificateConfigurationUsage",true);
        Property(features+"CertificateManagementConfiguration","CertificateExpirationEventActivated","Boolean",true); Property(features+"CertificateManagementConfiguration","RemainingCertificateLifetime","UInt16",true);
        Property(features+"CertificateManagementConfiguration","CertificateSupportedServices","CertificateSupportedServiceComposition"); Enum(hw+"CertificateConfigurationUsage","TIAPortal","Runtime");
        Property(hw+"CertificateSupportedService","ServiceType","CertificateSupportedServiceName"); Property(hw+"CertificateSupportedService","ServiceGroupName","String",true); Method(hw+"CertificateSupportedService","Delete",Type.EmptyTypes,"Void");
        if(v20) Enum(hw+"CertificateSupportedServiceName","OpcUaServer","Webserver"); else Enum(hw+"CertificateSupportedServiceName","None","OpcUaClient","OpcUaServer","Webserver");
        if(v20) { Property(hw+"CertificateSupportedService","Id","UInt16",false); Method(hw+"CertificateSupportedServiceComposition","Create",new[]{typeof(ushort),typeof(string),T(hw+"CertificateSupportedServiceName")},"CertificateSupportedService"); Console.WriteLine("CAPABILITY CertificateSupportedService.ApplicationUri / Guid absent on V20"); }
        else { Property(hw+"CertificateSupportedService","Id","UInt32",true); Property(hw+"CertificateSupportedService","ApplicationUri","String"); Property(hw+"CertificateSupportedService","Guid","Guid"); Method(hw+"CertificateSupportedServiceComposition","Create",new[]{typeof(uint),typeof(string),T(hw+"CertificateSupportedServiceName")},"CertificateSupportedService"); Method(hw+"CertificateSupportedServiceComposition","Find",new[]{typeof(Guid)},"CertificateSupportedService"); }
        Service(features+"TelecontrolManagement"); Method(features+"TelecontrolManagement","ExportDataPoints",new[]{file},"Void"); Method(features+"TelecontrolManagement","ImportDataPoints",new[]{file},"Void");
        if(v20) Console.WriteLine("CAPABILITY TelecontrolDataPoint* / DefaultWebPagesFeature / WebApplicationConfiguration absent on V20 (V21 only)");
        else
        {
            Property(features+"TelecontrolManagement","TelecontrolDataPoints","TelecontrolDataPointComposition");
            Property(hw+"TelecontrolDataPoint","Name","String",true); Property(hw+"TelecontrolDataPoint","DataPointType","Byte",true); Method(hw+"TelecontrolDataPoint","Delete",Type.EmptyTypes,"Void");
            Property(hw+"TelecontrolDnp3DataPoint","DataPointIndex","UInt32",true); Property(hw+"TelecontrolDnp3DataPoint","MasterFunction","Boolean",true); Property(hw+"TelecontrolIecDataPoint","DataPointIndex","UInt32",true); Property(hw+"TelecontrolIecDataPoint","MasterFunction","Boolean",true); Property(hw+"TelecontrolWdcDataPoint","DataPointIndex","UInt32",true);
            Service(features+"DefaultWebPagesFeature"); Property(features+"DefaultWebPagesFeature","WebApplicationConfigurations","WebApplicationConfigurationComposition");
            Property(hw+"WebApplicationConfiguration","Name","String"); Property(hw+"WebApplicationConfiguration","ApplicationType","WebApplicationType"); Property(hw+"WebApplicationConfiguration","IsDefault","Boolean",true);
            Method(hw+"WebApplicationConfigurationComposition","Find",new[]{typeof(string)},"WebApplicationConfiguration"); Enum(hw+"WebApplicationType","None","UserDefined","System","System_Inbuilt");
        }

        // ---- typed rows in existing tools ----
        foreach(var p in new[]{"ArticleNumber","CatalogPath","Description","TypeIdentifier","TypeIdentifierNormalized","TypeName","Version"}) Property(se+"HW.HardwareCatalog.CatalogEntry",p,"String");
        Property(se+"Compare.CompareResult","RootElement","CompareResultElement"); foreach(var p in new[]{"LeftName","RightName","ComparisonResult","DetailedInformation","Elements"}) Property(se+"Compare.CompareResultElement",p);
        foreach(var p in new[]{"Name","Path","TypeName","Address","Device","Children","References","UnderlyingObject"}) Property(se+"CrossReference.SourceObject",p);
        foreach(var p in new[]{"Name","Path","TypeName","Address","Device","Locations","UnderlyingObject"}) Property(se+"CrossReference.ReferenceObject",p);
        Property(se+"CrossReference.CrossReferenceResult","Sources","SourceObjectComposition");
        Method(se+"Multiuser.LockStateProvider","IsProjectLocked",Type.EmptyTypes,"Boolean"); Method(se+"Multiuser.LockStateProvider","GetLockOwner",Type.EmptyTypes,"String");
        Property(se+"Multiuser.Markings","AllMarkings","MarkingComposition"); Property(se+"Multiuser.Markings","ConflictedMarkings","MarkingComposition"); Property(se+"Multiuser.ProjectServerGroup","Name","String");
        Property(se+"VersionControl.VersionControlInterface","WorkspaceGroup","WorkspaceSystemGroup"); Property(se+"VersionControl.WorkspaceSystemGroup","Name","String"); Property(se+"VersionControl.WorkspaceUserGroup","Name","String",true);
        Property(se+"Settings.TiaPortalSettingsFolder","Name","String"); Property(se+"Settings.TiaPortalSettingsFolder","Folders","TiaPortalSettingsFolderComposition"); Property(se+"Settings.TiaPortalSettingsFolder","Settings","TiaPortalSettingComposition");
        Property(se+"Settings.TiaPortalSetting","Name","String"); Property(se+"Settings.TiaPortalSetting","Value","Object",true);
        check(!core.GetType(se+"Private.ProcessHelper")!.IsPublic,"Siemens.Engineering.Private.ProcessHelper is internal (excluded from the coverage audit)");
    }
}

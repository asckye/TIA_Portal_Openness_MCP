using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Every native type/member the HardwareServices family touches. Absent-on-V20 members print CAPABILITY and continue.
internal static class HardwareServicesShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        Type? Opt(string n)=>core.GetType(n) ?? step7.GetType(n);
        void Method(Type t,string name,Type[] sig)=>check(t.GetMethod(name,sig)!=null,t.FullName+"."+name+"("+string.Join(",",sig.Select(s=>s.Name))+")");
        void Property(Type t,string name)=>check(t.GetProperty(name)!=null,t.FullName+"."+name);
        void Capability(string what)=>Console.WriteLine("CAPABILITY "+what+" absent on this API version; the tool must answer NotSupported, never an empty success.");

        // Shared hardware entry points
        var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        var deviceItem=T(core,"Siemens.Engineering.HW.DeviceItem");
        var device=T(core,"Siemens.Engineering.HW.Device");
        check(serviceProvider.IsAssignableFrom(deviceItem) && serviceProvider.IsAssignableFrom(device),"Device/DeviceItem are IEngineeringServiceProvider (V20 HardwareObject has no GetService)");
        check(serviceProvider.GetMethod("GetService")!.IsGenericMethodDefinition,"IEngineeringServiceProvider.GetService<T>");
        check(serviceProvider.GetMethod("GetServiceInfos")!=null,"IEngineeringServiceProvider.GetServiceInfos");
        Property(T(core,"Siemens.Engineering.EngineeringServiceInfo"),"Type");
        var node=T(core,"Siemens.Engineering.HW.Node");
        Property(node,"Name"); Property(node,"ConnectedSubnet");
        Method(T(core,"Siemens.Engineering.HW.NodeComposition"),"Find",new[]{typeof(string)});
        Property(T(core,"Siemens.Engineering.HW.Features.NetworkInterface"),"Nodes");
        Property(T(core,"Siemens.Engineering.HW.Subnet"),"Name");

        // 1. CommunicationConnections (V21+)
        var composition=Opt("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition");
        if(composition==null) Capability("Siemens.Engineering.HW.CommunicationConnections");
        else {
            var create=composition.GetMethods().SingleOrDefault(m=>m.Name=="Create" && m.IsGenericMethodDefinition && m.GetParameters().Length==3);
            check(create!=null && create.GetParameters()[0].ParameterType==node && create.GetParameters()[1].ParameterType==deviceItem && create.GetParameters()[2].ParameterType==node,"ConnectionComposition.Create<T>(Node,DeviceItem,Node)");
            Property(composition,"Count");
            var connection=T(core,"Siemens.Engineering.HW.CommunicationConnections.Connection");
            Method(connection,"Delete",Type.EmptyTypes);
            foreach(var link in new[]{"LocalTarget","PartnerTarget","LocalInterface","PartnerInterface","ConnectionType","IsValid"}) Property(connection,link);
            foreach(var kind in new[]{"S7Connection","IsoConnection","IsoOnTcpConnection","TcpConnection","UdpConnection","FdlConnection","PtpConnection","HmiConnection"}) {
                var t=T(core,"Siemens.Engineering.HW.CommunicationConnections."+kind);
                check(connection.IsAssignableFrom(t) && t.GetProperty("LocalConnectionName")!=null,kind+" derives from Connection and exposes LocalConnectionName");
            }
        }

        // 2. Watch/force table web access (V20 and V21)
        var manager=T(step7,"Siemens.Engineering.HW.Features.WatchAndForceTableAccessManager");
        Property(manager,"WatchtableAccessRules"); Property(manager,"ForcetableAccessRules");
        var access=T(step7,"Siemens.Engineering.HW.WatchAndForceTableAccess");
        check(access.IsEnum && new[]{"None","Read","Write"}.All(n=>Enum.IsDefined(access,n)),"WatchAndForceTableAccess None/Read/Write");
        foreach(var (kind,table) in new[]{("Watch","Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable"),("Force","Siemens.Engineering.SW.WatchAndForceTables.PlcForceTable")}) {
            var tableType=T(step7,table);
            var rules=T(step7,"Siemens.Engineering.HW."+kind+"TableAccessRuleComposition");
            Method(rules,"Create",new[]{tableType,access}); Method(rules,"Find",new[]{tableType});
            var rule=T(step7,"Siemens.Engineering.HW."+kind+"TableAccessRule");
            Method(rule,"Delete",Type.EmptyTypes); Property(rule,"Access"); Property(rule,kind+"Table");
            Console.WriteLine("CAPABILITY "+kind+"TableAccessRule.Access writable="+(rule.GetProperty("Access")!.SetMethod?.IsPublic==true)+" (read-only means unassign+assign)");
        }
        var tableGroup=T(step7,"Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableGroup");
        foreach(var p in new[]{"WatchTables","ForceTables","Groups"}) Property(tableGroup,p);

        // 3. System diagnostics settings (V20 and V21)
        var sysdiag=T(core,"Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider");
        Method(sysdiag,"Export",new[]{typeof(FileInfo)}); Method(sysdiag,"Import",new[]{typeof(FileInfo)});
        Property(T(core,"Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsExportImportResult"),"State");
        check(T(core,"Siemens.Engineering.ProjectBase").GetMethod("GetService")!=null,"ProjectBase.GetService<T> for project-level providers");

        // 4. OPC UA access control (V21+)
        Property(T(step7,"Siemens.Engineering.SW.OpcUa.OpcUaProvider"),"CommunicationGroup");
        Property(T(step7,"Siemens.Engineering.SW.OpcUa.OpcUaCommunicationGroup"),"ServerInterfaceGroup");
        var sig=T(step7,"Siemens.Engineering.SW.OpcUa.ServerInterfaceGroup");
        if(sig.GetProperty("AccessControl")==null) Capability("Siemens.Engineering.SW.OpcUa.ServerInterfaceGroup.AccessControl");
        else {
            var control=T(step7,"Siemens.Engineering.SW.OpcUa.AccessControl.AccessControl");
            Property(control,"RoleMappings"); Property(control,"NamespaceAccessRestrictions");
            var mappings=T(step7,"Siemens.Engineering.SW.OpcUa.AccessControl.RoleMappingComposition");
            Method(mappings,"Create",new[]{typeof(string),typeof(string)}); Method(mappings,"AddStandardOpcUaRole",new[]{typeof(string)});
            var role=T(step7,"Siemens.Engineering.SW.OpcUa.AccessControl.RoleMapping");
            Method(role,"Delete",Type.EmptyTypes); Method(role,"SetProjectRole",new[]{typeof(string)});
            foreach(var p in new[]{"Name","ProjectRole","NamespacePermissions","DefinedInNamespace","RoleId"}) Property(role,p);
            var permission=T(step7,"Siemens.Engineering.SW.OpcUa.AccessControl.NamespacePermission");
            Method(permission,"SetPermission",new[]{typeof(string),typeof(bool)}); Property(permission,"NamespaceUri");
            foreach(var p in new[]{"Browse","Read","Write","Call","ReceiveEvents","ReadRolePermissions"}) check(permission.GetProperty(p)?.PropertyType==typeof(bool),"NamespacePermission."+p+" is bool (readback of SetPermission)");
            Property(T(step7,"Siemens.Engineering.SW.OpcUa.AccessControl.NamespaceAccessRestriction"),"NamespaceUri");
        }

        // 5. CAx import (V20 and V21)
        var cax=T(step7,"Siemens.Engineering.Cax.CaxProvider");
        var options=T(step7,"Siemens.Engineering.Cax.CaxImportOptions");
        var import=cax.GetMethod("Import",new[]{typeof(FileInfo),typeof(FileInfo),options});
        check(import!=null && import.ReturnType==typeof(bool),"CaxProvider.Import(FileInfo,FileInfo,CaxImportOptions) returns bool");
        check(new[]{"MoveToParkingLot","OverwriteTiaDevice","RetainTiaDevice"}.All(n=>Enum.IsDefined(options,n)),"CaxImportOptions values");

        // 6. HW.Features catalog: every V21 type must resolve, or be reported as a V20 gap.
        var service=T(core,"Siemens.Engineering.IEngineeringService");
        foreach(var feature in new[]{"AddressController","AddressControllerAssociation","CertificateManagementConfiguration","CommunicationManagement","DefaultWebPagesFeature","DeviceFeature","DeviceItemFeature","DisplayProtection","FrontPanelDisplay","GsdDevice","GsdDeviceItem","GsdExportProvider","HardwareFeature","HwIdentifierController","HwIdentifierControllerAssociation","IGsdObject","ImConnection","ModuleDescriptionUpdater","MrpDomainOwner","MrpInstancesOwner","NetworkInterface","NetworkInterfaceAssociation","NetworkPort","NetworkPortAssociation","OpcUaUserManagement","PcInterfaceAssignment","PlcAccessControlConfigurationProvider","PlcAccessLevelProvider","PlcAccountLockingAtRuntimeFeature","PlcMasterSecretConfigurator","ResourceAssignment","SimpleWebserverUserManagement","SoftwareContainer","SubnetFeature","SubnetOwner","SyncDomainOwner","SysLogConfigurationManager","SysLogServerConfiguration","SysLogServerConfigurationComposition","SystemWebPagesFeature","TelecontrolManagement","WatchAndForceTableAccessManager","WebDBGenerateOptions","WebserverUserDefinedPages","WebserverUserManagement"}) {
            var t=Opt("Siemens.Engineering.HW.Features."+feature);
            if(t==null) { if(v20) Capability("HW.Features."+feature); else check(false,"HW.Features."+feature+" resolves on V21"); continue; }
            Console.WriteLine("CAPABILITY HW.Features."+feature+" concreteService="+(!t.IsAbstract && !t.IsInterface && service.IsAssignableFrom(t)));
        }

        // Tool surface
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManageCommunicationConnection","ManageWatchForceTableWebAccess","ExchangeSystemDiagnosticsSettings","ManageOpcUaAccessControl","ImportDeviceAml"}) {
            var method=tools.GetMethod(name)!;
            var preview=method.GetParameters().Last();
            check(preview.Name=="dryRun" && Equals(preview.DefaultValue,true),name+" ends with dryRun=true");
        }
        foreach(var (name,confirm) in new[]{("ManageCommunicationConnection","confirmDelete"),("ManageWatchForceTableWebAccess","confirmChange"),("ExchangeSystemDiagnosticsSettings","confirmImport"),("ManageOpcUaAccessControl","confirmChange"),("ImportDeviceAml","confirmImport")})
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p=>p.Name==confirm).DefaultValue,false),name+" requires explicit "+confirm);
        foreach(var name in new[]{"ReadCommunicationConnections","ReadOpcUaAccessControl","ReadHardwareFeatures"}) {
            var method=tools.GetMethod(name)!;
            check(method.GetParameters().Any(p=>p.Name=="offset") && method.GetParameters().Any(p=>p.Name=="limit") && !method.GetParameters().Any(p=>p.Name=="dryRun"),name+" is a paginated read without dryRun");
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the hardware-network family (Portal.HardwareNetwork.cs): IO systems, sync/MRP domains,
// transfer areas, channels, addressing, device user groups, device users and port interconnections, verified against
// the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base) PublicAPI.
internal static class HardwareNetworkShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
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
        const string hw="Siemens.Engineering.HW.", features="Siemens.Engineering.HW.Features.";
        var engineeringObject=T("Siemens.Engineering.IEngineeringObject"); var serviceProvider=T("Siemens.Engineering.IEngineeringServiceProvider");
        var secure=typeof(System.Security.SecureString);

        // Project scope, subnets, interfaces
        Property("Siemens.Engineering.ProjectBase","Subnets","SubnetComposition");
        Property("Siemens.Engineering.ProjectBase","DeviceGroups","DeviceUserGroupComposition");
        Property("Siemens.Engineering.ProjectBase","UngroupedDevicesGroup","DeviceSystemGroup");
        Method(hw+"SubnetComposition","Find",new[]{typeof(string)},"Subnet");
        Property(hw+"Subnet","IoSystems","IoSystemAssociation"); Property(hw+"Subnet","Nodes","NodeAssociation"); Property(hw+"Subnet","Name","String");
        check(T(hw+"Subnet").GetMethods().Any(m=>m.Name=="GetService" && m.IsGenericMethodDefinition),hw+"Subnet.GetService<T>()");
        Property(features+"NetworkInterface","IoControllers","IoControllerComposition"); Property(features+"NetworkInterface","IoConnectors","IoConnectorComposition");
        Property(features+"NetworkInterface","TransferAreas","TransferAreaComposition"); Property(features+"NetworkInterface","MulticastableTransferAreas","MulticastableTransferAreaComposition");
        Property(features+"NetworkInterface","Nodes","NodeComposition"); Property(features+"NetworkInterface","Ports"); Property(features+"NetworkInterface","OwnedBy","DeviceItem");
        Property(features+"NetworkInterface","InterfaceOperatingMode","InterfaceOperatingModes"); Property(features+"NetworkInterface","InterfaceType","NetType");
        Enum(hw+"InterfaceOperatingModes","None","IoController","IoDevice");
        Property(hw+"Node","ConnectedSubnet","Subnet");
        // IO systems
        Property(hw+"IoSystem","Name","String",true); Property(hw+"IoSystem","Number","Int32",true); Property(hw+"IoSystem","Subnet","Subnet",false);
        Property(hw+"IoSystem","ConnectedIoDevices","IoConnectorAssociation",false); Property(hw+"IoSystem","HwIdentifiers","HwIdentifierComposition",false);
        Method(hw+"IoSystem","Delete",Type.EmptyTypes,"Void");
        check(engineeringObject.IsAssignableFrom(T(hw+"IoSystem")),"IoSystem is an IEngineeringObject (GetAttribute/SetAttribute)");
        Method(hw+"IoController","CreateIoSystem",new[]{typeof(string)},"IoSystem");
        Property(hw+"IoController","IoSystem","IoSystem",false); Property(hw+"IoController","Addresses","AddressComposition",false);
        Method(hw+"IoConnector","ConnectToIoSystem",new[]{T(hw+"IoSystem")},"Void"); Method(hw+"IoConnector","DisconnectFromIoSystem",Type.EmptyTypes,"Void");
        Method(hw+"IoConnector","GetIoController",Type.EmptyTypes,"IoController"); Property(hw+"IoConnector","ConnectedToIoSystem","IoSystem",false);
        check(engineeringObject.IsAssignableFrom(T(hw+"IoController")) && engineeringObject.IsAssignableFrom(T(hw+"IoConnector")),"IoController/IoConnector expose dynamic attributes");
        // Sync / MRP domains
        Property(features+"SyncDomainOwner","SyncDomains","SyncDomainComposition"); Property(features+"MrpDomainOwner","MrpDomains","MrpDomainComposition");
        Property(features+"MrpInstancesOwner","MrpInstances","MrpInstanceComposition"); Property(features+"MrpInstancesOwner","OwnedBy","DeviceItem");
        Method(hw+"SyncDomainComposition","Create",new[]{typeof(string)},"SyncDomain"); Method(hw+"SyncDomainComposition","Find",new[]{typeof(string)},"SyncDomain");
        Method(hw+"MrpDomainComposition","Create",new[]{typeof(string)},"MrpDomain"); Method(hw+"MrpDomainComposition","Find",new[]{typeof(string)},"MrpDomain");
        Property(hw+"SyncDomain","Name","String",true); Property(hw+"SyncDomain","ConvertedName","String",false); Property(hw+"SyncDomain","IsDefault","Boolean",true);
        Property(hw+"SyncDomain","DomainParticipants","ISyncDomainParticipantAssociation",false); Method(hw+"SyncDomain","Delete",Type.EmptyTypes,"Void");
        Method(hw+"ISyncDomainParticipantAssociation","Add",new[]{T(hw+"ISyncDomainParticipant")},"Void");
        check(T(hw+"ISyncDomainParticipant").IsAssignableFrom(T(features+"NetworkInterface")),"NetworkInterface is an ISyncDomainParticipant");
        Property(hw+"MrpDomain","Name","String",true); Property(hw+"MrpDomain","DomainParticipants","NetworkInterfaceAssociation",false); Method(hw+"MrpDomain","Delete",Type.EmptyTypes,"Void");
        Method(features+"NetworkInterfaceAssociation","Add",new[]{T(features+"NetworkInterface")},"Void");
        Property(hw+"MrpInstance","Name","String",false); Property(hw+"MrpInstance","ConnectedMrpDomain","MrpDomain",false); Property(hw+"MrpInstance","Interface","NetworkInterface",false);
        Property(hw+"MrpInstance","RingPort1","NetworkPort",false); Property(hw+"MrpInstance","RingPort2","NetworkPort",false);
        // Transfer areas
        Method(hw+"TransferAreaComposition","Create",new[]{typeof(string),T(hw+"TransferAreaType")},"TransferArea");
        Method(hw+"TransferAreaComposition","Create",new[]{typeof(string),T(hw+"TransferAreaType"),typeof(int)},"TransferArea");
        Method(hw+"TransferAreaComposition","Find",new[]{typeof(int)},"TransferArea"); Method(hw+"TransferAreaComposition","Find",new[]{typeof(int),typeof(int)},"TransferArea");
        Enum(hw+"TransferAreaType","None","MS","CD","F_PS","TM","IN","OUT","MSI","MSO","DDX","ISOCHRON_IN","ISOCHRON_OUT");
        check(v20 ? !System.Enum.IsDefined(T(hw+"TransferAreaType"),"F_CD") : System.Enum.IsDefined(T(hw+"TransferAreaType"),"F_CD"),"TransferAreaType.F_CD is V21-only (29 members on V20, 30 on V21)");
        Enum(hw+"TransferAreaDirection","None","LocalToPartner","PartnerToLocal","Bidirectional");
        Property(hw+"TransferArea","Name","String",true); Property(hw+"TransferArea","Type","TransferAreaType"); Property(hw+"TransferArea","Direction","TransferAreaDirection",true);
        Property(hw+"TransferArea","PositionNumber","Int32",false); Property(hw+"TransferArea","ExtendedPositionNumber","Int32",false);
        Property(hw+"TransferArea","LocalToPartnerLength","Int32",true); Property(hw+"TransferArea","PartnerToLocalLength","Int32",true);
        Property(hw+"TransferArea","LocalAddresses","AddressComposition",false); Property(hw+"TransferArea","PartnerAddresses","AddressComposition",false);
        Property(hw+"TransferArea","TransferAreaMappingRules","TransferAreaMappingRuleComposition",false); Method(hw+"TransferArea","Delete",Type.EmptyTypes,"Void");
        Method(hw+"TransferAreaMappingRuleComposition","Create",Type.EmptyTypes,"TransferAreaMappingRule");
        foreach(var name in new[]{"Begin","End","Offset"}) Property(hw+"TransferAreaMappingRule",name,"Int32",true);
        Property(hw+"TransferAreaMappingRule","IoType","AddressIoType",true); Property(hw+"TransferAreaMappingRule","Target","DeviceItem",true);
        Property(hw+"TransferAreaMappingRule","PositionNumber","Int32",false); Method(hw+"TransferAreaMappingRule","Delete",Type.EmptyTypes,"Void");
        var ni=T(features+"NetworkInterface"); var tat=T(hw+"TransferAreaType"); var mta=T(hw+"MulticastableTransferArea");
        Method(hw+"MulticastableTransferAreaComposition","Create",new[]{ni,tat},"MulticastableTransferArea");
        Method(hw+"MulticastableTransferAreaComposition","Create",new[]{ni,tat,typeof(string)},"MulticastableTransferArea");
        Method(hw+"MulticastableTransferAreaComposition","Create",new[]{ni,tat,typeof(string),typeof(int)},"MulticastableTransferArea");
        Method(hw+"MulticastableTransferAreaComposition","Create",new[]{mta,tat},"MulticastableTransferArea");
        Property(hw+"MulticastableTransferArea","Name","String",true); Property(hw+"MulticastableTransferArea","Comment","String",true); Property(hw+"MulticastableTransferArea","DataLength","Int32",true);
        Property(hw+"MulticastableTransferArea","Direction","TransferAreaDirection",false); Property(hw+"MulticastableTransferArea","Type","TransferAreaType",false);
        Property(hw+"MulticastableTransferArea","Addresses","AddressComposition",false); Property(hw+"MulticastableTransferArea","PartnerTransferAreas","MulticastableTransferAreaAssociation",false);
        Method(hw+"MulticastableTransferArea","Delete",Type.EmptyTypes,"Void");
        // Channels
        Property(hw+"DeviceItem","Channels","ChannelComposition");
        Method(hw+"ChannelComposition","Find",new[]{T(hw+"ChannelType"),T(hw+"ChannelIoType"),typeof(int)},"Channel");
        Property(hw+"Channel","Number","Int32",false); Property(hw+"Channel","Type","ChannelType",false); Property(hw+"Channel","IoType","ChannelIoType",false);
        Enum(hw+"ChannelType","None","Analog","Digital","Technology"); Enum(hw+"ChannelIoType","None","Input","Output","Complex");
        check(engineeringObject.IsAssignableFrom(T(hw+"Channel")),"Channel exposes GetAttributeInfos/GetAttribute/SetAttribute");
        check(v20 ? !T(hw+"Channel").GetMethods().Any(m=>m.Name=="GetService") : T(hw+"Channel").GetMethods().Any(m=>m.Name=="GetService"),"Channel.GetService<T> is V21-only (absent on V20)");
        // Addressing
        Property(hw+"DeviceItem","Addresses","AddressComposition"); Property(hw+"HardwareObject","HwIdentifiers","HwIdentifierComposition");
        Property(hw+"Address","StartAddress","Int32",true); Property(hw+"Address","Length","Int32",true); Property(hw+"Address","IoType","AddressIoType",false);
        Property(hw+"Address","AddressControllers","AddressControllerAssociation",false);
        Enum(hw+"AddressIoType","None","Input","Output","Substitute","Diagnosis");
        Property(hw+"HwIdentifier","Identifier","Int64",false); Property(hw+"HwIdentifier","HwIdentifierControllers","HwIdentifierControllerAssociation",false);
        Property(features+"AddressController","RegisteredAddresses","AddressAssociation"); Property(features+"HwIdentifierController","RegisteredHwIdentifiers","HwIdentifierAssociation");
        var assign=T(hw+"Address").GetMethod("AssignProcessImageToOrganizationBlock");
        check(v20 ? assign!=null && assign.GetParameters().Length==1 : assign==null,"Address.AssignProcessImageToOrganizationBlock(OB) is V20-only (removed on V21)");
        // Device user groups
        Method(hw+"DeviceUserGroupComposition","Create",new[]{typeof(string)},"DeviceUserGroup"); Method(hw+"DeviceUserGroupComposition","Find",new[]{typeof(string)},"DeviceUserGroup");
        Property(hw+"DeviceUserGroup","Groups","DeviceUserGroupComposition",false); Property(hw+"DeviceUserGroup","Devices","DeviceComposition",false); Property(hw+"DeviceUserGroup","Name","String",true);
        Method(hw+"DeviceUserGroup","Delete",Type.EmptyTypes,"Void");
        Property(hw+"DeviceGroup","Devices","DeviceComposition",false); Property(hw+"DeviceGroup","Name","String",false);
        check(T(hw+"DeviceGroup").IsAssignableFrom(T(hw+"DeviceSystemGroup")) && T(hw+"DeviceGroup").IsAssignableFrom(T(hw+"DeviceUserGroup")),"DeviceSystemGroup/DeviceUserGroup derive from DeviceGroup");
        // Device users
        Property(features+"WebserverUserManagement","WebserverUsers","WebserverUserComposition"); Property(features+"SimpleWebserverUserManagement","WebserverUsers","SimpleWebserverUserComposition");
        Property(features+"OpcUaUserManagement","OpcUaUsers","OpcUaUserComposition");
        Method(hw+"WebserverUserComposition","Create",new[]{typeof(string),T(hw+"WebserverUserPermissions"),secure},"WebserverUser"); Method(hw+"WebserverUserComposition","Find",new[]{typeof(string)},"WebserverUser");
        Property(hw+"WebserverUser","UserName","String",false); Property(hw+"WebserverUser","Permissions","WebserverUserPermissions",true);
        Method(hw+"WebserverUser","Delete",Type.EmptyTypes,"Void"); Method(hw+"WebserverUser","SetPassword",new[]{secure},"Void");
        Enum(hw+"WebserverUserPermissions","None","DoDiagnosis","ReadTag","ModifyTag","ReadTagStatus","ModifyTagStatus","AcknowledgeMessages","OpenUserDefinedWebPages","WriteUserDefinedWebPages",
            "ReadFiles","ModifyFiles","ChangeOperatingMode","FlashLed","WriteFirmware","ChangeSystemParameter","ChangeApplicationParameter","Backup","Restore","FAdmin","ManageUserDefinedWebPages");
        var bits=System.Enum.GetValues(T(hw+"WebserverUserPermissions")).Cast<object>().Select(Convert.ToInt64).Where(v=>v!=0).ToArray();
        check(bits.Length==19 && bits.All(v=>(v&(v-1))==0) && bits.Distinct().Count()==19,"WebserverUserPermissions values are distinct single bits (combinable; enum carries no [Flags], decoded by hand)");
        Method(hw+"SimpleWebserverUserComposition","Find",new[]{typeof(string)},"SimpleWebserverUser");
        check(T(hw+"SimpleWebserverUserComposition").GetMethod("Create")==null && T(hw+"SimpleWebserverUser").GetMethod("Delete")==null,"SimpleWebserverUser has no Create/Delete (fixed SIWAREX slots)");
        Property(hw+"SimpleWebserverUser","UserName","String",true); Property(hw+"SimpleWebserverUser","Active","Boolean",true); Property(hw+"SimpleWebserverUser","Permissions","SimpleWebserverUserPermissions",true);
        Method(hw+"SimpleWebserverUser","SetPassword",new[]{secure},"Void"); Enum(hw+"SimpleWebserverUserPermissions","None","ReadOnly","ReadWrite");
        Method(hw+"OpcUaUserComposition","Create",new[]{typeof(string),secure},"OpcUaUser"); Method(hw+"OpcUaUserComposition","Find",new[]{typeof(string)},"OpcUaUser");
        Property(hw+"OpcUaUser","UserName","String",false); Method(hw+"OpcUaUser","Delete",Type.EmptyTypes,"Void"); Method(hw+"OpcUaUser","SetPassword",new[]{secure},"Void");
        // Port interconnections
        Property(features+"NetworkPort","ConnectedPorts","NetworkPortAssociation",false); Property(features+"NetworkPort","Interface","NetworkInterface",false); Property(features+"NetworkPort","OwnedBy","DeviceItem",false);
        Method(features+"NetworkPort","ConnectToPort",new[]{T(features+"NetworkPort")},"Void"); Method(features+"NetworkPort","DisconnectFromPort",new[]{T(features+"NetworkPort")},"Void");
        check(serviceProvider.IsAssignableFrom(T(hw+"DeviceItem")),"DeviceItem is a service provider (NetworkInterface/NetworkPort/MrpInstancesOwner/AddressController/user managements)");
        // Engine surface
        var portal=server.GetType("TiaMcpServer.Siemens.Portal")!;
        foreach(var tool in new[]{"ReadIoSystems","ManageIoSystem","ReadNetworkDomains","ManageNetworkDomain","ReadTransferAreas","ManageTransferArea","ReadDeviceItemChannels","UpdateDeviceItemChannel","ReadDeviceAddressing","UpdateDeviceAddress","ManageDeviceUserGroup","ManageDeviceUsers","ManagePortInterconnection"})
            check(portal.GetMethod(tool)!=null,"Portal."+tool+" present");
    }
}

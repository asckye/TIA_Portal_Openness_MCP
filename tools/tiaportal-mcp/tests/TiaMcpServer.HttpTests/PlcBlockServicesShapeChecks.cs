using System;
using System.Linq;
using System.Reflection;

// Native members used by the PlcBlockServices family (Portal.PlcBlockServices.cs), verified against the installed API.
internal static class PlcBlockServicesShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(a,t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        void Property(Assembly a,string t,string name,string? type=null)
        {
            var p=T(a,t).GetProperty(name);
            check(p!=null && (type==null || p.PropertyType.Name==type),t+"."+name+(type==null?"":" : "+type));
        }
        var secure=typeof(System.Security.SecureString);
        // ManagePlcBlockProtection
        Method(step7,"Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider","Protect",new[]{secure},"Void");
        Method(step7,"Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider","Unprotect",new[]{secure},"Void");
        Method(core,"Siemens.Engineering.AdvancedProtection.ProtectionProviderBase","GetInvalidPasswordCharacters",Type.EmptyTypes);
        check(T(core,"Siemens.Engineering.AdvancedProtection.ProtectionProviderBase").IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider")),"PlcBlockProtectionProvider derives from ProtectionProviderBase (GetInvalidPasswordCharacters)");
        Property(step7,"Siemens.Engineering.SW.Blocks.PlcBlock","IsKnowHowProtected","Boolean");
        // ManagePlcDataBlockSnapshot
        Property(step7,"Siemens.Engineering.SW.Blocks.DataBlock","Interface","PlcBlockInterface");
        // Snapshot service resolution order in Portal.PlcBlockServices.cs: DataBlock.Interface, then DataBlock, then ValueService.
        var provider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        check(provider.IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Blocks.PlcBlock")),"PlcBlock is a service provider (InterfaceSnapshot fallback owner)");
        if(!provider.IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Blocks.Interface.PlcBlockInterface")) && v20) Console.WriteLine("CAPABILITY V20 PlcBlockInterface is not a service provider; InterfaceSnapshot must resolve from the DataBlock, ValueService is absent.");
        else check(provider.IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Blocks.Interface.PlcBlockInterface")),"PlcBlockInterface is a service provider");
        Method(step7,"Siemens.Engineering.SW.Blocks.InterfaceSnapshot","Export",new[]{typeof(System.IO.FileInfo),T(core,"Siemens.Engineering.ExportOptions")},"Void");
        check(T(core,"Siemens.Engineering.IEngineeringService").IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Blocks.InterfaceSnapshot")),"InterfaceSnapshot is an engineering service");
        var valueService=step7.GetType("Siemens.Engineering.SW.Blocks.Interface.ValueService");
        if(valueService==null && v20) Console.WriteLine("CAPABILITY V20 has no SW.Blocks.Interface.ValueService; createSnapshot/load actions must return NotSupported, not an empty success.");
        else {
            check(valueService!=null,"ValueService type present");
            foreach(var name in new[]{"CreateSnapshot","LoadSnapshotAsActualValues","LoadStartValuesAsActualValues"})
                check(valueService!.GetMethod(name,Type.EmptyTypes)?.ReturnType==typeof(void),"ValueService."+name+"() -> Void");
            check(T(core,"Siemens.Engineering.IEngineeringServiceProvider").IsAssignableFrom(valueService!),"ValueService is a service provider");
        }
        // UpdatePlcProgram
        Method(step7,"Siemens.Engineering.SW.PlcSoftware","UpdateProgram",Type.EmptyTypes,"Void");
        // ReadPlcBlockFingerprints
        Property(core,"Siemens.Engineering.FingerprintData.FingerprintDataProvider","Configuration","ConnectionConfiguration");
        Method(core,"Siemens.Engineering.FingerprintData.FingerprintDataProvider","GetFingerprintData",new[]{T(core,"Siemens.Engineering.Connection.ConfigurationAddress"),T(core,"Siemens.Engineering.Online.OnlineConfigurationDelegate")},"FingerprintDataResult");
        Property(core,"Siemens.Engineering.FingerprintData.FingerprintDataResult","FingerprintDataItems","FingerprintDataItemComposition");
        Property(core,"Siemens.Engineering.FingerprintData.FingerprintDataItem","FingerprintDataIdentifier","String");
        Property(core,"Siemens.Engineering.FingerprintData.FingerprintDataItem","FingerprintDataValue","String");
        Property(core,"Siemens.Engineering.Connection.ConfigurationAddress","Address","String");
        Property(core,"Siemens.Engineering.Connection.ConfigurationTargetInterface","Addresses");
        Property(core,"Siemens.Engineering.Connection.ConfigurationPcInterface","TargetInterfaces");
        Property(core,"Siemens.Engineering.Connection.ConfigurationMode","PcInterfaces");
        Property(core,"Siemens.Engineering.Connection.ConnectionConfiguration","Modes");
        Method(core,"Siemens.Engineering.Online.Configurations.OnlinePasswordConfiguration","SetPassword",new[]{secure});
        // ImportPlcAlarmInstanceTexts
        Method(step7,"Siemens.Engineering.SW.Alarm.PlcAlarmTextProvider","ImportInstanceTextsFromXlsx",new[]{typeof(System.IO.FileInfo),typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(T(core,"Siemens.Engineering.Language"))},"PlcAlarmTextXlsxResult");
        Property(step7,"Siemens.Engineering.SW.Alarm.PlcAlarmTextXlsxResult","State","PlcAlarmTextXlsxResultState");
        Property(step7,"Siemens.Engineering.SW.Alarm.PlcAlarmTextXlsxResult","LogFilePath","FileInfo");
        Method(core,"Siemens.Engineering.LanguageComposition","Find",new[]{typeof(System.Globalization.CultureInfo)});
        // ManagePlcAlarmTextList
        Property(step7,"Siemens.Engineering.SW.PlcSoftware","PlcAlarmTextlistGroup","PlcAlarmTextlistGroup");
        Property(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmTextlistGroup","PlcAlarmSystemTextlists");
        Property(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmTextlistGroup","PlcAlarmUserTextlists");
        foreach(var name in new[]{"Name","ID","ListRange"}) Property(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmTextlist",name);
        Method(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmUserTextlist","Delete",Type.EmptyTypes,"Void");
        var masterCopy=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopy");
        Method(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmUserTextlistComposition","CreateFrom",new[]{masterCopy},"PlcAlarmUserTextlist");
        Method(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmUserTextlistComposition","CreateFrom",new[]{masterCopy,T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyMode")},"PlcAlarmUserTextlist");
        foreach(var value in new[]{"ThrowIfExists","Rename","Replace"}) check(Enum.GetNames(T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyMode")).Contains(value),"MasterCopyMode."+value);
        // Tool surface: every mutating tool previews by default and destructive ones need an explicit confirmation.
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManagePlcBlockProtection","ManagePlcDataBlockSnapshot","UpdatePlcProgram","ReadPlcBlockFingerprints","ImportPlcAlarmInstanceTexts","ManagePlcAlarmTextList"})
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
        foreach(var (name,flag) in new[]{("ManagePlcBlockProtection","confirmProtectionChange"),("ManagePlcDataBlockSnapshot","confirmValueChange"),("UpdatePlcProgram","confirmUpdate"),("ManagePlcAlarmTextList","confirmDelete")})
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p=>p.Name==flag).DefaultValue,false),name+" requires explicit "+flag);
    }
}

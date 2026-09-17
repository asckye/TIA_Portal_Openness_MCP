using System;
using System.Linq;
using System.Reflection;

// 设备传输族 + 下载提示应答用到的官方成员形状。对 V20/V21 程序集各跑一次；V20 缺 ParameterUploadProvider 时输出 CAPABILITY 而不判失败。
internal static class DeviceTransferShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,int count)=>check(T(a,t).GetMethods().Any(m=>m.Name==name && m.GetParameters().Length==count),t+"."+name+"/"+count);
        void Prop(Assembly a,string t,string name,string typeName)=>check(T(a,t).GetProperty(name)?.PropertyType.Name==typeName,t+"."+name+" : "+typeName);

        Method(core,"Siemens.Engineering.Connection.ConfigurationPcInterface","GetAccessibleDevices",0);
        foreach(var p in new[]{"Name","Address","MACAddress","DeviceSeries"}) Prop(core,"Siemens.Engineering.Connection.ConfigurationAccessibleDevice",p,"String");
        Prop(core,"Siemens.Engineering.Connection.ConfigurationAddress","Address","String");
        Prop(core,"Siemens.Engineering.Connection.ConfigurationPcInterface","TargetInterfaces","ConfigurationTargetInterfaceComposition");
        Prop(core,"Siemens.Engineering.Connection.ConfigurationTargetInterface","Addresses","ConfigurationAddressComposition");

        var upload=T(core,"Siemens.Engineering.Upload.StationUploadProvider");
        check(upload.GetMethod("StationUpload",new[]{T(core,"Siemens.Engineering.Connection.ConfigurationAddress"),T(core,"Siemens.Engineering.Upload.UploadConfigurationDelegate")})!=null,"StationUploadProvider.StationUpload(ConfigurationAddress, delegate)");
        Prop(core,"Siemens.Engineering.Upload.StationUploadProvider","Configuration","ConnectionConfiguration");
        foreach(var p in new[]{"State","ErrorCount","WarningCount","Messages","UploadedStation"}) check(T(core,"Siemens.Engineering.Upload.UploadResult").GetProperty(p)!=null,"UploadResult."+p);
        check(T(core,"Siemens.Engineering.Upload.Configurations.UploadPasswordConfiguration").GetMethod("SetPassword",new[]{typeof(System.Security.SecureString)})!=null,"UploadPasswordConfiguration.SetPassword(SecureString)");
        var parameterUpload=core.GetType("Siemens.Engineering.Upload.ParameterUploadProvider");
        if(parameterUpload==null) Console.WriteLine("CAPABILITY ParameterUploadProvider absent (V21-only); UploadDeviceParameters must return NotSupported on this version.");
        else check(parameterUpload.GetMethods().Any(m=>m.Name=="ParameterUpload" && m.GetParameters().Length==3),"ParameterUploadProvider.ParameterUpload/3");

        check(T(core,"Siemens.Engineering.Download.DownloadProvider").GetMethod("Download",new[]{typeof(System.IO.DirectoryInfo),T(core,"Siemens.Engineering.Download.DownloadConfigurationDelegate")})!=null,"DownloadProvider.Download(DirectoryInfo, delegate)");

        // 提示形态：三种基类各自暴露 CurrentSelection / Checked / SetPassword；UserManagementDownload 必须是选择型
        check(T(core,"Siemens.Engineering.Download.Configurations.DownloadCheckConfiguration").GetProperty("Checked")?.CanWrite==true,"DownloadCheckConfiguration.Checked writable");
        check(T(core,"Siemens.Engineering.Download.Configurations.DownloadPasswordConfiguration").GetMethod("SetPassword",new[]{typeof(System.Security.SecureString)})!=null,"DownloadPasswordConfiguration.SetPassword(SecureString)");
        var userMgmt=T(core,"Siemens.Engineering.Download.Configurations.UserManagementDownload");
        var sel=userMgmt.GetProperty("CurrentSelection");
        check(sel!=null && sel.PropertyType.IsEnum && sel.CanWrite,"UserManagementDownload.CurrentSelection is a writable enum (not a checkbox)");
        if(sel!=null) foreach(var v in new[]{"KeepOnlineUserManagementData","UpdateUserManagementDataButKeepOnlinePassword","DownloadAllUserManagementDataResetToProject"}) check(Enum.GetNames(sel.PropertyType).Contains(v),"UserManagementPreDownloadSelections."+v);
        check(userMgmt.GetProperty("Checked")==null,"UserManagementDownload has no Checked property (the old handler was a no-op)");
        var alarmTexts=T(core,"Siemens.Engineering.Download.Configurations.AlarmTextLibrariesDownload").GetProperty("CurrentSelection");
        check(alarmTexts!=null && Enum.GetNames(alarmTexts.PropertyType).Contains("ConsistentDownload"),"AlarmTextLibrariesDownload is a selection with ConsistentDownload");
        foreach(var (t,v) in new[]{("ExpandDownload","Download"),("LoadIdentificationData","LoadData"),("WaitOnReboot","Wait"),("ResetModule","NoAction"),("OverwriteOnMemoryCard","NoAction"),("ProtectionLevelChanged","NoChange"),("SwitchBackupToPrimary","NoAction"),("InitializeMemory","NoAction"),("OverwriteSystemData","NoAction")})
        {
            var prop=T(core,"Siemens.Engineering.Download.Configurations."+t).GetProperty("CurrentSelection");
            check(prop!=null && Enum.GetNames(prop.PropertyType).Contains(v),t+" default answer '"+v+"' exists in its enum");
        }
        var target=T(step7,"Siemens.Engineering.Download.Configurations.TargetForSoftware").GetProperty("CurrentSelection");
        check(target!=null && Enum.GetNames(target.PropertyType).Contains("CPU") && Enum.GetNames(target.PropertyType).Contains("PlcSimulationAdvanced"),"TargetForSoftware enum has CPU and PlcSimulationAdvanced");
        foreach(var t in new[]{"BlockBindingPassword","OverwriteTargetLanguages","UpgradeTargetDevice","DownloadWebApplication","DeleteWebApplication"}) check(step7.GetType("Siemens.Engineering.Download.Configurations."+t)!=null,"Step7 download prompt type "+t);
        check(T(core,"Siemens.Engineering.Download.Configurations.DownloadConfiguration").GetProperty("Message")!=null,"DownloadConfiguration.Message (recorded for unanswered prompts)");
        check(T(core,"Siemens.Engineering.Upload.Configurations.UploadConfiguration").GetProperty("Message")!=null,"UploadConfiguration.Message");
        check(server.GetType("TiaMcpServer.Siemens.DownloadPromptPolicy")!=null,"engine carries DownloadPromptPolicy");
    }
}

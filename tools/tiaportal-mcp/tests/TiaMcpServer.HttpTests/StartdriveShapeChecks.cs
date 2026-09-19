using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.39 phase 6 ⑥-② Startdrive option-package tools (Portal.Startdrive.cs), verified member by member
// against the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Startdrive) PublicAPI. V21 adds DriveItemHardwareModule,
// SafetyAcceptanceTestProvider / Report, TestFunction, FileOperations and the SDR hardware-connection interfaces (Telegram connects moved
// off the base interfaces, which carry them on V20 with MC.Drives.Enums.ConnectOption).
internal static class StartdriveShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var drives=Api("Siemens.Engineering.Startdrive","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        Type? TryT(Assembly a,string n)=>a.GetType(n,false);
        void Method(Assembly a,string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(a,t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        void Property(Assembly a,string t,string name,string? type=null,bool? writable=null)
        {
            var p=T(a,t).GetProperty(name);
            check(p!=null && (type==null || p.PropertyType.Name==type) && (writable==null || p.CanWrite==writable),t+"."+name+(type==null?"":" : "+type)+(writable==null?"":writable.Value?" (writable)":" (read-only)"));
        }
        void Enum(Assembly a,string t,params string[] names)
        {
            var type=T(a,t); var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        const string ns="Siemens.Engineering.MC.Drives."; const string dfi=ns+"DFI."; const string en=ns+"Enums."; const string motion="Siemens.Engineering.SW.TechnologicalObjects.Motion.";
        var text=typeof(string); var file=typeof(FileInfo); var i32=typeof(int); var u16=typeof(ushort); var boolean=typeof(bool); var secure=typeof(System.Security.SecureString);
        var addressIoType=T(core,"Siemens.Engineering.HW.AddressIoType"); var telegramType=T(drives,en+"TelegramType"); var telegram=T(drives,ns+"Telegram");

        // ---- enums (names mirrored in StartdriveLogic) ----
        Enum(drives,en+"TelegramType","MainTelegram","SupplementaryTelegram","AdditionalTelegram","SafetyTelegram","TorqueTelegram","EdgeTelegram");
        Enum(drives,en+"DriveObjectActivationState","Deactivate","Activate","DeactivateAndNotPresent");
        Enum(drives,en+"FunctionKeys","BasicPositioner","TechnologyController");
        Enum(drives,en+"RotaryLinearFlag","Rotary","Linear"); Enum(drives,en+"AbsoluteIncrementalFlag","Absolute","Incremental");
        Enum(drives,en+"EncoderInterface","None","Terminal","DSub","DriveCliQ","HTL","SSI");
        Enum(drives,en+"EncoderType","NoEncoder","Resolver","HTLTTL","SSIProtocoll","SinCos","EnDat","HTL","SSIProtocollAndHTLTTL","SSIProtocollAndSinCos","DriveCliQ");
        Enum(drives,en+"MotorType","NoMotor","InductionMotor","SynchronousMotor","NoCodeNumber1LE1InductionMotor","NoCodeNumber1LG6InductionMotor","NoCodeNumber1xx1SIMOTICSFDInductionMotor","NoCodeNumber1LA7InductionMotorNoCodeNumber","MotorSeriesNumber1LA81PQ8StandardInduction","NoCodeNumber1LA9InductionMotor","InductionMotor1LE1","InductionMotor1PC1","InductionMotor1PH4","InductionMotor1LE5","InductionMotor1PH7","InductionMotor1PH8","NoEncoder1FG1GearedSynchronousMotor","NoEncoder1FK7SynchronousMotor","DriveCliqMotor","DriveCliqMotorDataSet");
        Enum(drives,en+"ResetMode","ParameterReset","SafetyParameterReset");
        Enum(core,"Siemens.Engineering.HW.AddressIoType","Input","Output");
        if(v20) Enum(drives,en+"ConnectOption","Default","AllowAllModules"); else { Enum(drives,en+"FileOperations","None","Overwrite"); check(TryT(drives,en+"ConnectOption")==null,"MC.Drives.Enums.ConnectOption is V20 only (V21 uses Motion.ConnectOption)"); }
        Enum(step7,motion+"ConnectOption","Default","AllowAllModules");

        // ---- drive object container / drive objects ----
        check(engineeringService.IsAssignableFrom(T(drives,ns+"DriveObjectContainer")),"DriveObjectContainer is an IEngineeringService (DeviceItem.GetService)");
        Property(drives,ns+"DriveObjectContainer","DriveObjects","DriveObjectComposition",false);
        check(serviceProvider.IsAssignableFrom(T(drives,ns+"DriveObject")),"DriveObject is a service provider (DriveFunctionInterface / TechnologyExtensionContainer / DriveControlChartContainer)");
        Property(drives,ns+"DriveObject","DriveObjectNumber","UInt16",false); Property(drives,ns+"DriveObject","Parameters","DriveParameterComposition",false); Property(drives,ns+"DriveObject","ReadParameters","ReadDriveParameterComposition",false);
        Property(drives,ns+"DriveObject","Telegrams","TelegramComposition",false); Property(drives,ns+"DriveObject","Security","Security",false);
        // V20 declares the composition as IEnumerable<DriveObject> + Count + indexer, V21 as IList<DriveObject>; the index selector needs Count and Item(int) on both.
        var driveObjects=T(drives,ns+"DriveObjectComposition"); check(driveObjects.GetProperty("Count")!=null && driveObjects.GetProperty("Item",new[]{typeof(int)})!=null && typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(T(drives,ns+"DriveObject")).IsAssignableFrom(driveObjects),"DriveObjectComposition has Count / Item(int) and is IEnumerable<DriveObject> (index selector)");
        var onlineObjects=T(drives,ns+"OnlineDriveObjectComposition"); check(onlineObjects.GetProperty("Count")!=null && onlineObjects.GetProperty("Item",new[]{typeof(int)})!=null,"OnlineDriveObjectComposition has Count / Item(int)");
        check(engineeringService.IsAssignableFrom(T(drives,ns+"OnlineDriveObjectContainer")),"OnlineDriveObjectContainer is an IEngineeringService");
        Property(drives,ns+"OnlineDriveObjectContainer","OnlineDriveObjects","OnlineDriveObjectComposition",false);
        check(serviceProvider.IsAssignableFrom(T(drives,ns+"OnlineDriveObject")),"OnlineDriveObject is a service provider (OnlineDriveFunctionInterface)");
        Property(drives,ns+"OnlineDriveObject","DriveObjectNumber","UInt16",false); Property(drives,ns+"OnlineDriveObject","ReadParameters","ReadDriveParameterComposition",false); Property(drives,ns+"OnlineDriveObject","Parameters","DriveParameterComposition",false); Property(drives,ns+"OnlineDriveObject","Security","Security",false);
        check(engineeringService.IsAssignableFrom(T(drives,ns+"ModuleAccessPoint")),"ModuleAccessPoint is an IEngineeringService"); Property(drives,ns+"ModuleAccessPoint","HwIdentifiers","HwIdentifierComposition",false);

        // ---- parameters ----
        foreach(var t in new[]{ns+"DriveParameter",ns+"ReadDriveParameter"})
        {
            var composition=t.Substring(ns.Length);
            Property(drives,t,"Name","String",false); Property(drives,t,"Number","Int32",false); Property(drives,t,"ArrayIndex","Int32",false); Property(drives,t,"ArrayLength","Int32",false);
            Property(drives,t,"Value","Object",true); Property(drives,t,"MinValue","Object",false); Property(drives,t,"MaxValue","Object",false); Property(drives,t,"Unit","String",false); Property(drives,t,"ParameterText","String",false);
            Property(drives,t,"EnumValueList","IDictionary`2",false); Property(drives,t,"Bits",composition+"Composition",false);
            Method(drives,t+"Composition","Find",new[]{text},composition); Method(drives,t+"Composition","Find",new[]{i32,i32},composition);
        }

        // ---- telegrams ----
        Property(drives,ns+"Telegram","TelegramNumber","Int32",true); Property(drives,ns+"Telegram","Type","TelegramType",false); Property(drives,ns+"Telegram","Addresses","AddressComposition",false);
        Property(drives,ns+"Telegram","HwIdentifiers","HwIdentifierComposition",false); Property(drives,ns+"Telegram","PKW","Telegram",false);
        Method(drives,ns+"Telegram","GetSize",new[]{addressIoType},"Int32"); Method(drives,ns+"Telegram","GetSizeInBytes",new[]{addressIoType},"Int32");
        Method(drives,ns+"Telegram","CanChangeTelegram",new[]{i32},"Boolean"); Method(drives,ns+"Telegram","CanChangeSize",new[]{addressIoType,i32,boolean},"Boolean"); Method(drives,ns+"Telegram","ChangeSize",new[]{addressIoType,i32,boolean},"Boolean");
        Method(drives,ns+"TelegramComposition","Find",new[]{telegramType},"Telegram");
        Method(drives,ns+"TelegramComposition","CanInsertTelegram",new[]{i32,telegramType},"Boolean"); Method(drives,ns+"TelegramComposition","InsertTelegram",new[]{i32,telegramType},"Void");
        Method(drives,ns+"TelegramComposition","CanInsertMainTelegram",new[]{i32},"Boolean"); Method(drives,ns+"TelegramComposition","InsertMainTelegram",new[]{i32},"Void");
        Method(drives,ns+"TelegramComposition","CanInsertSupplementaryTelegram",new[]{i32},"Boolean"); Method(drives,ns+"TelegramComposition","InsertSupplementaryTelegram",new[]{i32},"Void");
        Method(drives,ns+"TelegramComposition","CanInsertSafetyTelegram",new[]{i32},"Boolean"); Method(drives,ns+"TelegramComposition","InsertSafetyTelegram",new[]{i32},"Void");
        Method(drives,ns+"TelegramComposition","CanInsertTorqueTelegram",new[]{i32},"Boolean"); Method(drives,ns+"TelegramComposition","InsertTorqueTelegram",new[]{i32},"Void");
        Method(drives,ns+"TelegramComposition","CanInsertAdditionalTelegram",new[]{i32,i32},"Boolean"); Method(drives,ns+"TelegramComposition","InsertAdditionalTelegram",new[]{i32,i32},"Void");
        Method(drives,ns+"TelegramComposition","EraseTelegram",new[]{telegramType},"Void");
        Property(core,"Siemens.Engineering.HW.HwIdentifier","Identifier","Int64",false);

        // ---- drive function interface ----
        check(engineeringService.IsAssignableFrom(T(drives,dfi+"DriveFunctionInterface")),"DriveFunctionInterface is an IEngineeringService (DriveObject.GetService)");
        Property(drives,dfi+"DriveFunctionInterface","Commissioning","Commissioning",false); Property(drives,dfi+"DriveFunctionInterface","DriveObjectFunctions","DriveObjectFunctions",false);
        Property(drives,dfi+"DriveFunctionInterface","FunctionInUse","FunctionInUse",false); Property(drives,dfi+"DriveFunctionInterface","HardwareProjection","HardwareProjection",false); Property(drives,dfi+"DriveFunctionInterface","SafetyCommissioning","SafetyCommissioning",false);
        Property(drives,dfi+"DriveObjectFunctions","DriveObjectActivation","DriveObjectActivation",false); Property(drives,dfi+"DriveObjectFunctions","DriveObjectTypeHandler","DriveObjectTypeHandler",false);
        Property(drives,dfi+"DriveObjectActivation","ActivationState","DriveObjectActivationState",false); Property(drives,dfi+"DriveObjectActivation","IsActive","Boolean",false);
        Method(drives,dfi+"DriveObjectActivation","ChangeActivationState",new[]{T(drives,en+"DriveObjectActivationState")},"Boolean");
        Property(drives,dfi+"DriveObjectTypeHandler","CurrentDriveObjectType","DriveObjectType",false); Property(drives,dfi+"DriveObjectTypeHandler","PossibleDriveObjectTypes","DriveObjectTypeComposition",false);
        Method(drives,dfi+"DriveObjectTypeHandler","ChangeDriveObjectType",new[]{T(drives,dfi+"DriveObjectType")},"Boolean");
        Property(drives,dfi+"DriveObjectType","Name","String",false); Property(drives,dfi+"DriveObjectType","Number","Int32",false); Method(drives,dfi+"DriveObjectTypeComposition","Find",new[]{text},"DriveObjectType");
        Method(drives,dfi+"FunctionInUse","Activate",new[]{T(drives,en+"FunctionKeys")},"Boolean"); Method(drives,dfi+"FunctionInUse","Deactivate",new[]{T(drives,en+"FunctionKeys")},"Boolean"); Method(drives,dfi+"FunctionInUse","SetSIAxisType",new[]{T(drives,en+"RotaryLinearFlag")},"Boolean");
        Method(drives,dfi+"Commissioning","SetMotorCode",new[]{i32,i32},"Boolean"); Method(drives,dfi+"Commissioning","SetSimoGearMlfb",new[]{text},"Void");
        Method(drives,dfi+"SafetyCommissioning","UpdateCheckSums",Type.EmptyTypes,"Boolean");
        Method(drives,dfi+"HardwareProjection","SetMotorType",new[]{T(drives,en+"MotorType"),u16},"Boolean"); Method(drives,dfi+"HardwareProjection","GetCurrentMotorConfiguration",new[]{u16},"MotorConfiguration");
        Method(drives,dfi+"HardwareProjection","ProjectMotorConfiguration",new[]{T(drives,dfi+"MotorConfiguration"),u16},"Boolean");
        Method(drives,dfi+"HardwareProjection","SetEncoder",new[]{T(drives,en+"EncoderInterface"),T(drives,en+"EncoderType"),T(drives,en+"AbsoluteIncrementalFlag"),T(drives,en+"RotaryLinearFlag"),u16},"Boolean");
        Method(drives,dfi+"HardwareProjection","GetCurrentEncoderConfiguration",new[]{u16},"EncoderConfiguration"); Method(drives,dfi+"HardwareProjection","ProjectEncoderConfiguration",new[]{T(drives,dfi+"EncoderConfiguration"),u16},"Boolean");
        Property(drives,dfi+"MotorConfiguration","RequiredConfigurationEntries","ConfigurationEntryComposition",false); Property(drives,dfi+"MotorConfiguration","OptionalConfigurationEntries","ConfigurationEntryComposition",false);
        Method(drives,dfi+"MotorConfiguration","CanSetEquivalentCircuitDiagramData",Type.EmptyTypes,"Boolean"); Method(drives,dfi+"MotorConfiguration","SetEquivalentCircuitDiagramData",new[]{boolean},"Boolean");
        Property(drives,dfi+"EncoderConfiguration","RequiredConfigurationEntries","ConfigurationEntryComposition",false); Property(drives,dfi+"EncoderConfiguration","EncoderTypes","ConfigurationEntryComposition",false);
        Method(drives,dfi+"EncoderConfiguration","SetEncoderType",new[]{T(drives,en+"RotaryLinearFlag")},"Boolean"); Method(drives,dfi+"EncoderConfiguration","SetEncoderType",new[]{T(drives,en+"RotaryLinearFlag"),T(drives,en+"AbsoluteIncrementalFlag")},"Boolean");
        Property(drives,dfi+"ConfigurationEntry","Name","String",false); Property(drives,dfi+"ConfigurationEntry","Number","UInt32",false); Property(drives,dfi+"ConfigurationEntry","Value","Object",true);
        Property(drives,dfi+"ConfigurationEntry","MinValue","Object",false); Property(drives,dfi+"ConfigurationEntry","MaxValue","Object",false); Property(drives,dfi+"ConfigurationEntry","Unit","String",false); Property(drives,dfi+"ConfigurationEntry","Description","String",false); Property(drives,dfi+"ConfigurationEntry","EnumValueList","IDictionary`2",false);
        Method(drives,dfi+"ConfigurationEntryComposition","Find",new[]{typeof(uint)},"ConfigurationEntry"); Method(drives,dfi+"ConfigurationEntryComposition","Find",new[]{text},"ConfigurationEntry");
        check(engineeringService.IsAssignableFrom(T(drives,dfi+"OnlineDriveFunctionInterface")),"OnlineDriveFunctionInterface is an IEngineeringService (OnlineDriveObject.GetService)");
        Property(drives,dfi+"OnlineDriveFunctionInterface","DriveDomainFunctions","DriveDomainFunctions",false); Property(drives,dfi+"OnlineDriveFunctionInterface","DriveObjectFunctions","DriveObjectFunctions",false);
        Property(drives,dfi+"OnlineDriveFunctionInterface","FunctionInUse","FunctionInUse",false); Property(drives,dfi+"OnlineDriveFunctionInterface","HardwareProjection","HardwareProjection",false);
        Method(drives,dfi+"DriveDomainFunctions","PerformFactoryReset",new[]{T(drives,en+"ResetMode")},"Boolean"); Method(drives,dfi+"DriveDomainFunctions","PerformRAMtoROMCopyAllDriveObject",Type.EmptyTypes,"Boolean");

        // ---- security / technology extensions ----
        Property(drives,ns+"Security","UmacConfiguration","UmacConfiguration",false); Property(drives,ns+"Security","DriveDataEncryption","DriveDataEncryption",false);
        Method(drives,ns+"SecurityObjects.UmacConfiguration","Activate",Type.EmptyTypes,"Boolean"); Method(drives,ns+"SecurityObjects.UmacConfiguration","Deactivate",Type.EmptyTypes,"Boolean");
        Method(drives,ns+"SecurityObjects.DriveDataEncryption","Activate",new[]{secure},"Boolean"); Method(drives,ns+"SecurityObjects.DriveDataEncryption","Deactivate",new[]{secure},"Boolean");
        check(engineeringService.IsAssignableFrom(T(drives,ns+"TechnologyExtensionContainer")),"TechnologyExtensionContainer is an IEngineeringService (DriveObject.GetService)");
        Property(drives,ns+"TechnologyExtensionContainer","TechnologyExtensions","TechnologyExtensionComposition",false); Method(drives,ns+"TechnologyExtensionComposition","Find",new[]{text},"TechnologyExtension");
        Property(drives,ns+"TechnologyExtension","Identifier","String",false); Property(drives,ns+"TechnologyExtension","Name","String",false); Property(drives,ns+"TechnologyExtension","DisplayName","String",false); Property(drives,ns+"TechnologyExtension","IsActivated","Boolean",false); Property(drives,ns+"TechnologyExtension","Parameters","DriveParameterComposition",false);
        Method(drives,ns+"TechnologyExtension","Activate",Type.EmptyTypes,"Boolean"); Method(drives,ns+"TechnologyExtension","Deactivate",Type.EmptyTypes,"Boolean");
        check(engineeringService.IsAssignableFrom(T(drives,ns+"TechnologyExtensionInstallationProvider")),"TechnologyExtensionInstallationProvider is an IEngineeringService (TiaPortal.GetService)");
        check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.TiaPortal")),"TiaPortal is a service provider");
        Property(drives,ns+"TechnologyExtensionInstallationProvider","TechnologyExtensionPackages","TechnologyExtensionPackageComposition",false);
        Method(drives,ns+"TechnologyExtensionInstallationProvider","Install",new[]{file},"Boolean"); Method(drives,ns+"TechnologyExtensionInstallationProvider","InstallAndGetIdentifier",new[]{file},"String"); Method(drives,ns+"TechnologyExtensionInstallationProvider","Uninstall",new[]{text,boolean},"Boolean");
        Method(drives,ns+"TechnologyExtensionPackageComposition","Find",new[]{text},"TechnologyExtensionPackage");
        Property(drives,ns+"TechnologyExtensionPackage","Identifier","String",false); Property(drives,ns+"TechnologyExtensionPackage","Name","String",false); Property(drives,ns+"TechnologyExtensionPackage","DisplayName","String",false);

        // ---- V21 additions: hardware module, safety acceptance test, SDR interfaces, upload prompts ----
        if(v20)
        {
            foreach(var t in new[]{ns+"DriveItemHardwareModule",ns+"SafetyAcceptanceTestProvider",ns+"SafetyAcceptanceTestReport",ns+"TestFunction",motion+"AxisHardwareConnectionSDRProvider","Siemens.Engineering.Upload.Configurations.OverrideTelegramMismatch"})
                check(TryT(drives,t)==null && TryT(step7,t)==null,t+" is absent on V20 (V21 addition)");
            Method(step7,motion+"AxisEncoderHardwareConnectionInterface","Connect",new[]{telegram},"Void"); Method(step7,motion+"AxisEncoderHardwareConnectionInterface","Connect",new[]{telegram,T(drives,en+"ConnectOption")},"Void");
            Method(step7,motion+"TorqueHardwareConnectionInterface","Connect",new[]{telegram},"Void"); Method(step7,motion+"TorqueHardwareConnectionInterface","Connect",new[]{telegram,T(drives,en+"ConnectOption")},"Void");
        }
        else
        {
            check(engineeringService.IsAssignableFrom(T(drives,ns+"DriveItemHardwareModule")),"DriveItemHardwareModule is an IEngineeringService (DeviceItem.GetService)");
            Property(drives,ns+"DriveItemHardwareModule","ComponentName","String",false); Property(drives,ns+"DriveItemHardwareModule","PositionNumber","Int32",true); Property(drives,ns+"DriveItemHardwareModule","TypeName","String",false); Property(drives,ns+"DriveItemHardwareModule","TypeIdentifier","String",false);
            Method(drives,ns+"DriveItemHardwareModule","ChangeType",new[]{text},"Void");
            check(engineeringService.IsAssignableFrom(T(drives,ns+"SafetyAcceptanceTestProvider")),"SafetyAcceptanceTestProvider is an IEngineeringService (DeviceItem feature)");
            Property(drives,ns+"SafetyAcceptanceTestProvider","TestFunctions","TestFunctionComposition",false); Method(drives,ns+"SafetyAcceptanceTestProvider","ResetTestFunctions",Type.EmptyTypes,"Void");
            Property(drives,ns+"TestFunction","Identifier","String",false); Property(drives,ns+"TestFunction","Active","Boolean",true); Method(drives,ns+"TestFunctionComposition","Find",new[]{text},"TestFunction");
            check(engineeringService.IsAssignableFrom(T(drives,ns+"SafetyAcceptanceTestReport")),"SafetyAcceptanceTestReport is an IEngineeringService (Device feature)");
            Method(drives,ns+"SafetyAcceptanceTestReport","CreateProtocol",new[]{file,T(drives,en+"FileOperations")},"Boolean");
            var motionOption=T(step7,motion+"ConnectOption");
            check(engineeringService.IsAssignableFrom(T(drives,motion+"AxisHardwareConnectionSDRProvider")),"AxisHardwareConnectionSDRProvider is an IEngineeringService (technology object)");
            Property(drives,motion+"AxisHardwareConnectionSDRProvider","ActorInterface","AxisEncoderHardwareConnectionSDRInterface",false); Property(drives,motion+"AxisHardwareConnectionSDRProvider","SensorInterface","AxisEncoderHardwareConnectionSDRInterfaceComposition",false); Property(drives,motion+"AxisHardwareConnectionSDRProvider","TorqueInterface","TorqueHardwareConnectionSDRInterface",false);
            check(T(drives,motion+"EncoderHardwareConnectionSDRProvider").IsSubclassOf(T(step7,motion+"EncoderHardwareConnectionProvider")),"EncoderHardwareConnectionSDRProvider derives from EncoderHardwareConnectionProvider");
            // the SDR provider re-declares SensorInterface with the SDR type (hides the base property) - declared-only lookup avoids AmbiguousMatchException
            var sdrSensor=T(drives,motion+"EncoderHardwareConnectionSDRProvider").GetProperty("SensorInterface",BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly);
            check(sdrSensor!=null && sdrSensor.PropertyType.Name=="AxisEncoderHardwareConnectionSDRInterface" && !sdrSensor.CanWrite,motion+"EncoderHardwareConnectionSDRProvider.SensorInterface : AxisEncoderHardwareConnectionSDRInterface (read-only, hides the base property)");
            check(T(drives,motion+"AxisEncoderHardwareConnectionSDRInterface").IsSubclassOf(T(step7,motion+"AxisEncoderHardwareConnectionInterface")) && T(drives,motion+"TorqueHardwareConnectionSDRInterface").IsSubclassOf(T(step7,motion+"TorqueHardwareConnectionInterface")),"SDR interfaces derive from the Motion base interfaces (InterfaceRow applies)");
            foreach(var t in new[]{motion+"AxisEncoderHardwareConnectionSDRInterface",motion+"TorqueHardwareConnectionSDRInterface"}) { Method(drives,t,"Connect",new[]{telegram},"Void"); Method(drives,t,"Connect",new[]{telegram,motionOption},"Void"); }
            foreach(var t in new[]{"Siemens.Engineering.Upload.Configurations.OverrideTelegramMismatch","Siemens.Engineering.Upload.Configurations.OverwriteOfflineConfiguration"}) { check(T(drives,t).IsSubclassOf(T(core,"Siemens.Engineering.Upload.Configurations.UploadConfiguration")),t+" derives from UploadConfiguration"); Property(drives,t,"Checked","Boolean",true); }
            foreach(var t in new[]{"StartDriveDownloadCheckConfiguration","AcceptDownloadOfUnencryptedSensitiveData","ReplaceDownloadedData"}) { check(T(drives,"Siemens.Engineering.Download.Configurations."+t).IsSubclassOf(T(core,"Siemens.Engineering.Download.Configurations.DownloadCheckConfiguration")),t+" derives from DownloadCheckConfiguration"); }
        }

        // ---- server side ----
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ReadDriveObjects","ReadDriveParameters","ManageStartdriveParameter","ManageDriveTelegrams","ManageDriveFunctions","ManageDriveSecurity","ManageTechnologyExtensions","ManageDriveHardwareModule","ManageDriveSafetyAcceptanceTest","ReadOnlineDriveParameters","ManageOnlineDriveFunctions"})
            check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("ManageStartdriveParameter")!.GetParameters().Any(p=>p.Name=="driveObjectIndex"),"ManageStartdriveParameter takes driveObjectIndex (G120: DriveObjectNumber not retrievable)");
    }
}

using System;
using System.Linq;
using System.Reflection;

// Native members used by the Safety family (Portal.SafetyManagement.cs), verified against the installed API.
// V20 keeps Siemens.Engineering.Safety inside Siemens.Engineering.dll and lacks ProgramSignatures / SafetyBaseIdProvider.
internal static class SafetyShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var safety=Api("Siemens.Engineering.Safety","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
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
        var secure=typeof(System.Security.SecureString);
        const string ns="Siemens.Engineering.Safety.";
        var service=T(core,"Siemens.Engineering.IEngineeringService");
        var engineeringObject=T(core,"Siemens.Engineering.IEngineeringObject");
        // SafetyAdministration (PLC device item service; ResolvePlcService walks PlcSoftware then the DeviceItem chain)
        check(service.IsAssignableFrom(T(safety,ns+"SafetyAdministration")),"SafetyAdministration is an engineering service");
        Property(safety,ns+"SafetyAdministration","IsSafetyOfflineProgramPasswordSet","Boolean");
        Property(safety,ns+"SafetyAdministration","IsLoggedOnToSafetyOfflineProgram","Boolean");
        Property(safety,ns+"SafetyAdministration","Settings","SafetySettings");
        Property(safety,ns+"SafetyAdministration","RuntimeGroups","RuntimeGroupComposition");
        foreach(var name in new[]{"LoginToSafetyOfflineProgram","SetSafetyOfflineProgramPassword","RevokeSafetyOfflineProgramPassword"}) Method(safety,ns+"SafetyAdministration",name,new[]{secure},"Void");
        Method(safety,ns+"SafetyAdministration","LogoffFromSafetyOfflineProgram",Type.EmptyTypes,"Void");
        if(T(safety,ns+"SafetyAdministration").GetProperty("ProgramSignatures")==null && v20) Console.WriteLine("CAPABILITY V20 has no SafetyAdministration.ProgramSignatures; collective signatures are reported as null with a note.");
        else Property(safety,ns+"SafetyAdministration","ProgramSignatures","SafetySignatureProvider");
        // SafetySettings: CLR scalars, nested AssignmentOfBlockNumbers, SafetySystemVersion, documented dynamic attributes via GetAttribute/SetAttribute
        foreach(var name in new[]{"ActivationOfFChangeHistory","CreateDriverInstanceDataBlocksWithoutPrefix","SafetyModeCanBeDisabled"}) Property(safety,ns+"SafetySettings",name,"Boolean",true);
        Property(safety,ns+"SafetySettings","AssignmentOfBlockNumbers","AssignmentOfBlockNumbers",false);
        Property(safety,ns+"SafetySettings","SafetySystemVersion","SafetySystemVersion",true);
        Property(safety,ns+"SafetySystemVersion","Value","String");
        Method(safety,ns+"SafetySettings","GetApplicableSafetySystemVersions",Type.EmptyTypes);
        Method(safety,ns+"SafetySettings","CleanSystemGeneratedObjects",Type.EmptyTypes,"Void");
        check(engineeringObject.IsAssignableFrom(T(safety,ns+"SafetySettings")),"SafetySettings exposes GetAttribute/SetAttribute (EnableConsistentUploadFromFCpu, EnableFCommunicationIdTag)");
        foreach(var name in new[]{"FromFB","ToFB","FromFC","ToFC","FromDB","ToDB"}) Property(safety,ns+"AssignmentOfBlockNumbers",name,"UInt16",true);
        Property(safety,ns+"AssignmentOfBlockNumbers","ManagementMode","BlockNumbersManagementMode",true);
        foreach(var value in new[]{"FSystemManaged","FixedRange"}) check(Enum.GetNames(T(safety,ns+"BlockNumbersManagementMode")).Contains(value),"BlockNumbersManagementMode."+value);
        // RuntimeGroup
        Method(safety,ns+"RuntimeGroupComposition","Find",new[]{typeof(string)},"RuntimeGroup");
        var plcBlock=T(step7,"Siemens.Engineering.SW.Blocks.PlcBlock");
        Method(safety,ns+"RuntimeGroupComposition","Create",new[]{typeof(string)},"RuntimeGroup");
        Method(safety,ns+"RuntimeGroupComposition","Create",new[]{typeof(string),plcBlock},"RuntimeGroup");
        Method(safety,ns+"RuntimeGroupComposition","Create",new[]{typeof(string),plcBlock,plcBlock},"RuntimeGroup");
        Method(safety,ns+"RuntimeGroup","Delete",Type.EmptyTypes,"Void");
        Method(safety,ns+"RuntimeGroup","GenerateGlobalFIOStatusBlock",Type.EmptyTypes,"PlcBlock");
        foreach(var name in new[]{"Name","FOBName","FOBEventClass","MainSafetyBlockName","MainSafetyBlockIDbName"}) Property(safety,ns+"RuntimeGroup",name,"String",false);
        foreach(var name in new[]{"InfoDbName","PreProcessingName","PostProcessingName"}) Property(safety,ns+"RuntimeGroup",name,"String",true);
        foreach(var name in new[]{"WarnCycleTime","MaximumCycleTime"}) Property(safety,ns+"RuntimeGroup",name,"Int32",true);
        check(engineeringObject.IsAssignableFrom(T(safety,ns+"RuntimeGroup")),"RuntimeGroup exposes GetAttribute/SetAttribute (FOBNumber, FOBCycleTime, FOBPhaseShift, FOBPriority)");
        // Signatures
        check(service.IsAssignableFrom(T(safety,ns+"SafetySignatureProvider")),"SafetySignatureProvider is an engineering service (PlcBlock.GetService)");
        Property(safety,ns+"SafetySignatureProvider","Signatures","SafetySignatureComposition");
        Property(safety,ns+"SafetySignature","Type","SafetySignatureType");
        Property(safety,ns+"SafetySignature","Value","UInt64");
        var signatureTypes=Enum.GetNames(T(safety,ns+"SafetySignatureType"));
        check(signatureTypes.Contains("BlockOfflineSignature"),"SafetySignatureType.BlockOfflineSignature");
        if(signatureTypes.Length==1 && v20) Console.WriteLine("CAPABILITY V20 SafetySignatureType has only BlockOfflineSignature; collective/software/hardware/communication-address types are V21.");
        else foreach(var value in new[]{"CollectiveOfflineSignature","SoftwareOfflineSignature","HardwareOfflineSignature","CommunicationAddressOfflineSignature"})
            check(signatureTypes.Contains(value),"SafetySignatureType."+value);
        // Printout (DeviceItem service)
        check(service.IsAssignableFrom(T(safety,ns+"SafetyPrintout")),"SafetyPrintout is an engineering service (DeviceItem.GetService)");
        Method(safety,ns+"SafetyPrintout","Print",new[]{T(safety,ns+"SafetyPrintoutFilePrinter"),typeof(System.IO.FileInfo),typeof(string),T(safety,ns+"SafetyPrintoutOption")},"Boolean");
        foreach(var value in new[]{"MicrosoftPrintToPdf","MicrosoftXpsDocumentWriter"}) check(Enum.GetNames(T(safety,ns+"SafetyPrintoutFilePrinter")).Contains(value),"SafetyPrintoutFilePrinter."+value);
        foreach(var value in new[]{"All","Compact"}) check(Enum.GetNames(T(safety,ns+"SafetyPrintoutOption")).Contains(value),"SafetyPrintoutOption."+value);
        // GlobalSettings (TiaPortal service): getters and setters are method overloads, not properties
        check(service.IsAssignableFrom(T(safety,ns+"GlobalSettings")),"GlobalSettings is an engineering service (TiaPortal.GetService)");
        check(T(core,"Siemens.Engineering.IEngineeringServiceProvider").IsAssignableFrom(T(core,"Siemens.Engineering.TiaPortal")),"TiaPortal is a service provider");
        foreach(var name in new[]{"SafetyModificationsPossible","GenerationOfDefaultFailsafeProgram","ManagementOfFailsafeInSoftwareUnitsEnvironment"})
        {
            Method(safety,ns+"GlobalSettings",name,Type.EmptyTypes,"Boolean");
            Method(safety,ns+"GlobalSettings",name,new[]{typeof(bool)},"Void");
        }
        Method(safety,ns+"GlobalSettings","UsernameForFChangeHistory",Type.EmptyTypes,"String");
        Method(safety,ns+"GlobalSettings","UsernameForFChangeHistory",new[]{typeof(string)},"Void");
        // Base ID (V21)
        var baseId=safety.GetType(ns+"SafetyBaseIdProvider");
        if(baseId==null && v20) Console.WriteLine("CAPABILITY V20 has no SafetyBaseIdProvider; generateBaseId must return NotSupportedOnVersion.");
        else { check(baseId!=null && service.IsAssignableFrom(baseId),"SafetyBaseIdProvider is an engineering service"); check(baseId!.GetMethod("GenerateBaseId",Type.EmptyTypes)?.ReturnType==typeof(void),"SafetyBaseIdProvider.GenerateBaseId() -> Void"); }
        // Download configuration answered by DownloadPromptPolicy
        Property(safety,ns+"Download.Configurations.SafetyProgram","CurrentSelection","SafetyProgramSelections");
        check(Enum.GetNames(T(safety,ns+"Download.Configurations.SafetyProgramSelections")).SequenceEqual(new[]{"ConsistentDownload"}),"SafetyProgramSelections has the single value ConsistentDownload");
        // Tool surface
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManagePlcSafety","ManageSafetyGlobalSettings","ExportSafetyPrintout"})
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
        check(Equals(tools.GetMethod("ManagePlcSafety")!.GetParameters().Single(p=>p.Name=="confirmSafetyChange").DefaultValue,false),"ManagePlcSafety requires explicit confirmSafetyChange");
        check(tools.GetMethod("ReadSafetyBlockSignatures")!.GetParameters().All(p=>p.Name!="dryRun"),"ReadSafetyBlockSignatures is read-only (no dryRun)");
        check(Equals(tools.GetMethod("ManagePlcSafety")!.GetParameters().Single(p=>p.Name=="password").DefaultValue,""),"ManagePlcSafety password is optional and empty by default");
    }
}

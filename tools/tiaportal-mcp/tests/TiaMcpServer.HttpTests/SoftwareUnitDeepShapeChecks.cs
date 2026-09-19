using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.34 Step7 sub-batch 1 (Portal.SoftwareUnitDeep.cs: software / safety units, named value
// type and UDT documents, checksums, fingerprints, block write protection, project compilation settings, channel linked
// tags, V21 process image assignment), verified member by member against the installed V20 (Siemens.Engineering) or V21
// (Siemens.Engineering.Base / Siemens.Engineering.Step7) PublicAPI.
internal static class SoftwareUnitDeepShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
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
        void Enum(Assembly a,string t,params string[] names)
        {
            var type=T(a,t); var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        void Service(string t) => check(engineeringService.IsAssignableFrom(T(step7,t)),t+" is an IEngineeringService");
        const string sw="Siemens.Engineering.SW.", units="Siemens.Engineering.SW.Units.", blocks="Siemens.Engineering.SW.Blocks.", types="Siemens.Engineering.SW.Types.", hw="Siemens.Engineering.HW.";
        var secure=typeof(SecureString); var directory=typeof(DirectoryInfo); var text=typeof(string);
        var masterCopy=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopy"); var masterCopyMode=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyMode"); var updatePaths=T(core,"Siemens.Engineering.Library.Types.UpdatePathsMode");

        // ---- software units ----
        Service(units+"PlcUnitProvider"); Property(step7,units+"PlcUnitProvider","UnitGroup","PlcUnitSystemGroup");
        Property(step7,units+"PlcUnitSystemGroup","Units","PlcUnitComposition"); Property(step7,units+"PlcUnitSystemGroup","SafetyUnits","PlcSafetyUnitComposition");
        if(v20) Console.WriteLine("CAPABILITY PlcUnitSystemGroup.Name absent on V20 (ReadPlcSoftwareUnits reports unitGroup.name null there)"); else Property(step7,units+"PlcUnitSystemGroup","Name","String",false);
        Property(step7,units+"PlcUnitBase","Name","String",true); Property(step7,units+"PlcUnitBase","Author","String",true); Property(step7,units+"PlcUnitBase","NamespacePreset","String",true);
        Property(step7,units+"PlcUnitBase","Comment","MultilingualText",false); Property(step7,units+"PlcUnitBase","Relations","PlcUnitRelationComposition");
        Property(step7,units+"PlcUnitBase","BlockGroup","PlcBlockSystemGroup"); Property(step7,units+"PlcUnitBase","TypeGroup","PlcTypeSystemGroup"); Property(step7,units+"PlcUnitBase","TagTableGroup","PlcTagTableSystemGroup");
        Property(step7,units+"PlcUnitBase","ExternalSourceGroup","PlcExternalSourceSystemGroup"); Property(step7,units+"PlcUnitBase","PlcAlarmTextlistGroup","PlcAlarmTextlistGroup");
        check(T(step7,units+"PlcUnit").IsSubclassOf(T(step7,units+"PlcUnitBase")) && T(step7,units+"PlcSafetyUnit").IsSubclassOf(T(step7,units+"PlcUnitBase")),"PlcUnit and PlcSafetyUnit derive from PlcUnitBase");
        Method(step7,units+"PlcUnit","Delete",Type.EmptyTypes,"Void"); check(T(step7,units+"PlcSafetyUnit").GetMethod("Delete",Type.EmptyTypes)==null,"PlcSafetyUnit has no Delete (system generated)");
        Method(step7,units+"PlcUnitComposition","Create",new[]{text},"PlcUnit"); Method(step7,units+"PlcUnitComposition","Find",new[]{text},"PlcUnit");
        Method(step7,units+"PlcUnitComposition","CreateFrom",new[]{masterCopy},"PlcUnit"); Method(step7,units+"PlcUnitComposition","CreateFrom",new[]{masterCopy,masterCopyMode},"PlcUnit");
        Method(step7,units+"PlcSafetyUnitComposition","Find",new[]{text},"PlcSafetyUnit"); check(T(step7,units+"PlcSafetyUnitComposition").GetMethod("Create")==null,"PlcSafetyUnitComposition has no Create");
        Method(step7,units+"PlcUnitRelationComposition","Create",new[]{text,T(step7,units+"UnitRelationType")},"PlcUnitRelation"); Method(step7,units+"PlcUnitRelationComposition","Find",new[]{text},"PlcUnitRelation");
        Property(step7,units+"PlcUnitRelation","RelatedObject","String"); Property(step7,units+"PlcUnitRelation","RelationType","UnitRelationType",false); Method(step7,units+"PlcUnitRelation","Delete",Type.EmptyTypes,"Void");
        Enum(step7,units+"UnitRelationType","SoftwareUnit","NonUnitDB","TODB"); Enum(step7,units+"UnitAccessType","Published","Unpublished");
        Property(core,"Siemens.Engineering.MultilingualTextItem","Text","String",true); Property(core,"Siemens.Engineering.MultilingualTextItem","Language","Language");
        Property(step7,blocks+"PlcBlockSystemGroup","SystemBlockGroups","PlcSystemBlockGroupComposition"); Property(step7,"Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmTextlistGroup","PlcAlarmUserTextlists");
        Property(step7,"Siemens.Engineering.SW.ExternalSources.PlcExternalSourceSystemGroup","ExternalSources","PlcExternalSourceComposition");

        // ---- documents ----
        Property(step7,types+"PlcTypeGroup","Documents","PlcDocumentComposition"); Property(step7,sw+"PlcDocument","Name","String",false);
        Method(step7,sw+"PlcDocument","ExportAsDocuments",new[]{directory,text},"DocumentExportResult"); check(serviceProvider.IsAssignableFrom(T(step7,sw+"PlcDocument")),"PlcDocument is a service provider");
        Method(step7,sw+"PlcDocumentComposition","Find",new[]{text},"PlcDocument");
        Method(step7,sw+"PlcDocumentComposition","ImportFromDocuments",new[]{directory,text,T(step7,sw+"ImportDocumentOptions")},"DocumentImportResultForSplDocument");
        if(v20) { Console.WriteLine("CAPABILITY PlcDocumentComposition.CreateFrom absent on V20 (ManagePlcDocuments createFrom* for documents answers NotSupported there)"); check(T(step7,sw+"PlcDocumentComposition").GetMethod("CreateFrom",new[]{masterCopy})==null,"V20 PlcDocumentComposition.CreateFrom(MasterCopy) absent"); }
        else
        {
            Method(step7,sw+"PlcDocumentComposition","CreateFrom",new[]{masterCopy},"PlcDocument"); Method(step7,sw+"PlcDocumentComposition","CreateFrom",new[]{masterCopy,masterCopyMode},"PlcDocument");
            Method(step7,sw+"PlcDocumentComposition","CreateFrom",new[]{T(step7,types+"PlcDocumentLibraryTypeVersion")},"PlcDocument"); Method(step7,sw+"PlcDocumentComposition","CreateFrom",new[]{T(step7,types+"PlcDocumentLibraryTypeVersion"),updatePaths},"PlcDocument");
        }
        check(T(step7,types+"PlcDocumentLibraryTypeVersion").IsSubclassOf(T(core,"Siemens.Engineering.Library.Types.LibraryTypeVersion")),"PlcDocumentLibraryTypeVersion derives from LibraryTypeVersion");
        check(T(step7,types+"PlcTypeLibraryTypeVersion").IsSubclassOf(T(core,"Siemens.Engineering.Library.Types.LibraryTypeVersion")),"PlcTypeLibraryTypeVersion derives from LibraryTypeVersion");
        Method(step7,types+"PlcType","ExportAsDocuments",new[]{directory,text},"DocumentExportResult");
        Method(step7,types+"PlcTypeComposition","ImportFromDocuments",new[]{directory,text,T(step7,sw+"ImportDocumentOptions")},"DocumentImportResultForTypes");
        Method(step7,types+"PlcTypeComposition","CreateFrom",new[]{masterCopy},"PlcType"); Method(step7,types+"PlcTypeComposition","CreateFrom",new[]{masterCopy,masterCopyMode},"PlcType");
        Method(step7,types+"PlcTypeComposition","CreateFrom",new[]{T(step7,types+"PlcTypeLibraryTypeVersion")},"PlcType"); Method(step7,types+"PlcTypeComposition","CreateFrom",new[]{T(step7,types+"PlcTypeLibraryTypeVersion"),updatePaths},"PlcType");
        Method(step7,blocks+"PlcBlockComposition","ImportFromDocuments",new[]{directory,text,T(step7,sw+"ImportDocumentOptions")},"DocumentImportResultForBlocks");
        Property(step7,sw+"DocumentImportResult","State","DocumentResultState"); Property(step7,sw+"DocumentImportResult","Messages","DocumentResultMessageComposition");
        foreach(var r in new[]{"DocumentImportResultForBlocks","DocumentImportResultForTypes","DocumentImportResultForSplDocument"}) check(T(step7,sw+r).IsSubclassOf(T(step7,sw+"DocumentImportResult")),r+" derives from DocumentImportResult");
        Property(step7,sw+"DocumentImportResultForBlocks","ImportedPlcBlocks","PlcBlockAssociation"); Property(step7,sw+"DocumentImportResultForTypes","ImportedPlcTypes","PlcTypeAssociation"); Property(step7,sw+"DocumentImportResultForSplDocument","ImportedDocuments","PlcDocumentAssociation");
        Property(step7,sw+"DocumentResultMessage","Message","String",false);
        Property(step7,sw+"DocumentExportResult","State","DocumentResultState"); Property(step7,sw+"DocumentExportResult","Messages","DocumentResultMessageComposition");
        var exported=T(step7,sw+"DocumentExportResult").GetProperty("ExportedDocuments"); check(exported!=null && exported.PropertyType.IsGenericType && exported.PropertyType.GetGenericArguments()[0]==typeof(FileInfo),"DocumentExportResult.ExportedDocuments : IEnumerable<FileInfo>");
        Enum(step7,sw+"ImportDocumentOptions","None","Override","SkipInactiveCultures","ActivateInactiveCultures"); Enum(step7,sw+"DocumentResultState","Success","PartialSuccess","Failure");
        Enum(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyMode","ThrowIfExists","Rename","Replace"); Enum(core,"Siemens.Engineering.Library.Types.UpdatePathsMode","UpdatePathsInTarget","KeepExistingPathsInTarget","ThrowIfPathsConflict");

        // ---- checksums / fingerprints ----
        Service(sw+"PlcChecksumProvider"); Property(step7,sw+"PlcChecksumProvider","Software","String",false); Property(step7,sw+"PlcChecksumProvider","TextLists","String",false);
        Service(sw+"FingerprintProvider");
        var fingerprints=T(step7,sw+"FingerprintProvider").GetMethod("GetFingerprints",Type.EmptyTypes); check(fingerprints!=null && fingerprints.ReturnType.IsGenericType && fingerprints.ReturnType.GetGenericArguments()[0]==T(step7,sw+"Fingerprint"),"FingerprintProvider.GetFingerprints() -> IList<Fingerprint>");
        Property(step7,sw+"Fingerprint","Id","FingerprintId",false); Property(step7,sw+"Fingerprint","Value","String",false);
        Enum(step7,sw+"FingerprintId","Code","Comments","Interface","LibraryType","Texts","Alarms","Supervisions","TechnologyObject","Events","TextualInterface","Properties","ProgramCode");
        check(serviceProvider.IsAssignableFrom(T(step7,blocks+"PlcBlock")) && serviceProvider.IsAssignableFrom(T(step7,types+"PlcType")),"PlcBlock and PlcType are service providers (FingerprintProvider owners)");

        // ---- block write protection (V21) ----
        if(v20) { Console.WriteLine("CAPABILITY PlcBlockWriteProtectionProvider absent on V20 (ManagePlcBlockWriteProtection answers NotSupported there)"); check(step7.GetType(blocks+"PlcBlockWriteProtectionProvider")==null,"V20 has no PlcBlockWriteProtectionProvider"); }
        else
        {
            Service(blocks+"PlcBlockWriteProtectionProvider"); check(T(step7,blocks+"PlcBlockWriteProtectionProvider").IsSubclassOf(T(core,"Siemens.Engineering.AdvancedProtection.ProtectionProviderBase")),"PlcBlockWriteProtectionProvider derives from ProtectionProviderBase (GetInvalidPasswordCharacters)");
            Property(step7,blocks+"PlcBlockWriteProtectionProvider","IsDefined","Boolean",false); Property(step7,blocks+"PlcBlockWriteProtectionProvider","IsProtected","Boolean",false);
            Method(step7,blocks+"PlcBlockWriteProtectionProvider","Define",new[]{secure},"Void"); Method(step7,blocks+"PlcBlockWriteProtectionProvider","Protect",new[]{secure},"Void");
            Method(step7,blocks+"PlcBlockWriteProtectionProvider","Unprotect",new[]{secure},"Void"); Method(step7,blocks+"PlcBlockWriteProtectionProvider","Change",new[]{secure,secure},"Void");
        }

        // ---- project compilation settings ----
        if(v20)
        {
            Property(core,"Siemens.Engineering.Project","IsSimulationDuringBlockCompilationEnabled","Boolean",true); Property(core,"Siemens.Engineering.Project","IsVirtualPlcDuringBlockCompilationEnabled","Boolean",true);
            Console.WriteLine("CAPABILITY V20 exposes the compilation settings as Project properties (no PlcSimulationSettingsProvider / VirtualPlcSettingsProvider)");
            check(step7.GetType(sw+"PlcSimulationSettingsProvider")==null && step7.GetType(sw+"VirtualPlcSettingsProvider")==null,"V20 has no simulation / virtual PLC settings providers");
        }
        else
        {
            check(T(core,"Siemens.Engineering.Project").GetProperty("IsSimulationDuringBlockCompilationEnabled")==null,"V21 removed Project.IsSimulationDuringBlockCompilationEnabled (service provider instead)");
            Service(sw+"PlcSimulationSettingsProvider"); Property(step7,sw+"PlcSimulationSettingsProvider","IsSimulationDuringBlockCompilationEnabled","Boolean",true);
            Service(sw+"VirtualPlcSettingsProvider"); Property(step7,sw+"VirtualPlcSettingsProvider","IsVirtualPlcDuringBlockCompilationEnabled","Boolean",true);
            check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.Project")),"Project is a service provider (settings provider owner)");
        }

        // ---- channel linked tags / process image (V21) ----
        if(v20)
        {
            check(!serviceProvider.IsAssignableFrom(T(core,hw+"Channel")) && !serviceProvider.IsAssignableFrom(T(core,hw+"Address")),"V20 Channel and Address are not service providers (no per-channel / per-address services)");
            Console.WriteLine("CAPABILITY PlcTagProvider / ProcessImageProvider absent on V20 (linked tags answer null; V20 uses Address.AssignProcessImageToOrganizationBlock)");
            check(step7.GetType(sw+"PlcTagProvider")==null && step7.GetType(sw+"ProcessImageProvider")==null,"V20 has no PlcTagProvider / ProcessImageProvider");
            Method(core,hw+"Address","AssignProcessImageToOrganizationBlock",new[]{T(step7,blocks+"OB")},"Void");
        }
        else
        {
            check(serviceProvider.IsAssignableFrom(T(core,hw+"Channel")) && serviceProvider.IsAssignableFrom(T(core,hw+"Address")),"V21 Channel and Address are service providers (PlcTagProvider / ProcessImageProvider owners)");
            Service(sw+"PlcTagProvider");
            var linked=T(step7,sw+"PlcTagProvider").GetMethod("GetLinkedTags",Type.EmptyTypes); check(linked!=null && linked.ReturnType.IsGenericType && linked.ReturnType.GetGenericArguments()[0]==T(step7,"Siemens.Engineering.SW.Tags.PlcTag"),"PlcTagProvider.GetLinkedTags() -> IList<PlcTag>");
            Service(sw+"ProcessImageProvider"); Method(step7,sw+"ProcessImageProvider","AssignProcessImageToOrganizationBlock",new[]{T(step7,blocks+"OB")},"Void");
            check(T(core,hw+"Address").GetMethod("AssignProcessImageToOrganizationBlock")==null,"V21 removed Address.AssignProcessImageToOrganizationBlock (ProcessImageProvider instead)");
        }
        Property(step7,"Siemens.Engineering.SW.Tags.PlcTag","LogicalAddress","String"); Property(step7,"Siemens.Engineering.SW.Tags.PlcTag","DataTypeName","String");
    }
}

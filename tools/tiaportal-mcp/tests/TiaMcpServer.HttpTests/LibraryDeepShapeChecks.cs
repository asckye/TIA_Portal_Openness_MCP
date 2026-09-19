using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the deep library family (Portal.LibraryDeep.cs, the 2.7.31 extensions of ManageGlobalLibrary and
// ImportLibraryTypeDocuments), verified against the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base) PublicAPI.
internal static class LibraryDeepShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var wincc=Api("Siemens.Engineering.WinCC","Siemens.Engineering");
        var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering");
        Type T(string n,Assembly? a=null)=>(a??core).GetType(n,true)!;
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
        const string lib="Siemens.Engineering.Library.", types="Siemens.Engineering.Library.Types.", copies="Siemens.Engineering.Library.MasterCopies.", compare="Siemens.Engineering.Library.Compare.";
        var enumerable=typeof(System.Collections.Generic.IEnumerable<>);
        var selection=T(types+"ILibraryTypeOrFolderSelection"); var scope=T(types+"IUpdateProjectScope"); var ilibrary=T(lib+"ILibrary");
        var selectionSeq=enumerable.MakeGenericType(selection); var scopeSeq=enumerable.MakeGenericType(scope);
        var projectBase=T("Siemens.Engineering.ProjectBase"); var dir=typeof(DirectoryInfo); var file=typeof(FileInfo);

        // ILibrary and its implementations
        Method(lib+"ILibrary","FindType",new[]{typeof(Guid)},"LibraryType"); Method(lib+"ILibrary","FindVersion",new[]{typeof(Guid)},"LibraryTypeVersion");
        Method(lib+"ILibrary","UpdateCheck",new[]{projectBase,T(types+"UpdateCheckMode")},"UpdateCheckResult");
        Method(lib+"ILibrary","UpdateLibrary",new[]{selectionSeq,ilibrary,T(types+"ForceUpdateMode"),T(types+"DeleteUnusedVersionsMode"),T(types+"StructureConflictResolutionMode")},"Void");
        Method(lib+"ILibrary","UpdateProject",new[]{selectionSeq,scopeSeq},"Void");
        Method(lib+"ILibrary","HarmonizeProject",new[]{selectionSeq,scopeSeq,T(types+"HarmonizeProjectOptions")},"Void");
        Property(lib+"ILibrary","TypeFolder","LibraryTypeSystemFolder"); Property(lib+"ILibrary","MasterCopyFolder","MasterCopySystemFolder");
        Method(lib+"ProjectLibrary","CleanUpLibrary",new[]{selectionSeq,T(types+"CleanUpMode")},"Void");
        Method(lib+"ProjectLibrary","UpdateProject",new[]{selectionSeq,scopeSeq,T(types+"DeleteUnusedVersionsMode")},"Void");
        Method(lib+"GlobalLibrary","UpdateProject",new[]{selectionSeq,scopeSeq,T(types+"ForceUpdateMode"),T(types+"DeleteUnusedVersionsMode"),T(types+"StructureConflictResolutionMode")},"Void");
        Method(lib+"UserGlobalLibrary","CleanUpLibrary",new[]{selectionSeq},"Void");
        Method(lib+"UserGlobalLibrary","Archive",new[]{dir,typeof(string),T(lib+"LibraryArchivationMode")},"Void");
        check(ilibrary.IsAssignableFrom(T(lib+"ProjectLibrary")) && ilibrary.IsAssignableFrom(T(lib+"GlobalLibrary")),"ProjectLibrary and GlobalLibrary implement ILibrary");
        // 2.7.32 (2.7.31 real project): the label is synthesized because ProjectLibrary has no Name; only UserGlobalLibrary can be closed / saved.
        check(T(lib+"ProjectLibrary").GetProperty("Name")==null,"ProjectLibrary has no Name (label synthesized by LibraryRef)");
        Property(lib+"GlobalLibrary","Name","String");
        Method(lib+"UserGlobalLibrary","Close",Type.EmptyTypes,"Void"); Method(lib+"UserGlobalLibrary","Save",Type.EmptyTypes,"Void");
        check(T(lib+"SystemGlobalLibrary").GetMethod("Close",Type.EmptyTypes)==null && T(lib+"GlobalLibrary").GetMethod("Close",Type.EmptyTypes)==null,"SystemGlobalLibrary / GlobalLibrary have no Close (system libraries cannot be closed through the API)");
        check(T(lib+"SystemGlobalLibrary").IsSubclassOf(T(lib+"GlobalLibrary")) && T(lib+"UserGlobalLibrary").IsSubclassOf(T(lib+"GlobalLibrary")),"System/UserGlobalLibrary derive from GlobalLibrary");
        // GlobalLibrary header
        foreach(var name in new[]{"Author","Copyright","Family","Version","LastModifiedBy"}) Property(lib+"GlobalLibrary",name,"String",false);
        Property(lib+"GlobalLibrary","Comment","MultilingualText",false); Property(lib+"GlobalLibrary","Path","FileInfo",false); Property(lib+"GlobalLibrary","Size","Int64",false);
        Property(lib+"GlobalLibrary","CreationTime","DateTime",false); Property(lib+"GlobalLibrary","LastModified","DateTime",false);
        Property(lib+"GlobalLibrary","IsModified","Boolean",false); Property(lib+"GlobalLibrary","IsWriteProtected","Boolean",false);
        Property(lib+"GlobalLibrary","HistoryEntries","HistoryEntryComposition",false); Property(lib+"GlobalLibrary","UsedProducts","UsedProductComposition",false);
        Property("Siemens.Engineering.HistoryEntry","DateTime","DateTime"); Property("Siemens.Engineering.HistoryEntry","Text","String");
        Property("Siemens.Engineering.UsedProduct","Name","String"); Property("Siemens.Engineering.UsedProduct","Version","String");
        Property("Siemens.Engineering.MultilingualText","Items","MultilingualTextItemComposition"); Property("Siemens.Engineering.MultilingualTextItem","Text","String"); Property("Siemens.Engineering.MultilingualTextItem","Language","Language");
        // Global library composition / infos
        Method(lib+"GlobalLibraryComposition","GetGlobalLibraryInfos",Type.EmptyTypes); Method(lib+"GlobalLibraryComposition","Open",new[]{T(lib+"GlobalLibraryInfo")},"GlobalLibrary");
        Method(lib+"GlobalLibraryComposition","Open",new[]{file,T("Siemens.Engineering.OpenMode")},"UserGlobalLibrary"); Method(lib+"GlobalLibraryComposition","OpenWithUpgrade",new[]{file},"UserGlobalLibrary");
        Method(lib+"GlobalLibraryComposition","Retrieve",new[]{file,dir,T("Siemens.Engineering.OpenMode")},"UserGlobalLibrary"); Method(lib+"GlobalLibraryComposition","RetrieveWithUpgrade",new[]{file,dir,T("Siemens.Engineering.OpenMode")},"UserGlobalLibrary");
        check(T(lib+"GlobalLibraryComposition").GetMethods().Any(m=>m.Name=="Create" && m.IsGenericMethodDefinition),lib+"GlobalLibraryComposition.Create<T>(DirectoryInfo,String)");
        Property(lib+"GlobalLibraryInfo","Name","String"); Property(lib+"GlobalLibraryInfo","Path","FileInfo"); Property(lib+"GlobalLibraryInfo","IsOpen","Boolean"); Property(lib+"GlobalLibraryInfo","LibraryType","GlobalLibraryType");
        Enum(lib+"GlobalLibraryType","System","Corporate","User"); Enum(lib+"LibraryArchivationMode","None","Compressed","DiscardRestorableData","DiscardRestorableDataAndCompressed");
        // Types, versions, folders
        Property(types+"LibraryType","Name","String",true); Property(types+"LibraryType","Guid","Guid",false); Property(types+"LibraryType","Namespace","String",false);
        Property(types+"LibraryType","Author","String",false); Property(types+"LibraryType","Comment","MultilingualText",false);
        Property(types+"LibraryType","DoNotUse","Boolean",true); Property(types+"LibraryType","SetForUpdate","Boolean",true);
        Property(types+"LibraryType","MinimumTargetDeviceVersion","Version",false); Property(types+"LibraryType","Status","ConsistencyStatus",false); Property(types+"LibraryType","Versions","LibraryTypeVersionComposition",false);
        Method(types+"LibraryType","GetSupportedExportFormats",Type.EmptyTypes); Method(types+"LibraryType","CompareTo",new[]{T(types+"LibraryType")},"DetailedCompareResult"); Method(types+"LibraryType","Delete",Type.EmptyTypes,"Void");
        Method(types+"LibraryType","UpdateLibrary",new[]{ilibrary,T(types+"DeleteUnusedVersionsMode"),T(types+"StructureConflictResolutionMode"),T(types+"ForceUpdateMode")},"Void");
        Method(types+"LibraryType","UpdateProject",new[]{scope,T(types+"DeleteUnusedVersionsMode"),T(types+"StructureConflictResolutionMode"),T(types+"ForceUpdateMode")},"Void");
        Property(types+"LibraryTypeVersion","Guid","Guid",false); Property(types+"LibraryTypeVersion","IsDefault","Boolean",false); Property(types+"LibraryTypeVersion","State","LibraryTypeVersionState",false);
        Property(types+"LibraryTypeVersion","VersionNumber","Version",false); Property(types+"LibraryTypeVersion","ModifiedDate","DateTime",false); Property(types+"LibraryTypeVersion","OriginalLibrary","String",false);
        Property(types+"LibraryTypeVersion","TypeObject","LibraryType",false); Property(types+"LibraryTypeVersion","Dependencies","LibraryTypeVersionAssociation",false); Property(types+"LibraryTypeVersion","Dependents","LibraryTypeVersionAssociation",false);
        Property(types+"LibraryTypeVersion","MasterCopiesContainingInstances","MasterCopyAssociation",false); Method(types+"LibraryTypeVersion","CompareTo",new[]{T(types+"LibraryTypeVersion")},"DetailedCompareResult");
        Property(types+"LibraryTypeFolder","Name","String"); Property(types+"LibraryTypeFolder","Status","ConsistencyStatus",false); Property(types+"LibraryTypeFolder","Types","LibraryTypeComposition"); Property(types+"LibraryTypeFolder","Folders","LibraryTypeUserFolderComposition");
        check(T(types+"LibraryTypeFolder").IsAssignableFrom(T(types+"LibraryTypeSystemFolder")) && T(types+"LibraryTypeFolder").IsAssignableFrom(T(types+"LibraryTypeUserFolder")),"system/user type folders derive from LibraryTypeFolder");
        check(selection.IsAssignableFrom(T(types+"LibraryType")) && selection.IsAssignableFrom(T(types+"LibraryTypeFolder")),"LibraryType and LibraryTypeFolder are ILibraryTypeOrFolderSelection");
        bool v20=core.GetName().Version!.Major==20;
        check(scope.IsAssignableFrom(T("Siemens.Engineering.SW.PlcSoftware",step7)) && scope.IsAssignableFrom(T("Siemens.Engineering.Hmi.HmiTarget",wincc)),"PlcSoftware / HmiTarget are IUpdateProjectScope");
        check(v20 ? !scope.IsAssignableFrom(T("Siemens.Engineering.HmiUnified.HmiSoftware",unified)) : scope.IsAssignableFrom(T("Siemens.Engineering.HmiUnified.HmiSoftware",unified)),"Unified HmiSoftware is IUpdateProjectScope on V21 only");
        Method(types+"LibraryTypeComposition","CreateFromDocuments",new[]{dir,typeof(string),T(lib+"LibraryImportOptions")},"TypeCreateTransferResults");
        Method(types+"LibraryTypeComposition","CreateFromDocuments",new[]{dir,typeof(string),T("Siemens.Engineering.IEngineeringObject"),T(lib+"LibraryImportOptions")},"TypeCreateTransferResults");
        Method(types+"LibraryTypeVersionComposition","CreateFromDocuments",new[]{dir,typeof(string),T(types+"CreateOptions"),T(lib+"LibraryImportOptions")},"VersionCreateTransferResults");
        Method(types+"LibraryTypeVersionComposition","CreateFromDocuments",new[]{dir,typeof(string),T("Siemens.Engineering.IEngineeringObject"),T(types+"CreateOptions"),T(lib+"LibraryImportOptions")},"VersionCreateTransferResults");
        Property(lib+"TypeCreateTransferResults","CreatedType","LibraryType"); Property(lib+"TypeCreateTransferResults","Messages","TransferResultMessageComposition"); Property(lib+"TypeCreateTransferResults","TransferResultState","TransferResultState");
        Property(lib+"VersionCreateTransferResults","CreatedVersion","LibraryTypeVersion"); Property(lib+"VersionCreateTransferResults","Messages","TransferResultMessageComposition"); Property(lib+"VersionCreateTransferResults","TransferResultState","TransferResultState");
        Property(lib+"TransferResultMessage","Message","String");
        // Update check
        Property(types+"UpdateCheckResult","Messages","UpdateCheckResultMessageComposition"); Property(types+"UpdateCheckResultMessage","Description","String"); Property(types+"UpdateCheckResultMessage","MessageParts"); Property(types+"UpdateCheckResultMessage","Messages","UpdateCheckResultMessageComposition");
        // Master copies
        Property(copies+"MasterCopy","Name","String",true); Property(copies+"MasterCopy","Author","String",false); Property(copies+"MasterCopy","CreationDate","DateTime",false);
        Property(copies+"MasterCopy","ContentDescriptions","MasterCopyContentDescriptionComposition",false); Method(copies+"MasterCopy","CompareTo",new[]{T(copies+"MasterCopy")},"DetailedCompareResult");
        Property(copies+"MasterCopyContentDescription","ContentName","String"); Property(copies+"MasterCopyContentDescription","ContentType","Type");
        Property(copies+"MasterCopyFolder","MasterCopies","MasterCopyComposition"); Property(copies+"MasterCopyFolder","Folders","MasterCopyUserFolderComposition"); Property(copies+"MasterCopyFolder","Name","String");
        check(T(copies+"MasterCopyFolder").IsAssignableFrom(T(copies+"MasterCopyUserFolder")) && T(copies+"MasterCopyFolder").IsAssignableFrom(T(copies+"MasterCopySystemFolder")),"master copy user/system folders derive from MasterCopyFolder");
        // Detailed compare
        Property(compare+"DetailedCompareResult","Properties","DetailedCompareResultElementComposition");
        Property(compare+"DetailedCompareResultElement","Description","String"); Property(compare+"DetailedCompareResultElement","DetailCompareStatus","DetailCompareStatus");
        Property(compare+"DetailedCompareResultElement","LeftValue","Object"); Property(compare+"DetailedCompareResultElement","RightValue","Object");
        // Enums
        Enum(types+"UpdateCheckMode","ReportOutOfDateOnly","ReportOutOfDateAndUpToDate"); Enum(types+"ForceUpdateMode","SetOnlyHigherUpdatedVersionAsDefault","ForceSetAnyUpdatedVersionAsDefault","NoDefaultVersionChange");
        Enum(types+"DeleteUnusedVersionsMode","AutomaticallyDelete","DoNotDelete"); Enum(types+"StructureConflictResolutionMode","UpdateStructure","RetainStructure","CancelIfStructureConflicts");
        Enum(types+"HarmonizeProjectOptions","None","HarmonizeNames","HarmonizePaths"); Enum(types+"CleanUpMode","PreserveDefaultVersionOfUnusedTypes","DeleteUnusedTypes");
        Enum(types+"CreateOptions","None","Override"); Enum(lib+"LibraryImportOptions","None","SkipInactiveCultures","ActivateInactiveCultures");
        Enum(types+"ConsistencyStatus","None","Consistent","DefaultVersionInconsistent","DuplicateVersionInconsistent","NonDefaultVersionInstantiation","MultipleVersionsInstantiationInSameDevice");
        Enum(types+"LibraryTypeVersionState","InWork","Committed"); Enum(compare+"DetailCompareStatus","NotCompared","ObjectsDifferent","ObjectsIdentical"); Enum(lib+"TransferResultState","Success","Warning");
        // Engine surface
        var portal=server.GetType("TiaMcpServer.Siemens.Portal")!;
        foreach(var tool in new[]{"ReadLibraryOverview","ReadLibraryType","ManageLibraryType","CheckLibraryUpdates","SynchronizeLibrary","CompareLibraryObjects"}) check(portal.GetMethod(tool)!=null,"Portal."+tool+" present");
        check(portal.GetMethod("ManageGlobalLibrary")?.GetParameters().Any(p=>p.Name=="archiveMode")==true,"Portal.ManageGlobalLibrary carries archiveMode (infos/openInfo/archive actions)");
        check(portal.GetMethod("ImportLibraryTypeDocuments")?.GetParameters().Any(p=>p.Name=="createOptions")==true,"Portal.ImportLibraryTypeDocuments carries createOptions (version import)");
    }
}

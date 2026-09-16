using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private MasterCopy ExactMasterCopy(string libraryName,string path) {
            var parts=EngineeringGroupOperations.Parts(path);
            var folder=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
            return (MasterCopy)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(folder,"MasterCopies"),parts.Last()) ?? throw new InvalidOperationException("Exact master copy not found."));
        }
        public ResponseMessage ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",string destinationPath="",bool dryRun=true)
            =>RunHmiStepTool("ManageLibraryMasterCopy",meta=>{
                if(!new[]{"read","copy","compare","delete"}.Contains(action))throw new ArgumentException("Invalid master-copy action.");
                using var access=!dryRun&&(action=="copy"||action=="delete") ? AcquireHmiEditAccess() : null;
                var source=ExactMasterCopy(libraryName,sourcePath);meta["before"]=EngineeringObjectAddress.Read(source);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(action=="read")return "Master copy scalar properties read.";
                if(action=="compare") { var other=ExactMasterCopy(destinationLibraryName,destinationPath);meta["comparison"]=OfficialServiceAccess.Result(source.CompareTo(other));return "Native comparison returned; inspect differences and exclusions."; }
                MasterCopyFolder? folder=null;
                if(action=="copy") {
                    folder=(MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(destinationLibraryName),destinationPath,"MasterCopyFolder");
                    if(EngineeringGroupOperations.Find(folder.MasterCopies,source.Name)!=null)throw new InvalidOperationException("Destination master-copy name exists; overwrite refused.");
                }
                if(dryRun)return "Master-copy mutation preview; no changes.";
                meta["mayHaveChanged"]=true;
                if(action=="copy")meta["after"]=EngineeringObjectAddress.Read(folder!.MasterCopies.CreateFrom(source));
                else {
                    var parts=EngineeringGroupOperations.Parts(sourcePath);
                    var parent=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
                    source.Delete();meta["deleteReturned"]=true;
                    if(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(parent,"MasterCopies"),parts.Last())!=null)throw new InvalidOperationException("Master-copy deletion not verified.");
                    meta["absenceVerified"]=true;
                }
                return "Native master-copy operation returned; no project/library save or Portal close.";
            });
        public ResponseMessage ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",bool dryRun=true)
            =>RunHmiStepTool("ImportLibraryTypeDocuments",meta=>{
                var file=new FileInfo(filePath);if(!file.Exists)throw new FileNotFoundException("Library type native document not found.");
                var folder=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),folderPath,"TypeFolder");
                var types=EngineeringGroupOperations.Get(folder,"Types");
                var options=(LibraryImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions),typeof(LibraryImportOptions))!;
                var signature=new[]{typeof(DirectoryInfo),typeof(string),typeof(LibraryImportOptions)};
                if(types.GetType().GetMethod("CreateFromDocuments",signature)==null)throw new NotSupportedException("Native library type document import signature unavailable.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(dryRun)return "Library native document import preview; type/dependency semantics checked on execution.";
                using var access=AcquireHmiEditAccess();meta["mayHaveChanged"]=true;
                OfficialServiceAccess.AttachResult(meta,EngineeringGroupOperations.Call(types,"CreateFromDocuments",signature,file.Directory!,file.Name,options));
                return "Native type import returned. Inspect native results and dependencies; no automatic library/project save.";
            });
    }
}

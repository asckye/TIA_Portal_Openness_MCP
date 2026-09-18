using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering;
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
        public ResponseMessage ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",string typePath="",string createOptions="None",string targetSoftwarePath="",string targetGroupKind="",string targetGroupPath="",bool dryRun=true)
            =>RunHmiStepTool("ImportLibraryTypeDocuments",meta=>{
                var file=new FileInfo(filePath);if(!file.Exists)throw new FileNotFoundException("Library type native document not found.");
                LibraryDeepLogic.RequireOneOf(importOptions,LibraryDeepLogic.ImportOptions,"importOptions");LibraryDeepLogic.RequireOneOf(createOptions,LibraryDeepLogic.CreateOptions,"createOptions");
                if(targetGroupKind!="" && targetGroupKind!="blocks" && targetGroupKind!="types")throw new ArgumentException("targetGroupKind must be empty, blocks or types.");
                if((targetGroupKind=="")!=string.IsNullOrEmpty(targetSoftwarePath))throw new ArgumentException("targetSoftwarePath and targetGroupKind go together (STEP 7 documents need a PlcBlockGroup/PlcTypeGroup target environment).");
                var library=ExactOpenEngineeringLibrary(libraryName);
                var options=(LibraryImportOptions)System.Enum.Parse(typeof(LibraryImportOptions),importOptions);var create=(CreateOptions)System.Enum.Parse(typeof(CreateOptions),createOptions);
                string fileName=Path.GetFileNameWithoutExtension(file.Name);meta["fileName"]=fileName;meta["importOptions"]=importOptions;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                IEngineeringObject? environment=null;
                if(targetGroupKind!=""){
                    var plc=ExactPlcForEngineering(targetSoftwarePath,false);
                    environment=(IEngineeringObject)EngineeringGroupOperations.Group(targetGroupKind=="blocks"?(object)plc.BlockGroup:plc.TypeGroup,targetGroupPath);
                    meta["targetEnvironment"]=new JsonObject{["softwarePath"]=targetSoftwarePath,["groupKind"]=targetGroupKind,["groupPath"]=targetGroupPath,["type"]=environment.GetType().Name};
                }
                if(!string.IsNullOrEmpty(typePath)){
                    // Version import: LibraryTypeVersionComposition.CreateFromDocuments on an existing type ("Updating type from document").
                    var type=ExactLibraryType(library,typePath);meta["typePath"]=typePath;meta["createOptions"]=createOptions;meta["before"]=TypeRow(type,true);
                    if(dryRun)return "Library version document import preview; CreateOptions.None fails natively if an in-work version exists.";
                    using var access=AcquireHmiEditAccess();meta["mayHaveChanged"]=true;
                    VersionCreateTransferResults result=environment==null?type.Versions.CreateFromDocuments(file.Directory!,fileName,create,options):type.Versions.CreateFromDocuments(file.Directory!,fileName,environment,create,options);
                    meta["transferResultState"]=result.TransferResultState.ToString();
                    meta["messages"]=new JsonArray(EngineeringGroupOperations.Items(result.Messages).Cast<TransferResultMessage>().Select(m=>(JsonNode)m.Message).ToArray());
                    meta["createdVersion"]=result.CreatedVersion==null?null:VersionRow(result.CreatedVersion,false);meta["after"]=TypeRow(type,true);meta["apiCallSuccess"]=true;
                    if(result.TransferResultState!=TransferResultState.Success)meta["operationSuccess"]=false;
                    return "Native version import returned "+result.TransferResultState+"; created version and messages attached. No automatic library/project save.";
                }
                var folder=EngineeringLibraryFolder(library,folderPath,"TypeFolder");
                var types=(LibraryTypeComposition)EngineeringGroupOperations.Get(folder,"Types");
                if(dryRun)return "Library native document import preview; type/dependency semantics checked on execution.";
                using var edit=AcquireHmiEditAccess();meta["mayHaveChanged"]=true;
                TypeCreateTransferResults created=environment==null?types.CreateFromDocuments(file.Directory!,fileName,options):types.CreateFromDocuments(file.Directory!,fileName,environment,options);
                meta["transferResultState"]=created.TransferResultState.ToString();
                meta["messages"]=new JsonArray(EngineeringGroupOperations.Items(created.Messages).Cast<TransferResultMessage>().Select(m=>(JsonNode)m.Message).ToArray());
                meta["createdType"]=created.CreatedType==null?null:TypeRow(created.CreatedType,true);meta["apiCallSuccess"]=true;
                if(created.TransferResultState!=TransferResultState.Success)meta["operationSuccess"]=false;
                return "Native type import returned "+created.TransferResultState+"; created type (default version InWork) and messages attached. No automatic library/project save.";
            });
    }
}

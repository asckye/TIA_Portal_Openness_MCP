using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false, bool dryRun=true)
            => RunHmiStepTool("ManageGlobalLibrary", meta => {
                if (_portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (!new[] { "list", "create", "open", "retrieve", "save", "saveAs", "close" }.Contains(action)) throw new ArgumentException("Invalid global library action.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list") {
                    var items = EngineeringGroupOperations.Items(_portal.GlobalLibraries).ToArray();
                    meta["records"] = new JsonArray(items.Select(x => (JsonNode)EngineeringScalarProperties.Read(x)).ToArray());
                    meta["actualCount"] = items.Length; meta["truncated"] = false; meta["dataComplete"] = false;
                    return "Open global libraries, scalar scope only.";
                }
                var mode = (OpenMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(openMode), typeof(OpenMode))!;
                UserGlobalLibrary? library = null; FileInfo? file = null; DirectoryInfo? directory = null;
                if (action == "create" || action == "open" || action == "retrieve") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Expected library name required.");
                    if (EngineeringGroupOperations.Find(_portal.GlobalLibraries, libraryName) != null) throw new InvalidOperationException("Library with this name already open.");
                } else library = EngineeringGroupOperations.Find(_portal.GlobalLibraries, libraryName) as UserGlobalLibrary ?? throw new InvalidOperationException("Exact open user global library not found.");
                if (action == "open" || action == "retrieve") {
                    file = new FileInfo(filePath); if (!file.Exists) throw new FileNotFoundException("Library file not found.");
                }
                if (action == "create" || action == "retrieve" || action == "saveAs") {
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute new destination directory required.");
                    directory = new DirectoryInfo(destinationDirectory);
                    if (directory.Exists || File.Exists(directory.FullName)) throw new IOException("Destination already exists; overwrite/merge refused.");
                }
                if (upgrade && action != "open" && action != "retrieve") throw new ArgumentException("upgrade applies only to open/retrieve.");
                if (upgrade && action == "open" && mode != OpenMode.ReadWrite) throw new ArgumentException("OpenWithUpgrade requires explicit ReadWrite mode; native overload has no mode parameter.");
                if (dryRun) return "Library lifecycle preview. Opening/upgrading can write library files; close is explicit and never saves automatically.";
                meta["mayHaveChanged"] = true;
                switch (action) {
                    case "create": library = (UserGlobalLibrary)_portal.GlobalLibraries.Create<UserGlobalLibrary>(directory!, libraryName); break;
                    case "open": library = upgrade ? _portal.GlobalLibraries.OpenWithUpgrade(file!) : _portal.GlobalLibraries.Open(file!,mode); break;
                    case "retrieve": library = upgrade ? _portal.GlobalLibraries.RetrieveWithUpgrade(file!,directory!,mode) : _portal.GlobalLibraries.Retrieve(file!,directory!,mode); break;
                    case "save": library!.Save(); break;
                    case "saveAs": library!.SaveAs(directory!); break;
                    case "close": library!.Close(); meta["closed"] = true; return "Selected global library closed; project and Portal remain open. No implicit save.";
                }
                meta["after"] = EngineeringScalarProperties.Read(library!); meta["apiCallSuccess"] = true;
                if (library!.Name != libraryName) { meta["operationSuccess"] = false; meta["actualName"] = library.Name; return "Native operation completed but opened library name differs; handle retained, no automatic close or rename."; }
                return "Native global library operation completed. Project not saved, compiled or downloaded.";
            }, requiresProject:false);

        public ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
            => RunHmiStepTool("ManageLibraryFolder", meta => {
                if (folderKind != "types" && folderKind != "masterCopies") throw new ArgumentException("folderKind must be types/masterCopies.");
                if (!new[] { "read", "create", "rename", "delete" }.Contains(action)) throw new ArgumentException("Invalid action.");
                var parts = EngineeringGroupOperations.Parts(folderPath); // Root cannot be mutated.
                var library = ExactOpenEngineeringLibrary(libraryName);
                var parent = EngineeringLibraryFolder(library,string.Join("/",parts.Take(parts.Length-1)),folderKind=="types" ? "TypeFolder" : "MasterCopyFolder");
                var collection = EngineeringGroupOperations.Get(parent,"Folders");
                var target = EngineeringGroupOperations.Find(collection,parts.Last());
                if ((action=="create") == (target!=null)) throw new InvalidOperationException(action=="create" ? "Folder exists." : "Exact folder not found.");
                if (action=="delete" && (EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(target!,"Folders")).Any() || EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(target!,folderKind=="types" ? "Types" : "MasterCopies")).Any())) throw new InvalidOperationException("Only empty folders can be deleted.");
                if (action=="rename") {
                    if (EngineeringGroupOperations.Parts(newName).Length!=1) throw new ArgumentException("Single new name required.");
                    if (EngineeringGroupOperations.Find(collection,newName)!=null) throw new InvalidOperationException("Destination name exists.");
                }
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(target!=null) meta["before"]=EngineeringScalarProperties.Read(target);
                if(action=="read" || dryRun) return "Library folder read/preview; no mutation.";
                using var access=AcquireHmiEditAccess(); meta["mayHaveChanged"]=true;
                if(action=="create") target=EngineeringGroupOperations.Call(collection,"Create",new[]{typeof(string)},parts.Last());
                else if(action=="delete") {
                    EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(EngineeringGroupOperations.Find(collection,parts.Last())!=null) throw new InvalidOperationException("Folder remains after delete.");
                    meta["verifiedAbsent"]=true; return "Empty library folder deleted; no save.";
                } else EngineeringScalarProperties.Apply(target!,EngineeringScalarProperties.Prepare(target!.GetType(),new JsonObject{["Name"]=newName}),meta);
                meta["after"]=EngineeringScalarProperties.Read(target!);
                return "Library folder changed and read back; no save/compile/download.";
            });
    }
}

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
        public ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false,
            string archiveName="", string archiveMode="Compressed", bool dryRun=true)
            => RunHmiStepTool("ManageGlobalLibrary", meta => {
                if (_portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (!new[] { "list", "infos", "create", "open", "openInfo", "retrieve", "save", "saveAs", "close", "archive" }.Contains(action)) throw new ArgumentException("action must be one of: list/infos/create/open/openInfo/retrieve/save/saveAs/close/archive (case-sensitive).");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                GlobalLibraryComposition libraries = _portal.GlobalLibraries;
                if (action == "list") {
                    var items = EngineeringGroupOperations.Items(libraries).ToArray();
                    meta["records"] = new JsonArray(items.Select(x => (JsonNode)EngineeringScalarProperties.Read(x)).ToArray());
                    meta["actualCount"] = items.Length; meta["truncated"] = false; meta["dataComplete"] = false;
                    return "Open global libraries, scalar scope only.";
                }
                if (action == "infos") {
                    // GetGlobalLibraryInfos lists the libraries known to this Portal (system, corporate, recently used user libraries), open or not.
                    var infos = libraries.GetGlobalLibraryInfos().ToArray();
                    meta["records"] = new JsonArray(infos.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["path"] = i.Path?.FullName, ["libraryType"] = i.LibraryType.ToString(), ["isOpen"] = i.IsOpen }).ToArray());
                    meta["actualCount"] = infos.Length; meta["truncated"] = false; meta["dataComplete"] = true; meta["apiCallSuccess"] = true;
                    return "Global library infos read (GetGlobalLibraryInfos); nothing opened.";
                }
                if (action == "openInfo") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Exact library name from action=infos required.");
                    var info = libraries.GetGlobalLibraryInfos().FirstOrDefault(i => string.Equals(i.Name, libraryName, StringComparison.Ordinal)) ?? throw new PortalException(PortalErrorCode.NotFound, "No global library info named " + libraryName + " (see action=infos).");
                    meta["info"] = new JsonObject { ["name"] = info.Name, ["path"] = info.Path?.FullName, ["libraryType"] = info.LibraryType.ToString(), ["isOpen"] = info.IsOpen };
                    if (info.IsOpen) throw new InvalidOperationException("Library is already open.");
                    if (dryRun) return "Open-by-info preview; nothing opened (system/corporate libraries open read-only).";
                    meta["mayHaveChanged"] = true;
                    var opened = libraries.Open(info);
                    meta["after"] = EngineeringScalarProperties.Read(opened); meta["libraryClass"] = opened.GetType().Name; meta["apiCallSuccess"] = true;
                    meta["closableThroughApi"] = opened is UserGlobalLibrary; meta["hasTypeFolder"] = ((ILibrary)opened).TypeFolder != null;
                    return opened is UserGlobalLibrary
                        ? "Global library opened from its info entry (GlobalLibraryComposition.Open(GlobalLibraryInfo)). Close is explicit."
                        : "Global library opened from its info entry as " + opened.GetType().Name + "; the Openness API has Close only on UserGlobalLibrary, so it stays open until TIA closes it.";
                }
                if (action == "archive") {
                    LibraryDeepLogic.RequireOneOf(archiveMode, LibraryDeepLogic.ArchivationModes, "archiveMode"); LibraryDeepLogic.ValidateArchiveName(archiveName);
                    var target = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                    var archiveDirectory = new DirectoryInfo(destinationDirectory);
                    meta["library"] = EngineeringScalarProperties.Read(target); meta["archiveMode"] = archiveMode; meta["archiveName"] = archiveName; meta["destinationDirectory"] = archiveDirectory.FullName;
                    if (target.IsModified) throw new InvalidOperationException("Library has unsaved changes; TIA refuses Archive until it is saved (action=save first).");
                    if (dryRun) return "Archive preview; nothing written (None/DiscardRestorableData produce a folder that cannot be retrieved through the API; Compressed modes produce a .zalXX file).";
                    meta["mayHaveChanged"] = true;
                    var before = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Select(f => f.Name).ToArray() : Array.Empty<string>();
                    target.Archive(archiveDirectory, archiveName, (LibraryArchivationMode)System.Enum.Parse(typeof(LibraryArchivationMode), archiveMode));
                    var created = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Where(f => !before.Contains(f.Name)).Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["bytes"] = f is FileInfo fi ? fi.Length : (long?)null }).ToArray() : Array.Empty<JsonNode>();
                    meta["createdEntries"] = new JsonArray(created); meta["apiCallSuccess"] = true;
                    if (created.Length == 0) { meta["operationSuccess"] = false; return "Archive returned but no new entry appeared in the destination directory; inspect the directory before retrying."; }
                    return "User global library archived; new directory entries listed. The library stays open at its original location.";
                }
                // 2.7.46: an empty openMode (create / save / close do not open anything) only produced "要在此字符串中进行分析，必须指定有效信息".
                if (string.IsNullOrWhiteSpace(openMode)) openMode = "ReadOnly";
                if (!System.Enum.GetNames(typeof(OpenMode)).Contains(openMode)) throw new ArgumentException("openMode must be one of: " + string.Join("/", System.Enum.GetNames(typeof(OpenMode))) + ".");
                var mode = (OpenMode)System.Enum.Parse(typeof(OpenMode), openMode);
                UserGlobalLibrary? library = null; FileInfo? file = null; DirectoryInfo? directory = null;
                if (action == "create" || action == "open" || action == "retrieve") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Expected library name required.");
                    if (EngineeringGroupOperations.Find(libraries, libraryName) != null) throw new InvalidOperationException("Library with this name already open.");
                } else library = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
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
                    case "create": library = (UserGlobalLibrary)libraries.Create<UserGlobalLibrary>(directory!, libraryName); break;
                    case "open": library = upgrade ? libraries.OpenWithUpgrade(file!) : libraries.Open(file!,mode); break;
                    case "retrieve": library = upgrade ? libraries.RetrieveWithUpgrade(file!,directory!,mode) : libraries.Retrieve(file!,directory!,mode); break;
                    case "save": library!.Save(); break;
                    case "saveAs": library!.SaveAs(directory!); break;
                    case "close": library!.Close(); meta["closed"] = true; return "Selected global library closed; project and Portal remain open. No implicit save.";
                }
                meta["after"] = EngineeringScalarProperties.Read(library!); meta["apiCallSuccess"] = true;
                if (library!.Name != libraryName) { meta["operationSuccess"] = false; meta["actualName"] = library.Name; return "Native operation completed but opened library name differs; handle retained, no automatic close or rename."; }
                return "Native global library operation completed. Project not saved, compiled or downloaded.";
            }, requiresProject:false);

        // Close / Save / SaveAs / Archive exist on UserGlobalLibrary only; a system library opened through Open(GlobalLibraryInfo) is
        // found but cannot be closed through the API (2.7.31 real project) - say so instead of "not found".
        private static UserGlobalLibrary ExactOpenUserGlobalLibrary(GlobalLibraryComposition libraries, string libraryName, string action)
        {
            var found = EngineeringGroupOperations.Find(libraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found (action=list shows the open ones).");
            return found as UserGlobalLibrary ?? throw new NotSupportedException(LibraryDeepLogic.UserGlobalLibraryOnlyMessage(action, libraryName, found.GetType().Name));
        }
        public ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
            => RunHmiStepTool("ManageLibraryFolder", meta => {
                if (folderKind != "types" && folderKind != "masterCopies") throw new ArgumentException("folderKind must be types/masterCopies.");
                if (!new[] { "read", "create", "rename", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/create/rename/delete (case-sensitive).");
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

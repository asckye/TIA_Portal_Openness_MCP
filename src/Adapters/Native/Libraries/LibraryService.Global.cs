using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Engineering;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
#if LIBRARY_COMPARE
using Siemens.Engineering.Library.Compare;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Libraries;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public HardwareAddressingReply ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="",
                string openMode="ReadOnly", bool upgrade=false,
            string archiveName="", string archiveMode="Compressed", bool dryRun=true)
            => RunEngineeringServicesStep("ManageGlobalLibrary", meta => {
                if (portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (!new[] { "list", "infos", "create", "open", "openInfo", "retrieve", "save", "saveAs", "close", "archive" }.Contains(action))
                    throw new ArgumentException("action must be one of: list/infos/create/open/openInfo/retrieve/save/saveAs/close/archive (case-sensitive).");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                GlobalLibraryComposition libraries = portal.GlobalLibraries;
                if (action == "list") {
                    var items = PlcGroupOperations.Items(libraries).ToArray();
                    meta["records"] = new List<object?>(items.Select(x => (object)HardwareScalarEvidence.Read(x)).ToArray());
                    meta["actualCount"] = items.Length; meta["truncated"] = false; meta["dataComplete"] = false;
                    return "Open global libraries, scalar scope only.";
                }
                if (action == "infos") {
                    // GetGlobalLibraryInfos lists the libraries known to this Portal (system, corporate, recently used user libraries), open or not.
                    var infos = libraries.GetGlobalLibraryInfos().ToArray();
                    meta["records"] = new List<object
                        ?>(infos
                            .Select(i => (object)new Dictionary<string, object?> {
                                ["name"] = i.Name, ["path"] = i.Path?.FullName, ["libraryType"] = i.LibraryType.ToString(), ["isOpen"] = i.IsOpen })
                            .ToArray());
                    meta["actualCount"] = infos.Length; meta["truncated"] = false; meta["dataComplete"] = true; meta["apiCallSuccess"] = true;
                    return "Global library infos read (GetGlobalLibraryInfos); nothing opened.";
                }
                if (action == "openInfo") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Exact library name from action=infos required.");
                    var info = libraries.GetGlobalLibraryInfos().FirstOrDefault(i => string.Equals(i.Name, libraryName, StringComparison.Ordinal))
                        ?? throw new PlcSoftwareException("NotFound", "No global library info named " + libraryName + " (see action=infos).");
                    meta["info"] = new Dictionary<string, object?> {
                        ["name"] = info.Name, ["path"] = info.Path?.FullName, ["libraryType"] = info.LibraryType.ToString(), ["isOpen"] = info.IsOpen };
                    if (info.IsOpen) throw new InvalidOperationException("Library is already open.");
                    if (dryRun) return "Open-by-info preview; nothing opened (system/corporate libraries open read-only).";
                    meta["mayHaveChanged"] = true;
                    var opened = libraries.Open(info);
                    meta["after"] = HardwareScalarEvidence.Read(opened); meta["libraryClass"] = opened.GetType().Name; meta["apiCallSuccess"] = true;
                    meta["closableThroughApi"] = opened is UserGlobalLibrary; meta["hasTypeFolder"] = ((ILibrary)opened).TypeFolder != null;
                    return opened is UserGlobalLibrary
                        ? "Global library opened from its info entry (GlobalLibraryComposition.Open(GlobalLibraryInfo)). Close is explicit."
                        : "Global library opened from its info entry as " + opened.GetType().Name
                            + "; the Openness API has Close only on UserGlobalLibrary, so it stays open until TIA closes it.";
                }
#if LIBRARY_V151
                if (action == "archive") {
                    LibraryPolicy.RequireOneOf(archiveMode, LibraryPolicy.ArchivationModes, "archiveMode"); LibraryPolicy.ValidateArchiveName(archiveName);
                    var target = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                    var archiveDirectory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                    meta["library"] = HardwareScalarEvidence.Read(target); meta["archiveMode"] = archiveMode; meta["archiveName"] = archiveName;
                    meta["destinationDirectory"] = archiveDirectory.FullName;
                    if (target.IsModified)
                        throw new InvalidOperationException("Library has unsaved changes; TIA refuses Archive until it is saved (action=save first).");
                    if (dryRun)
                        return "Archive preview; nothing written (None/DiscardRestorableData produce a folder that cannot be retrieved through the API; " +
                        "Compressed modes produce a .zalXX file).";
                    meta["mayHaveChanged"] = true;
                    var before = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Select(f => f.Name).ToArray() : Array.Empty<string>();
                    target.Archive(archiveDirectory, archiveName, (LibraryArchivationMode)System.Enum.Parse(typeof(LibraryArchivationMode), archiveMode));
                    var created = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Where(f => !before.Contains(f.Name))
                        .Select(f => (object)new Dictionary<string, object?> { ["name"] = f.Name, ["bytes"] = f is FileInfo fi ? fi.Length : (long?)null })
                        .ToArray() : Array.Empty<object>();
                    meta["createdEntries"] = new List<object?>(created); meta["apiCallSuccess"] = true;
                    if (created.Length == 0) {
                        meta["operationSuccess"] = false;
                        return "Archive returned but no new entry appeared in the destination directory; inspect the directory before retrying.";
                    }
                    return "User global library archived; new directory entries listed. The library stays open at its original location.";
                }
#endif
                // An empty openMode defaults to ReadOnly before enum parsing, including actions that do not open a library.
                if (string.IsNullOrWhiteSpace(openMode)) openMode = "ReadOnly";
                if (!System.Enum.GetNames(typeof(OpenMode)).Contains(openMode))
                    throw new ArgumentException("openMode must be one of: " + string.Join("/", System.Enum.GetNames(typeof(OpenMode))) + ".");
                var mode = (OpenMode)System.Enum.Parse(typeof(OpenMode), openMode);
                UserGlobalLibrary? library = null; FileInfo? file = null; DirectoryInfo? directory = null;
                if (action == "create" || action == "open" || action == "retrieve") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Expected library name required.");
                    if (PlcGroupOperations.Find(libraries, libraryName) != null) throw new InvalidOperationException("Library with this name already open.");
                } else library = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
                if (action == "open" || action == "retrieve") {
                    file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (!file.Exists)
                        throw new FileNotFoundException("Library file not found.");
                }
                if (action == "create" || action == "retrieve" || action == "saveAs") {
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute new destination directory required.");
                    directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                    if (directory.Exists || File.Exists(directory.FullName)) throw new IOException("Destination already exists; overwrite/merge refused.");
                }
                if (upgrade && action != "open" && action != "retrieve") throw new ArgumentException("upgrade applies only to open/retrieve.");
                if (upgrade && action == "open" && mode != OpenMode.ReadWrite)
                    throw new ArgumentException("OpenWithUpgrade requires explicit ReadWrite mode; native overload has no mode parameter.");
                if (dryRun) return "Library lifecycle preview. Opening/upgrading can write library files; close is explicit and never saves automatically.";
                meta["mayHaveChanged"] = true;
                switch (action) {
                    case "create": library = (UserGlobalLibrary)libraries.Create<UserGlobalLibrary>(directory!, libraryName); break;
                    case "open": library = upgrade ? libraries.OpenWithUpgrade(file!) : libraries.Open(file!,mode); break;
#if LIBRARY_V151
                    case "retrieve": library = upgrade ? libraries.RetrieveWithUpgrade(file!,directory!,mode) : libraries.Retrieve(file!,directory!,mode);
                    break;
#endif
                    case "save": library!.Save(); break;
#if LIBRARY_V151
                    case "saveAs": library!.SaveAs(directory!); break;
#endif
                    case "close": library!.Close(); meta["closed"] = true;
                    return "Selected global library closed; project and Portal remain open. No implicit save.";
                }
                meta["after"] = HardwareScalarEvidence.Read(library!); meta["apiCallSuccess"] = true;
                if (library!.Name != libraryName) {
                    meta["operationSuccess"] = false; meta["actualName"] = library.Name;
                    return "Native operation completed but opened library name differs; handle retained, no automatic close or rename.";
                }
                return "Native global library operation completed. Project not saved, compiled or downloaded.";
            }, requiresProject:false);

        // Close / Save / SaveAs / Archive exist on UserGlobalLibrary only; a system library opened through Open(GlobalLibraryInfo) is
        // found but cannot be closed through the API (observed on TIA V21) - say so instead of "not found".
        private static UserGlobalLibrary ExactOpenUserGlobalLibrary(GlobalLibraryComposition libraries, string libraryName, string action)
        {
            var found = PlcGroupOperations.Find(libraries, libraryName)
                ?? throw new InvalidOperationException("Exact open global library not found (action=list shows the open ones).");
            return found as UserGlobalLibrary
                ?? throw new NotSupportedException(LibraryPolicy.UserGlobalLibraryOnlyMessage(action, libraryName, found.GetType().Name));
        }
        public HardwareAddressingReply ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="",
                bool dryRun=true)
            => RunEngineeringServicesStep("ManageLibraryFolder", meta => {
                if (folderKind != "types" && folderKind != "masterCopies") throw new ArgumentException("folderKind must be types/masterCopies.");
                if (!new[] { "read", "create", "rename", "delete" }.Contains(action))
                    throw new ArgumentException("action must be one of: read/create/rename/delete (case-sensitive).");
                var parts = PlcGroupOperations.Parts(folderPath); // Root cannot be mutated.
                var library = ExactOpenEngineeringLibrary(libraryName);
                var parent =
                    EngineeringLibraryFolder(library,string.Join("/",parts.Take(parts.Length-1)),folderKind=="types" ? "TypeFolder" : "MasterCopyFolder");
                var collection = PlcGroupOperations.Get(parent,"Folders");
                var target = PlcGroupOperations.Find(collection,parts.Last());
                if ((action=="create") == (target!=null)) throw new InvalidOperationException(action=="create" ? "Folder exists." : "Exact folder not found.");
                if (action=="delete"
                        && (PlcGroupOperations.Items(PlcGroupOperations.Get(target!,"Folders")).Any() || PlcGroupOperations
                            .Items(PlcGroupOperations.Get(target!,folderKind=="types" ? "Types" : "MasterCopies")).Any()))
                    throw new InvalidOperationException("Only empty folders can be deleted.");
                if (action=="rename") {
                    if (PlcGroupOperations.Parts(newName).Length!=1) throw new ArgumentException("Single new name required.");
                    if (PlcGroupOperations.Find(collection,newName)!=null) throw new InvalidOperationException("Destination name exists.");
                }
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(target!=null) meta["before"]=HardwareScalarEvidence.Read(target);
                if(action=="read" || dryRun) return "Library folder read/preview; no mutation.";
                using var access=HardwareEditAccess(); meta["mayHaveChanged"]=true;
                if(action=="create") target=PlcGroupOperations.Call(collection,"Create",new[]{typeof(string)},parts.Last());
                else if(action=="delete") {
                    PlcGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(PlcGroupOperations.Find(collection,parts.Last())!=null) throw new InvalidOperationException("Folder remains after delete.");
                    meta["verifiedAbsent"]=true; return "Empty library folder deleted; no save.";
                } else
                    LibraryApply(target!,
                        LibraryPrepare(target!.GetType(),new Dictionary<string, HardwareScalar>{["Name"]=new HardwareScalar { Kind = "string", Text = newName }
                        }),
                        meta);
                meta["after"]=HardwareScalarEvidence.Read(target!);
                return "Library folder changed and read back; no save/compile/download.";
            });

        public GlobalLibraryProbeEvidence ProbeGlobalLibrary(string libraryPath, int maxItems = 500)
        {
            var warnings = new List<string>();
            var raw = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath
            };

            try
            {
                if (portal == null)
                {
                    return new GlobalLibraryProbeEvidence
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "TIA Portal is not connected. Call ConnectPortal first.",
                        Warnings = new[] { "This is a read-only probe and does not import library content." },
                        Raw = raw
                    };
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return new GlobalLibraryProbeEvidence
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "Global library .al file not found.",
                        Warnings = new[] { "Pass either the .al21 file path or its containing folder." },
                        Raw = raw
                    };
                }

                var globalLibraries = TryGetPropertyValue(portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return new GlobalLibraryProbeEvidence
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = "TiaPortal.GlobalLibraries property not found.",
                        Warnings = new[] { "Installed Openness API may not expose global library access through this build." },
                        Raw = raw
                    };
                }

                // Reuse a library already opened by the user or ManageGlobalLibrary and leave it open afterwards.
                bool wasAlreadyOpen = false;
                object? library = FindOpenGlobalLibraryByFile(globalLibraries, resolved!);
                string? openError = null;
                if (library != null) wasAlreadyOpen = true; else library = TryOpenGlobalLibrary(globalLibraries, resolved!, out openError);
                if (library == null)
                {
                    return new GlobalLibraryProbeEvidence
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = openError ?? "Failed to open global library.",
                        Warnings = new[] { "No write operation was attempted." },
                        Raw = raw
                    };
                }

                var memberList = LibraryReflectionValues.DescribeMembers(library, 300)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(Math.Max(10, Math.Min(1000, maxItems)))
                    .ToList();
                var masterCopies =
                    ListLibraryNamesByHints(library, Math.Max(1, maxItems), "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups",
                        "Folders");
                var types = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Types", "TypeFolder", "TypeFolders", "LibraryTypes", "Folders");
                var folders = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Folders", "Groups", "MasterCopyFolders", "TypeFolders");

                raw["libraryType"] = library.GetType().FullName ?? library.GetType().Name;
                raw["memberCount"] = memberList.Count;
                raw["masterCopyCount"] = masterCopies.Count;
                raw["typeCount"] = types.Count;
                raw["folderCount"] = folders.Count;

                if (!wasAlreadyOpen) TryCloseOrDispose(library); else warnings.Add("Library was already open; it stays open.");

                warnings.Add("This probe only opens and lists library metadata; it does not import master copies or library types into a project.");
                if (masterCopies.Count == 0 && types.Count == 0)
                {
                    warnings
                        .Add("No master copies/types were listed through public/reflection access; use DescribeObject/DescribeObjectProperty for deeper API " +
                        "discovery.");
                }

                return new GlobalLibraryProbeEvidence
                {
                    Ok = true,
                    Message = "Global library read-only probe completed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = resolved,
                    LibraryType = library.GetType().FullName ?? library.GetType().Name,
                    Members = memberList,
                    MasterCopies = masterCopies,
                    Types = types,
                    Folders = folders,
                    Warnings = warnings,
                    Raw = raw
                };
            }
            catch (Exception ex)
            {
                raw["error"] = ex.ToString();
                return new GlobalLibraryProbeEvidence
                {
                    Ok = false,
                    Message = ex.Message,
                    LibraryPath = libraryPath,
                    Error = ex.ToString(),
                    Warnings = warnings,
                    Raw = raw
                };
            }
        }

        private static string? ResolveGlobalLibraryFile(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) return null;
            var p = libraryPath.Trim().Trim('"');
            if (File.Exists(p)) return p;
            if (!Directory.Exists(p)) return null;

            var direct = Directory.EnumerateFiles(p, "*.al*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
            if (direct != null) return direct;

            return Directory.EnumerateFiles(p, "*.al*", SearchOption.AllDirectories)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
        }

        private static object? FindOpenGlobalLibraryByFile(object globalLibraries, string libraryFile)
        {
            try
            {
                if (globalLibraries is not IEnumerable open) return null;
                foreach (var candidate in open)
                {
                    var path = TryGetPropertyValue(candidate, "Path");
                    var full = path is FileInfo fi ? fi.FullName : path?.ToString();
                    if (!string.IsNullOrEmpty(full) && string
                            .Equals(Path.GetFullPath(full!), Path.GetFullPath(libraryFile), StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            catch { /* swallow(probe-optional): If open-library enumeration is unavailable, retain the existing explicit open attempt. */ }
            return null;
        }

        private static object? TryOpenGlobalLibrary(object globalLibraries, string libraryFile, out string? error)
        {
            error = null;
            var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(libraryFile));
            try
            {
                var methods = globalLibraries.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Open", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(FileInfo))
                            return m.Invoke(globalLibraries, new object[] { fi });

                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                            return m.Invoke(globalLibraries, new object[] { libraryFile });

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(FileInfo))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)fi, arg2 });
                        }

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)libraryFile, arg2 });
                        }
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        error = $"{m.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    }
                    catch (Exception ex)
                    {
                        error = $"{m.Name}: {ex.GetType().FullName}: {ex.Message}";
                    }
                }

                error ??= "No supported GlobalLibraries.Open overload accepted FileInfo/string path.";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return null;
            }
        }

        private List<string> ListLibraryNamesByHints(object root, int limit, params string[] propertyHints)
        {
            var result = new List<string>();
            var seen = new HashSet<object>();

            void Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 8 || result.Count >= limit) return;
                if (!seen.Add(node)) return;

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null || result.Count >= limit) break;
                            var name = LibraryReflectionValues.TryGetName(item);
                            var nextPath = string.IsNullOrWhiteSpace(path)
                                ? (name ?? item.ToString() ?? "")
                                : path + "/" + (name ?? item.ToString() ?? "");
                            if (!string.IsNullOrWhiteSpace(name) && !result.Contains(nextPath, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Add(nextPath);
                            }
                            Visit(item, nextPath, depth + 1);
                        }
                    }
                    else
                    {
                        Visit(value, path, depth + 1);
                    }
                }
            }

            Visit(root, "", 0);
            return result;
        }

        private object? FindLibraryObjectByPathOrName(object root, string wantedPathOrName, List<string> attempts, params string[] propertyHints)
        {
            var wanted = (wantedPathOrName ?? string.Empty).Trim();
            var wantedLeaf = LastPathSegment(wanted);
            var seen = new HashSet<object>(EngineeringPlcSession.ReferenceEqualityComparer);

            object? Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 10) return null;
                if (!seen.Add(node)) return null;

                var name = LibraryReflectionValues.TryGetName(node) ?? TryGetPropertyValue(node, "Name")?.ToString() ?? string.Empty;
                var nodePath = string.IsNullOrWhiteSpace(path)
                    ? name
                    : string.IsNullOrWhiteSpace(name) ? path : path + "/" + name;

                if (!string.IsNullOrWhiteSpace(name) &&
                    (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nodePath, wanted, StringComparison.OrdinalIgnoreCase) ||
                     nodePath.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, wantedLeaf, StringComparison.OrdinalIgnoreCase)))
                {
                    attempts.Add("Found MasterCopy candidate: " + nodePath + " type=" + (node.GetType().FullName ?? node.GetType().Name));
                    return node;
                }

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;
                    attempts.Add($"Scan {node.GetType().Name}.{hint}: {value.GetType().FullName}");

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var child in enumerable)
                        {
                            var found = Visit(child, nodePath, depth + 1);
                            if (found != null) return found;
                        }
                    }
                    else
                    {
                        var found = Visit(value, nodePath, depth + 1);
                        if (found != null) return found;
                    }
                }

                return null;
            }

            return Visit(root, "", 0);
        }

        private List<string> ListNamedChildren(object collection, int limit)
        {
            var result = new List<string>();
            if (collection is not IEnumerable enumerable || collection is string)
                return result;

            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var name = LibraryReflectionValues.TryGetName(item) ?? TryGetPropertyValue(item, "Name")?.ToString() ?? item.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(name);
                if (result.Count >= Math.Max(1, limit)) break;
            }

            return result;
        }


        private static void TryCloseOrDispose(object obj)
        {
            try
            {
                var close = obj.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                close?.Invoke(obj, null);
            }
            catch { /* swallow(cleanup): Library cleanup must not replace the completed probe or import result. */ }

            try
            {
                if (obj is IDisposable d) d.Dispose();
            }
            catch { /* swallow(cleanup): Library cleanup must not replace the completed probe or import result. */ }
        }
    }
}

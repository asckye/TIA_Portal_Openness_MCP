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
        public HardwareAddressingReply ManageLibraryTypeVersion(string typePath, string version, string action, string libraryName = "",
            string newVersion = "", string dependenciesMode = "", string author = "", string comment = "", string targetSoftwarePath = "", bool dryRun = true)
            => RunEngineeringServicesStep("ManageLibraryTypeVersion", meta => {
                if (!new[] { "read", "edit", "release", "setDefault", "deleteVersion", "updateInstances", "discard", "findInstances" }.Contains(action))
                    throw new ArgumentException("action must be one of: read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances " +
                    "(case-sensitive).");
                bool writing = action != "read" && action != "findInstances" && !dryRun;
                using var access = writing && action != "edit" && action != "discard" ? HardwareEditAccess() : null;
                var library = ExactOpenEngineeringLibrary(libraryName);
                if ((action == "edit" || action == "discard") && library is not ProjectLibrary)
                    throw new NotSupportedException("Library version edit/discard is supported only in the project library.");
                var parts = PlcGroupOperations.Parts(typePath);
                var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
                var type =
                    (LibraryType)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Types"), parts.Last())
                        ?? throw new InvalidOperationException("Exact library type not found."));
                var matches = type.Versions.Where(v => v.VersionNumber.ToString() == version).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one matching version, found " + matches.Length);
                var selected = matches[0];
                meta["before"] = HardwareScalarEvidence.Read(selected); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["typePath"] = typePath; meta["libraryName"] = libraryName; meta["action"] = action;
#if LIBRARY_V20
                LibraryTypeInstanceInfo? editInstance = null;
                CreateOrReleaseDependenciesMode mode = default;
#endif
                Version? releaseVersion = null;
                IUpdateProjectScope? scope = null;
#if LIBRARY_V20
                if (action == "edit" && new[] { "codeBlock", "plcType", "plcDocument" }.Contains(LibraryTypeKind(type)))
                {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath))
                        throw new ArgumentException("STEP 7 type edit requires exact targetSoftwarePath containing a single instance of this version.");
                    var search = EngineeringPlcSession.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IInstanceSearchScope
                        ?? throw new NotSupportedException("Selected software does not support instance search.");
                    var instances = selected.FindInstances(search).Take(2).ToArray();
                    if (instances.Length != 1)
                        throw new ArgumentException("STEP 7 type edit requires exactly one instance in targetSoftwarePath; found " + instances.Length
                            + ". Use a software scope with a single matching instance; ambiguous edits are refused.");
                    editInstance = instances[0];
                }
                if (action == "release")
                {
                    mode = (CreateOrReleaseDependenciesMode)HardwareAddressingPolicy
                        .ConvertValue(new HardwareScalar { Kind = "string", Text = dependenciesMode }, typeof(CreateOrReleaseDependenciesMode))!;
                    releaseVersion = Version.Parse(newVersion);
                }
#endif
                if (action == "updateInstances")
                {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath))
                        throw new ArgumentException("Exact targetSoftwarePath required; unbounded project-wide update refused.");
                    scope = EngineeringPlcSession.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IUpdateProjectScope
                        ?? throw new NotSupportedException("Selected software does not implement IUpdateProjectScope.");
                    meta["versionSelection"] =
                        "Native LibraryType.UpdateProject chooses applicable released/default versions, not necessarily the version argument. The argument " +
                        "identifies evidence only.";
                }
                if (action == "findInstances") {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath))
                        throw new ArgumentException("Exact targetSoftwarePath required; whole project search refused.");
                    var search = EngineeringPlcSession.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IInstanceSearchScope
                        ?? throw new NotSupportedException("Selected software does not support instance search.");
                    var found = PlcGroupOperations.Items(selected.FindInstances(search)).ToArray();
                    meta["instances"] = new List<object?>(found.Select(x => (object)LibraryObjectAddress(x)).ToArray());
                    meta["actualCount"] = found.Length; meta["dataComplete"] = false; meta["truncated"] = false;
                    return "Native instances found in selected software; scalar result scope only.";
                }
                if (!writing) return "Library version read/preview. No modification; native semantic/state checks run only on execution.";
                using var editAccess = action == "edit" || action == "discard" ? HardwareEditAccess() : null;
                meta["mayHaveChanged"] = true;
                switch (action)
                {
#if LIBRARY_V20
                    case "edit": selected = editInstance == null ? selected.Edit() : selected.Edit(editInstance); break;
                    case "discard": selected.Discard(); meta["discardedEditVersion"] = true; break;
                    case "release": selected.Release(mode, releaseVersion!, author, comment); break;
#endif
#if LIBRARY_V17
                    case "setDefault": selected.SetAsDefault(); break;
#endif
                    case "deleteVersion": selected.Delete();
                        if (type.Versions.Any(v => v.VersionNumber.ToString() == version)) throw new InvalidOperationException("Version remains after Delete.");
                        meta["verifiedAbsent"] = true; break;
                    case "updateInstances": type.UpdateProject(scope!); break;
                }
                if (action != "deleteVersion" && action != "discard") meta["after"] = HardwareScalarEvidence.Read(selected);
                return "Native library operation completed; project/library not explicitly saved. Dependency changes may occur according to the selected " +
                    "native operation.";
            });

        public HardwareAddressingReply CreateLibraryMasterCopy(string sourceKind, string sourcePath, string softwarePath = "", string folderPath = "",
                string libraryName = "", bool dryRun = true, string[]? sourceDevicePath = null)
            => RunEngineeringServicesStep("CreateLibraryMasterCopy", meta => {
                using var access = dryRun ? null : HardwareEditAccess();
                object source = sourceKind switch {
                    "block" => ExactMasterCopyPlcSource(softwarePath, sourcePath, true),
                    "type" => ExactMasterCopyPlcSource(softwarePath, sourcePath, false),
                    "device" => ResolveLibraryDevice(sourcePath, sourceDevicePath!),
                    "screen" => ResolveLibraryHmiScreen(softwarePath, sourcePath),
                    _ => throw new ArgumentException("sourceKind must be block/type/device/screen. device sourcePath is a JSON array of exact names.") };
                if (source is not IMasterCopySource copySource)
                    throw new NotSupportedException("Selected object is not a supported native master-copy source.");
                var folder = (MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName), folderPath, "MasterCopyFolder");
                meta["sourceType"] = source.GetType().FullName; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) {
                    meta["mayHaveChanged"] = true; var copy = folder.MasterCopies.Create(copySource); meta["after"] = HardwareScalarEvidence.Read(copy); }
                return dryRun ? "Master copy preview; no copy created." : "Master copy created; no explicit save or close.";
            });
        private static ILibrary AsLibrary(object library) => library as ILibrary
            ?? throw new NotSupportedException(library.GetType().FullName + " does not implement ILibrary.");
        private Dictionary<string, object?> VersionRow(LibraryTypeVersion version, bool deep)
        {
            var row = new Dictionary<string, object?>
            {
                ["versionNumber"] = version.VersionNumber == null ? null : version.VersionNumber.ToString(), ["guid"] = version.Guid.ToString(), ["state"] =
                    version.State.ToString(), ["isDefault"] = LibraryIsDefault(version),
                ["author"] = version.Author, ["modifiedDate"] = LibraryValue(version.ModifiedDate), ["comment"] = MultilingualJson(version.Comment),
                ["versionClass"] = version.GetType().Name
            };
            try { row["originalLibrary"] = LibraryTypedMember(version, "OriginalLibrary"); } catch (Exception ex) {
                row["originalLibraryError"] = ex.GetBaseException().Message; }
            if (!deep) return row;
            // Dependencies / dependents can throw on InWork versions (official note); capture instead of failing the row.
            object Assoc(Func<object> get)
            {
                try
                {
                    return new List<object?>(PlcGroupOperations.Items(get()).Cast<LibraryTypeVersion>()
                        .Select(v => (object)new Dictionary<string, object?> {
                                ["type"] = v.TypeObject?.Name, ["versionNumber"] = v.VersionNumber == null ? null : v.VersionNumber.ToString(), ["guid"] = v
                                    .Guid.ToString(), ["state"] = v.State.ToString()
                            })
                            .ToArray());
                }
                catch (Exception ex) { return new Dictionary<string, object?> { ["error"] = ex.GetBaseException().Message }; }
            }
            row["dependencies"] = Assoc(() => version.Dependencies); row["dependents"] = Assoc(() => version.Dependents);
            try {
                row["masterCopiesContainingInstances"] = new List<object
                    ?>(PlcGroupOperations.Items(version.MasterCopiesContainingInstances).Cast<MasterCopy>().Select(m => (object)m.Name).ToArray());
            }
            catch (Exception ex) { row["masterCopiesContainingInstancesError"] = ex.GetBaseException().Message; }
            return row;
        }
        // The STEP 7 library type subclasses (CodeBlockLibraryType / PlcTypeLibraryType / PlcDocumentLibraryType) name what a version instantiates;
        // Classic HMI and Unified subclasses identify their supported kinds; observed V21 Unified faceplates / graphics are plain LibraryType -> "other".
        // The SiVArc rule-table types and the DCC block type are identified by OptionPackageLibraryTypeKind.
        private string LibraryTypeKind(LibraryType type)
        {
            foreach (var pair in new[] {
                ("Siemens.Engineering.SW.Blocks.CodeBlockLibraryType", "codeBlock"),
                ("Siemens.Engineering.SW.Types.PlcTypeLibraryType", "plcType"),
                ("Siemens.Engineering.SW.Types.PlcDocumentLibraryType", "plcDocument"),
                ("Siemens.Engineering.Hmi.Screen.ScreenLibraryType", "hmiScreen"),
                ("Siemens.Engineering.Hmi.Screen.StyleLibraryType", "hmiStyle"),
                ("Siemens.Engineering.Hmi.Screen.StyleSheetLibraryType", "hmiStyleSheet"),
                ("Siemens.Engineering.Hmi.Tag.HmiUdtLibraryType", "hmiUdt"),
                ("Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType", "hmiFaceplate"),
                ("Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryType", "hmiVbScript"),
                ("Siemens.Engineering.Hmi.RuntimeScripting.CScriptLibraryType", "hmiCScript"),
                ("Siemens.Engineering.HmiUnified.Library.ScriptModuleType", "unifiedScriptModule"),
            })
                for (var current = type.GetType(); current != null; current = current.BaseType)
                    if (current.FullName == pair.Item1) return pair.Item2;
            return LibraryOptionPackageKind(type);
        }
        private Dictionary<string, object?> TypeRow(LibraryType type, bool versions)
        {
            var row = new Dictionary<string, object?>
            {
                ["name"] = type.Name, ["guid"] = type.Guid.ToString(), ["typeClass"] = type.GetType().Name, ["typeKind"] = LibraryTypeKind(type), ["author"] =
                    type.Author, ["namespace"] = LibraryTypedMember(type, "Namespace"),
                ["comment"] = MultilingualJson(type.Comment), ["doNotUse"] = LibraryTypedMember(type, "DoNotUse"), ["minimumTargetDeviceVersion"] =
                    LibraryTypedMember(type, "MinimumTargetDeviceVersion")?.ToString()
            };
            try { row["setForUpdate"] = LibraryTypedMember(type, "SetForUpdate"); } catch (Exception ex) {
                row["setForUpdateError"] = ex.GetBaseException().Message; }
            try { row["status"] = LibraryTypedMember(type, "Status").ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message; }
            var all = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().ToArray();
            row["versionCount"] = all.Length; var def = all.FirstOrDefault(v => LibraryIsDefault(v)); row["defaultVersion"] = def?.VersionNumber == null ? null
                : def.VersionNumber.ToString();
            if (versions) row["versions"] = new List<object?>(all.Select(v => (object)VersionRow(v, false)).ToArray());
            return row;
        }
        private Dictionary<string, object?> MasterCopyRow(MasterCopy copy) => new Dictionary<string, object?>
        {
            ["name"] = copy.Name, ["author"] = copy.Author, ["creationDate"] = LibraryValue(copy.CreationDate),
            ["contents"] = new List<object?>(PlcGroupOperations.Items(copy.ContentDescriptions).Cast<MasterCopyContentDescription>()
                .Select(c => (object)new Dictionary<string, object?> { ["contentName"] = c.ContentName, ["contentType"] = c.ContentType?.FullName }).ToArray())
        };
        private static int LibraryTypeCount(LibraryTypeFolder folder)
            => PlcGroupOperations.Items(folder.Types).Count()
                + PlcGroupOperations.Items(folder.Folders).Cast<LibraryTypeUserFolder>().Sum(LibraryTypeCount);

        private static void RecordUnchangedLibraryImport(Dictionary<string, object?> meta, int before, Func<int> countAfter, bool canProveUnchanged)
        {
            try
            {
                int after = countAfter();
                meta["countAfter"] = after;
                if (canProveUnchanged && before == after) meta["mayHaveChanged"] = false;
            }
            catch { /* swallow(probe-optional): a failed readback cannot clear an uncertain import. */ }
        }

        private Dictionary<string, object?> TypeFolderTree(LibraryTypeFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
        {
            var row = new Dictionary<string, object?> { ["path"] = path, ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name };
            try { row["status"] = LibraryTypedMember(folder, "Status").ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message;
            }
            var types = new List<object?>(); row["types"] = types;
            foreach (var type in PlcGroupOperations.Items(folder.Types).Cast<LibraryType>()) {
                if (++items > maxItems) { row["typesTruncated"] = true; break; } types.Add(TypeRow(type, false)); }
            var folders = new List<object?>(); row["folders"] = folders;
            if (depth >= maxDepth) { row["foldersTruncated"] = PlcGroupOperations.Items(folder.Folders).Any(); return row; }
            foreach (var sub in PlcGroupOperations.Items(folder.Folders).Cast<LibraryTypeUserFolder>())
            {
                if (items > maxItems) { row["foldersTruncated"] = true; break; }
                folders.Add(TypeFolderTree(sub, path.Length == 0 ? sub.Name : path + "/" + sub.Name, depth + 1, maxDepth, ref items, maxItems));
            }
            return row;
        }
        private Dictionary<string, object?> MasterCopyFolderTree(MasterCopyFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
        {
            var row = new Dictionary<string, object?> { ["path"] = path, ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name };
            var copies = new List<object?>(); row["masterCopies"] = copies;
            foreach (var copy in PlcGroupOperations.Items(folder.MasterCopies).Cast<MasterCopy>()) {
                if (++items > maxItems) { row["masterCopiesTruncated"] = true; break; } copies.Add(MasterCopyRow(copy)); }
            var folders = new List<object?>(); row["folders"] = folders;
            if (depth >= maxDepth) { row["foldersTruncated"] = PlcGroupOperations.Items(folder.Folders).Any(); return row; }
            foreach (var sub in PlcGroupOperations.Items(folder.Folders).Cast<MasterCopyUserFolder>())
            {
                if (items > maxItems) { row["foldersTruncated"] = true; break; }
                folders.Add(MasterCopyFolderTree(sub, path.Length == 0 ? sub.Name : path + "/" + sub.Name, depth + 1, maxDepth, ref items, maxItems));
            }
            return row;
        }
        private Dictionary<string, object?> LibraryHeader(object library)
        {
            var row = EngineeringLibraryRef(library);
            if (library is GlobalLibrary global)
            {
#if LIBRARY_V20
                row["author"] = global.Author; row["comment"] = MultilingualJson(global.Comment); row["copyright"] = global.Copyright; row["family"] = global
                    .Family; row["version"] = global.Version;
                row["path"] = global.Path?.FullName; row["creationTime"] = LibraryValue(global.CreationTime); row["lastModified"] =
                    LibraryValue(global.LastModified);
                row["lastModifiedBy"] = global.LastModifiedBy; row["isModified"] = global.IsModified; row["isWriteProtected"] = LibraryWriteProtected(global);
                row["sizeKb"] = global.Size;
                row["historyEntries"] = new List<object?>(PlcGroupOperations.Items(global.HistoryEntries).Cast<HistoryEntry>()
                    .Select(h => (object)new Dictionary<string, object?> { ["dateTime"] = LibraryValue(h.DateTime), ["text"] = h.Text }).ToArray());
                row["usedProducts"] = new List<object?>(PlcGroupOperations.Items(global.UsedProducts).Cast<UsedProduct>()
                    .Select(p => (object)new Dictionary<string, object?> { ["name"] = p.Name, ["version"] = p.Version }).ToArray());
#else
                row["author"] = global.Author; row["comment"] = MultilingualJson(global.Comment); row["path"] = global.Path?.FullName;
                foreach (var pair in new[] {
                        ("copyright", "Copyright"), ("family", "Family"), ("version", "Version"), ("creationTime", "CreationTime"),
                            ("lastModified", "LastModified"), ("lastModifiedBy", "LastModifiedBy"), ("isModified", "IsModified"),
                            ("isWriteProtected", "IsWriteProtected"), ("sizeKb", "Size")
                    })
                    if (global.GetType().GetProperty(pair.Item2) is PropertyInfo property) row[pair.Item1] = LibraryValue(property.GetValue(global));
                foreach (var pair in new[] { ("historyEntries", "HistoryEntries"), ("usedProducts", "UsedProducts") })
                    if (global.GetType().GetProperty(pair.Item2) is PropertyInfo property) row[pair.Item1] = PlcGroupOperations
                        .Items(property.GetValue(global)!).Select(HardwareScalarEvidence.Read).Cast<object?>().ToList();
#endif
            }
            return row;
        }
        private ILibraryTypeOrFolderSelection[] ResolveSelection(object library, LibrarySelection[] selection, Dictionary<string, object?> meta)
        {
            var resolved = new List<ILibraryTypeOrFolderSelection>(); var rows = new List<object?>(); meta["selection"] = rows;
            foreach (var entry in selection)
            {
                if (entry.IsFolder)
                {
                    var folder = (LibraryTypeFolder)EngineeringLibraryFolder(library, entry.Path, "TypeFolder");
                    resolved.Add(folder); rows
                        .Add(new Dictionary<string, object?> {
                            ["folder"] = entry.Path, ["folderClass"] = folder.GetType().Name, ["types"] = PlcGroupOperations.Items(folder.Types).Count() });
                }
                else
                {
                    var type = ExactLibraryType(library, entry.Path);
                    resolved.Add(type); rows
                        .Add(new Dictionary<string, object?> {
                            ["type"] = entry.Path, ["guid"] = type.Guid.ToString(), ["versions"] = PlcGroupOperations.Items(type.Versions).Count() });
                }
            }
            return resolved.ToArray();
        }
        private IUpdateProjectScope[] ResolveScopes(string[] softwarePaths, Dictionary<string, object?> meta)
        {
            var scopes = new List<IUpdateProjectScope>(); var rows = new List<object?>(); meta["scopes"] = rows;
            foreach (var path in softwarePaths)
            {
                var software = EngineeringPlcSession.ResolveSoftwareContainerUncached(path)?.Software
                    ?? throw new PlcSoftwareException("NotFound", "Exact software not found: " + path);
                scopes
                    .Add(software as IUpdateProjectScope
                        ?? throw new NotSupportedException(path + " (" + software.GetType().Name
                            + ") does not implement IUpdateProjectScope (PlcSoftware / HmiTarget / Unified HmiSoftware only)."));
                rows.Add(new Dictionary<string, object?> { ["softwarePath"] = path, ["softwareClass"] = software.GetType().Name });
            }
            return scopes.ToArray();
        }
        private static T ParseEnum<T>(string value) where T : struct => (T)System.Enum.Parse(typeof(T), value, false);

        // ---- read --------------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadLibraryOverview(string libraryName = "", bool includeTypes = true, bool includeMasterCopies = true, int maxDepth = 6,
                int maxItems = 500)
            => RunEngineeringServicesStep("GetLibraryOverview", meta => {
                LibraryPolicy.ValidateBounds(maxDepth, maxItems);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                if (string.IsNullOrEmpty(libraryName) && (project == null))
                    throw new PlcSoftwareException("InvalidState", "Project library requested but no project is bound.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["library"] = LibraryHeader(library);
                int items = 0;
                if (includeTypes)
                {
                    // SystemGlobalLibrary.TypeFolder is null (system libraries carry master copies only; observed on TIA V21).
                    var typeFolder = lib.TypeFolder;
                    meta["typeFolder"] = typeFolder == null ? null : TypeFolderTree(typeFolder, "", 0, maxDepth, ref items, maxItems);
                    if (typeFolder == null) meta["typeFolderNote"] = library.GetType().Name + " exposes no TypeFolder (master copies only).";
                }
                int copies = 0;
                if (includeMasterCopies)
                {
                    var masterCopyFolder = lib.MasterCopyFolder;
                    meta["masterCopyFolder"] = masterCopyFolder == null ? null : MasterCopyFolderTree(masterCopyFolder, "", 0, maxDepth, ref copies, maxItems);
                    if (masterCopyFolder == null) meta["masterCopyFolderNote"] = library.GetType().Name + " exposes no MasterCopyFolder.";
                }
                meta["typeItems"] = items; meta["masterCopyItems"] = copies; meta["apiCallSuccess"] = true; meta["dataComplete"] = items <= maxItems
                    && copies <= maxItems;
                meta["scope"] =
                    "Library header (GlobalLibrary scalars, comment per culture, history entries, used products), type folder tree with consistency Status / " +
                    "Guid / DoNotUse / SetForUpdate / version summary, master copy tree with ContentDescriptions. Bounded by maxDepth/maxItems.";
                return "Library overview read; no modification.";
            }, requiresProject: false);

        public HardwareAddressingReply ReadLibraryType(string libraryName = "", string typePath = "", string guid = "", int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("GetLibraryType", meta => {
                EngineeringEvidence.ValidatePage(offset, limit);
                var parsed = LibraryPolicy.ParseGuid(guid, "guid");
                if ((parsed == null) == string.IsNullOrEmpty(typePath))
                    throw new ArgumentException("Give exactly one of typePath (relative to the Types folder) or guid (type or version GUID).");
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                LibraryType type; LibraryTypeVersion? matchedVersion = null;
                if (parsed != null)
                {
                    var byType = lib.FindType(parsed.Value);
                    if (byType != null) type = byType;
                    else {
                        matchedVersion = lib.FindVersion(parsed.Value)
                            ?? throw new PlcSoftwareException("NotFound", "No type or version with GUID " + parsed.Value
                                + " in this library (FindType/FindVersion both returned null).");
                        type = matchedVersion.TypeObject;
                    }
                    meta["foundBy"] = matchedVersion == null ? "FindType" : "FindVersion";
                }
                else type = ExactLibraryType(library, typePath);
                meta["typePath"] = LibraryPathOf(type, "LibraryTypeSystemFolder") is { Length: > 0 } p ? p + "/" + type.Name : type.Name;
                meta["type"] = TypeRow(type, false);
                #if LIBRARY_V20
                try {
                    meta["supportedExportFormats"] = new List<object
                        ?>(PlcGroupOperations.Items(type.GetSupportedExportFormats()).Select(f => (object)f.ToString()).ToArray());
                }
                catch (Exception ex) { meta["supportedExportFormatsError"] = ex.GetBaseException().Message; }
#endif
                if (matchedVersion != null) meta["matchedVersion"] = VersionRow(matchedVersion, true);
                var versions = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Select(v => (object)VersionRow(v, true)).ToArray();
                LibraryPage(versions, offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] =
                    "LibraryType scalars incl. Guid / Namespace / DoNotUse / SetForUpdate / MinimumTargetDeviceVersion / Status, GetSupportedExportFormats, " +
                    "and every version with Dependencies / Dependents / MasterCopiesContainingInstances / OriginalLibrary (failures captured per field).";
                return "Library type and versions read; no modification.";
            }, requiresProject: false);

        public HardwareAddressingReply CheckLibraryUpdates(string libraryName = "", string updateCheckMode = "ReportOutOfDateOnly", int maxItems = 2000)
            => RunEngineeringServicesStep("CheckLibraryUpdates", meta => {
                LibraryPolicy.RequireOneOf(updateCheckMode, LibraryPolicy.UpdateCheckModes, "updateCheckMode"); LibraryPolicy.ValidateBounds(1, maxItems);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                if ((project == null)) throw new PlcSoftwareException("InvalidState", "UpdateCheck runs against the bound project; attach a project first.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["library"] = EngineeringLibraryRef(library); meta["updateCheckMode"] = updateCheckMode;
                // TIA answers UpdateCheck on a library without types (SystemGlobalLibrary) with a NonRecoverableException that the
                // connection guard treats as a lost session (observed on TIA V21): refuse before calling.
                if (lib.TypeFolder == null) throw new ArgumentException(LibraryPolicy.NoTypeFolderMessage(library.GetType().Name, "UpdateCheck"));
                var result = lib.UpdateCheck(project!, ParseEnum<UpdateCheckMode>(updateCheckMode));
                int count = 0; bool truncated = false;
                List<object?> Flatten(object messages, int depth)
                {
                    var rows = new List<object?>();
                    if (depth > 12) { truncated = true; return rows; }
                    foreach (var message in PlcGroupOperations.Items(messages).Cast<UpdateCheckResultMessage>())
                    {
                        if (++count > maxItems) { truncated = true; break; }
                        var row = new Dictionary<string, object?> { ["description"] = message.Description ?? "" };
                        try
                        {
                            // Real project: every part is a KeyValuePair<string,string> (DeviceName, LibraryTypeName, LibraryVersionNumber,
                            // UpToDate, PathToInstance, InstanceName, CurrentVersionNumber); rendered flat instead of the 60x scalar dump.
                            var parts = new Dictionary<string, object?>(); var other = new List<object?>();
                            foreach (var part in PlcGroupOperations.Items(message.MessageParts))
                            {
                                var type = part.GetType();
                                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                                    parts[type.GetProperty("Key")?.GetValue(part)?.ToString() ?? ""] = LibraryValue(type.GetProperty("Value")?.GetValue(part));
                                else other.Add(HardwareScalarEvidence.Scalar(type) ? LibraryValue(part) : (object)HardwareScalarEvidence.Read(part));
                            }
                            row["parts"] = parts; if (other.Count > 0) row["messageParts"] = other;
                        }
                        catch (Exception ex) { row["messagePartsError"] = ex.GetBaseException().Message; }
                        row["messages"] = Flatten(message.Messages, depth + 1);
                        rows.Add(row);
                    }
                    return rows;
                }
                meta["messages"] = Flatten(result.Messages, 0); meta["messageCount"] = count; meta["truncated"] = truncated;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = !truncated;
                meta["scope"] =
                    "Native ILibrary.UpdateCheck(project, mode) message tree: Description, parts {Key: Value} (DeviceName / LibraryTypeName / " +
                    "LibraryVersionNumber / UpToDate / PathToInstance / InstanceName / CurrentVersionNumber), nested Messages. Read-only; no update performed.";
                return count == 0 ? "Update check completed: no messages (nothing out of date for this mode)."
                    : "Update check completed; inspect the message tree.";
            });

        // ---- write --------------------------------------------------------------------------------------------------------
#if LIBRARY_DOCUMENT_IMPORT
        public HardwareAddressingReply ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",
                string typePath="",string createOptions="None",string targetSoftwarePath="",string targetGroupKind="",string targetGroupPath="",bool dryRun=
                true)
            =>RunEngineeringServicesStep("ImportLibraryTypeDocuments",meta=>{
                var file=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));if(!file.Exists)
                    throw new FileNotFoundException("Library type native document not found.");
                LibraryPolicy.RequireOneOf(importOptions,LibraryPolicy.ImportOptions,"importOptions");LibraryPolicy
                    .RequireOneOf(createOptions,LibraryPolicy.CreateOptions,"createOptions");
                if(targetGroupKind!="" && targetGroupKind!="blocks" && targetGroupKind!="types")
                    throw new ArgumentException("targetGroupKind must be empty, blocks or types.");
                if((targetGroupKind=="")!=string.IsNullOrEmpty(targetSoftwarePath))
                    throw new ArgumentException("targetSoftwarePath and targetGroupKind go together (STEP 7 documents need a PlcBlockGroup/PlcTypeGroup " +
                    "target environment).");
                var library=ExactOpenEngineeringLibrary(libraryName);
                string basePath=Path.Combine(file.DirectoryName!,Path.GetFileNameWithoutExtension(file.Name));
                LibraryPolicy
                    .ValidateDocumentImportScope(int
                        .Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey, System.Globalization.CultureInfo.InvariantCulture),
                        library is ProjectLibrary,
                    importOptions, File.Exists(basePath+".xml"), File.Exists(basePath+".s7dcl"));
                var options=(LibraryImportOptions)System.Enum.Parse(typeof(LibraryImportOptions),importOptions);var create=(CreateOptions)System.Enum
                    .Parse(typeof(CreateOptions),createOptions);
                string fileName=Path.GetFileNameWithoutExtension(file.Name);meta["fileName"]=fileName;meta["importOptions"]=importOptions;meta["dryRun"]=dryRun;
                meta["mayHaveChanged"]=false;
                IEngineeringObject? environment=null;
                if(targetGroupKind!=""){
                    var plc=EngineeringPlcSession.ExactPlcForEngineering(targetSoftwarePath,false);
                    environment=(IEngineeringObject)PlcGroupOperations.Group(targetGroupKind=="blocks"?(object)plc.BlockGroup:plc.TypeGroup,targetGroupPath);
                    meta["targetEnvironment"]=new Dictionary<string, object?>{
                        ["softwarePath"]=targetSoftwarePath,["groupKind"]=targetGroupKind,["groupPath"]=targetGroupPath,["type"]=environment.GetType().Name};
                }
                if(!string.IsNullOrEmpty(typePath)){
                    // Version import: LibraryTypeVersionComposition.CreateFromDocuments on an existing type ("Updating type from document").
                    var type=ExactLibraryType(library,typePath);meta["typePath"]=typePath;meta["createOptions"]=createOptions;meta["before"]=TypeRow(type,true);
                    if(dryRun)return "Library version document import preview; CreateOptions.None fails natively if an in-work version exists.";
                    using var access=HardwareEditAccess();
                    bool countProof = LibraryPolicy.CanUseDocumentImportCountProof(importOptions, createOptions, environment != null);
                    int versionCountBefore = PlcGroupOperations.Items(type.Versions).Count();
                    meta["mayHaveChanged"]=true;
                    VersionCreateTransferResults result;
                    try { result=environment==null?type.Versions.CreateFromDocuments(file.Directory!,fileName,create,
                        options):type.Versions.CreateFromDocuments(file.Directory!,fileName,environment,create,options); }
                    catch
                    {
                        RecordUnchangedLibraryImport(meta, versionCountBefore, () => PlcGroupOperations.Items(type.Versions).Count(), countProof);
                        throw;
                    }
                    meta["transferResultState"]=result.TransferResultState.ToString();

                    meta["messages"]=new List<object
                        ?>(PlcGroupOperations.Items(result.Messages).Cast<TransferResultMessage>().Select(m=>(object)m.Message).ToArray());

                    meta["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Library;
                    PlcSoftwareValues.RecordNativeResult(meta, meta["transferResultState"], meta["messages"]);
                    meta["createdVersion"]=result.CreatedVersion==null?null:VersionRow(result.CreatedVersion,false);meta["after"]=TypeRow(type,true);
                    meta["apiCallSuccess"]=true;
                    if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.TransferResultState))meta["operationSuccess"]=false;
                    return "Native version import returned "+result.TransferResultState
                        +"; created version and messages attached. No automatic library/project save.";
                }
                var folder=EngineeringLibraryFolder(library,folderPath,"TypeFolder");
                var types=(LibraryTypeComposition)PlcGroupOperations.Get(folder,"Types");
                if(dryRun)return "Library native document import preview; type/dependency semantics checked on execution.";
                using var edit=HardwareEditAccess();
                var libraryRoot = (LibraryTypeFolder)PlcGroupOperations.Get(library, "TypeFolder");
                bool typeCountProof = LibraryPolicy.CanUseDocumentImportCountProof(importOptions, createOptions, environment != null);
                int countBefore = LibraryTypeCount(libraryRoot);
                meta["mayHaveChanged"]=true;
                TypeCreateTransferResults created;
                try { created=environment==null?types.CreateFromDocuments(file.Directory!,fileName,options):types.CreateFromDocuments(file.Directory!,
                    fileName,environment,options); }
                catch
                {
                    RecordUnchangedLibraryImport(meta, countBefore, () => LibraryTypeCount(libraryRoot), typeCountProof);
                    throw;
                }
                meta["transferResultState"]=created.TransferResultState.ToString();

                meta["messages"]=new List<object
                    ?>(PlcGroupOperations.Items(created.Messages).Cast<TransferResultMessage>().Select(m=>(object)m.Message).ToArray());

                meta["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Library;
                PlcSoftwareValues.RecordNativeResult(meta, meta["transferResultState"], meta["messages"]);
                meta["createdType"]=created.CreatedType==null?null:TypeRow(created.CreatedType,true);meta["apiCallSuccess"]=true;
                if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(created.TransferResultState))meta["operationSuccess"]=false;
                return "Native type import returned "+created.TransferResultState
                    +"; created type (default version InWork) and messages attached. No automatic library/project save.";
            });
#endif
        public GlobalLibraryImportEvidence ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
            => LibraryPolicy.RefuseScreenItemMasterCopyImport(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName);

        private static object? BuildDefaultArgument(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(int)) return 0;
            if (type == typeof(string)) return "";
            if (type.IsEnum) return Enum.ToObject(type, 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static string LastPathSegment(string path)
        {
            var value = (path ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var parts = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[parts.Length - 1];
        }

    }
}

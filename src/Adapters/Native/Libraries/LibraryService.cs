using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
                if (!new[] { "read", "edit", "release", "setDefault", "deleteVersion", "updateInstances", "discard", "findInstances" }.Contains(action)) throw new ArgumentException("action must be one of: read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances (case-sensitive).");
                bool writing = action != "read" && action != "findInstances" && !dryRun;
                using var access = writing ? HardwareEditAccess() : null;
                var library = ExactOpenEngineeringLibrary(libraryName);
                var parts = PlcGroupOperations.Parts(typePath);
                var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
                var type = (LibraryType)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Types"), parts.Last()) ?? throw new InvalidOperationException("Exact library type not found."));
                var matches = type.Versions.Where(v => v.VersionNumber.ToString() == version).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one matching version, found " + matches.Length);
                var selected = matches[0];
                meta["before"] = HardwareScalarEvidence.Read(selected); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["typePath"] = typePath; meta["libraryName"] = libraryName; meta["action"] = action;
#if LIBRARY_V20
                CreateOrReleaseDependenciesMode mode = default;
#endif
                Version? releaseVersion = null;
                IUpdateProjectScope? scope = null;
#if LIBRARY_V20
                if (action == "release")
                {
                    mode = (CreateOrReleaseDependenciesMode)HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = dependenciesMode }, typeof(CreateOrReleaseDependenciesMode))!;
                    releaseVersion = Version.Parse(newVersion);
                }
#endif
                if (action == "updateInstances")
                {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("Exact targetSoftwarePath required; unbounded project-wide update refused.");
                    scope = EngineeringPlcSession.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IUpdateProjectScope
                        ?? throw new NotSupportedException("Selected software does not implement IUpdateProjectScope.");
                    meta["versionSelection"] = "Native LibraryType.UpdateProject chooses applicable released/default versions, not necessarily the version argument. The argument identifies evidence only.";
                }
                if (action == "findInstances") {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("Exact targetSoftwarePath required; whole project search refused.");
                    var search = EngineeringPlcSession.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IInstanceSearchScope ?? throw new NotSupportedException("Selected software does not support instance search.");
                    var found = PlcGroupOperations.Items(selected.FindInstances(search)).ToArray();
                    meta["instances"] = new List<object?>(found.Select(x => (object)LibraryObjectAddress(x)).ToArray());
                    meta["actualCount"] = found.Length; meta["dataComplete"] = false; meta["truncated"] = false;
                    return "Native instances found in selected software; scalar result scope only.";
                }
                if (!writing) return "Library version read/preview. No modification; native semantic/state checks run only on execution.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
#if LIBRARY_V20
                    case "edit": selected = selected.Edit(); break;
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
                return "Native library operation completed; project/library not explicitly saved. Dependency changes may occur according to the selected native operation.";
            });

        public HardwareAddressingReply CreateLibraryMasterCopy(string sourceKind, string sourcePath, string softwarePath = "", string folderPath = "", string libraryName = "", bool dryRun = true, string[]? sourceDevicePath = null)
            => RunEngineeringServicesStep("CreateLibraryMasterCopy", meta => {
                using var access = dryRun ? null : HardwareEditAccess();
                object source = sourceKind switch {
                    "block" => ExactMasterCopyPlcSource(softwarePath, sourcePath, true),
                    "type" => ExactMasterCopyPlcSource(softwarePath, sourcePath, false),
                    "device" => ResolveLibraryDevice(sourcePath, sourceDevicePath!),
                    "screen" => ResolveLibraryHmiScreen(softwarePath, sourcePath),
                    _ => throw new ArgumentException("sourceKind must be block/type/device/screen. device sourcePath is a JSON array of exact names.") };
                if (source is not IMasterCopySource copySource) throw new NotSupportedException("Selected object is not a supported native master-copy source.");
                var folder = (MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName), folderPath, "MasterCopyFolder");
                meta["sourceType"] = source.GetType().FullName; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; var copy = folder.MasterCopies.Create(copySource); meta["after"] = HardwareScalarEvidence.Read(copy); }
                return dryRun ? "Master copy preview; no copy created." : "Master copy created; no explicit save or close.";
            });
        private static ILibrary AsLibrary(object library) => library as ILibrary ?? throw new NotSupportedException(library.GetType().FullName + " does not implement ILibrary.");
        private Dictionary<string, object?> VersionRow(LibraryTypeVersion version, bool deep)
        {
            var row = new Dictionary<string, object?>
            {
                ["versionNumber"] = version.VersionNumber == null ? null : version.VersionNumber.ToString(), ["guid"] = version.Guid.ToString(), ["state"] = version.State.ToString(), ["isDefault"] = LibraryIsDefault(version),
                ["author"] = version.Author, ["modifiedDate"] = LibraryValue(version.ModifiedDate), ["comment"] = MultilingualJson(version.Comment),
                ["versionClass"] = version.GetType().Name
            };
            try { row["originalLibrary"] = LibraryTypedMember(version, "OriginalLibrary"); } catch (Exception ex) { row["originalLibraryError"] = ex.GetBaseException().Message; }
            if (!deep) return row;
            // Dependencies / dependents can throw on InWork versions (official note); capture instead of failing the row.
            object Assoc(Func<object> get)
            {
                try
                {
                    return new List<object?>(PlcGroupOperations.Items(get()).Cast<LibraryTypeVersion>()
                        .Select(v => (object)new Dictionary<string, object?> { ["type"] = v.TypeObject?.Name, ["versionNumber"] = v.VersionNumber == null ? null : v.VersionNumber.ToString(), ["guid"] = v.Guid.ToString(), ["state"] = v.State.ToString() }).ToArray());
                }
                catch (Exception ex) { return new Dictionary<string, object?> { ["error"] = ex.GetBaseException().Message }; }
            }
            row["dependencies"] = Assoc(() => version.Dependencies); row["dependents"] = Assoc(() => version.Dependents);
            try { row["masterCopiesContainingInstances"] = new List<object?>(PlcGroupOperations.Items(version.MasterCopiesContainingInstances).Cast<MasterCopy>().Select(m => (object)m.Name).ToArray()); }
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
                ["name"] = type.Name, ["guid"] = type.Guid.ToString(), ["typeClass"] = type.GetType().Name, ["typeKind"] = LibraryTypeKind(type), ["author"] = type.Author, ["namespace"] = LibraryTypedMember(type, "Namespace"),
                ["comment"] = MultilingualJson(type.Comment), ["doNotUse"] = LibraryTypedMember(type, "DoNotUse"), ["minimumTargetDeviceVersion"] = LibraryTypedMember(type, "MinimumTargetDeviceVersion")?.ToString()
            };
            try { row["setForUpdate"] = LibraryTypedMember(type, "SetForUpdate"); } catch (Exception ex) { row["setForUpdateError"] = ex.GetBaseException().Message; }
            try { row["status"] = LibraryTypedMember(type, "Status").ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message; }
            var all = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().ToArray();
            row["versionCount"] = all.Length; var def = all.FirstOrDefault(v => LibraryIsDefault(v)); row["defaultVersion"] = def?.VersionNumber == null ? null : def.VersionNumber.ToString();
            if (versions) row["versions"] = new List<object?>(all.Select(v => (object)VersionRow(v, false)).ToArray());
            return row;
        }
        private Dictionary<string, object?> MasterCopyRow(MasterCopy copy) => new Dictionary<string, object?>
        {
            ["name"] = copy.Name, ["author"] = copy.Author, ["creationDate"] = LibraryValue(copy.CreationDate),
            ["contents"] = new List<object?>(PlcGroupOperations.Items(copy.ContentDescriptions).Cast<MasterCopyContentDescription>()
                .Select(c => (object)new Dictionary<string, object?> { ["contentName"] = c.ContentName, ["contentType"] = c.ContentType?.FullName }).ToArray())
        };
        private Dictionary<string, object?> TypeFolderTree(LibraryTypeFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
        {
            var row = new Dictionary<string, object?> { ["path"] = path, ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name };
            try { row["status"] = LibraryTypedMember(folder, "Status").ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message; }
            var types = new List<object?>(); row["types"] = types;
            foreach (var type in PlcGroupOperations.Items(folder.Types).Cast<LibraryType>()) { if (++items > maxItems) { row["typesTruncated"] = true; break; } types.Add(TypeRow(type, false)); }
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
            foreach (var copy in PlcGroupOperations.Items(folder.MasterCopies).Cast<MasterCopy>()) { if (++items > maxItems) { row["masterCopiesTruncated"] = true; break; } copies.Add(MasterCopyRow(copy)); }
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
                row["author"] = global.Author; row["comment"] = MultilingualJson(global.Comment); row["copyright"] = global.Copyright; row["family"] = global.Family; row["version"] = global.Version;
                row["path"] = global.Path?.FullName; row["creationTime"] = LibraryValue(global.CreationTime); row["lastModified"] = LibraryValue(global.LastModified);
                row["lastModifiedBy"] = global.LastModifiedBy; row["isModified"] = global.IsModified; row["isWriteProtected"] = LibraryWriteProtected(global); row["sizeKb"] = global.Size;
                row["historyEntries"] = new List<object?>(PlcGroupOperations.Items(global.HistoryEntries).Cast<HistoryEntry>()
                    .Select(h => (object)new Dictionary<string, object?> { ["dateTime"] = LibraryValue(h.DateTime), ["text"] = h.Text }).ToArray());
                row["usedProducts"] = new List<object?>(PlcGroupOperations.Items(global.UsedProducts).Cast<UsedProduct>()
                    .Select(p => (object)new Dictionary<string, object?> { ["name"] = p.Name, ["version"] = p.Version }).ToArray());
#else
                row["author"] = global.Author; row["comment"] = MultilingualJson(global.Comment); row["path"] = global.Path?.FullName;
                foreach (var pair in new[] { ("copyright", "Copyright"), ("family", "Family"), ("version", "Version"), ("creationTime", "CreationTime"), ("lastModified", "LastModified"), ("lastModifiedBy", "LastModifiedBy"), ("isModified", "IsModified"), ("isWriteProtected", "IsWriteProtected"), ("sizeKb", "Size") })
                    if (global.GetType().GetProperty(pair.Item2) is PropertyInfo property) row[pair.Item1] = LibraryValue(property.GetValue(global));
                foreach (var pair in new[] { ("historyEntries", "HistoryEntries"), ("usedProducts", "UsedProducts") })
                    if (global.GetType().GetProperty(pair.Item2) is PropertyInfo property) row[pair.Item1] = PlcGroupOperations.Items(property.GetValue(global)!).Select(HardwareScalarEvidence.Read).Cast<object?>().ToList();
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
                    resolved.Add(folder); rows.Add(new Dictionary<string, object?> { ["folder"] = entry.Path, ["folderClass"] = folder.GetType().Name, ["types"] = PlcGroupOperations.Items(folder.Types).Count() });
                }
                else
                {
                    var type = ExactLibraryType(library, entry.Path);
                    resolved.Add(type); rows.Add(new Dictionary<string, object?> { ["type"] = entry.Path, ["guid"] = type.Guid.ToString(), ["versions"] = PlcGroupOperations.Items(type.Versions).Count() });
                }
            }
            return resolved.ToArray();
        }
        private IUpdateProjectScope[] ResolveScopes(string[] softwarePaths, Dictionary<string, object?> meta)
        {
            var scopes = new List<IUpdateProjectScope>(); var rows = new List<object?>(); meta["scopes"] = rows;
            foreach (var path in softwarePaths)
            {
                var software = EngineeringPlcSession.ResolveSoftwareContainerUncached(path)?.Software ?? throw new PlcSoftwareException("NotFound", "Exact software not found: " + path);
                scopes.Add(software as IUpdateProjectScope ?? throw new NotSupportedException(path + " (" + software.GetType().Name + ") does not implement IUpdateProjectScope (PlcSoftware / HmiTarget / Unified HmiSoftware only)."));
                rows.Add(new Dictionary<string, object?> { ["softwarePath"] = path, ["softwareClass"] = software.GetType().Name });
            }
            return scopes.ToArray();
        }
        private static T ParseEnum<T>(string value) where T : struct => (T)System.Enum.Parse(typeof(T), value, false);

        // ---- read --------------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadLibraryOverview(string libraryName = "", bool includeTypes = true, bool includeMasterCopies = true, int maxDepth = 6, int maxItems = 500)
            => RunEngineeringServicesStep("GetLibraryOverview", meta => {
                LibraryPolicy.ValidateBounds(maxDepth, maxItems);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                if (string.IsNullOrEmpty(libraryName) && (project == null)) throw new PlcSoftwareException("InvalidState", "Project library requested but no project is bound.");
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
                meta["typeItems"] = items; meta["masterCopyItems"] = copies; meta["apiCallSuccess"] = true; meta["dataComplete"] = items <= maxItems && copies <= maxItems;
                meta["scope"] = "Library header (GlobalLibrary scalars, comment per culture, history entries, used products), type folder tree with consistency Status / Guid / DoNotUse / SetForUpdate / version summary, master copy tree with ContentDescriptions. Bounded by maxDepth/maxItems.";
                return "Library overview read; no modification.";
            }, requiresProject: false);

        public HardwareAddressingReply ReadLibraryType(string libraryName = "", string typePath = "", string guid = "", int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("GetLibraryType", meta => {
                EngineeringEvidence.ValidatePage(offset, limit);
                var parsed = LibraryPolicy.ParseGuid(guid, "guid");
                if ((parsed == null) == string.IsNullOrEmpty(typePath)) throw new ArgumentException("Give exactly one of typePath (relative to the Types folder) or guid (type or version GUID).");
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                LibraryType type; LibraryTypeVersion? matchedVersion = null;
                if (parsed != null)
                {
                    var byType = lib.FindType(parsed.Value);
                    if (byType != null) type = byType;
                    else { matchedVersion = lib.FindVersion(parsed.Value) ?? throw new PlcSoftwareException("NotFound", "No type or version with GUID " + parsed.Value + " in this library (FindType/FindVersion both returned null)."); type = matchedVersion.TypeObject; }
                    meta["foundBy"] = matchedVersion == null ? "FindType" : "FindVersion";
                }
                else type = ExactLibraryType(library, typePath);
                meta["typePath"] = LibraryPathOf(type, "LibraryTypeSystemFolder") is { Length: > 0 } p ? p + "/" + type.Name : type.Name;
                meta["type"] = TypeRow(type, false);
                #if LIBRARY_V20
                try { meta["supportedExportFormats"] = new List<object?>(PlcGroupOperations.Items(type.GetSupportedExportFormats()).Select(f => (object)f.ToString()).ToArray()); }
                catch (Exception ex) { meta["supportedExportFormatsError"] = ex.GetBaseException().Message; }
#endif
                if (matchedVersion != null) meta["matchedVersion"] = VersionRow(matchedVersion, true);
                var versions = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Select(v => (object)VersionRow(v, true)).ToArray();
                LibraryPage(versions, offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "LibraryType scalars incl. Guid / Namespace / DoNotUse / SetForUpdate / MinimumTargetDeviceVersion / Status, GetSupportedExportFormats, and every version with Dependencies / Dependents / MasterCopiesContainingInstances / OriginalLibrary (failures captured per field).";
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
                meta["scope"] = "Native ILibrary.UpdateCheck(project, mode) message tree: Description, parts {Key: Value} (DeviceName / LibraryTypeName / LibraryVersionNumber / UpToDate / PathToInstance / InstanceName / CurrentVersionNumber), nested Messages. Read-only; no update performed.";
                return count == 0 ? "Update check completed: no messages (nothing out of date for this mode)." : "Update check completed; inspect the message tree.";
            });

        // ---- write --------------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ManageLibraryType(string typePath, string action, string libraryName = "", Dictionary<string, HardwareScalar> properties = null!, string targetLibraryName = "", string[] scopePaths = null!,
            string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure", string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", bool confirmDelete = false, bool dryRun = true)
            => RunEngineeringServicesStep("ManageLibraryType", meta => {
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["typePath"] = typePath; meta["libraryName"] = libraryName;
                
                LibraryPolicy.ValidateTypeRequest(action, properties, targetLibraryName, libraryName, scopePaths);
                LibraryPolicy.RequireOneOf(deleteUnusedVersionsMode, LibraryPolicy.DeleteUnusedVersionsModes, "deleteUnusedVersionsMode");
                LibraryPolicy.RequireOneOf(structureConflictResolutionMode, LibraryPolicy.StructureConflictResolutionModes, "structureConflictResolutionMode");
                LibraryPolicy.RequireOneOf(forceUpdateMode, LibraryPolicy.ForceUpdateModes, "forceUpdateMode");
                if (action == "delete") LibraryRequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var library = ExactOpenEngineeringLibrary(libraryName); var type = ExactLibraryType(library, typePath);
                var prepared = action == "update" ? LibraryPrepare(type.GetType(), properties) : null;
                if (action == "update" && library is GlobalLibrary global) LibraryPolicy.ValidateTypeWriteProtection(LibraryWriteProtected(global));
                meta["before"] = TypeRow(type, true);
                switch (action)
                {
                    case "update":
                    {
                        meta["requestedProperties"] = HardwareScalarEvidence.Requested(properties);
                        if (dryRun) return "Library type update preview; known unsafe writes and library restrictions checked. Nothing changed.";
                        LibraryApply(type, prepared!, meta); meta["after"] = TypeRow(type, false);
                        return "Library type properties written and read back. No save.";
                    }
                    case "delete":
                    {
                        var parent = EngineeringLibraryFolder(library, LibraryPathOf(type, "LibraryTypeSystemFolder"), "TypeFolder"); var name = type.Name;
                        if (dryRun) return "Library type delete preview; deletes every version. Nothing changed.";
                        meta["mayHaveChanged"] = true; type.Delete();
                        if (PlcGroupOperations.Find(PlcGroupOperations.Get(parent, "Types"), name) != null) throw new InvalidOperationException("Delete returned but the type is still listed in its folder; do not blindly retry.");
                        meta["verifiedAbsent"] = true; return "Library type deleted and verified absent. No save.";
                    }
                    case "updateLibrary":
                    {
                        var target = AsLibrary(ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = EngineeringLibraryRef(target);
                        if (dryRun) return "Type UpdateLibrary preview (single-type overload with DeleteUnusedVersionsMode / StructureConflictResolutionMode / ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        type.UpdateLibrary(target, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
#else
                        type.UpdateLibrary(target);
#endif
                        var synced = target.FindType(type.Guid); meta["targetTypeAfter"] = synced == null ? null : TypeRow(synced, true);
                        return "Type synchronized into the target library; target readback by GUID attached. No save.";
                    }
                    default: // updateProject
                    {
                        var scopes = ResolveScopes(scopePaths, meta);
                        if (dryRun) return "Type UpdateProject preview (per scope, with DeleteUnusedVersionsMode / StructureConflictResolutionMode / ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        foreach (var scope in scopes) type.UpdateProject(scope, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
#else
                        foreach (var scope in scopes) type.UpdateProject(scope);
#endif
                        meta["after"] = TypeRow(type, true);
                        return "Instances of the type updated in the selected scopes (native version choice). No save/compile/download.";
                    }
                }
            });

        public HardwareAddressingReply SynchronizeLibrary(string action, LibrarySelection[] selection = null!, string libraryName = "", string targetLibraryName = "", string[] scopePaths = null!,
            string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure",
            string[] harmonizeOptions = null!, string cleanUpMode = "PreserveDefaultVersionOfUnusedTypes", bool confirmChange = false, bool dryRun = true)
            => RunEngineeringServicesStep("SynchronizeLibrary", meta => {
                
                LibraryPolicy.ValidateSyncRequest(action, libraryName, targetLibraryName, scopePaths, selection, forceUpdateMode, deleteUnusedVersionsMode, structureConflictResolutionMode, cleanUpMode);
                var harmonize = action == "harmonizeProject" ? LibraryPolicy.JoinHarmonizeOptions(harmonizeOptions) : "";
                LibraryRequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["library"] = EngineeringLibraryRef(library);
                var selected = ResolveSelection(library, selection, meta);
                switch (action)
                {
                    case "updateLibrary":
                    {
                        var target = AsLibrary(ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = EngineeringLibraryRef(target);
                        meta["modes"] = new Dictionary<string, object?> { ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode, ["structureConflictResolutionMode"] = structureConflictResolutionMode };
                        if (dryRun) return "UpdateLibrary preview; nothing changed (types are matched by GUID; a version GUID mismatch aborts natively).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        lib.UpdateLibrary(selected, target, ParseEnum<ForceUpdateMode>(forceUpdateMode), ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
#else
                        lib.UpdateLibrary(selected, target);
#endif
                        meta["targetTypesAfter"] = new List<object?>(selected.OfType<LibraryType>().Select(t => target.FindType(t.Guid)).Where(t => t != null).Select(t => (object)TypeRow(t!, false)).ToArray());
                        return "Selected types/folders synchronized into the target library. No save.";
                    }
                    case "updateProject":
                    {
                        var scopes = ResolveScopes(scopePaths, meta);
                        meta["modes"] = new Dictionary<string, object?> { ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode, ["structureConflictResolutionMode"] = structureConflictResolutionMode };
                        if (dryRun) return "UpdateProject preview; nothing changed (a global library source synchronizes the project library first).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        if (library is ProjectLibrary projectLibrary) projectLibrary.UpdateProject(selected, scopes, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode));
                        else ((GlobalLibrary)library).UpdateProject(selected, scopes, ParseEnum<ForceUpdateMode>(forceUpdateMode), ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
#else
                        if (library is ProjectLibrary projectLibrary) projectLibrary.UpdateProject(selected, scopes);
                        else ((GlobalLibrary)library).UpdateProject(selected, scopes);
#endif
                        return "Project instances updated from the selected types/folders in the given scopes. No save/compile/download.";
                    }
                    case "harmonizeProject":
                    {
                        var scopes = ResolveScopes(scopePaths, meta); meta["harmonizeOptions"] = harmonize;
                        if (dryRun) return "HarmonizeProject preview; nothing changed (renames instances / moves them into the library folder structure).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        lib.HarmonizeProject(selected, scopes, ParseEnum<HarmonizeProjectOptions>(harmonize));
#else
                        throw new NotSupportedException("This library action requires V17 or later.");
#endif
                        return "Project instances harmonized (" + harmonize + ") in the given scopes. No save/compile/download.";
                    }
                    default: // cleanUp
                    {
                        meta["cleanUpMode"] = library is ProjectLibrary ? cleanUpMode : "(global library: fixed native behaviour, types are never deleted completely)";
                        if (dryRun) return "CleanUpLibrary preview; nothing changed (deletes unused, non-in-test versions of the selection).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        if (library is ProjectLibrary projectLibrary) projectLibrary.CleanUpLibrary(selected, ParseEnum<CleanUpMode>(cleanUpMode));
                        else if (library is UserGlobalLibrary user) user.CleanUpLibrary(selected);
                        else throw new NotSupportedException("CleanUpLibrary exists on ProjectLibrary and UserGlobalLibrary only (" + library.GetType().Name + ").");
#else
                        throw new NotSupportedException("This library action requires V17 or later.");
#endif
                        meta["after"] = new List<object?>(selected.OfType<LibraryType>().Select(t => (object)TypeRow(t, true)).ToArray());
                        return "Library clean-up completed for the selection. No save.";
                    }
                }
            });

#if LIBRARY_COMPARE
        public HardwareAddressingReply CompareLibraryObjects(string kind, string leftPath, string rightPath, string leftLibraryName = "", string rightLibraryName = "", string leftVersion = "", string rightVersion = "",
            bool includeIdentical = false, int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("CompareLibraryObjects", meta => {
                LibraryPolicy.ValidateCompareRequest(kind, leftPath, rightPath, leftVersion, rightVersion); EngineeringEvidence.ValidatePage(offset, limit);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                var leftLibrary = ExactOpenEngineeringLibrary(leftLibraryName); var rightLibrary = ExactOpenEngineeringLibrary(rightLibraryName);
                DetailedCompareResult result; string leftName, rightName;
                switch (kind)
                {
                    case "type": { var l = ExactLibraryType(leftLibrary, leftPath); var r = ExactLibraryType(rightLibrary, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
                    case "version": { var l = ExactTypeVersion(ExactLibraryType(leftLibrary, leftPath), leftVersion); var r = ExactTypeVersion(ExactLibraryType(rightLibrary, rightPath), rightVersion); leftName = l.TypeObject?.Name + " " + l.VersionNumber; rightName = r.TypeObject?.Name + " " + r.VersionNumber; result = l.CompareTo(r); break; }
                    default: { var l = ExactMasterCopy(leftLibraryName, leftPath); var r = ExactMasterCopy(rightLibraryName, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
                }
                meta["kind"] = kind; meta["left"] = leftName; meta["right"] = rightName; meta["resultType"] = result.GetType().FullName;
                var rows = PlcGroupOperations.Items(result.Properties).Cast<DetailedCompareResultElement>()
                    .Select(e => new Dictionary<string, object?> { ["description"] = e.Description, ["status"] = e.DetailCompareStatus.ToString(), ["leftValue"] = LibraryValue(e.LeftValue), ["rightValue"] = LibraryValue(e.RightValue) }).ToArray();
                meta["summary"] = rows.GroupBy(r => (string)r["status"]!).ToDictionary(g => g.Key, g => (object?)g.Count());
                var selected = includeIdentical ? rows : rows.Where(r => (string)r["status"]! != "ObjectsIdentical").ToArray();
                LibraryPage(selected.Cast<object>().ToArray(), offset, limit, meta);
                meta["totalProperties"] = rows.Length; meta["includeIdentical"] = includeIdentical; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "Native DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue); OriginalLibrary is not compared by default per the official note. Read-only.";
                return "Detailed comparison completed; " + rows.Length + " properties compared.";
            }, requiresProject: false);
#endif
        public HardwareAddressingReply ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",string destinationPath="",bool dryRun=true)
            =>RunEngineeringServicesStep("ManageLibraryMasterCopy",meta=>{
                if (!new[] { "read", "copy", "compare", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/copy/compare/delete (case-sensitive).");
                using var access=!dryRun&&(action=="copy"||action=="delete") ? HardwareEditAccess() : null;
                var source=ExactMasterCopy(libraryName,sourcePath);meta["before"]=LibraryObjectAddress(source);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(action=="read")return "Master copy scalar properties read.";
                #if LIBRARY_COMPARE
                if(action=="compare") { var other=ExactMasterCopy(destinationLibraryName,destinationPath);meta["comparison"]=HardwareOfficialService.Result(source.CompareTo(other));return "Native comparison returned; inspect differences and exclusions."; }
#endif
                MasterCopyFolder? folder=null;
                if(action=="copy") {
                    folder=(MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(destinationLibraryName),destinationPath,"MasterCopyFolder");
                    if(PlcGroupOperations.Find(folder.MasterCopies,source.Name)!=null)throw new InvalidOperationException("Destination master-copy name exists; overwrite refused.");
                }
                if(dryRun)return "Master-copy mutation preview; no changes.";
                meta["mayHaveChanged"]=true;
                if(action=="copy")meta["after"]=LibraryObjectAddress(folder!.MasterCopies.CreateFrom(source));
                else {
                    var parts=PlcGroupOperations.Parts(sourcePath);
                    var parent=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
                    source.Delete();meta["deleteReturned"]=true;
                    if(PlcGroupOperations.Find(PlcGroupOperations.Get(parent,"MasterCopies"),parts.Last())!=null)throw new InvalidOperationException("Master-copy deletion not verified.");
                    meta["absenceVerified"]=true;
                }
                return "Native master-copy operation returned; no project/library save or Portal close.";
            });
#if LIBRARY_DOCUMENT_IMPORT
        public HardwareAddressingReply ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",string typePath="",string createOptions="None",string targetSoftwarePath="",string targetGroupKind="",string targetGroupPath="",bool dryRun=true)
            =>RunEngineeringServicesStep("ImportLibraryTypeDocuments",meta=>{
                var file=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));if(!file.Exists)throw new FileNotFoundException("Library type native document not found.");
                LibraryPolicy.RequireOneOf(importOptions,LibraryPolicy.ImportOptions,"importOptions");LibraryPolicy.RequireOneOf(createOptions,LibraryPolicy.CreateOptions,"createOptions");
                if(targetGroupKind!="" && targetGroupKind!="blocks" && targetGroupKind!="types")throw new ArgumentException("targetGroupKind must be empty, blocks or types.");
                if((targetGroupKind=="")!=string.IsNullOrEmpty(targetSoftwarePath))throw new ArgumentException("targetSoftwarePath and targetGroupKind go together (STEP 7 documents need a PlcBlockGroup/PlcTypeGroup target environment).");
                var library=ExactOpenEngineeringLibrary(libraryName);
                string basePath=Path.Combine(file.DirectoryName!,Path.GetFileNameWithoutExtension(file.Name));
                LibraryPolicy.ValidateDocumentImportScope(int.Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey), library is ProjectLibrary,
                    importOptions, File.Exists(basePath+".xml"), File.Exists(basePath+".s7dcl"));
                var options=(LibraryImportOptions)System.Enum.Parse(typeof(LibraryImportOptions),importOptions);var create=(CreateOptions)System.Enum.Parse(typeof(CreateOptions),createOptions);
                string fileName=Path.GetFileNameWithoutExtension(file.Name);meta["fileName"]=fileName;meta["importOptions"]=importOptions;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                IEngineeringObject? environment=null;
                if(targetGroupKind!=""){
                    var plc=EngineeringPlcSession.ExactPlcForEngineering(targetSoftwarePath,false);
                    environment=(IEngineeringObject)PlcGroupOperations.Group(targetGroupKind=="blocks"?(object)plc.BlockGroup:plc.TypeGroup,targetGroupPath);
                    meta["targetEnvironment"]=new Dictionary<string, object?>{["softwarePath"]=targetSoftwarePath,["groupKind"]=targetGroupKind,["groupPath"]=targetGroupPath,["type"]=environment.GetType().Name};
                }
                if(!string.IsNullOrEmpty(typePath)){
                    // Version import: LibraryTypeVersionComposition.CreateFromDocuments on an existing type ("Updating type from document").
                    var type=ExactLibraryType(library,typePath);meta["typePath"]=typePath;meta["createOptions"]=createOptions;meta["before"]=TypeRow(type,true);
                    if(dryRun)return "Library version document import preview; CreateOptions.None fails natively if an in-work version exists.";
                    using var access=HardwareEditAccess();meta["mayHaveChanged"]=true;
                    VersionCreateTransferResults result=environment==null?type.Versions.CreateFromDocuments(file.Directory!,fileName,create,options):type.Versions.CreateFromDocuments(file.Directory!,fileName,environment,create,options);
                    meta["transferResultState"]=result.TransferResultState.ToString();

                    meta["messages"]=new List<object?>(PlcGroupOperations.Items(result.Messages).Cast<TransferResultMessage>().Select(m=>(object)m.Message).ToArray());

                    meta["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Library;
                    PlcSoftwareValues.RecordNativeResult(meta, meta["transferResultState"], meta["messages"]);
                    meta["createdVersion"]=result.CreatedVersion==null?null:VersionRow(result.CreatedVersion,false);meta["after"]=TypeRow(type,true);meta["apiCallSuccess"]=true;
                    if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.TransferResultState))meta["operationSuccess"]=false;
                    return "Native version import returned "+result.TransferResultState+"; created version and messages attached. No automatic library/project save.";
                }
                var folder=EngineeringLibraryFolder(library,folderPath,"TypeFolder");
                var types=(LibraryTypeComposition)PlcGroupOperations.Get(folder,"Types");
                if(dryRun)return "Library native document import preview; type/dependency semantics checked on execution.";
                using var edit=HardwareEditAccess();meta["mayHaveChanged"]=true;
                TypeCreateTransferResults created=environment==null?types.CreateFromDocuments(file.Directory!,fileName,options):types.CreateFromDocuments(file.Directory!,fileName,environment,options);
                meta["transferResultState"]=created.TransferResultState.ToString();

                meta["messages"]=new List<object?>(PlcGroupOperations.Items(created.Messages).Cast<TransferResultMessage>().Select(m=>(object)m.Message).ToArray());

                meta["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Library;
                PlcSoftwareValues.RecordNativeResult(meta, meta["transferResultState"], meta["messages"]);
                meta["createdType"]=created.CreatedType==null?null:TypeRow(created.CreatedType,true);meta["apiCallSuccess"]=true;
                if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(created.TransferResultState))meta["operationSuccess"]=false;
                return "Native type import returned "+created.TransferResultState+"; created type (default version InWork) and messages attached. No automatic library/project save.";
            });
#endif
        public HardwareAddressingReply ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false,
            string archiveName="", string archiveMode="Compressed", bool dryRun=true)
            => RunEngineeringServicesStep("ManageGlobalLibrary", meta => {
                if (portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (!new[] { "list", "infos", "create", "open", "openInfo", "retrieve", "save", "saveAs", "close", "archive" }.Contains(action)) throw new ArgumentException("action must be one of: list/infos/create/open/openInfo/retrieve/save/saveAs/close/archive (case-sensitive).");
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
                    meta["records"] = new List<object?>(infos.Select(i => (object)new Dictionary<string, object?> { ["name"] = i.Name, ["path"] = i.Path?.FullName, ["libraryType"] = i.LibraryType.ToString(), ["isOpen"] = i.IsOpen }).ToArray());
                    meta["actualCount"] = infos.Length; meta["truncated"] = false; meta["dataComplete"] = true; meta["apiCallSuccess"] = true;
                    return "Global library infos read (GetGlobalLibraryInfos); nothing opened.";
                }
                if (action == "openInfo") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Exact library name from action=infos required.");
                    var info = libraries.GetGlobalLibraryInfos().FirstOrDefault(i => string.Equals(i.Name, libraryName, StringComparison.Ordinal)) ?? throw new PlcSoftwareException("NotFound", "No global library info named " + libraryName + " (see action=infos).");
                    meta["info"] = new Dictionary<string, object?> { ["name"] = info.Name, ["path"] = info.Path?.FullName, ["libraryType"] = info.LibraryType.ToString(), ["isOpen"] = info.IsOpen };
                    if (info.IsOpen) throw new InvalidOperationException("Library is already open.");
                    if (dryRun) return "Open-by-info preview; nothing opened (system/corporate libraries open read-only).";
                    meta["mayHaveChanged"] = true;
                    var opened = libraries.Open(info);
                    meta["after"] = HardwareScalarEvidence.Read(opened); meta["libraryClass"] = opened.GetType().Name; meta["apiCallSuccess"] = true;
                    meta["closableThroughApi"] = opened is UserGlobalLibrary; meta["hasTypeFolder"] = ((ILibrary)opened).TypeFolder != null;
                    return opened is UserGlobalLibrary
                        ? "Global library opened from its info entry (GlobalLibraryComposition.Open(GlobalLibraryInfo)). Close is explicit."
                        : "Global library opened from its info entry as " + opened.GetType().Name + "; the Openness API has Close only on UserGlobalLibrary, so it stays open until TIA closes it.";
                }
#if LIBRARY_V151
                if (action == "archive") {
                    LibraryPolicy.RequireOneOf(archiveMode, LibraryPolicy.ArchivationModes, "archiveMode"); LibraryPolicy.ValidateArchiveName(archiveName);
                    var target = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                    var archiveDirectory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                    meta["library"] = HardwareScalarEvidence.Read(target); meta["archiveMode"] = archiveMode; meta["archiveName"] = archiveName; meta["destinationDirectory"] = archiveDirectory.FullName;
                    if (target.IsModified) throw new InvalidOperationException("Library has unsaved changes; TIA refuses Archive until it is saved (action=save first).");
                    if (dryRun) return "Archive preview; nothing written (None/DiscardRestorableData produce a folder that cannot be retrieved through the API; Compressed modes produce a .zalXX file).";
                    meta["mayHaveChanged"] = true;
                    var before = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Select(f => f.Name).ToArray() : Array.Empty<string>();
                    target.Archive(archiveDirectory, archiveName, (LibraryArchivationMode)System.Enum.Parse(typeof(LibraryArchivationMode), archiveMode));
                    var created = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Where(f => !before.Contains(f.Name)).Select(f => (object)new Dictionary<string, object?> { ["name"] = f.Name, ["bytes"] = f is FileInfo fi ? fi.Length : (long?)null }).ToArray() : Array.Empty<object>();
                    meta["createdEntries"] = new List<object?>(created); meta["apiCallSuccess"] = true;
                    if (created.Length == 0) { meta["operationSuccess"] = false; return "Archive returned but no new entry appeared in the destination directory; inspect the directory before retrying."; }
                    return "User global library archived; new directory entries listed. The library stays open at its original location.";
                }
#endif
                // An empty openMode defaults to ReadOnly before enum parsing, including actions that do not open a library.
                if (string.IsNullOrWhiteSpace(openMode)) openMode = "ReadOnly";
                if (!System.Enum.GetNames(typeof(OpenMode)).Contains(openMode)) throw new ArgumentException("openMode must be one of: " + string.Join("/", System.Enum.GetNames(typeof(OpenMode))) + ".");
                var mode = (OpenMode)System.Enum.Parse(typeof(OpenMode), openMode);
                UserGlobalLibrary? library = null; FileInfo? file = null; DirectoryInfo? directory = null;
                if (action == "create" || action == "open" || action == "retrieve") {
                    if (string.IsNullOrWhiteSpace(libraryName)) throw new ArgumentException("Expected library name required.");
                    if (PlcGroupOperations.Find(libraries, libraryName) != null) throw new InvalidOperationException("Library with this name already open.");
                } else library = ExactOpenUserGlobalLibrary(libraries, libraryName, action);
                if (action == "open" || action == "retrieve") {
                    file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (!file.Exists) throw new FileNotFoundException("Library file not found.");
                }
                if (action == "create" || action == "retrieve" || action == "saveAs") {
                    if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute new destination directory required.");
                    directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                    if (directory.Exists || File.Exists(directory.FullName)) throw new IOException("Destination already exists; overwrite/merge refused.");
                }
                if (upgrade && action != "open" && action != "retrieve") throw new ArgumentException("upgrade applies only to open/retrieve.");
                if (upgrade && action == "open" && mode != OpenMode.ReadWrite) throw new ArgumentException("OpenWithUpgrade requires explicit ReadWrite mode; native overload has no mode parameter.");
                if (dryRun) return "Library lifecycle preview. Opening/upgrading can write library files; close is explicit and never saves automatically.";
                meta["mayHaveChanged"] = true;
                switch (action) {
                    case "create": library = (UserGlobalLibrary)libraries.Create<UserGlobalLibrary>(directory!, libraryName); break;
                    case "open": library = upgrade ? libraries.OpenWithUpgrade(file!) : libraries.Open(file!,mode); break;
#if LIBRARY_V151
                    case "retrieve": library = upgrade ? libraries.RetrieveWithUpgrade(file!,directory!,mode) : libraries.Retrieve(file!,directory!,mode); break;
#endif
                    case "save": library!.Save(); break;
#if LIBRARY_V151
                    case "saveAs": library!.SaveAs(directory!); break;
#endif
                    case "close": library!.Close(); meta["closed"] = true; return "Selected global library closed; project and Portal remain open. No implicit save.";
                }
                meta["after"] = HardwareScalarEvidence.Read(library!); meta["apiCallSuccess"] = true;
                if (library!.Name != libraryName) { meta["operationSuccess"] = false; meta["actualName"] = library.Name; return "Native operation completed but opened library name differs; handle retained, no automatic close or rename."; }
                return "Native global library operation completed. Project not saved, compiled or downloaded.";
            }, requiresProject:false);

        // Close / Save / SaveAs / Archive exist on UserGlobalLibrary only; a system library opened through Open(GlobalLibraryInfo) is
        // found but cannot be closed through the API (observed on TIA V21) - say so instead of "not found".
        private static UserGlobalLibrary ExactOpenUserGlobalLibrary(GlobalLibraryComposition libraries, string libraryName, string action)
        {
            var found = PlcGroupOperations.Find(libraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found (action=list shows the open ones).");
            return found as UserGlobalLibrary ?? throw new NotSupportedException(LibraryPolicy.UserGlobalLibraryOnlyMessage(action, libraryName, found.GetType().Name));
        }
        public HardwareAddressingReply ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
            => RunEngineeringServicesStep("ManageLibraryFolder", meta => {
                if (folderKind != "types" && folderKind != "masterCopies") throw new ArgumentException("folderKind must be types/masterCopies.");
                if (!new[] { "read", "create", "rename", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/create/rename/delete (case-sensitive).");
                var parts = PlcGroupOperations.Parts(folderPath); // Root cannot be mutated.
                var library = ExactOpenEngineeringLibrary(libraryName);
                var parent = EngineeringLibraryFolder(library,string.Join("/",parts.Take(parts.Length-1)),folderKind=="types" ? "TypeFolder" : "MasterCopyFolder");
                var collection = PlcGroupOperations.Get(parent,"Folders");
                var target = PlcGroupOperations.Find(collection,parts.Last());
                if ((action=="create") == (target!=null)) throw new InvalidOperationException(action=="create" ? "Folder exists." : "Exact folder not found.");
                if (action=="delete" && (PlcGroupOperations.Items(PlcGroupOperations.Get(target!,"Folders")).Any() || PlcGroupOperations.Items(PlcGroupOperations.Get(target!,folderKind=="types" ? "Types" : "MasterCopies")).Any())) throw new InvalidOperationException("Only empty folders can be deleted.");
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
                } else LibraryApply(target!,LibraryPrepare(target!.GetType(),new Dictionary<string, HardwareScalar>{["Name"]=new HardwareScalar { Kind = "string", Text = newName }}),meta);
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
                var masterCopies = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
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
                    warnings.Add("No master copies/types were listed through public/reflection access; use DescribeObject/DescribeObjectProperty for deeper API discovery.");
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

        public GlobalLibraryImportEvidence ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
        {
            var attempts = new List<string>();
            var warnings = new List<string>();
            var raw = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath,
                ["masterCopyName"] = masterCopyName,
                ["hmiSoftwarePath"] = hmiSoftwarePath,
                ["screenName"] = screenName,
                ["left"] = left,
                ["top"] = top
            };

            try
            {
                if (portal == null)
                {
                    return GlobalLibraryImportFailure("TIA Portal is not connected. Call ConnectPortal first.");
                }

                if ((project == null))
                {
                    return GlobalLibraryImportFailure("Project is null. Open or attach a temporary project first.");
                }

                if (string.IsNullOrWhiteSpace(masterCopyName))
                {
                    return GlobalLibraryImportFailure("masterCopyName is required and must come from ProbeGlobalLibrary readback.");
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return GlobalLibraryImportFailure("Global library .al file not found.");
                }

                var globalLibraries = TryGetPropertyValue(portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return GlobalLibraryImportFailure("TiaPortal.GlobalLibraries property not found.");
                }

                var screen = ResolveLibraryHmiScreen(hmiSoftwarePath, screenName);
                var screenItems = TryGetPropertyValue(screen, "ScreenItems");
                if (screenItems == null)
                {
                    return GlobalLibraryImportFailure("Target screen has no ScreenItems collection.");
                }

                var before = ListNamedChildren(screenItems, 200);
                raw["screenItemsBefore"] = LibraryList(before);

                // Reuse a library already opened by the user or ManageGlobalLibrary and leave it open afterwards.
                bool wasAlreadyOpen = false;
                object? library = FindOpenGlobalLibraryByFile(globalLibraries, resolved!);
                string? openError = null;
                if (library != null) wasAlreadyOpen = true; else library = TryOpenGlobalLibrary(globalLibraries, resolved!, out openError);
                if (library == null)
                {
                    return GlobalLibraryImportFailure(openError ?? "Failed to open global library.");
                }

                try
                {
                    var masterCopy = FindLibraryObjectByPathOrName(library, masterCopyName, attempts, "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
                    if (masterCopy == null)
                    {
                        raw["libraryMembers"] = string.Join(" | ", LibraryReflectionValues.DescribeMembers(library, 160).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return GlobalLibraryImportFailure("MasterCopy was not found by exact/suffix path or name.");
                    }

                    raw["masterCopyType"] = masterCopy.GetType().FullName ?? masterCopy.GetType().Name;
                    raw["masterCopyMembers"] = new List<object?>(LibraryReflectionValues.DescribeMembers(masterCopy, 240)
                        .Select(m => LibraryValue($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}"))
                        .ToArray());
                    raw["masterCopyAttributes"] = LibraryList(LibraryInterestingAttributes(masterCopy));
                    var expectedName = string.IsNullOrWhiteSpace(importedItemName)
                        ? LastPathSegment(masterCopyName)
                        : importedItemName.Trim();

                    object? imported = TryImportMasterCopyIntoScreen(screen, screenItems, masterCopy, expectedName, left, top, attempts);
                    if (imported != null)
                    {
                        LibraryTrySetProperty(imported, "Left", left);
                        LibraryTrySetProperty(imported, "Top", top);
                        if (!string.IsNullOrWhiteSpace(expectedName))
                            LibraryTrySetProperty(imported, "Name", expectedName);
                    }

                    var after = ListNamedChildren(screenItems, 500);
                    raw["screenItemsAfter"] = LibraryList(after);
                    var readbackName = ResolveImportedReadbackName(before, after, expectedName);
                    var ok = !string.IsNullOrWhiteSpace(readbackName);
                    if (!ok)
                    {
                        warnings.Add("Import attempts finished, but the target screen item was not visible in readback.");
                        if (!attempts.Any(x => x.StartsWith("Try ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Try source ", StringComparison.OrdinalIgnoreCase)))
                        {
                            warnings.Add("No compatible public Openness import/copy method was found. The target ScreenItems collection exposed only create-style methods, and the MasterCopy object exposed no copy/instantiate method.");
                        }
                    }

                    return new GlobalLibraryImportEvidence
                    {
                        Ok = ok,
                        Message = ok
                            ? "Global library MasterCopy imported and read back from target screen"
                            : "Global library MasterCopy import did not produce screen-item readback evidence",
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        MasterCopyName = masterCopyName,
                        HmiSoftwarePath = hmiSoftwarePath,
                        ScreenName = screenName,
                        ImportedItemName = readbackName ?? expectedName,
                        Attempts = attempts,
                        ReadbackItems = after,
                        Warnings = warnings,
                        Error = ok ? null : "No matching/new ScreenItems readback after import.",
                        Raw = raw
                    };
                }
                finally
                {
                    if (!wasAlreadyOpen) TryCloseOrDispose(library);
                }
            }
            catch (Exception ex)
            {
                return GlobalLibraryImportFailure((FormatEngineeringException?.Invoke(ex) ?? ex.ToString()));
            }

            GlobalLibraryImportEvidence GlobalLibraryImportFailure(string error)
            {
                return new GlobalLibraryImportEvidence
                {
                    Ok = false,
                    Message = "Global library MasterCopy import failed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = raw["resolvedLibraryFile"]?.ToString(),
                    MasterCopyName = masterCopyName,
                    HmiSoftwarePath = hmiSoftwarePath,
                    ScreenName = screenName,
                    ImportedItemName = importedItemName,
                    Attempts = attempts,
                    ReadbackItems = Array.Empty<string>(),
                    Warnings = warnings,
                    Error = error,
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
                    if (!string.IsNullOrEmpty(full) && string.Equals(Path.GetFullPath(full!), Path.GetFullPath(libraryFile), StringComparison.OrdinalIgnoreCase)) return candidate;
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

        private static object? BuildDefaultArgument(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(int)) return 0;
            if (type == typeof(string)) return "";
            if (type.IsEnum) return Enum.ToObject(type, 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
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

        private object? TryImportMasterCopyIntoScreen(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(EngineeringPlcSession.ReferenceEqualityComparer).ToArray();
            var sourceArgs = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(EngineeringPlcSession.ReferenceEqualityComparer!)
                .ToArray();

            foreach (var target in targets)
            {
                attempts.Add("Target candidate methods on " + target.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(target).Take(80)));
                foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => IsMasterCopyImportMethodName(m.Name)))
                {
                    var ps = method.GetParameters();
                    foreach (var source in sourceArgs)
                    {
                        if (!CanAssignParameter(ps, source))
                            continue;

                        var args = BuildMasterCopyImportArgs(ps, source!, expectedName, left, top);
                        if (args == null)
                        {
                            attempts.Add($"Skip {target.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            continue;
                        }

                        try
                        {
                            attempts.Add($"Try {target.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            var result = method.Invoke(target, args);
                            attempts.Add($"OK {target.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                            if (result != null) return result;
                            var readback = FindExistingByName(screenItems, expectedName);
                            if (readback != null) return readback;
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                        }
                        catch (Exception ex)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                        }
                    }
                }
            }

            var extensionImported = TryImportMasterCopyViaExtensionMethods(screen, screenItems, masterCopy, expectedName, left, top, attempts);
            if (extensionImported != null)
                return extensionImported;

            attempts.Add("Source candidate methods on " + masterCopy.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(masterCopy).Take(120)));
            foreach (var method in masterCopy.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => IsMasterCopySourceMethodName(m.Name)))
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                {
                    if (!CanAssignParameter(ps, target))
                        continue;

                    var args = BuildMasterCopySourceArgs(ps, target, screen, screenItems, expectedName, left, top);
                    if (args == null)
                    {
                        attempts.Add($"Skip source {masterCopy.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        continue;
                    }

                    try
                    {
                        attempts.Add($"Try source {masterCopy.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(masterCopy, args);
                        attempts.Add($"OK source {masterCopy.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private object? TryImportMasterCopyViaExtensionMethods(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(EngineeringPlcSession.ReferenceEqualityComparer).ToArray();
            var sources = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(EngineeringPlcSession.ReferenceEqualityComparer!)
                .ToArray();
            var candidates = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                .SelectMany(GetLoadableTypes)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.IsDefined(typeof(ExtensionAttribute), false))
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name))
                .Where(m => !m.ContainsGenericParameters)
                .Take(300)
                .ToList();

            attempts.Add("Extension candidate methods: " + string.Join(" | ", candidates.Select(m => m.DeclaringType?.FullName + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ")").Take(120)));

            foreach (var method in candidates)
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                foreach (var source in sources)
                {
                    var args = BuildMasterCopyExtensionArgs(ps, target, screen, screenItems, source!, expectedName, left, top);
                    if (args == null)
                        continue;

                    try
                    {
                        attempts.Add($"Try extension {method.DeclaringType?.Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(null, args);
                        attempts.Add($"OK extension {method.DeclaringType?.Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
            catch
            { /* swallow(probe-optional): Assemblies without readable type metadata contribute no extension-method candidates. */
                return Array.Empty<Type>();
            }
        }

        private static IEnumerable<string> DescribeMasterCopyCandidateMethods(object target)
        {
            return target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name) || m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ") -> " + m.ReturnType.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsMasterCopyImportMethodName(string name)
        {
            return name.Equals("CreateFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateFromMasterCopy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Import", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Paste", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Insert", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("MasterCopy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMasterCopySourceMethodName(string name)
        {
            return name.Equals("CopyTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Copy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("PasteTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Instantiate", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateInstance", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("InsertInto", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportTo", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Instantiate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanAssignParameter(ParameterInfo[] parameters, object? source)
        {
            if (parameters.Length == 0 || source == null) return false;
            return parameters.Any(p => p.ParameterType.IsInstanceOfType(source));
        }

        private static object[]? BuildMasterCopyImportArgs(ParameterInfo[] parameters, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopyExtensionArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed && sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopySourceArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed ? Array.ConvertAll(args!, a => a!) : null;
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

        private static string? ResolveImportedReadbackName(List<string> before, List<string> after, string expectedName)
        {
            if (!string.IsNullOrWhiteSpace(expectedName) && after.Any(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase)))
                return after.First(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase));

            var added = after
                .Where(x => !before.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return added.Count == 1 ? added[0] : null;
        }

        private static string LastPathSegment(string path)
        {
            var value = (path ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var parts = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[parts.Length - 1];
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

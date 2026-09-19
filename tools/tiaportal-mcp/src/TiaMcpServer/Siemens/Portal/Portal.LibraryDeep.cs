using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Compare;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Deep library family (phase 3 sub-batch 2): ILibrary update check / synchronization / harmonize / clean-up, type and
    // version details (dependencies, dependents, master copies containing instances, consistency status), detailed object
    // comparison and global library infos / archiving. Everything is the official V20/V21 Siemens.Engineering.Library API.
    public partial class Portal
    {
        private static ILibrary AsLibrary(object library) => library as ILibrary ?? throw new NotSupportedException(library.GetType().FullName + " does not implement ILibrary.");
        private static JsonNode? MultilingualJson(MultilingualText? text)
        {
            if (text == null) return null;
            var items = new JsonObject();
            foreach (var item in EngineeringGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"] = item.Text;
            return items;
        }
        private static string LibraryPathOf(object node, string rootTypeName)
        {
            // Folder path of a type/master copy/folder relative to the library's system folder, by walking Parent names.
            var names = new List<string>(); object? current = (node as IEngineeringInstance)?.Parent;
            for (int hop = 0; hop < 32 && current != null && current.GetType().Name != rootTypeName && current is not ILibrary; hop++)
            {
                names.Insert(0, EngineeringGroupOperations.Get(current, "Name").ToString()!);
                current = (current as IEngineeringInstance)?.Parent;
            }
            return string.Join("/", names);
        }
        private static JsonObject VersionRow(LibraryTypeVersion version, bool deep)
        {
            var row = new JsonObject
            {
                ["versionNumber"] = version.VersionNumber == null ? null : version.VersionNumber.ToString(), ["guid"] = version.Guid.ToString(), ["state"] = version.State.ToString(), ["isDefault"] = version.IsDefault,
                ["author"] = version.Author, ["modifiedDate"] = EngineeringScalarProperties.Json(version.ModifiedDate), ["comment"] = MultilingualJson(version.Comment),
                ["versionClass"] = version.GetType().Name
            };
            try { row["originalLibrary"] = version.OriginalLibrary; } catch (Exception ex) { row["originalLibraryError"] = ex.GetBaseException().Message; }
            if (!deep) return row;
            // Dependencies / dependents can throw on InWork versions (official note); capture instead of failing the row.
            JsonNode Assoc(Func<object> get)
            {
                try
                {
                    return new JsonArray(EngineeringGroupOperations.Items(get()).Cast<LibraryTypeVersion>()
                        .Select(v => (JsonNode)new JsonObject { ["type"] = v.TypeObject?.Name, ["versionNumber"] = v.VersionNumber == null ? null : v.VersionNumber.ToString(), ["guid"] = v.Guid.ToString(), ["state"] = v.State.ToString() }).ToArray());
                }
                catch (Exception ex) { return new JsonObject { ["error"] = ex.GetBaseException().Message }; }
            }
            row["dependencies"] = Assoc(() => version.Dependencies); row["dependents"] = Assoc(() => version.Dependents);
            try { row["masterCopiesContainingInstances"] = new JsonArray(EngineeringGroupOperations.Items(version.MasterCopiesContainingInstances).Cast<MasterCopy>().Select(m => (JsonNode)m.Name).ToArray()); }
            catch (Exception ex) { row["masterCopiesContainingInstancesError"] = ex.GetBaseException().Message; }
            return row;
        }
        // 2.7.35: the STEP 7 library type subclasses (CodeBlockLibraryType / PlcTypeLibraryType / PlcDocumentLibraryType) name what a version instantiates.
        private static string LibraryTypeKind(LibraryType type) => type switch
        {
            global::Siemens.Engineering.SW.Blocks.CodeBlockLibraryType => "codeBlock",
            global::Siemens.Engineering.SW.Types.PlcTypeLibraryType => "plcType",
            global::Siemens.Engineering.SW.Types.PlcDocumentLibraryType => "plcDocument",
            global::Siemens.Engineering.Hmi.Screen.ScreenLibraryType => "hmiScreen",
            global::Siemens.Engineering.Hmi.Screen.StyleLibraryType => "hmiStyle",
            global::Siemens.Engineering.Hmi.Screen.StyleSheetLibraryType => "hmiStyleSheet",
            global::Siemens.Engineering.Hmi.Tag.HmiUdtLibraryType => "hmiUdt",
            _ => "other"
        };
        private static JsonObject TypeRow(LibraryType type, bool versions)
        {
            var row = new JsonObject
            {
                ["name"] = type.Name, ["guid"] = type.Guid.ToString(), ["typeClass"] = type.GetType().Name, ["typeKind"] = LibraryTypeKind(type), ["author"] = type.Author, ["namespace"] = type.Namespace,
                ["comment"] = MultilingualJson(type.Comment), ["doNotUse"] = type.DoNotUse, ["minimumTargetDeviceVersion"] = type.MinimumTargetDeviceVersion?.ToString()
            };
            try { row["setForUpdate"] = type.SetForUpdate; } catch (Exception ex) { row["setForUpdateError"] = ex.GetBaseException().Message; }
            try { row["status"] = type.Status.ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message; }
            var all = EngineeringGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().ToArray();
            row["versionCount"] = all.Length; var def = all.FirstOrDefault(v => v.IsDefault); row["defaultVersion"] = def?.VersionNumber == null ? null : def.VersionNumber.ToString();
            if (versions) row["versions"] = new JsonArray(all.Select(v => (JsonNode)VersionRow(v, false)).ToArray());
            return row;
        }
        private static JsonObject MasterCopyRow(MasterCopy copy) => new JsonObject
        {
            ["name"] = copy.Name, ["author"] = copy.Author, ["creationDate"] = EngineeringScalarProperties.Json(copy.CreationDate),
            ["contents"] = new JsonArray(EngineeringGroupOperations.Items(copy.ContentDescriptions).Cast<MasterCopyContentDescription>()
                .Select(c => (JsonNode)new JsonObject { ["contentName"] = c.ContentName, ["contentType"] = c.ContentType?.FullName }).ToArray())
        };
        private static JsonObject TypeFolderTree(LibraryTypeFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
        {
            var row = new JsonObject { ["path"] = path, ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name };
            try { row["status"] = folder.Status.ToString(); } catch (Exception ex) { row["statusError"] = ex.GetBaseException().Message; }
            var types = new JsonArray(); row["types"] = types;
            foreach (var type in EngineeringGroupOperations.Items(folder.Types).Cast<LibraryType>()) { if (++items > maxItems) { row["typesTruncated"] = true; break; } types.Add(TypeRow(type, false)); }
            var folders = new JsonArray(); row["folders"] = folders;
            if (depth >= maxDepth) { row["foldersTruncated"] = EngineeringGroupOperations.Items(folder.Folders).Any(); return row; }
            foreach (var sub in EngineeringGroupOperations.Items(folder.Folders).Cast<LibraryTypeUserFolder>())
            {
                if (items > maxItems) { row["foldersTruncated"] = true; break; }
                folders.Add(TypeFolderTree(sub, path.Length == 0 ? sub.Name : path + "/" + sub.Name, depth + 1, maxDepth, ref items, maxItems));
            }
            return row;
        }
        private static JsonObject MasterCopyFolderTree(MasterCopyFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
        {
            var row = new JsonObject { ["path"] = path, ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name };
            var copies = new JsonArray(); row["masterCopies"] = copies;
            foreach (var copy in EngineeringGroupOperations.Items(folder.MasterCopies).Cast<MasterCopy>()) { if (++items > maxItems) { row["masterCopiesTruncated"] = true; break; } copies.Add(MasterCopyRow(copy)); }
            var folders = new JsonArray(); row["folders"] = folders;
            if (depth >= maxDepth) { row["foldersTruncated"] = EngineeringGroupOperations.Items(folder.Folders).Any(); return row; }
            foreach (var sub in EngineeringGroupOperations.Items(folder.Folders).Cast<MasterCopyUserFolder>())
            {
                if (items > maxItems) { row["foldersTruncated"] = true; break; }
                folders.Add(MasterCopyFolderTree(sub, path.Length == 0 ? sub.Name : path + "/" + sub.Name, depth + 1, maxDepth, ref items, maxItems));
            }
            return row;
        }
        private JsonObject LibraryHeader(object library)
        {
            var row = LibraryRef(library);
            if (library is GlobalLibrary global)
            {
                row["author"] = global.Author; row["comment"] = MultilingualJson(global.Comment); row["copyright"] = global.Copyright; row["family"] = global.Family; row["version"] = global.Version;
                row["path"] = global.Path?.FullName; row["creationTime"] = EngineeringScalarProperties.Json(global.CreationTime); row["lastModified"] = EngineeringScalarProperties.Json(global.LastModified);
                row["lastModifiedBy"] = global.LastModifiedBy; row["isModified"] = global.IsModified; row["isWriteProtected"] = global.IsWriteProtected; row["sizeKb"] = global.Size;
                row["historyEntries"] = new JsonArray(EngineeringGroupOperations.Items(global.HistoryEntries).Cast<HistoryEntry>()
                    .Select(h => (JsonNode)new JsonObject { ["dateTime"] = EngineeringScalarProperties.Json(h.DateTime), ["text"] = h.Text }).ToArray());
                row["usedProducts"] = new JsonArray(EngineeringGroupOperations.Items(global.UsedProducts).Cast<UsedProduct>()
                    .Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["version"] = p.Version }).ToArray());
            }
            return row;
        }
        private static LibraryType ExactLibraryType(object library, string typePath)
        {
            var parts = EngineeringGroupOperations.Parts(typePath);
            var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
            return (LibraryType)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(folder, "Types"), parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact library type not found: " + typePath));
        }
        private static LibraryTypeVersion ExactTypeVersion(LibraryType type, string version)
        {
            var matches = EngineeringGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Where(v => v.VersionNumber?.ToString() == version).Take(2).ToArray();
            if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, "Expected exactly one version '" + version + "' on type '" + type.Name + "', found " + matches.Length + ".");
            return matches[0];
        }
        private ILibraryTypeOrFolderSelection[] ResolveSelection(object library, LibraryDeepLogic.Selection[] selection, JsonObject meta)
        {
            var resolved = new List<ILibraryTypeOrFolderSelection>(); var rows = new JsonArray(); meta["selection"] = rows;
            foreach (var entry in selection)
            {
                if (entry.IsFolder)
                {
                    var folder = (LibraryTypeFolder)EngineeringLibraryFolder(library, entry.Path, "TypeFolder");
                    resolved.Add(folder); rows.Add(new JsonObject { ["folder"] = entry.Path, ["folderClass"] = folder.GetType().Name, ["types"] = EngineeringGroupOperations.Items(folder.Types).Count() });
                }
                else
                {
                    var type = ExactLibraryType(library, entry.Path);
                    resolved.Add(type); rows.Add(new JsonObject { ["type"] = entry.Path, ["guid"] = type.Guid.ToString(), ["versions"] = EngineeringGroupOperations.Items(type.Versions).Count() });
                }
            }
            return resolved.ToArray();
        }
        private IUpdateProjectScope[] ResolveScopes(string[] softwarePaths, JsonObject meta)
        {
            var scopes = new List<IUpdateProjectScope>(); var rows = new JsonArray(); meta["scopes"] = rows;
            foreach (var path in softwarePaths)
            {
                var software = ResolveSoftwareContainerUncached(path)?.Software ?? throw new PortalException(PortalErrorCode.NotFound, "Exact software not found: " + path);
                scopes.Add(software as IUpdateProjectScope ?? throw new NotSupportedException(path + " (" + software.GetType().Name + ") does not implement IUpdateProjectScope (PlcSoftware / HmiTarget / Unified HmiSoftware only)."));
                rows.Add(new JsonObject { ["softwarePath"] = path, ["softwareClass"] = software.GetType().Name });
            }
            return scopes.ToArray();
        }
        private static T ParseEnum<T>(string value) where T : struct => (T)System.Enum.Parse(typeof(T), value, false);

        // ---- read --------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadLibraryOverview(string libraryName = "", bool includeTypes = true, bool includeMasterCopies = true, int maxDepth = 6, int maxItems = 500)
            => RunHmiStepTool("ReadLibraryOverview", meta => {
                LibraryDeepLogic.ValidateBounds(maxDepth, maxItems);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                if (string.IsNullOrEmpty(libraryName) && IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project library requested but no project is bound.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["library"] = LibraryHeader(library);
                int items = 0;
                if (includeTypes)
                {
                    // SystemGlobalLibrary.TypeFolder is null (system libraries carry master copies only; 2.7.31 real project).
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

        public ResponseMessage ReadLibraryType(string libraryName = "", string typePath = "", string guid = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadLibraryType", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var parsed = LibraryDeepLogic.ParseGuid(guid, "guid");
                if ((parsed == null) == string.IsNullOrEmpty(typePath)) throw new ArgumentException("Give exactly one of typePath (relative to the Types folder) or guid (type or version GUID).");
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                LibraryType type; LibraryTypeVersion? matchedVersion = null;
                if (parsed != null)
                {
                    var byType = lib.FindType(parsed.Value);
                    if (byType != null) type = byType;
                    else { matchedVersion = lib.FindVersion(parsed.Value) ?? throw new PortalException(PortalErrorCode.NotFound, "No type or version with GUID " + parsed.Value + " in this library (FindType/FindVersion both returned null)."); type = matchedVersion.TypeObject; }
                    meta["foundBy"] = matchedVersion == null ? "FindType" : "FindVersion";
                }
                else type = ExactLibraryType(library, typePath);
                meta["typePath"] = LibraryPathOf(type, "LibraryTypeSystemFolder") is { Length: > 0 } p ? p + "/" + type.Name : type.Name;
                meta["type"] = TypeRow(type, false);
                try { meta["supportedExportFormats"] = new JsonArray(EngineeringGroupOperations.Items(type.GetSupportedExportFormats()).Select(f => (JsonNode)f.ToString()).ToArray()); }
                catch (Exception ex) { meta["supportedExportFormatsError"] = ex.GetBaseException().Message; }
                if (matchedVersion != null) meta["matchedVersion"] = VersionRow(matchedVersion, true);
                var versions = EngineeringGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Select(v => (JsonNode)VersionRow(v, true)).ToArray();
                Page(versions, offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "LibraryType scalars incl. Guid / Namespace / DoNotUse / SetForUpdate / MinimumTargetDeviceVersion / Status, GetSupportedExportFormats, and every version with Dependencies / Dependents / MasterCopiesContainingInstances / OriginalLibrary (failures captured per field).";
                return "Library type and versions read; no modification.";
            }, requiresProject: false);

        public ResponseMessage CheckLibraryUpdates(string libraryName = "", string updateCheckMode = "ReportOutOfDateOnly", int maxItems = 2000)
            => RunHmiStepTool("CheckLibraryUpdates", meta => {
                LibraryDeepLogic.RequireOneOf(updateCheckMode, LibraryDeepLogic.UpdateCheckModes, "updateCheckMode"); LibraryDeepLogic.ValidateBounds(1, maxItems);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "UpdateCheck runs against the bound project; attach a project first.");
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["library"] = LibraryRef(library); meta["updateCheckMode"] = updateCheckMode;
                // TIA answers UpdateCheck on a library without types (SystemGlobalLibrary) with a NonRecoverableException that the
                // connection guard treats as a lost session (2.7.31 real project): refuse before calling.
                if (lib.TypeFolder == null) throw new ArgumentException(LibraryDeepLogic.NoTypeFolderMessage(library.GetType().Name, "UpdateCheck"));
                var result = lib.UpdateCheck(_project!, ParseEnum<UpdateCheckMode>(updateCheckMode));
                int count = 0; bool truncated = false;
                JsonArray Flatten(object messages, int depth)
                {
                    var rows = new JsonArray();
                    if (depth > 12) { truncated = true; return rows; }
                    foreach (var message in EngineeringGroupOperations.Items(messages).Cast<UpdateCheckResultMessage>())
                    {
                        if (++count > maxItems) { truncated = true; break; }
                        var row = new JsonObject { ["description"] = message.Description ?? "" };
                        try
                        {
                            // Real project: every part is a KeyValuePair<string,string> (DeviceName, LibraryTypeName, LibraryVersionNumber,
                            // UpToDate, PathToInstance, InstanceName, CurrentVersionNumber); rendered flat instead of the 60x scalar dump.
                            var parts = new JsonObject(); var other = new JsonArray();
                            foreach (var part in EngineeringGroupOperations.Items(message.MessageParts))
                            {
                                var type = part.GetType();
                                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                                    parts[type.GetProperty("Key")?.GetValue(part)?.ToString() ?? ""] = EngineeringScalarProperties.Json(type.GetProperty("Value")?.GetValue(part));
                                else other.Add(EngineeringScalarProperties.Scalar(type) ? EngineeringScalarProperties.Json(part) : (JsonNode)EngineeringScalarProperties.Read(part));
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
        public ResponseMessage ManageLibraryType(string typePath, string action, string libraryName = "", string propertiesJson = "{}", string targetLibraryName = "", string scopeSoftwarePathsJson = "[]",
            string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure", string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageLibraryType", meta => {
                var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); var scopePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
                LibraryDeepLogic.ValidateTypeRequest(action, properties, targetLibraryName, libraryName, scopePaths);
                LibraryDeepLogic.RequireOneOf(deleteUnusedVersionsMode, LibraryDeepLogic.DeleteUnusedVersionsModes, "deleteUnusedVersionsMode");
                LibraryDeepLogic.RequireOneOf(structureConflictResolutionMode, LibraryDeepLogic.StructureConflictResolutionModes, "structureConflictResolutionMode");
                LibraryDeepLogic.RequireOneOf(forceUpdateMode, LibraryDeepLogic.ForceUpdateModes, "forceUpdateMode");
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var library = ExactOpenEngineeringLibrary(libraryName); var type = ExactLibraryType(library, typePath);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["typePath"] = typePath; meta["before"] = TypeRow(type, true);
                switch (action)
                {
                    case "update":
                    {
                        var prepared = EngineeringScalarProperties.Prepare(type.GetType(), properties); meta["requestedProperties"] = properties.DeepClone();
                        if (dryRun) return "Library type update preview; SetForUpdate is refused by TIA on project-library and write-protected types.";
                        EngineeringScalarProperties.Apply(type, prepared, meta); meta["after"] = TypeRow(type, false);
                        return "Library type properties written and read back. No save.";
                    }
                    case "delete":
                    {
                        var parent = EngineeringLibraryFolder(library, LibraryPathOf(type, "LibraryTypeSystemFolder"), "TypeFolder"); var name = type.Name;
                        if (dryRun) return "Library type delete preview; deletes every version. Nothing changed.";
                        meta["mayHaveChanged"] = true; type.Delete();
                        if (EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(parent, "Types"), name) != null) throw new InvalidOperationException("Delete returned but the type is still listed in its folder; do not blindly retry.");
                        meta["verifiedAbsent"] = true; return "Library type deleted and verified absent. No save.";
                    }
                    case "updateLibrary":
                    {
                        var target = AsLibrary(ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = LibraryRef(target);
                        if (dryRun) return "Type UpdateLibrary preview (single-type overload with DeleteUnusedVersionsMode / StructureConflictResolutionMode / ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
                        type.UpdateLibrary(target, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
                        var synced = target.FindType(type.Guid); meta["targetTypeAfter"] = synced == null ? null : TypeRow(synced, true);
                        return "Type synchronized into the target library; target readback by GUID attached. No save.";
                    }
                    default: // updateProject
                    {
                        var scopes = ResolveScopes(scopePaths, meta);
                        if (dryRun) return "Type UpdateProject preview (per scope, with DeleteUnusedVersionsMode / StructureConflictResolutionMode / ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
                        foreach (var scope in scopes) type.UpdateProject(scope, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
                        meta["after"] = TypeRow(type, true);
                        return "Instances of the type updated in the selected scopes (native version choice). No save/compile/download.";
                    }
                }
            });

        public ResponseMessage SynchronizeLibrary(string action, string selectionJson, string libraryName = "", string targetLibraryName = "", string scopeSoftwarePathsJson = "[]",
            string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure",
            string harmonizeOptionsJson = "[\"HarmonizeNames\",\"HarmonizePaths\"]", string cleanUpMode = "PreserveDefaultVersionOfUnusedTypes", bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("SynchronizeLibrary", meta => {
                var selection = LibraryDeepLogic.ParseSelection(selectionJson); var scopePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
                LibraryDeepLogic.ValidateSyncRequest(action, libraryName, targetLibraryName, scopePaths, selection, forceUpdateMode, deleteUnusedVersionsMode, structureConflictResolutionMode, cleanUpMode);
                var harmonize = action == "harmonizeProject" ? LibraryDeepLogic.JoinHarmonizeOptions(HardwareNetworkLogic.ParseNames(harmonizeOptionsJson, "harmonizeOptionsJson", 2)) : "";
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var library = ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["library"] = LibraryRef(library);
                var selected = ResolveSelection(library, selection, meta);
                switch (action)
                {
                    case "updateLibrary":
                    {
                        var target = AsLibrary(ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = LibraryRef(target);
                        meta["modes"] = new JsonObject { ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode, ["structureConflictResolutionMode"] = structureConflictResolutionMode };
                        if (dryRun) return "UpdateLibrary preview; nothing changed (types are matched by GUID; a version GUID mismatch aborts natively).";
                        meta["mayHaveChanged"] = true;
                        lib.UpdateLibrary(selected, target, ParseEnum<ForceUpdateMode>(forceUpdateMode), ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
                        meta["targetTypesAfter"] = new JsonArray(selected.OfType<LibraryType>().Select(t => target.FindType(t.Guid)).Where(t => t != null).Select(t => (JsonNode)TypeRow(t!, false)).ToArray());
                        return "Selected types/folders synchronized into the target library. No save.";
                    }
                    case "updateProject":
                    {
                        var scopes = ResolveScopes(scopePaths, meta);
                        meta["modes"] = new JsonObject { ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode, ["structureConflictResolutionMode"] = structureConflictResolutionMode };
                        if (dryRun) return "UpdateProject preview; nothing changed (a global library source synchronizes the project library first).";
                        meta["mayHaveChanged"] = true;
                        if (library is ProjectLibrary projectLibrary) projectLibrary.UpdateProject(selected, scopes, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode));
                        else ((GlobalLibrary)library).UpdateProject(selected, scopes, ParseEnum<ForceUpdateMode>(forceUpdateMode), ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode), ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
                        return "Project instances updated from the selected types/folders in the given scopes. No save/compile/download.";
                    }
                    case "harmonizeProject":
                    {
                        var scopes = ResolveScopes(scopePaths, meta); meta["harmonizeOptions"] = harmonize;
                        if (dryRun) return "HarmonizeProject preview; nothing changed (renames instances / moves them into the library folder structure).";
                        meta["mayHaveChanged"] = true;
                        lib.HarmonizeProject(selected, scopes, ParseEnum<HarmonizeProjectOptions>(harmonize));
                        return "Project instances harmonized (" + harmonize + ") in the given scopes. No save/compile/download.";
                    }
                    default: // cleanUp
                    {
                        meta["cleanUpMode"] = library is ProjectLibrary ? cleanUpMode : "(global library: fixed native behaviour, types are never deleted completely)";
                        if (dryRun) return "CleanUpLibrary preview; nothing changed (deletes unused, non-in-test versions of the selection).";
                        meta["mayHaveChanged"] = true;
                        if (library is ProjectLibrary projectLibrary) projectLibrary.CleanUpLibrary(selected, ParseEnum<CleanUpMode>(cleanUpMode));
                        else if (library is UserGlobalLibrary user) user.CleanUpLibrary(selected);
                        else throw new NotSupportedException("CleanUpLibrary exists on ProjectLibrary and UserGlobalLibrary only (" + library.GetType().Name + ").");
                        meta["after"] = new JsonArray(selected.OfType<LibraryType>().Select(t => (JsonNode)TypeRow(t, true)).ToArray());
                        return "Library clean-up completed for the selection. No save.";
                    }
                }
            });

        public ResponseMessage CompareLibraryObjects(string kind, string leftPath, string rightPath, string leftLibraryName = "", string rightLibraryName = "", string leftVersion = "", string rightVersion = "",
            bool includeIdentical = false, int offset = 0, int limit = 100)
            => RunHmiStepTool("CompareLibraryObjects", meta => {
                LibraryDeepLogic.ValidateCompareRequest(kind, leftPath, rightPath, leftVersion, rightVersion); HardwareServicesLogic.ValidatePagination(offset, limit);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                var leftLibrary = ExactOpenEngineeringLibrary(leftLibraryName); var rightLibrary = ExactOpenEngineeringLibrary(rightLibraryName);
                DetailedCompareResult result; string leftName, rightName;
                switch (kind)
                {
                    case "type": { var l = ExactLibraryType(leftLibrary, leftPath); var r = ExactLibraryType(rightLibrary, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
                    case "version": { var l = ExactTypeVersion(ExactLibraryType(leftLibrary, leftPath), leftVersion); var r = ExactTypeVersion(ExactLibraryType(rightLibrary, rightPath), rightVersion); leftName = l.TypeObject?.Name + " " + l.VersionNumber; rightName = r.TypeObject?.Name + " " + r.VersionNumber; result = l.CompareTo(r); break; }
                    default: { var l = ExactMasterCopy(leftLibraryName, leftPath); var r = ExactMasterCopy(rightLibraryName, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
                }
                meta["kind"] = kind; meta["left"] = leftName; meta["right"] = rightName; meta["resultType"] = result.GetType().FullName;
                var rows = EngineeringGroupOperations.Items(result.Properties).Cast<DetailedCompareResultElement>()
                    .Select(e => new JsonObject { ["description"] = e.Description, ["status"] = e.DetailCompareStatus.ToString(), ["leftValue"] = EngineeringScalarProperties.Json(e.LeftValue), ["rightValue"] = EngineeringScalarProperties.Json(e.RightValue) }).ToArray();
                meta["summary"] = new JsonObject(rows.GroupBy(r => r["status"]!.GetValue<string>()).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count())));
                var selected = includeIdentical ? rows : rows.Where(r => r["status"]!.GetValue<string>() != "ObjectsIdentical").ToArray();
                Page(selected.Cast<JsonNode>().ToArray(), offset, limit, meta);
                meta["totalProperties"] = rows.Length; meta["includeIdentical"] = includeIdentical; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "Native DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue); OriginalLibrary is not compared by default per the official note. Read-only.";
                return "Detailed comparison completed; " + rows.Length + " properties compared.";
            }, requiresProject: false);
    }
}

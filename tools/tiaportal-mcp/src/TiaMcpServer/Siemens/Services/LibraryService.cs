using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Library.Compare;

namespace TiaMcpServer.Siemens.Services
{
    // Deep library family: ILibrary update check / synchronization / harmonize / clean-up, type and
    // version details (dependencies, dependents, master copies containing instances, consistency status), detailed object
    // comparison and global library infos / archiving. Everything is the official V20/V21 Siemens.Engineering.Library API.
    // Native observations: TIA V21; original observation date not recorded. See docs/reference/real-machine-ledger.md.
    internal sealed class LibraryService
    {
        private readonly IEngineeringSession _session;

        public LibraryService(IEngineeringSession session) => _session = session;

        public ResponseMessage ManageLibraryTypeVersion(string typePath, string version, string action, string libraryName = "",
            string newVersion = "", string dependenciesMode = "", string author = "", string comment = "", string targetSoftwarePath = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageLibraryTypeVersion", meta => {
                if (!new[] { "read", "edit", "release", "setDefault", "deleteVersion", "updateInstances", "discard", "findInstances" }.Contains(action)) throw new ArgumentException("action must be one of: read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances (case-sensitive).");
                bool writing = action != "read" && action != "findInstances" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var library = _session.ExactOpenEngineeringLibrary(libraryName);
                var parts = EngineeringGroupOperations.Parts(typePath);
                var folder = _session.EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
                var type = (LibraryType)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(folder, "Types"), parts.Last()) ?? throw new InvalidOperationException("Exact library type not found."));
                var matches = type.Versions.Where(v => v.VersionNumber.ToString() == version).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one matching version, found " + matches.Length);
                var selected = matches[0];
                meta["before"] = EngineeringScalarProperties.Read(selected); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["typePath"] = typePath; meta["libraryName"] = libraryName; meta["action"] = action;
                CreateOrReleaseDependenciesMode mode = default;
                Version? releaseVersion = null;
                IUpdateProjectScope? scope = null;
                if (action == "release")
                {
                    mode = (CreateOrReleaseDependenciesMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(dependenciesMode), typeof(CreateOrReleaseDependenciesMode))!;
                    releaseVersion = Version.Parse(newVersion);
                }
                if (action == "updateInstances")
                {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("Exact targetSoftwarePath required; unbounded project-wide update refused.");
                    scope = _session.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IUpdateProjectScope
                        ?? throw new NotSupportedException("Selected software does not implement IUpdateProjectScope.");
                    meta["versionSelection"] = "Native LibraryType.UpdateProject chooses applicable released/default versions, not necessarily the version argument. The argument identifies evidence only.";
                }
                if (action == "findInstances") {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("Exact targetSoftwarePath required; whole project search refused.");
                    var search = _session.ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IInstanceSearchScope ?? throw new NotSupportedException("Selected software does not support instance search.");
                    var found = EngineeringGroupOperations.Items(selected.FindInstances(search)).ToArray();
                    meta["instances"] = new JsonArray(found.Select(x => (JsonNode)EngineeringObjectAddress.Read(x)).ToArray());
                    meta["actualCount"] = found.Length; meta["dataComplete"] = false; meta["truncated"] = false;
                    return "Native instances found in selected software; scalar result scope only.";
                }
                if (!writing) return "Library version read/preview. No modification; native semantic/state checks run only on execution.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "edit": selected = selected.Edit(); break;
                    case "discard": selected.Discard(); meta["discardedEditVersion"] = true; break;
                    case "release": selected.Release(mode, releaseVersion!, author, comment); break;
                    case "setDefault": selected.SetAsDefault(); break;
                    case "deleteVersion": selected.Delete();
                        if (type.Versions.Any(v => v.VersionNumber.ToString() == version)) throw new InvalidOperationException("Version remains after Delete.");
                        meta["verifiedAbsent"] = true; break;
                    case "updateInstances": type.UpdateProject(scope!); break;
                }
                if (action != "deleteVersion" && action != "discard") meta["after"] = EngineeringScalarProperties.Read(selected);
                return "Native library operation completed; project/library not explicitly saved. Dependency changes may occur according to the selected native operation.";
            });

        public ResponseMessage CreateLibraryMasterCopy(string sourceKind, string sourcePath, string softwarePath = "", string folderPath = "", string libraryName = "", bool dryRun = true)
            => _session.RunHmiStepTool("CreateLibraryMasterCopy", meta => {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                object source = sourceKind switch {
                    "block" => _session.ExactMasterCopyPlcSource(softwarePath, sourcePath, true),
                    "type" => _session.ExactMasterCopyPlcSource(softwarePath, sourcePath, false),
                    "device" => _session.ExactEngineeringDevice(sourcePath),
                    "screen" => HmiExactAccess.Screen(_session.ResolveHmiSoftwareOrThrow(softwarePath), sourcePath),
                    _ => throw new ArgumentException("sourceKind must be block/type/device/screen. device sourcePath is a JSON array of exact names.") };
                if (source is not IMasterCopySource copySource) throw new NotSupportedException("Selected object is not a supported native master-copy source.");
                var folder = (MasterCopyFolder)_session.EngineeringLibraryFolder(_session.ExactOpenEngineeringLibrary(libraryName), folderPath, "MasterCopyFolder");
                meta["sourceType"] = source.GetType().FullName; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; var copy = folder.MasterCopies.Create(copySource); meta["after"] = EngineeringScalarProperties.Read(copy); }
                return dryRun ? "Master copy preview; no copy created." : "Master copy created; no explicit save or close.";
            });
        private static ILibrary AsLibrary(object library) => library as ILibrary ?? throw new NotSupportedException(library.GetType().FullName + " does not implement ILibrary.");
        private JsonObject VersionRow(LibraryTypeVersion version, bool deep)
        {
            var row = new JsonObject
            {
                ["versionNumber"] = version.VersionNumber == null ? null : version.VersionNumber.ToString(), ["guid"] = version.Guid.ToString(), ["state"] = version.State.ToString(), ["isDefault"] = version.IsDefault,
                ["author"] = version.Author, ["modifiedDate"] = EngineeringScalarProperties.Json(version.ModifiedDate), ["comment"] = _session.MultilingualJson(version.Comment),
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
        // The STEP 7 library type subclasses (CodeBlockLibraryType / PlcTypeLibraryType / PlcDocumentLibraryType) name what a version instantiates;
        // Classic HMI and Unified subclasses identify their supported kinds; observed V21 Unified faceplates / graphics are plain LibraryType -> "other".
        // The SiVArc rule-table types and the DCC block type are identified by OptionPackageLibraryTypeKind.
        private string LibraryTypeKind(LibraryType type) => type switch
        {
            global::Siemens.Engineering.SW.Blocks.CodeBlockLibraryType => "codeBlock",
            global::Siemens.Engineering.SW.Types.PlcTypeLibraryType => "plcType",
            global::Siemens.Engineering.SW.Types.PlcDocumentLibraryType => "plcDocument",
            global::Siemens.Engineering.Hmi.Screen.ScreenLibraryType => "hmiScreen",
            global::Siemens.Engineering.Hmi.Screen.StyleLibraryType => "hmiStyle",
            global::Siemens.Engineering.Hmi.Screen.StyleSheetLibraryType => "hmiStyleSheet",
            global::Siemens.Engineering.Hmi.Tag.HmiUdtLibraryType => "hmiUdt",
            global::Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType => "hmiFaceplate",
            global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryType => "hmiVbScript",
            global::Siemens.Engineering.Hmi.RuntimeScripting.CScriptLibraryType => "hmiCScript",
            global::Siemens.Engineering.HmiUnified.Library.ScriptModuleType => "unifiedScriptModule",
            _ => _session.OptionPackageLibraryTypeKind(type)
        };
        private JsonObject TypeRow(LibraryType type, bool versions)
        {
            var row = new JsonObject
            {
                ["name"] = type.Name, ["guid"] = type.Guid.ToString(), ["typeClass"] = type.GetType().Name, ["typeKind"] = LibraryTypeKind(type), ["author"] = type.Author, ["namespace"] = type.Namespace,
                ["comment"] = _session.MultilingualJson(type.Comment), ["doNotUse"] = type.DoNotUse, ["minimumTargetDeviceVersion"] = type.MinimumTargetDeviceVersion?.ToString()
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
        private JsonObject TypeFolderTree(LibraryTypeFolder folder, string path, int depth, int maxDepth, ref int items, int maxItems)
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
            var row = _session.LibraryRef(library);
            if (library is GlobalLibrary global)
            {
                row["author"] = global.Author; row["comment"] = _session.MultilingualJson(global.Comment); row["copyright"] = global.Copyright; row["family"] = global.Family; row["version"] = global.Version;
                row["path"] = global.Path?.FullName; row["creationTime"] = EngineeringScalarProperties.Json(global.CreationTime); row["lastModified"] = EngineeringScalarProperties.Json(global.LastModified);
                row["lastModifiedBy"] = global.LastModifiedBy; row["isModified"] = global.IsModified; row["isWriteProtected"] = global.IsWriteProtected; row["sizeKb"] = global.Size;
                row["historyEntries"] = new JsonArray(EngineeringGroupOperations.Items(global.HistoryEntries).Cast<HistoryEntry>()
                    .Select(h => (JsonNode)new JsonObject { ["dateTime"] = EngineeringScalarProperties.Json(h.DateTime), ["text"] = h.Text }).ToArray());
                row["usedProducts"] = new JsonArray(EngineeringGroupOperations.Items(global.UsedProducts).Cast<UsedProduct>()
                    .Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["version"] = p.Version }).ToArray());
            }
            return row;
        }
        private ILibraryTypeOrFolderSelection[] ResolveSelection(object library, LibraryDeepLogic.Selection[] selection, JsonObject meta)
        {
            var resolved = new List<ILibraryTypeOrFolderSelection>(); var rows = new JsonArray(); meta["selection"] = rows;
            foreach (var entry in selection)
            {
                if (entry.IsFolder)
                {
                    var folder = (LibraryTypeFolder)_session.EngineeringLibraryFolder(library, entry.Path, "TypeFolder");
                    resolved.Add(folder); rows.Add(new JsonObject { ["folder"] = entry.Path, ["folderClass"] = folder.GetType().Name, ["types"] = EngineeringGroupOperations.Items(folder.Types).Count() });
                }
                else
                {
                    var type = _session.ExactLibraryType(library, entry.Path);
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
                var software = _session.ResolveSoftwareContainerUncached(path)?.Software ?? throw new PortalException(PortalErrorCode.NotFound, "Exact software not found: " + path);
                scopes.Add(software as IUpdateProjectScope ?? throw new NotSupportedException(path + " (" + software.GetType().Name + ") does not implement IUpdateProjectScope (PlcSoftware / HmiTarget / Unified HmiSoftware only)."));
                rows.Add(new JsonObject { ["softwarePath"] = path, ["softwareClass"] = software.GetType().Name });
            }
            return scopes.ToArray();
        }
        private static T ParseEnum<T>(string value) where T : struct => (T)System.Enum.Parse(typeof(T), value, false);

        // ---- read --------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadLibraryOverview(string libraryName = "", bool includeTypes = true, bool includeMasterCopies = true, int maxDepth = 6, int maxItems = 500)
            => _session.RunHmiStepTool("ReadLibraryOverview", meta => {
                LibraryDeepLogic.ValidateBounds(maxDepth, maxItems);
                if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                if (string.IsNullOrEmpty(libraryName) && _session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project library requested but no project is bound.");
                var library = _session.ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
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

        public ResponseMessage ReadLibraryType(string libraryName = "", string typePath = "", string guid = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadLibraryType", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var parsed = LibraryDeepLogic.ParseGuid(guid, "guid");
                if ((parsed == null) == string.IsNullOrEmpty(typePath)) throw new ArgumentException("Give exactly one of typePath (relative to the Types folder) or guid (type or version GUID).");
                if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                var library = _session.ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                LibraryType type; LibraryTypeVersion? matchedVersion = null;
                if (parsed != null)
                {
                    var byType = lib.FindType(parsed.Value);
                    if (byType != null) type = byType;
                    else { matchedVersion = lib.FindVersion(parsed.Value) ?? throw new PortalException(PortalErrorCode.NotFound, "No type or version with GUID " + parsed.Value + " in this library (FindType/FindVersion both returned null)."); type = matchedVersion.TypeObject; }
                    meta["foundBy"] = matchedVersion == null ? "FindType" : "FindVersion";
                }
                else type = _session.ExactLibraryType(library, typePath);
                meta["typePath"] = _session.LibraryPathOf(type, "LibraryTypeSystemFolder") is { Length: > 0 } p ? p + "/" + type.Name : type.Name;
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
            => _session.RunHmiStepTool("CheckLibraryUpdates", meta => {
                LibraryDeepLogic.RequireOneOf(updateCheckMode, LibraryDeepLogic.UpdateCheckModes, "updateCheckMode"); LibraryDeepLogic.ValidateBounds(1, maxItems);
                if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "UpdateCheck runs against the bound project; attach a project first.");
                var library = _session.ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["library"] = _session.LibraryRef(library); meta["updateCheckMode"] = updateCheckMode;
                // TIA answers UpdateCheck on a library without types (SystemGlobalLibrary) with a NonRecoverableException that the
                // connection guard treats as a lost session (observed on TIA V21): refuse before calling.
                if (lib.TypeFolder == null) throw new ArgumentException(LibraryDeepLogic.NoTypeFolderMessage(library.GetType().Name, "UpdateCheck"));
                var result = lib.UpdateCheck(_session.CurrentProject!, ParseEnum<UpdateCheckMode>(updateCheckMode));
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
            => _session.RunHmiStepTool("ManageLibraryType", meta => {
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["typePath"] = typePath; meta["libraryName"] = libraryName;
                var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); var scopePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
                LibraryDeepLogic.ValidateTypeRequest(action, properties, targetLibraryName, libraryName, scopePaths);
                LibraryDeepLogic.RequireOneOf(deleteUnusedVersionsMode, LibraryDeepLogic.DeleteUnusedVersionsModes, "deleteUnusedVersionsMode");
                LibraryDeepLogic.RequireOneOf(structureConflictResolutionMode, LibraryDeepLogic.StructureConflictResolutionModes, "structureConflictResolutionMode");
                LibraryDeepLogic.RequireOneOf(forceUpdateMode, LibraryDeepLogic.ForceUpdateModes, "forceUpdateMode");
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var library = _session.ExactOpenEngineeringLibrary(libraryName); var type = _session.ExactLibraryType(library, typePath);
                var prepared = action == "update" ? EngineeringScalarProperties.Prepare(type.GetType(), properties) : null;
                if (action == "update" && library is GlobalLibrary global) LibraryDeepLogic.ValidateTypeWriteProtection(global.IsWriteProtected);
                meta["before"] = TypeRow(type, true);
                switch (action)
                {
                    case "update":
                    {
                        meta["requestedProperties"] = properties.DeepClone();
                        if (dryRun) return "Library type update preview; known unsafe writes and library restrictions checked. Nothing changed.";
                        EngineeringScalarProperties.Apply(type, prepared!, meta); meta["after"] = TypeRow(type, false);
                        return "Library type properties written and read back. No save.";
                    }
                    case "delete":
                    {
                        var parent = _session.EngineeringLibraryFolder(library, _session.LibraryPathOf(type, "LibraryTypeSystemFolder"), "TypeFolder"); var name = type.Name;
                        if (dryRun) return "Library type delete preview; deletes every version. Nothing changed.";
                        meta["mayHaveChanged"] = true; type.Delete();
                        if (EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(parent, "Types"), name) != null) throw new InvalidOperationException("Delete returned but the type is still listed in its folder; do not blindly retry.");
                        meta["verifiedAbsent"] = true; return "Library type deleted and verified absent. No save.";
                    }
                    case "updateLibrary":
                    {
                        var target = AsLibrary(_session.ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = _session.LibraryRef(target);
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
            => _session.RunHmiStepTool("SynchronizeLibrary", meta => {
                var selection = LibraryDeepLogic.ParseSelection(selectionJson); var scopePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
                LibraryDeepLogic.ValidateSyncRequest(action, libraryName, targetLibraryName, scopePaths, selection, forceUpdateMode, deleteUnusedVersionsMode, structureConflictResolutionMode, cleanUpMode);
                var harmonize = action == "harmonizeProject" ? LibraryDeepLogic.JoinHarmonizeOptions(HardwareNetworkLogic.ParseNames(harmonizeOptionsJson, "harmonizeOptionsJson", 2)) : "";
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var library = _session.ExactOpenEngineeringLibrary(libraryName); var lib = AsLibrary(library);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["library"] = _session.LibraryRef(library);
                var selected = ResolveSelection(library, selection, meta);
                switch (action)
                {
                    case "updateLibrary":
                    {
                        var target = AsLibrary(_session.ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = _session.LibraryRef(target);
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
            => _session.RunHmiStepTool("CompareLibraryObjects", meta => {
                LibraryDeepLogic.ValidateCompareRequest(kind, leftPath, rightPath, leftVersion, rightVersion); HardwareServicesLogic.ValidatePagination(offset, limit);
                if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                var leftLibrary = _session.ExactOpenEngineeringLibrary(leftLibraryName); var rightLibrary = _session.ExactOpenEngineeringLibrary(rightLibraryName);
                DetailedCompareResult result; string leftName, rightName;
                switch (kind)
                {
                    case "type": { var l = _session.ExactLibraryType(leftLibrary, leftPath); var r = _session.ExactLibraryType(rightLibrary, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
                    case "version": { var l = _session.ExactTypeVersion(_session.ExactLibraryType(leftLibrary, leftPath), leftVersion); var r = _session.ExactTypeVersion(_session.ExactLibraryType(rightLibrary, rightPath), rightVersion); leftName = l.TypeObject?.Name + " " + l.VersionNumber; rightName = r.TypeObject?.Name + " " + r.VersionNumber; result = l.CompareTo(r); break; }
                    default: { var l = _session.ExactMasterCopy(leftLibraryName, leftPath); var r = _session.ExactMasterCopy(rightLibraryName, rightPath); leftName = l.Name; rightName = r.Name; result = l.CompareTo(r); break; }
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
        public ResponseMessage ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",string destinationPath="",bool dryRun=true)
            =>_session.RunHmiStepTool("ManageLibraryMasterCopy",meta=>{
                if (!new[] { "read", "copy", "compare", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/copy/compare/delete (case-sensitive).");
                using var access=!dryRun&&(action=="copy"||action=="delete") ? _session.AcquireHmiEditAccess() : null;
                var source=_session.ExactMasterCopy(libraryName,sourcePath);meta["before"]=EngineeringObjectAddress.Read(source);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(action=="read")return "Master copy scalar properties read.";
                if(action=="compare") { var other=_session.ExactMasterCopy(destinationLibraryName,destinationPath);meta["comparison"]=OfficialServiceAccess.Result(source.CompareTo(other));return "Native comparison returned; inspect differences and exclusions."; }
                MasterCopyFolder? folder=null;
                if(action=="copy") {
                    folder=(MasterCopyFolder)_session.EngineeringLibraryFolder(_session.ExactOpenEngineeringLibrary(destinationLibraryName),destinationPath,"MasterCopyFolder");
                    if(EngineeringGroupOperations.Find(folder.MasterCopies,source.Name)!=null)throw new InvalidOperationException("Destination master-copy name exists; overwrite refused.");
                }
                if(dryRun)return "Master-copy mutation preview; no changes.";
                meta["mayHaveChanged"]=true;
                if(action=="copy")meta["after"]=EngineeringObjectAddress.Read(folder!.MasterCopies.CreateFrom(source));
                else {
                    var parts=EngineeringGroupOperations.Parts(sourcePath);
                    var parent=_session.EngineeringLibraryFolder(_session.ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
                    source.Delete();meta["deleteReturned"]=true;
                    if(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(parent,"MasterCopies"),parts.Last())!=null)throw new InvalidOperationException("Master-copy deletion not verified.");
                    meta["absenceVerified"]=true;
                }
                return "Native master-copy operation returned; no project/library save or Portal close.";
            });
        public ResponseMessage ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",string typePath="",string createOptions="None",string targetSoftwarePath="",string targetGroupKind="",string targetGroupPath="",bool dryRun=true)
            =>_session.RunHmiStepTool("ImportLibraryTypeDocuments",meta=>{
                var file=new FileInfo(filePath);if(!file.Exists)throw new FileNotFoundException("Library type native document not found.");
                LibraryDeepLogic.RequireOneOf(importOptions,LibraryDeepLogic.ImportOptions,"importOptions");LibraryDeepLogic.RequireOneOf(createOptions,LibraryDeepLogic.CreateOptions,"createOptions");
                if(targetGroupKind!="" && targetGroupKind!="blocks" && targetGroupKind!="types")throw new ArgumentException("targetGroupKind must be empty, blocks or types.");
                if((targetGroupKind=="")!=string.IsNullOrEmpty(targetSoftwarePath))throw new ArgumentException("targetSoftwarePath and targetGroupKind go together (STEP 7 documents need a PlcBlockGroup/PlcTypeGroup target environment).");
                var library=_session.ExactOpenEngineeringLibrary(libraryName);
                string basePath=Path.Combine(file.DirectoryName!,Path.GetFileNameWithoutExtension(file.Name));
                LibraryDeepLogic.ValidateDocumentImportScope(Engineering.TiaMajorVersion, library is ProjectLibrary,
                    importOptions, File.Exists(basePath+".xml"), File.Exists(basePath+".s7dcl"));
                var options=(LibraryImportOptions)System.Enum.Parse(typeof(LibraryImportOptions),importOptions);var create=(CreateOptions)System.Enum.Parse(typeof(CreateOptions),createOptions);
                string fileName=Path.GetFileNameWithoutExtension(file.Name);meta["fileName"]=fileName;meta["importOptions"]=importOptions;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                IEngineeringObject? environment=null;
                if(targetGroupKind!=""){
                    var plc=_session.ExactPlcForEngineering(targetSoftwarePath,false);
                    environment=(IEngineeringObject)EngineeringGroupOperations.Group(targetGroupKind=="blocks"?(object)plc.BlockGroup:plc.TypeGroup,targetGroupPath);
                    meta["targetEnvironment"]=new JsonObject{["softwarePath"]=targetSoftwarePath,["groupKind"]=targetGroupKind,["groupPath"]=targetGroupPath,["type"]=environment.GetType().Name};
                }
                if(!string.IsNullOrEmpty(typePath)){
                    // Version import: LibraryTypeVersionComposition.CreateFromDocuments on an existing type ("Updating type from document").
                    var type=_session.ExactLibraryType(library,typePath);meta["typePath"]=typePath;meta["createOptions"]=createOptions;meta["before"]=TypeRow(type,true);
                    if(dryRun)return "Library version document import preview; CreateOptions.None fails natively if an in-work version exists.";
                    using var access=_session.AcquireHmiEditAccess();meta["mayHaveChanged"]=true;
                    VersionCreateTransferResults result=environment==null?type.Versions.CreateFromDocuments(file.Directory!,fileName,create,options):type.Versions.CreateFromDocuments(file.Directory!,fileName,environment,create,options);
                    meta["transferResultState"]=result.TransferResultState.ToString();
                    meta["messages"]=new JsonArray(EngineeringGroupOperations.Items(result.Messages).Cast<TransferResultMessage>().Select(m=>(JsonNode)m.Message).ToArray());
                    meta["createdVersion"]=result.CreatedVersion==null?null:VersionRow(result.CreatedVersion,false);meta["after"]=TypeRow(type,true);meta["apiCallSuccess"]=true;
                    if(result.TransferResultState!=TransferResultState.Success)meta["operationSuccess"]=false;
                    return "Native version import returned "+result.TransferResultState+"; created version and messages attached. No automatic library/project save.";
                }
                var folder=_session.EngineeringLibraryFolder(library,folderPath,"TypeFolder");
                var types=(LibraryTypeComposition)EngineeringGroupOperations.Get(folder,"Types");
                if(dryRun)return "Library native document import preview; type/dependency semantics checked on execution.";
                using var edit=_session.AcquireHmiEditAccess();meta["mayHaveChanged"]=true;
                TypeCreateTransferResults created=environment==null?types.CreateFromDocuments(file.Directory!,fileName,options):types.CreateFromDocuments(file.Directory!,fileName,environment,options);
                meta["transferResultState"]=created.TransferResultState.ToString();
                meta["messages"]=new JsonArray(EngineeringGroupOperations.Items(created.Messages).Cast<TransferResultMessage>().Select(m=>(JsonNode)m.Message).ToArray());
                meta["createdType"]=created.CreatedType==null?null:TypeRow(created.CreatedType,true);meta["apiCallSuccess"]=true;
                if(created.TransferResultState!=TransferResultState.Success)meta["operationSuccess"]=false;
                return "Native type import returned "+created.TransferResultState+"; created type (default version InWork) and messages attached. No automatic library/project save.";
            });
        public ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false,
            string archiveName="", string archiveMode="Compressed", bool dryRun=true)
            => _session.RunHmiStepTool("ManageGlobalLibrary", meta => {
                if (_session.CurrentPortal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (!new[] { "list", "infos", "create", "open", "openInfo", "retrieve", "save", "saveAs", "close", "archive" }.Contains(action)) throw new ArgumentException("action must be one of: list/infos/create/open/openInfo/retrieve/save/saveAs/close/archive (case-sensitive).");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                GlobalLibraryComposition libraries = _session.CurrentPortal.GlobalLibraries;
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
                // An empty openMode defaults to ReadOnly before enum parsing, including actions that do not open a library.
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
        // found but cannot be closed through the API (observed on TIA V21) - say so instead of "not found".
        private static UserGlobalLibrary ExactOpenUserGlobalLibrary(GlobalLibraryComposition libraries, string libraryName, string action)
        {
            var found = EngineeringGroupOperations.Find(libraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found (action=list shows the open ones).");
            return found as UserGlobalLibrary ?? throw new NotSupportedException(LibraryDeepLogic.UserGlobalLibraryOnlyMessage(action, libraryName, found.GetType().Name));
        }
        public ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
            => _session.RunHmiStepTool("ManageLibraryFolder", meta => {
                if (folderKind != "types" && folderKind != "masterCopies") throw new ArgumentException("folderKind must be types/masterCopies.");
                if (!new[] { "read", "create", "rename", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/create/rename/delete (case-sensitive).");
                var parts = EngineeringGroupOperations.Parts(folderPath); // Root cannot be mutated.
                var library = _session.ExactOpenEngineeringLibrary(libraryName);
                var parent = _session.EngineeringLibraryFolder(library,string.Join("/",parts.Take(parts.Length-1)),folderKind=="types" ? "TypeFolder" : "MasterCopyFolder");
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
                using var access=_session.AcquireHmiEditAccess(); meta["mayHaveChanged"]=true;
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

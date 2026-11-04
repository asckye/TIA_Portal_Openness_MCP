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
        public HardwareAddressingReply ManageLibraryType(string typePath, string action, string libraryName = "", Dictionary<string,
                HardwareScalar> properties = null!, string targetLibraryName = "", string[] scopePaths = null!,
            string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure", string forceUpdateMode =
                "SetOnlyHigherUpdatedVersionAsDefault", bool confirmDelete = false, bool dryRun = true)
            => RunEngineeringServicesStep("ManageLibraryType", meta => {
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["typePath"] = typePath; meta["libraryName"] =
                    libraryName;

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
                        if (PlcGroupOperations.Find(PlcGroupOperations.Get(parent, "Types"), name) != null)
                            throw new InvalidOperationException("Delete returned but the type is still listed in its folder; do not blindly retry.");
                        meta["verifiedAbsent"] = true; return "Library type deleted and verified absent. No save.";
                    }
                    case "updateLibrary":
                    {
                        var target = AsLibrary(ExactOpenEngineeringLibrary(targetLibraryName));
                        meta["targetLibrary"] = EngineeringLibraryRef(target);
                        if (dryRun)
                            return "Type UpdateLibrary preview (single-type overload with DeleteUnusedVersionsMode / StructureConflictResolutionMode / " +
                            "ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        type
                            .UpdateLibrary(target, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode),
                                ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
#else
                        type.UpdateLibrary(target);
#endif
                        var synced = target.FindType(type.Guid); meta["targetTypeAfter"] = synced == null ? null : TypeRow(synced, true);
                        return "Type synchronized into the target library; target readback by GUID attached. No save.";
                    }
                    default: // updateProject
                    {
                        var scopes = ResolveScopes(scopePaths, meta);
                        if (dryRun)
                            return "Type UpdateProject preview (per scope, with DeleteUnusedVersionsMode / StructureConflictResolutionMode / " +
                            "ForceUpdateMode); nothing changed.";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        foreach (var scope in scopes) type
                            .UpdateProject(scope, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode),
                                ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode), ParseEnum<ForceUpdateMode>(forceUpdateMode));
#else
                        foreach (var scope in scopes) type.UpdateProject(scope);
#endif
                        meta["after"] = TypeRow(type, true);
                        return "Instances of the type updated in the selected scopes (native version choice). No save/compile/download.";
                    }
                }
            });

        public HardwareAddressingReply SynchronizeLibrary(string action, LibrarySelection[] selection = null!, string libraryName = "",
                string targetLibraryName = "", string[] scopePaths = null!,
            string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode = "DoNotDelete",
                string structureConflictResolutionMode = "RetainStructure",
            string[] harmonizeOptions = null!, string cleanUpMode = "PreserveDefaultVersionOfUnusedTypes", bool confirmChange = false, bool dryRun = true)
            => RunEngineeringServicesStep("SynchronizeLibrary", meta => {

                LibraryPolicy
                    .ValidateSyncRequest(action, libraryName, targetLibraryName, scopePaths, selection, forceUpdateMode, deleteUnusedVersionsMode,
                        structureConflictResolutionMode, cleanUpMode);
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
                        meta["modes"] = new Dictionary<string, object?> {
                            ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode,
                                ["structureConflictResolutionMode"] = structureConflictResolutionMode
                        };
                        if (dryRun) return "UpdateLibrary preview; nothing changed (types are matched by GUID; a version GUID mismatch aborts natively).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        lib
                            .UpdateLibrary(selected, target, ParseEnum<ForceUpdateMode>(forceUpdateMode),
                                ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode),
                                ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
#else
                        lib.UpdateLibrary(selected, target);
#endif
                        meta["targetTypesAfter"] = new List<object
                            ?>(selected.OfType<LibraryType>().Select(t => target.FindType(t.Guid)).Where(t => t != null).Select(t => (object)TypeRow(t!, false))
                                .ToArray());
                        return "Selected types/folders synchronized into the target library. No save.";
                    }
                    case "updateProject":
                    {
                        LibraryPolicy.ValidateProjectUpdateModes(library is ProjectLibrary, forceUpdateMode, structureConflictResolutionMode);
                        var scopes = ResolveScopes(scopePaths, meta);
                        meta["modes"] = new Dictionary<string, object?> {
                            ["forceUpdateMode"] = forceUpdateMode, ["deleteUnusedVersionsMode"] = deleteUnusedVersionsMode,
                                ["structureConflictResolutionMode"] = structureConflictResolutionMode
                        };
                        if (dryRun) return "UpdateProject preview; nothing changed (a global library source synchronizes the project library first).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        if (library is ProjectLibrary projectLibrary) projectLibrary
                            .UpdateProject(selected, scopes, ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode));
                        else ((GlobalLibrary)library)
                            .UpdateProject(selected, scopes, ParseEnum<ForceUpdateMode>(forceUpdateMode),
                                ParseEnum<DeleteUnusedVersionsMode>(deleteUnusedVersionsMode),
                                ParseEnum<StructureConflictResolutionMode>(structureConflictResolutionMode));
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
                        meta["cleanUpMode"] = library is ProjectLibrary ? cleanUpMode
                            : "(global library: fixed native behaviour, types are never deleted completely)";
                        if (dryRun) return "CleanUpLibrary preview; nothing changed (deletes unused, non-in-test versions of the selection).";
                        meta["mayHaveChanged"] = true;
#if LIBRARY_V17
                        if (library is ProjectLibrary projectLibrary) projectLibrary.CleanUpLibrary(selected, ParseEnum<CleanUpMode>(cleanUpMode));
                        else if (library is UserGlobalLibrary user) user.CleanUpLibrary(selected);
                        else
                            throw new NotSupportedException("CleanUpLibrary exists on ProjectLibrary and UserGlobalLibrary only (" + library.GetType().Name
                                + ").");
#else
                        throw new NotSupportedException("This library action requires V17 or later.");
#endif
                        meta["after"] = new List<object?>(selected.OfType<LibraryType>().Select(t => (object)TypeRow(t, true)).ToArray());
                        return "Library clean-up completed for the selection. No save.";
                    }
                }
            });

#if LIBRARY_COMPARE
        public HardwareAddressingReply CompareLibraryObjects(string kind, string leftPath, string rightPath, string leftLibraryName = "",
                string rightLibraryName = "", string leftVersion = "", string rightVersion = "",
            bool includeIdentical = false, int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("CompareLibraryObjects", meta => {
                LibraryPolicy.ValidateCompareRequest(kind, leftPath, rightPath, leftVersion, rightVersion); EngineeringEvidence.ValidatePage(offset, limit);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                var leftLibrary = ExactOpenEngineeringLibrary(leftLibraryName); var rightLibrary = ExactOpenEngineeringLibrary(rightLibraryName);
                DetailedCompareResult result; string leftName, rightName;
                switch (kind)
                {
                    case "type": {
                        var l = ExactLibraryType(leftLibrary, leftPath); var r = ExactLibraryType(rightLibrary, rightPath); leftName = l.Name; rightName = r
                            .Name; result = l.CompareTo(r); break;
                    }
                    case "version": {
                        var l = ExactTypeVersion(ExactLibraryType(leftLibrary, leftPath), leftVersion); var r =
                            ExactTypeVersion(ExactLibraryType(rightLibrary, rightPath), rightVersion); leftName = l.TypeObject?.Name + " " + l.VersionNumber;
                        rightName = r.TypeObject?.Name + " " + r.VersionNumber; result = l.CompareTo(r); break;
                    }
                    default: {
                        var l = ExactMasterCopy(leftLibraryName, leftPath); var r = ExactMasterCopy(rightLibraryName, rightPath); leftName = l.Name; rightName =
                            r.Name; result = l.CompareTo(r); break;
                    }
                }
                meta["kind"] = kind; meta["left"] = leftName; meta["right"] = rightName; meta["resultType"] = result.GetType().FullName;
                var rows = PlcGroupOperations.Items(result.Properties).Cast<DetailedCompareResultElement>()
                    .Select(e => new Dictionary<string, object?> {
                            ["description"] = e.Description, ["status"] = e.DetailCompareStatus.ToString(), ["leftValue"] = LibraryValue(e.LeftValue),
                                ["rightValue"] = LibraryValue(e.RightValue)
                        })
                        .ToArray();
                meta["summary"] = rows.GroupBy(r => (string)r["status"]!).ToDictionary(g => g.Key, g => (object?)g.Count());
                var selected = includeIdentical ? rows : rows.Where(r => (string)r["status"]! != "ObjectsIdentical").ToArray();
                LibraryPage(selected.Cast<object>().ToArray(), offset, limit, meta);
                meta["totalProperties"] = rows.Length; meta["includeIdentical"] = includeIdentical; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] =
                    "Native DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue); OriginalLibrary is not compared " +
                    "by default per the official note. Read-only.";
                return "Detailed comparison completed; " + rows.Length + " properties compared.";
            }, requiresProject: false);
#endif
        public HardwareAddressingReply ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",
                string destinationPath="",bool dryRun=true)
            =>RunEngineeringServicesStep("ManageLibraryMasterCopy",meta=>{
                if (!new[] { "read", "copy", "compare", "delete" }.Contains(action))
                    throw new ArgumentException("action must be one of: read/copy/compare/delete (case-sensitive).");
                using var access=!dryRun&&(action=="copy"||action=="delete") ? HardwareEditAccess() : null;
                var source=ExactMasterCopy(libraryName,sourcePath);meta["before"]=LibraryObjectAddress(source);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=
                    false;
                if(action=="read")return "Master copy scalar properties read.";
                #if LIBRARY_COMPARE
                if(action=="compare") {
                    var other=ExactMasterCopy(destinationLibraryName,destinationPath);meta["comparison"]=HardwareOfficialService
                        .Result(source.CompareTo(other));return "Native comparison returned; inspect differences and exclusions.";
                }
#endif
                MasterCopyFolder? folder=null;
                if(action=="copy") {
                    folder=(MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(destinationLibraryName),destinationPath,"MasterCopyFolder");
                    if(PlcGroupOperations.Find(folder.MasterCopies,source.Name)!=null)
                        throw new InvalidOperationException("Destination master-copy name exists; overwrite refused.");
                }
                if(dryRun)return "Master-copy mutation preview; no changes.";
                meta["mayHaveChanged"]=true;
                if(action=="copy")meta["after"]=LibraryObjectAddress(folder!.MasterCopies.CreateFrom(source));
                else {
                    var parts=PlcGroupOperations.Parts(sourcePath);
                    var parent=
                        EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
                    source.Delete();meta["deleteReturned"]=true;
                    if(PlcGroupOperations.Find(PlcGroupOperations.Get(parent,"MasterCopies"),parts.Last())!=null)
                        throw new InvalidOperationException("Master-copy deletion not verified.");
                    meta["absenceVerified"]=true;
                }
                return "Native master-copy operation returned; no project/library save or Portal close.";
            });
    }
}

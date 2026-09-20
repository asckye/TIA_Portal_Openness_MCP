using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactOpenEngineeringLibrary(string libraryName)
            => string.IsNullOrEmpty(libraryName) ? _project!.ProjectLibrary
                : EngineeringGroupOperations.Find(_portal!.GlobalLibraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found. Open it in TIA first; no implicit open or close.");
        // ProjectLibrary has no Name in the PublicAPI (2.7.31 real project); GlobalLibrary does.
        private static string LibraryLabel(object library) => library is GlobalLibrary global ? global.Name : LibraryDeepLogic.ProjectLibraryLabel;
        private JsonObject LibraryRef(object library)
        {
            var row = new JsonObject { ["name"] = LibraryLabel(library), ["libraryClass"] = library.GetType().Name };
            if (library is ProjectLibrary) row["project"] = _project?.Name;
            return row;
        }
        private static object EngineeringLibraryFolder(object library, string folderPath, string rootProperty)
        {
            // SystemGlobalLibrary.TypeFolder is null (2.7.31 real project): say so instead of the generic "unavailable".
            var folder = library.GetType().GetProperty(rootProperty)?.GetValue(library)
                ?? throw new PortalException(PortalErrorCode.NotFound, LibraryDeepLogic.NoTypeFolderMessage(library.GetType().Name, "resolving '" + folderPath + "' under " + rootProperty));
            foreach (var part in EngineeringGroupOperations.Parts(folderPath, true))
                folder = EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(folder, "Folders"), part) ?? throw new InvalidOperationException("Library folder not found: " + part);
            return folder;
        }
        private object ExactMasterCopyPlcSource(string softwarePath, string sourcePath, bool block)
        {
            var plc = ExactPlcForEngineering(softwarePath, false);
            var parts = EngineeringGroupOperations.Parts(sourcePath);
            var root = block ? (object)plc.BlockGroup : plc.TypeGroup;
            var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            return EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, block ? "Blocks" : "Types"), parts.Last())
                ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC " + (block ? "block" : "type") + " not found: " + sourcePath + " (path under " + (block ? "Program blocks" : "PLC data types") + ", groups separated by '/', see GetSoftwareTree).");
        }
        public ResponseMessage ManageLibraryTypeVersion(string typePath, string version, string action, string libraryName = "",
            string newVersion = "", string dependenciesMode = "", string author = "", string comment = "", string targetSoftwarePath = "", bool dryRun = true)
            => RunHmiStepTool("ManageLibraryTypeVersion", meta => {
                if (!new[] { "read", "edit", "release", "setDefault", "deleteVersion", "updateInstances", "discard", "findInstances" }.Contains(action)) throw new ArgumentException("Invalid library version action.");
                bool writing = action != "read" && action != "findInstances" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var library = ExactOpenEngineeringLibrary(libraryName);
                var parts = EngineeringGroupOperations.Parts(typePath);
                var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
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
                    scope = ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IUpdateProjectScope
                        ?? throw new NotSupportedException("Selected software does not implement IUpdateProjectScope.");
                    meta["versionSelection"] = "Native LibraryType.UpdateProject chooses applicable released/default versions, not necessarily the version argument. The argument identifies evidence only.";
                }
                if (action == "findInstances") {
                    if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("Exact targetSoftwarePath required; whole project search refused.");
                    var search = ResolveSoftwareContainerUncached(targetSoftwarePath)?.Software as IInstanceSearchScope ?? throw new NotSupportedException("Selected software does not support instance search.");
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
            => RunHmiStepTool("CreateLibraryMasterCopy", meta => {
                using var access = dryRun ? null : AcquireHmiEditAccess();
                object source = sourceKind switch {
                    "block" => ExactMasterCopyPlcSource(softwarePath, sourcePath, true),
                    "type" => ExactMasterCopyPlcSource(softwarePath, sourcePath, false),
                    "device" => ExactEngineeringDevice(sourcePath),
                    "screen" => HmiExactAccess.Screen(ResolveHmiSoftwareOrThrow(softwarePath), sourcePath),
                    _ => throw new ArgumentException("sourceKind must be block/type/device/screen. device sourcePath is a JSON array of exact names.") };
                if (source is not IMasterCopySource copySource) throw new NotSupportedException("Selected object is not a supported native master-copy source.");
                var folder = (MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName), folderPath, "MasterCopyFolder");
                meta["sourceType"] = source.GetType().FullName; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun) { meta["mayHaveChanged"] = true; var copy = folder.MasterCopies.Create(copySource); meta["after"] = EngineeringScalarProperties.Read(copy); }
                return dryRun ? "Master copy preview; no copy created." : "Master copy created; no explicit save or close.";
            });
    }
}

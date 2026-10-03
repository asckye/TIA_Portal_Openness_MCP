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
        // ProjectLibrary has no Name in the PublicAPI (observed on TIA V21); GlobalLibrary does.
        private static string LibraryLabel(object library) => library is GlobalLibrary global ? global.Name : LibraryDeepLogic.ProjectLibraryLabel;
        private JsonObject LibraryRef(object library)
        {
            var row = new JsonObject { ["name"] = LibraryLabel(library), ["libraryClass"] = library.GetType().Name };
            if (library is ProjectLibrary) row["project"] = _project?.Name;
            return row;
        }
        private static object EngineeringLibraryFolder(object library, string folderPath, string rootProperty)
        {
            // SystemGlobalLibrary.TypeFolder is null (observed on TIA V21): say so instead of the generic "unavailable".
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
    }
}

using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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
    public partial class Portal
    {
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
    }
}

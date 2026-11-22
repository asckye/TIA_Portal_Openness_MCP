using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.Library.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static (string Name, object Collection, object? Target) SelectLibraryFolder(string path, string action, System.Func<string, object> parentAt)
        {
            var parts = PlcGroupOperations.Parts(path, rootAllowed: action == "read");
            var parent = parentAt(string.Join("/", parts.Take(parts.Length - 1)));
            var folders = PlcGroupOperations.Get(parent, "Folders");
            string name = parts.LastOrDefault() ?? "";
            return (name, folders, parts.Length == 0 ? parent : PlcGroupOperations.Find(folders, name));
        }

        internal static void RequireSelectedTypes(IEnumerable<ILibraryTypeOrFolderSelection> selection)
        {
            bool ContainsType(LibraryTypeFolder folder) => PlcGroupOperations.Items(folder.Types).Any()
                || PlcGroupOperations.Items(folder.Folders).Cast<LibraryTypeUserFolder>().Any(ContainsType);
            if (!selection.Any(item => item is LibraryType || item is LibraryTypeFolder folder && ContainsType(folder)))
                throw AdapterPreconditionException.Failed("The resolved library selection contains no types.", "empty-type-selection", "selection");
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.Library.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static void RequireSelectedTypes(IEnumerable<ILibraryTypeOrFolderSelection> selection)
        {
            bool ContainsType(LibraryTypeFolder folder) => PlcGroupOperations.Items(folder.Types).Any()
                || PlcGroupOperations.Items(folder.Folders).Cast<LibraryTypeUserFolder>().Any(ContainsType);
            if (!selection.Any(item => item is LibraryType || item is LibraryTypeFolder folder && ContainsType(folder)))
                throw AdapterPreconditionException.Failed("The resolved library selection contains no types.", "empty-type-selection", "selection");
        }
    }
}

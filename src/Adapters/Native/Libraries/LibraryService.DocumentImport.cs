using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Libraries;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static Func<bool>? CaptureLibraryImportEnvironment(PlcSoftware? plc, string importOptions, string createOptions,
            Func<int>? libraryTypeCount = null)
        {
            if (plc == null || !LibraryPolicy.CanUseDocumentImportEnvironmentProof(importOptions, createOptions)) return null;
            string[] Names() => LibraryScopeWithSystemObjects(plc.BlockGroup, "Blocks", "SystemBlockGroups", "blocks/")
                .Concat(LibraryScopeWithSystemObjects(plc.TypeGroup, "Types", "SystemTypeGroups", "types/"))
                .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            try
            {
                var before = Names();
                var typesBefore = libraryTypeCount?.Invoke();
                return () => typesBefore == libraryTypeCount?.Invoke() && before.SequenceEqual(Names(), StringComparer.Ordinal);
            }
            catch (Exception) { /* swallow(probe-optional): import may proceed without an unchanged-environment proof. */ return null; }
        }

        internal static void RecordUnchangedLibraryImport(Dictionary<string, object?> meta, Func<bool>? environmentUnchanged,
            int before, Func<int> countAfter, bool canProveUnchanged)
        {
            try
            {
                int after = countAfter();
                meta["countAfter"] = after;
                if (before == after && environmentUnchanged != null) canProveUnchanged = environmentUnchanged();
                if (canProveUnchanged && before == after) meta["mayHaveChanged"] = false;
            }
            catch { /* swallow(probe-optional): a failed readback cannot clear an uncertain import. */ }
        }
    }
}

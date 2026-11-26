using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static Func<bool>? CaptureLibraryImportEnvironment(PlcSoftware? plc, string importOptions, string createOptions,
            Func<object>? librarySnapshot = null)
        {
            if ((plc == null && librarySnapshot == null) || !LibraryPolicy.CanUseDocumentImportEnvironmentProof(importOptions, createOptions))
                return null;
            string[] Names() => plc == null ? Array.Empty<string>() : LibraryScopeWithSystemObjects(plc.BlockGroup, "Blocks", "SystemBlockGroups", "blocks/")
                .Concat(LibraryScopeWithSystemObjects(plc.TypeGroup, "Types", "SystemTypeGroups", "types/"))
                .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            try
            {
                var before = Names();
                var libraryBefore = librarySnapshot?.Invoke();
                return () => Equals(libraryBefore, librarySnapshot?.Invoke()) && before.SequenceEqual(Names(), StringComparer.Ordinal);
            }
            catch (Exception) { /* swallow(probe-optional): import may proceed without an unchanged-environment proof. */ return null; }
        }

        internal static string LibraryVersionSet(object root)
        {
            // Encoded type paths have no literal line breaks. A trailing slash distinguishes version rows from type rows.
            var rows = new List<string>();
            foreach (var type in LibraryScopeObjects(root, "Types", "Folders"))
            {
                rows.Add(type.Path);
                foreach (var version in PlcGroupOperations.Items(PlcGroupOperations.Get(type.Item, "Versions")))
                    rows.Add(type.Path + "/" + (Version)PlcGroupOperations.Get(version, "VersionNumber") + "/");
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("\n", rows);
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

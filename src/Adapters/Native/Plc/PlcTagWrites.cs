using System;
using System.Collections.Generic;
using System.IO;
using TiaMcp.Adapters.Contracts;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcTagWrites
    {
        internal static void CreateTable(PlcTagTableGroup target, string name)
            => Create(() => target.TagTables.Create(name),
                () => !PlcFoundationPolicy.SymbolExists(target.TagTables, n => target.TagTables.Find(n), t => t.Name, name));

        internal static void CreateTag(PlcTagTable target, string name, string dataType, string address)
            => Create(() => target.Tags.Create(name, dataType, address),
                () => !PlcFoundationPolicy.SymbolExists(target.Tags, n => target.Tags.Find(n), t => t.Name, name));

        internal static void CreateConstant(PlcTagTable target, string name, string dataType, string value)
            => Create(() => target.UserConstants.Create(name, dataType, value),
                () => !PlcFoundationPolicy.SymbolExists(target.UserConstants, n => target.UserConstants.Find(n), t => t.Name, name));

        // These Creates require an explicit symbol name and have no automatic-name overload.
        // Admission checked that name; it retained no composition count. A case-insensitive lookup covers the only possible new symbol.
        private static void Create(Action write, Func<bool> stillAbsent)
        {
            try { write(); }
            catch (Exception error) when (NativeRefusalProbe.ProvesUnchanged(error, stillAbsent))
            {
                error.Data["phase"] = "create";
                error.Data["verifiedAbsent"] = true;
                error.Data["foundationKnownNoMutation"] = true;
                if (error is EngineeringException native) PlcFoundationEngine.RecordSiemensReason(native);
                throw;
            }
        }

        internal static IList<PlcTagTable> Import(PlcTagTableComposition tables, FileInfo file, ImportOptions options, string? newName, Action? dispatch)
        {
            // Default import of one declared tag table preserves its name. Admission did not collect a destination count.
            dispatch?.Invoke();
            return PlcFoundationEngine.ImportWithReadback(() => tables.Import(file, options), newName, options == ImportOptions.None,
                name => PlcFoundationPolicy.SymbolExists(tables, selected => tables.Find(selected), t => t.Name, name));
        }
    }
}

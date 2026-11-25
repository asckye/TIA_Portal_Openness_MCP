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

        // Each caller already found the requested name absent before dispatch.
        private static void Create(Action write, Func<bool> stillAbsent)
        {
            try { write(); }
            catch (Exception error) when (NativeRefusalProbe.ProvesUnchanged(error, stillAbsent))
            {
                error.Data["foundationKnownNoMutation"] = true;
                throw;
            }
        }

        internal static IList<PlcTagTable> Import(PlcTagTableComposition tables, FileInfo file, ImportOptions options, string? newName, Action? dispatch)
        {
            dispatch?.Invoke();
            return PlcFoundationEngine.ImportWithReadback(() => tables.Import(file, options), newName, options == ImportOptions.None,
                name => PlcFoundationPolicy.SymbolExists(tables, selected => tables.Find(selected), t => t.Name, name));
        }
    }
}

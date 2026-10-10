using System;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcWatchPolicy
    {
        private static void RequireOneOf(string value, string[] allowed, string parameter) => PlcIntegrityPolicy.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim())
                throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        // ---- watch / force table entries -----------------------------------------------------------------------------------------------
        internal static readonly string[] TableKinds = { "watch", "force" };
        internal static readonly string[] TableEntryActions = { "read", "createComment", "deleteEntry", "deleteTable" };
        internal static bool ValidateTableEntryRequest(string tableKind, string tablePath, string action, int entryIndex, bool confirmDelete, bool dryRun, int offset, int limit)
        {
            RequireOneOf(tableKind, TableKinds, "tableKind");
            RequireName(tablePath, "tablePath", 1024);
            RequireOneOf(action, TableEntryActions, "action");
            HardwareAddressingPolicy.ValidatePage(offset, limit);
            // Force tables stay read-only on purpose: forcing bypasses the program and PlcForceTableEntry writes are deliberately not offered.
            if (tableKind == "force" && action != "read") throw new ArgumentException("Force tables are read-only here (PlcForceTableEntry writes are deliberately not offered); createComment / deleteEntry apply to watch tables.");
            if (action == "deleteEntry") { if (entryIndex < 0) throw new ArgumentException("deleteEntry needs the 0-based entryIndex from a read."); }
            else if (entryIndex >= 0) throw new ArgumentException("entryIndex applies to deleteEntry only.");
            if (action == "read") return false;
            if (dryRun) return false;
            if (action == "deleteEntry" && !confirmDelete) throw new ArgumentException("Real entry deletion requires confirmDelete=true besides dryRun=false.");
            if (action == "deleteTable" && !confirmDelete) throw new ArgumentException("Real table deletion requires confirmDelete=true besides dryRun=false.");
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class PlcTableRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        internal static readonly string[] ConstantKinds = { "all", "user", "system" };
        internal static void ValidateConstantRequest(string tablePath, string kind, string unitName, string unitKind, int offset, int limit)
        {
            RequireName(tablePath, "tablePath", 1024);
            RequireOneOf(kind, ConstantKinds, "kind");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            HardwareServicesLogic.ValidatePagination(offset, limit);
        }

        // ---- watch / force table entries -----------------------------------------------------------------------------------------------
        internal static readonly string[] TableKinds = { "watch", "force" };
        internal static readonly string[] TableEntryActions = { "read", "createComment", "deleteEntry", "deleteTable" };
        internal static bool ValidateTableEntryRequest(string tableKind, string tablePath, string action, int entryIndex, bool confirmDelete, bool dryRun, int offset, int limit)
        {
            RequireOneOf(tableKind, TableKinds, "tableKind");
            RequireName(tablePath, "tablePath", 1024);
            RequireOneOf(action, TableEntryActions, "action");
            HardwareServicesLogic.ValidatePagination(offset, limit);
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

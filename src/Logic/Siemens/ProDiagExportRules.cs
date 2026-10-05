using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ProDiagExportRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        private static void RequireAbsoluteDirectory(string path, string parameter)
            => ArgumentRules.RequireAbsolutePath(path, "Absolute existing " + parameter + " required.");
        // ---- ProDiag CSV export (CodeBlock.ExportProDIAGInfo) ---------------------------------------------------------------------------
        internal static void ValidateProDiagRequest(string blockPath, string directoryPath, string unitName, string unitKind)
        {
            RequireName(blockPath, "blockPath", 1024);
            RequireAbsoluteDirectory(directoryPath, "directoryPath");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
        }
        internal static string? ProDiagRefusal(string programmingLanguage, bool isConsistent)
        {
            if (!string.Equals(programmingLanguage, "ProDiag", StringComparison.Ordinal)) return "ExportProDIAGInfo needs a ProDiag FB; the block language is " + programmingLanguage + ".";
            return isConsistent ? null : "The ProDiag FB is inconsistent; compile first.";
        }
    }
}

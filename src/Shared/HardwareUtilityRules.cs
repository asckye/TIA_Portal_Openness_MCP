using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HardwareUtilityRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        private static void RequireAbsoluteNewFile(string filePath, string parameter)
            => ArgumentRules.RequireAbsolutePath(filePath, "Absolute " + parameter + " required.");
        // ---- hardware utilities (ProjectBase.HwUtilities) --------------------------------------------------------------------
        internal static readonly string[] HardwareUtilityActions = { "list", "findModuleTypes", "findContainerTypes", "normalizeTypeIdentifier", "exportOpcUa", "exportCardReaderPsc" };
        internal const string ModuleInformationProviderId = "ModuleInformationProvider";
        internal const string OpcUaExportProviderId = "OPCUAExportProvider";        // official page: project.HwUtilities.Find("OPCUAExportProvider")
        internal const string CardReaderPscProviderId = "CardReaderPscProvider";

        internal static void ValidateDevicePath(string devicePathJson)
        {
            JsonArray? names;
            try { names = JsonNode.Parse(devicePathJson ?? "") as JsonArray; }
            catch (System.Text.Json.JsonException ex) { throw new ArgumentException("devicePathJson must be a JSON array of exact names: " + ex.Message); }
            if (names == null) throw new ArgumentException("devicePathJson must be a JSON array of exact names.");
            if (names.Count < 1 || names.Count > 64 || names.Any(n => n is not JsonValue || string.IsNullOrWhiteSpace(n!.GetValue<string>()))) throw new ArgumentException("devicePathJson needs 1-64 exact nonempty names.");
        }
        internal static void ValidateHardwareUtilityRequest(string action, string typeIdentifier, string devicePathJson, string filePath, string password, bool dryRun)
        {
            RequireOneOf(action, HardwareUtilityActions, "action");
            bool needsType = action == "findModuleTypes" || action == "findContainerTypes" || action == "normalizeTypeIdentifier";
            if (needsType) RequireName(typeIdentifier, "typeIdentifier", 512); else if (!string.IsNullOrEmpty(typeIdentifier)) throw new ArgumentException("typeIdentifier applies to findModuleTypes / findContainerTypes / normalizeTypeIdentifier only.");
            if (action == "exportOpcUa" || action == "exportCardReaderPsc") { ValidateDevicePath(devicePathJson); RequireAbsoluteNewFile(filePath, "filePath"); }
            else if (!string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath applies to the export actions only.");
            if (action == "exportOpcUa" && !filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("exportOpcUa writes an OPC UA XML file; filePath must end with .xml.");
            if (action == "exportCardReaderPsc" && !filePath.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("exportCardReaderPsc writes a .psc file; filePath must end with .psc.");
            if (!string.IsNullOrEmpty(password) && action != "exportCardReaderPsc") throw new ArgumentException("password applies to exportCardReaderPsc only (encrypted PSC, CPU V40.0+; never echoed).");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 4 sub-batch 2 (2.7.35): pure logic (no Siemens dependency) for external sources (files, master copies, user
    // groups, block generation), system block / type groups, tag table constants, alarm text list XLSX exchange, watch /
    // force table entries and ProDiag CSV export.
    internal static class Step7LeftoversLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        private static void RequireAbsoluteDirectory(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute existing " + parameter + " required.");
        }
        internal static string[] ParseNames(string json, string parameter)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json); }
            catch (Exception ex) { throw new ArgumentException(parameter + " must be a JSON array of strings: " + ex.Message); }
            if (node is not JsonArray array) throw new ArgumentException(parameter + " must be a JSON array of strings.");
            var names = array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException(parameter + " entries must be strings.")).ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim() != n)) throw new ArgumentException(parameter + " entries must be exact nonempty names.");
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException(parameter + " contains duplicates.");
            return names;
        }

        // ---- external sources (PlcExternalSourceSystemGroup / PlcExternalSourceUserGroup / PlcExternalSource) ---------------------
        internal static readonly string[] ExternalSourceActions = { "list", "read", "createFromFile", "createFromMasterCopy", "delete", "generateBlocks", "createGroup", "renameGroup", "deleteGroup" };
        internal static readonly string[] GenerateBlockOptions = { "None", "KeepOnError" };
        internal static readonly string[] GenerateTargetKinds = { "block", "type" };
        internal static readonly string[] MasterCopyModes = SoftwareUnitDeepLogic.MasterCopyModes;
        // Official "Generating blocks from source": only ASCII sources; TIA accepts .scl / .awl / .db / .udt (and .stl for STL).
        internal static readonly string[] ExternalSourceExtensions = { ".scl", ".awl", ".stl", ".db", ".udt" };

        internal static bool ValidateExternalSourceRequest(string action, string name, string unitName, string unitKind, string filePath, string libraryName, string masterCopyPath, string copyMode,
            string generateOption, string targetKind, string targetGroupPath, string newName, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, ExternalSourceActions, "action");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            bool group = action.EndsWith("Group", StringComparison.Ordinal);
            if (action == "list") Refuse(name, "name", "applies to every action except list (groupPath selects the group).");
            else if (action == "createGroup") Refuse(name, "name", "does not apply to createGroup (newName is the group to create under groupPath).");
            else RequireName(name, group ? "name (the user group)" : "name (the external source, with extension)");
            if (action == "createFromFile")
            {
                if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("createFromFile needs the absolute filePath of an existing source file on the TIA Portal machine.");
                if (!ExternalSourceExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())) throw new ArgumentException("filePath must be an ASCII source file (" + string.Join(" / ", ExternalSourceExtensions) + ").");
            }
            else Refuse(filePath, "filePath", "applies to createFromFile only.");
            if (action == "createFromMasterCopy")
            {
                RequireName(masterCopyPath, "masterCopyPath", 1024);
                if (!string.IsNullOrEmpty(copyMode)) RequireOneOf(copyMode, MasterCopyModes, "copyMode");
            }
            else { Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only."); Refuse(copyMode, "copyMode", "applies to createFromMasterCopy only."); Refuse(libraryName, "libraryName", "applies to createFromMasterCopy only (empty = project library)."); }
            if (action == "generateBlocks")
            {
                RequireOneOf(generateOption, GenerateBlockOptions, "generateOption");
                if (!string.IsNullOrEmpty(targetKind) || !string.IsNullOrEmpty(targetGroupPath))
                {
                    RequireOneOf(targetKind, GenerateTargetKinds, "targetKind");
                    RequireName(targetGroupPath, "targetGroupPath", 1024);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(generateOption) && generateOption != "None") throw new ArgumentException("generateOption applies to generateBlocks only.");
                Refuse(targetKind, "targetKind", "applies to generateBlocks only."); Refuse(targetGroupPath, "targetGroupPath", "applies to generateBlocks only.");
            }
            if (action == "createGroup" || action == "renameGroup") RequireName(newName, "newName");
            else Refuse(newName, "newName", "applies to createGroup / renameGroup only.");
            bool writing = action != "list" && action != "read" && !dryRun;
            if ((action == "delete" || action == "deleteGroup") && writing && !confirmDelete) throw new ArgumentException("Real deletion requires confirmDelete=true besides dryRun=false.");
            return writing;
        }

        // ---- system groups / constants -----------------------------------------------------------------------------------------------
        internal static readonly string[] ConstantKinds = { "all", "user", "system" };
        internal static void ValidateSystemGroupRequest(string unitName, string unitKind, int maxDepth)
        {
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
        }
        internal static void ValidateConstantRequest(string tablePath, string kind, string unitName, string unitKind, int offset, int limit)
        {
            RequireName(tablePath, "tablePath", 1024);
            RequireOneOf(kind, ConstantKinds, "kind");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            HardwareServicesLogic.ValidatePagination(offset, limit);
        }

        // ---- alarm text lists XLSX (PlcAlarmTextListProvider) ----------------------------------------------------------------------------
        internal static readonly string[] XlsxActions = { "export", "import" };
        internal static readonly string[] XlsxImportOptions = { "None", "Override" };
        internal sealed class XlsxRequest { internal bool Writing; internal string[] TextLists = Array.Empty<string>(); internal string[] Cultures = Array.Empty<string>(); }
        internal static XlsxRequest ValidateXlsxRequest(string action, string filePath, string unitName, string unitKind, string textListNamesJson, string culturesJson, string importOption, bool confirmImport, bool dryRun)
        {
            RequireOneOf(action, XlsxActions, "action");
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath) || !filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Absolute filePath ending in .xlsx required.");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            var request = new XlsxRequest { TextLists = ParseNames(textListNamesJson, "textListNamesJson"), Cultures = ParseNames(culturesJson, "culturesJson") };
            if (action == "export")
            {
                if (importOption != "None" && !string.IsNullOrEmpty(importOption)) throw new ArgumentException("importOption applies to import only.");
                // Official: the filtered overload needs both lists; an empty list is not a valid filter.
                if ((request.TextLists.Length == 0) != (request.Cultures.Length == 0)) throw new ArgumentException("Filtered export needs both textListNamesJson and culturesJson (the native overload takes both); omit both to export every user text list in every project language.");
                return request;
            }
            if (request.TextLists.Length > 0 || request.Cultures.Length > 0) throw new ArgumentException("textListNamesJson / culturesJson apply to export only.");
            RequireOneOf(importOption, XlsxImportOptions, "importOption");
            if (dryRun) return request;
            if (!confirmImport) throw new ArgumentException("Real import requires confirmImport=true besides dryRun=false.");
            request.Writing = true;
            return request;
        }

        // ---- watch / force table entries -----------------------------------------------------------------------------------------------
        internal static readonly string[] TableKinds = { "watch", "force" };
        internal static readonly string[] TableEntryActions = { "read", "createComment", "deleteEntry" };
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
            return true;
        }

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

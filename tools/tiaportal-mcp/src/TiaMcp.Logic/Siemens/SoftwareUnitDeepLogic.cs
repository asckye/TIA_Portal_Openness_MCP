using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 4 sub-batch 1 (2.7.34): pure logic (no Siemens dependency) for software units (standard and safety), named
    // value type / UDT documents, software checksums, object fingerprints, block write protection, project compilation
    // settings and channel-linked tags.
    internal static class SoftwareUnitDeepLogic
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
        internal static JsonObject ParseObject(string json, string parameter)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
            catch (Exception ex) { throw new ArgumentException(parameter + " must be a JSON object: " + ex.Message); }
            return node as JsonObject ?? throw new ArgumentException(parameter + " must be a JSON object.");
        }

        // ---- software units (PlcUnitProvider.UnitGroup: Units / SafetyUnits) --------------------------------------------------
        internal static readonly string[] UnitKinds = { "unit", "safety" };
        internal static readonly string[] UnitListKinds = { "all", "unit", "safety" };
        internal static readonly string[] UnitActions = { "list", "read", "create", "createFromMasterCopy", "delete", "update", "createRelation", "deleteRelation" };
        // The safety unit is system generated: it can neither be created nor deleted (official page "Accessing the SafetyUnit").
        internal static readonly string[] SafetyUnitRefusedActions = { "create", "createFromMasterCopy", "delete" };
        internal static readonly string[] UnitRelationTypes = { "SoftwareUnit", "NonUnitDB", "TODB" };
        internal static readonly string[] MasterCopyModes = { "ThrowIfExists", "Rename", "Replace" };
        internal static readonly string[] UpdatePathsModes = { "UpdatePathsInTarget", "KeepExistingPathsInTarget", "ThrowIfPathsConflict" };
        // Writable scalars of PlcUnitBase. Name is refused (a rename needs a reference-impact review); Comment is a
        // MultilingualText written per culture through commentsJson.
        internal static readonly string[] UnitScalarProperties = { "Author", "NamespacePreset" };

        internal sealed class UnitRequest
        {
            internal bool Writing;
            internal JsonObject Properties = new JsonObject();
            internal (string Culture, string Text)[] Comments = Array.Empty<(string, string)>();
        }

        internal static UnitRequest ValidateUnitRequest(string action, string unitKind, string name, string relatedUnit, string relationType, string propertiesJson, string commentsJson,
            string libraryName, string masterCopyPath, string copyMode, bool dryRun)
        {
            RequireOneOf(action, UnitActions, "action");
            RequireOneOf(unitKind, UnitKinds, "unitKind");
            if (unitKind == "safety" && SafetyUnitRefusedActions.Contains(action)) throw new ArgumentException("The safety unit is system generated: " + action + " is refused (only read / update / createRelation / deleteRelation apply).");
            var request = new UnitRequest();
            if (action != "list") RequireName(name, "name");
            else Refuse(name, "name", "applies to every action except list.");
            bool relation = action == "createRelation" || action == "deleteRelation";
            if (relation) RequireName(relatedUnit, "relatedUnit");
            else Refuse(relatedUnit, "relatedUnit", "applies to createRelation / deleteRelation only.");
            if (action == "createRelation") RequireOneOf(relationType, UnitRelationTypes, "relationType");
            else Refuse(relationType, "relationType", "applies to createRelation only.");
            if (action == "createFromMasterCopy")
            {
                RequireName(masterCopyPath, "masterCopyPath", 1024);
                if (!string.IsNullOrEmpty(copyMode)) RequireOneOf(copyMode, MasterCopyModes, "copyMode");
            }
            else
            {
                Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");
                Refuse(copyMode, "copyMode", "applies to createFromMasterCopy only.");
                Refuse(libraryName, "libraryName", "applies to createFromMasterCopy only (empty = project library).");
            }
            var properties = ParseObject(propertiesJson, "propertiesJson");
            var comments = ParseObject(commentsJson, "commentsJson");
            if (action == "update")
            {
                var unknown = properties.Select(p => p.Key).Where(k => !UnitScalarProperties.Contains(k, StringComparer.Ordinal)).ToArray();
                if (unknown.Length > 0) throw new ArgumentException(unknown.Contains("Name") ? "Unit rename requires a separate reference-impact review; Name updates refused." : "propertiesJson accepts only " + string.Join(" / ", UnitScalarProperties) + "; unknown: " + string.Join(", ", unknown));
                foreach (var pair in properties) if (pair.Value is not JsonValue v || !v.TryGetValue<string>(out _)) throw new ArgumentException("propertiesJson." + pair.Key + " must be a string.");
                foreach (var pair in comments) if (pair.Value is not JsonValue v || !v.TryGetValue<string>(out _)) throw new ArgumentException("commentsJson maps culture names to plain-text strings; " + pair.Key + " is not a string.");
                if (properties.Count == 0 && comments.Count == 0) throw new ArgumentException("update needs at least one propertiesJson scalar or one commentsJson culture.");
                request.Properties = properties;
                request.Comments = comments.Select(p => (p.Key, p.Value!.GetValue<string>())).ToArray();
            }
            else
            {
                if (properties.Count > 0) throw new ArgumentException("propertiesJson applies to update only.");
                if (comments.Count > 0) throw new ArgumentException("commentsJson applies to update only.");
            }
            request.Writing = action != "list" && action != "read" && !dryRun;
            return request;
        }

        // ---- documents (PlcTypeGroup.Documents named value types, PlcTypeComposition UDT documents) ------------------------------
        internal static readonly string[] DocumentKinds = { "document", "type" };
        internal static readonly string[] DocumentActions = { "list", "read", "export", "import", "createFromMasterCopy", "createFromLibraryType" };
        internal static readonly string[] ImportDocumentOptionNames = { "None", "Override", "SkipInactiveCultures", "ActivateInactiveCultures" };

        internal static bool ValidateDocumentRequest(string action, string objectKind, string name, string unitName, string unitKind, string directoryPath, string importOption,
            string libraryName, string masterCopyPath, string copyMode, string typePath, string version, string updatePathsMode, bool dryRun)
        {
            RequireOneOf(action, DocumentActions, "action");
            RequireOneOf(objectKind, DocumentKinds, "objectKind");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, UnitKinds, "unitKind");
            if (action == "list") Refuse(name, "name", "applies to every action except list.");
            else RequireName(name, "name");
            bool file = action == "export" || action == "import";
            if (file)
            {
                if (string.IsNullOrWhiteSpace(directoryPath) || !Path.IsPathRooted(directoryPath)) throw new ArgumentException("Absolute existing directoryPath required for export / import.");
            }
            else Refuse(directoryPath, "directoryPath", "applies to export / import only.");
            if (action == "import") RequireOneOf(importOption, ImportDocumentOptionNames, "importOption");
            else if (importOption != "Override" && !string.IsNullOrEmpty(importOption)) throw new ArgumentException("importOption applies to import only.");
            if (action == "createFromMasterCopy")
            {
                RequireName(masterCopyPath, "masterCopyPath", 1024);
                if (!string.IsNullOrEmpty(copyMode)) RequireOneOf(copyMode, MasterCopyModes, "copyMode");
            }
            else { Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only."); Refuse(copyMode, "copyMode", "applies to createFromMasterCopy only."); }
            if (action == "createFromLibraryType")
            {
                RequireName(typePath, "typePath", 1024); RequireName(version, "version", 64);
                if (!string.IsNullOrEmpty(updatePathsMode)) RequireOneOf(updatePathsMode, UpdatePathsModes, "updatePathsMode");
            }
            else { Refuse(typePath, "typePath", "applies to createFromLibraryType only."); Refuse(version, "version", "applies to createFromLibraryType only."); Refuse(updatePathsMode, "updatePathsMode", "applies to createFromLibraryType only."); }
            if (action != "createFromMasterCopy" && action != "createFromLibraryType") Refuse(libraryName, "libraryName", "applies to createFromMasterCopy / createFromLibraryType only (empty = project library).");
            // export writes files, never the project: it needs no exclusive access and no Offline PLC.
            return action != "list" && action != "read" && action != "export" && !dryRun;
        }

        internal static HashSet<string> SnapshotDirectory(string directoryPath)
            => new HashSet<string>(Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly), StringComparer.OrdinalIgnoreCase);
        internal static JsonArray NewFilesSince(string directoryPath, HashSet<string> before)
            => new JsonArray(Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Where(f => !before.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (JsonNode)FileRow(new FileInfo(f))).ToArray());
        internal static JsonObject FileRow(FileInfo file)
        {
            file.Refresh();
            var row = new JsonObject { ["path"] = file.FullName, ["exists"] = file.Exists, ["bytes"] = file.Exists ? file.Length : 0 };
            if (file.Exists && file.Length > 0) { using var stream = file.OpenRead(); using var sha = SHA256.Create(); row["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
            return row;
        }

        // ---- fingerprints (FingerprintProvider on blocks and UDTs) ------------------------------------------------------------------
        internal static readonly string[] FingerprintObjectKinds = { "block", "type" };
        internal static readonly string[] FingerprintIds = { "Code", "Comments", "Interface", "LibraryType", "Texts", "Alarms", "Supervisions", "TechnologyObject", "Events", "TextualInterface", "Properties", "ProgramCode" };
        internal static void ValidateFingerprintRequest(string objectKind, string objectPath, string unitName, string unitKind)
        {
            RequireOneOf(objectKind, FingerprintObjectKinds, "objectKind");
            RequireName(objectPath, "objectPath", 1024);
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, UnitKinds, "unitKind");
        }

        // ---- block write protection (PlcBlockWriteProtectionProvider, V21) ---------------------------------------------------------
        internal static readonly string[] WriteProtectionActions = { "read", "define", "protect", "unprotect", "change", "remove" };
        internal static bool ValidateWriteProtectionRequest(string action, string password, string newPassword, bool confirmProtectionChange, bool dryRun)
        {
            RequireOneOf(action, WriteProtectionActions, "action");
            if (action == "read")
            {
                if (!string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(newPassword)) throw new ArgumentException("read takes no password.");
                return false;
            }
            if (string.IsNullOrEmpty(password)) throw new ArgumentException(action + " requires the current password (define: the password to set).");
            if (action == "change") { if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("change requires newPassword; use remove to drop the password entirely."); }
            else if (!string.IsNullOrEmpty(newPassword)) throw new ArgumentException("newPassword applies to change only.");
            if (dryRun) return false;
            if (!confirmProtectionChange) throw new ArgumentException("Real write-protection changes require confirmProtectionChange=true besides dryRun=false.");
            return true;
        }
        // State machine of the official workflow: Define sets the password, Protect / Unprotect toggle protection with it,
        // Change(pwd, newPwd) rotates it and Change(pwd, null) removes password and protection.
        internal static string? WriteProtectionStateRefusal(string action, bool isDefined, bool isProtected)
        {
            switch (action)
            {
                case "define": return isDefined ? "A write-protection password is already defined; use change or remove." : null;
                case "protect": return !isDefined ? "No write-protection password is defined; define first." : isProtected ? "Block is already write protected." : null;
                case "unprotect": return !isDefined ? "No write-protection password is defined." : !isProtected ? "Block is not write protected." : null;
                case "change": case "remove": return !isDefined ? "No write-protection password is defined." : null;
                default: return null;
            }
        }

        // ---- project compilation settings (simulation / virtual PLC support) ----------------------------------------------------------
        internal static readonly string[] CompilationSettingsActions = { "read", "update" };
        internal static readonly string[] CompilationSettingKeys = { "IsSimulationDuringBlockCompilationEnabled", "IsVirtualPlcDuringBlockCompilationEnabled" };
        internal static (bool Writing, Dictionary<string, bool> Values) ValidateCompilationSettingsRequest(string action, string propertiesJson, bool dryRun)
        {
            RequireOneOf(action, CompilationSettingsActions, "action");
            var properties = ParseObject(propertiesJson, "propertiesJson");
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (action == "read")
            {
                if (properties.Count > 0) throw new ArgumentException("propertiesJson applies to update only.");
                return (false, values);
            }
            foreach (var pair in properties)
            {
                if (!CompilationSettingKeys.Contains(pair.Key, StringComparer.Ordinal)) throw new ArgumentException("propertiesJson accepts only " + string.Join(" / ", CompilationSettingKeys) + "; unknown: " + pair.Key);
                if (pair.Value is not JsonValue v || !v.TryGetValue<bool>(out var flag)) throw new ArgumentException("propertiesJson." + pair.Key + " must be a boolean.");
                values[pair.Key] = flag;
            }
            if (values.Count == 0) throw new ArgumentException("update needs at least one of " + string.Join(" / ", CompilationSettingKeys) + ".");
            return (!dryRun, values);
        }
    }
}

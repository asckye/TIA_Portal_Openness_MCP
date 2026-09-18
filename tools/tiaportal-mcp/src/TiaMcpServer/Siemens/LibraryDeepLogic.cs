using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/catalog helpers for the deep library family (ILibrary update check / synchronization / harmonize /
    // clean-up, type and version details, detailed object comparison, global library infos and archiving). No Siemens dependency.
    internal static class LibraryDeepLogic
    {
        // Enum member names of Siemens.Engineering.Library(.Types/.Compare) in the V20/V21 PublicAPI (identical in both).
        internal static readonly string[] UpdateCheckModes = { "ReportOutOfDateOnly", "ReportOutOfDateAndUpToDate" };
        internal static readonly string[] ForceUpdateModes = { "SetOnlyHigherUpdatedVersionAsDefault", "ForceSetAnyUpdatedVersionAsDefault", "NoDefaultVersionChange" };
        internal static readonly string[] DeleteUnusedVersionsModes = { "AutomaticallyDelete", "DoNotDelete" };
        internal static readonly string[] StructureConflictResolutionModes = { "UpdateStructure", "RetainStructure", "CancelIfStructureConflicts" };
        internal static readonly string[] HarmonizeOptions = { "HarmonizeNames", "HarmonizePaths" }; // None is refused by TIA; the two flags combine
        internal static readonly string[] CleanUpModes = { "PreserveDefaultVersionOfUnusedTypes", "DeleteUnusedTypes" };
        internal static readonly string[] ArchivationModes = { "None", "Compressed", "DiscardRestorableData", "DiscardRestorableDataAndCompressed" };
        internal static readonly string[] CreateOptions = { "None", "Override" };
        internal static readonly string[] ImportOptions = { "None", "SkipInactiveCultures", "ActivateInactiveCultures" };
        internal static readonly string[] ConsistencyStatuses = { "None", "Consistent", "DefaultVersionInconsistent", "DuplicateVersionInconsistent", "NonDefaultVersionInstantiation", "MultipleVersionsInstantiationInSameDevice" };
        internal static readonly string[] CompareStates = { "ContainerContentsDifferent", "ContainerContentsIdentical", "ObjectsDifferent", "LeftMissing", "RightMissing", "ObjectsIdentical" };
        internal static readonly string[] DetailStatuses = { "NotCompared", "ObjectsDifferent", "ObjectsIdentical" };

        internal static readonly string[] TypeActions = { "update", "delete", "updateLibrary", "updateProject" };
        internal static readonly string[] SyncActions = { "updateLibrary", "updateProject", "harmonizeProject", "cleanUp" };
        internal static readonly string[] CompareKinds = { "type", "version", "masterCopy" };
        internal static readonly string[] GlobalLibraryExtraActions = { "infos", "openInfo", "archive" };
        internal static readonly string[] TypeEditableProperties = { "Name", "DoNotUse", "SetForUpdate" };

        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);

        internal static Guid? ParseGuid(string guid, string parameter)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            if (!Guid.TryParse(guid.Trim(), out var parsed)) throw new ArgumentException(parameter + " must be a GUID (any standard format).");
            return parsed;
        }

        // A selection is a JSON array of {"type": "Folder/Sub/TypeName"} or {"folder": "Folder/Sub"}; "" or "/" as folder = the whole
        // Types system folder. At least one entry; at most 200; no duplicates.
        internal readonly struct Selection
        {
            internal Selection(bool folder, string path) { IsFolder = folder; Path = path; }
            internal bool IsFolder { get; }
            internal string Path { get; }
        }
        internal static Selection[] ParseSelection(string json)
        {
            var array = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? throw new ArgumentException("selectionJson must be a JSON array.");
            if (array.Count == 0) throw new ArgumentException("selectionJson must select at least one type or folder (use [{\"folder\":\"\"}] for the whole Types folder).");
            if (array.Count > 200) throw new ArgumentException("selectionJson: at most 200 entries.");
            var result = new List<Selection>();
            foreach (var node in array)
            {
                var obj = node as JsonObject ?? throw new ArgumentException("selectionJson entries must be objects with a single 'type' or 'folder' key.");
                if (obj.Count != 1 || !(obj.ContainsKey("type") || obj.ContainsKey("folder"))) throw new ArgumentException("selectionJson entries must be {\"type\": path} or {\"folder\": path}.");
                bool folder = obj.ContainsKey("folder");
                var path = (obj[folder ? "folder" : "type"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s!.Trim().Trim('/') : throw new ArgumentException("selectionJson paths must be strings.");
                if (!folder && path.Length == 0) throw new ArgumentException("selectionJson: a type entry needs a non-empty path.");
                if (path.Length > 0) EngineeringGroupOperations.Parts(path);
                result.Add(new Selection(folder, path));
            }
            if (result.Select(s => (s.IsFolder ? "F:" : "T:") + s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count) throw new ArgumentException("selectionJson: duplicate entry.");
            return result.ToArray();
        }
        internal static string[] ParseScopes(string json)
        {
            var scopes = HardwareNetworkLogic.ParseNames(string.IsNullOrWhiteSpace(json) ? "[]" : json, "scopeSoftwarePathsJson", 32);
            return scopes;
        }
        // HarmonizeProjectOptions is a flags enum; TIA throws on None, so at least one flag is required.
        internal static string JoinHarmonizeOptions(string[] names)
        {
            if (names.Length == 0) throw new ArgumentException("harmonizeOptionsJson must name HarmonizeNames and/or HarmonizePaths (TIA refuses None).");
            return HardwareNetworkLogic.JoinFlags(names, HarmonizeOptions, "harmonizeOptionsJson");
        }
        internal static void ValidateSyncRequest(string action, string libraryName, string targetLibraryName, string[] scopes, Selection[] selection,
            string forceUpdateMode, string deleteUnusedVersionsMode, string structureConflictResolutionMode, string cleanUpMode)
        {
            RequireOneOf(action, SyncActions, "action");
            RequireOneOf(forceUpdateMode, ForceUpdateModes, "forceUpdateMode"); RequireOneOf(deleteUnusedVersionsMode, DeleteUnusedVersionsModes, "deleteUnusedVersionsMode");
            RequireOneOf(structureConflictResolutionMode, StructureConflictResolutionModes, "structureConflictResolutionMode"); RequireOneOf(cleanUpMode, CleanUpModes, "cleanUpMode");
            if (action == "updateLibrary")
            {
                if (string.Equals(libraryName ?? "", targetLibraryName ?? "", StringComparison.Ordinal)) throw new ArgumentException("updateLibrary needs a target library different from the source (empty name = project library).");
            }
            if ((action == "updateProject" || action == "harmonizeProject") && scopes.Length == 0) throw new ArgumentException(action + " needs scopeSoftwarePathsJson with at least one exact PLC/HMI software path; a project-wide update without scope is refused.");
            if (selection.Length == 0) throw new ArgumentException("selectionJson is required.");
        }
        internal static void ValidateTypeRequest(string action, JsonObject properties, string targetLibraryName, string libraryName, string[] scopes)
        {
            RequireOneOf(action, TypeActions, "action");
            if (action == "update")
            {
                if (properties.Count == 0) throw new ArgumentException("update needs propertiesJson with Name, DoNotUse and/or SetForUpdate.");
                foreach (var key in properties.Select(p => p.Key)) RequireOneOf(key, TypeEditableProperties, "propertiesJson");
            }
            else if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to action=update only.");
            if (action == "updateLibrary" && string.Equals(libraryName ?? "", targetLibraryName ?? "", StringComparison.Ordinal)) throw new ArgumentException("updateLibrary needs a target library different from the source.");
            if (action == "updateProject" && scopes.Length == 0) throw new ArgumentException("updateProject needs scopeSoftwarePathsJson with at least one exact software path.");
        }
        internal static void ValidateCompareRequest(string kind, string leftPath, string rightPath, string leftVersion, string rightVersion)
        {
            RequireOneOf(kind, CompareKinds, "kind");
            if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath)) throw new ArgumentException("leftPath and rightPath are required (exact relative paths).");
            if (kind == "version" && (string.IsNullOrWhiteSpace(leftVersion) || string.IsNullOrWhiteSpace(rightVersion))) throw new ArgumentException("kind=version needs leftVersion and rightVersion (Major.Minor.Build).");
            if (kind != "version" && (!string.IsNullOrWhiteSpace(leftVersion) || !string.IsNullOrWhiteSpace(rightVersion))) throw new ArgumentException("leftVersion/rightVersion apply to kind=version only.");
        }
        internal static void ValidateArchiveName(string archiveName)
        {
            if (string.IsNullOrWhiteSpace(archiveName) || archiveName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || archiveName.Length > 128) throw new ArgumentException("archiveName must be a plain file name (extension optional; TIA adds .zalXX for compressed modes when none is given).");
        }
        internal static void ValidateBounds(int maxDepth, int maxItems)
        {
            if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth must be 1..16.");
            if (maxItems < 1 || maxItems > 5000) throw new ArgumentException("maxItems must be 1..5000.");
        }
    }
}

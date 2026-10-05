using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ExternalSourceRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        private static void Refuse(string value, string parameter, string reason)
            => ArgumentRules.Refuse(value, parameter, reason);
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

        internal static void ValidateSystemGroupRequest(string unitName, string unitKind, int maxDepth)
        {
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
        }
    }
}

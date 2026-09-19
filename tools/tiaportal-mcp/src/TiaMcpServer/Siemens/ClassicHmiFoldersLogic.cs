using System;
using System.IO;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // Phase 5 (2.7.37): pure logic (no Siemens dependency) for the classic WinCC folder hierarchy - screen / pop-up / template /
    // tag / VB-script folders, pop-up / template / slide-in / overview / global-element screen objects and multilingual graphics.
    internal static class ClassicHmiFoldersLogic
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
        private static void RequireAbsoluteFile(string filePath, string parameter)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute " + parameter + " required.");
        }

        // ---- screen tree ----
        internal static readonly string[] ScreenTreeKinds = { "all", "screens", "popups", "templates", "slideins" };
        internal static void ValidateTreeRequest(string kind, string folderPath, int maxDepth)
        {
            RequireOneOf(kind, ScreenTreeKinds, "kind");
            if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
            if (!string.IsNullOrEmpty(folderPath) && kind != "screens" && kind != "popups" && kind != "templates") throw new ArgumentException("folderPath applies to kind screens / popups / templates (slide-ins have no folders).");
            if (!string.IsNullOrEmpty(folderPath)) EngineeringGroupOperations.Parts(folderPath);
        }

        // ---- screen objects ----
        internal static readonly string[] ObjectKinds = { "popup", "template", "slidein", "overview", "globalElements" };
        internal static readonly string[] ObjectActions = { "read", "export", "import", "delete" };
        internal static readonly string[] SlideinTypes = { "Top", "Bottom", "Left", "Right" };
        internal static readonly string[] ImportOptionNames = { "None", "Override" };
        internal static bool ValidateObjectRequest(string objectKind, string objectPath, string action, string filePath, string importOptions, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(objectKind, ObjectKinds, "objectKind");
            RequireOneOf(action, ObjectActions, "action");
            bool singleton = objectKind == "overview" || objectKind == "globalElements";
            if (singleton)
            {
                Refuse(objectPath, "objectPath", "does not apply to overview / globalElements (one per HMI device).");
                if (action == "delete") throw new ArgumentException("overview / globalElements cannot be deleted (Openness exposes Export / Import only).");
            }
            else if (action == "import")
            {
                // the target folder; empty = the system folder (slide-ins always import into the system folder)
                if (objectKind == "slidein") Refuse(objectPath, "objectPath", "does not apply to slide-in import (the slide-in system folder is the only target).");
                else if (!string.IsNullOrEmpty(objectPath)) EngineeringGroupOperations.Parts(objectPath);
            }
            else
            {
                RequireName(objectPath, "objectPath", 1024);
                if (objectKind == "slidein") { RequireOneOf(objectPath, SlideinTypes, "objectPath (slide-ins are addressed by SlideinType)"); if (action == "delete") throw new ArgumentException("Slide-in screens cannot be deleted (no native Delete)."); }
                else EngineeringGroupOperations.Parts(objectPath);
            }
            if (action == "export" || action == "import") RequireAbsoluteFile(filePath, "filePath"); else Refuse(filePath, "filePath", "applies to export / import only.");
            if (action == "import") RequireOneOf(importOptions, ImportOptionNames, "importOptions");
            else if (!string.IsNullOrEmpty(importOptions) && importOptions != "None") throw new ArgumentException("importOptions applies to import only.");
            bool writing = (action == "import" || action == "delete") && !dryRun;
            if (action == "delete" && writing && !confirmDelete) throw new ArgumentException("Real deletion requires confirmDelete=true besides dryRun=false.");
            if (action == "import" && writing && importOptions == "Override" && !confirmDelete) throw new ArgumentException("Import with Override replaces existing objects; confirmDelete=true is required.");
            return writing;
        }

        // ---- folders ----
        internal static readonly string[] FolderKinds = { "screens", "popups", "templates", "tags", "scripts" };
        internal static readonly string[] FolderActions = { "read", "create", "delete" };
        internal static bool ValidateFolderRequest(string folderKind, string folderPath, string action, string newName, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(folderKind, FolderKinds, "folderKind");
            RequireOneOf(action, FolderActions, "action");
            if (!string.IsNullOrEmpty(folderPath)) EngineeringGroupOperations.Parts(folderPath);
            if (action == "create") { RequireName(newName, "newName"); if (newName.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("newName is one folder segment."); }
            else Refuse(newName, "newName", "applies to create only.");
            if (action == "delete" && string.IsNullOrEmpty(folderPath)) throw new ArgumentException("The system folder cannot be deleted; folderPath must name a user folder.");
            bool writing = action != "read" && !dryRun;
            if (action == "delete" && writing && !confirmDelete) throw new ArgumentException("Real deletion requires confirmDelete=true besides dryRun=false.");
            return writing;
        }

        // ---- multilingual graphics ----
        internal static readonly string[] GraphicActions = { "list", "read", "export", "import", "delete" };
        internal static bool ValidateGraphicRequest(string action, string name, string filePath, string importOptions, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, GraphicActions, "action");
            if (action == "list" || action == "import") Refuse(name, "name", "applies to read / export / delete.");
            else RequireName(name, "name");
            if (action == "export" || action == "import") RequireAbsoluteFile(filePath, "filePath"); else Refuse(filePath, "filePath", "applies to export / import only.");
            if (action == "import") RequireOneOf(importOptions, ImportOptionNames, "importOptions");
            else if (!string.IsNullOrEmpty(importOptions) && importOptions != "None") throw new ArgumentException("importOptions applies to import only.");
            bool writing = (action == "import" || action == "delete") && !dryRun;
            if (action == "delete" && writing && !confirmDelete) throw new ArgumentException("Real deletion requires confirmDelete=true besides dryRun=false.");
            if (action == "import" && writing && importOptions == "Override" && !confirmDelete) throw new ArgumentException("Import with Override replaces existing graphics; confirmDelete=true is required.");
            return writing;
        }
    }
}

using System;
using System.Linq;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Native.Hmi { internal static class ClassicHmiLogic {
        internal const string GraphicsProvider = "Siemens.Engineering.Hmi.Globalization.GraphicsProvider";
        internal static readonly string[] ScriptActions = { "read", "export", "import", "delete", "createFolder", "deleteFolder", "setAttributes" };
        internal static readonly string[] CycleActions = { "read", "export", "import", "delete", "setAttributes" };
        internal static readonly string[] ListActions = { "read", "readEntries", "createEntry", "deleteEntry", "export", "import", "delete", "setAttributes" };
        internal static readonly IReadOnlyDictionary<string, (string Property, string Type)> ListKinds = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["text"] = ("TextLists", "Siemens.Engineering.Hmi.TextGraphicList.TextList"),
            ["graphic"] = ("GraphicLists", "Siemens.Engineering.Hmi.TextGraphicList.GraphicList"),
        };
        internal static readonly IReadOnlyDictionary<string, string> LibraryTypeKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["faceplate"] = "Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType",
            ["vbScript"] = "Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryType",
            ["cScript"] = "Siemens.Engineering.Hmi.RuntimeScripting.CScriptLibraryType",
        };

internal static string RequireAction(string action, string[] allowed)
        {
            if (!allowed.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", allowed) + ".");
            return action;
        }
internal static string RequireName(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact " + what + " (single nonempty segment) required.");
            return value;
        }
internal static void RequirePagination(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
        }
internal static void RequireImportConfirmation(string importOptions, bool confirmDelete)
        {
            if (importOptions == "Override" && !confirmDelete) throw new ArgumentException("importOptions=Override replaces existing objects; confirmDelete=true is required.");
        }
internal static (string[] Folder, string Name) SplitObjectPath(string path)
        {
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 64 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x == "." || x == ".."))
                throw new ArgumentException("Use a relative exact path with 1-64 nonempty segments, without '.' or '..'.");
            return (parts.Take(parts.Length - 1).ToArray(), parts[parts.Length - 1]);
        }
} }

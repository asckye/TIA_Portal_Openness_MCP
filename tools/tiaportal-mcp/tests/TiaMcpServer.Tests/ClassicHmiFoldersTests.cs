using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 5 (2.7.37) pure logic: classic WinCC screen-tree requests, pop-up / template / slide-in / overview / global-element object
    // requests, folder requests for the five folder families and multilingual graphic requests.
    internal static class ClassicHmiFoldersTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string xml = Path.Combine(temp, "popup.xml");

            // ---- screen tree ----
            ClassicHmiFoldersLogic.ValidateTreeRequest("all", "", 4); ClassicHmiFoldersLogic.ValidateTreeRequest("popups", "Folder/Sub", 1); ClassicHmiFoldersLogic.ValidateTreeRequest("slideins", "", 16);
            check(true, "hmi tree: requests accepted");
            check(Fails<ArgumentException>(() => ClassicHmiFoldersLogic.ValidateTreeRequest("faceplates", "", 4)), "hmi tree: unknown kind refused");
            check(Fails<ArgumentException>(() => ClassicHmiFoldersLogic.ValidateTreeRequest("slideins", "F", 4)), "hmi tree: folderPath on slide-ins refused");
            check(Fails<ArgumentException>(() => ClassicHmiFoldersLogic.ValidateTreeRequest("all", "F", 4)), "hmi tree: folderPath with kind all refused");
            check(Fails<ArgumentException>(() => ClassicHmiFoldersLogic.ValidateTreeRequest("screens", "", 0)), "hmi tree: maxDepth 0 refused");
            check(Fails<ArgumentException>(() => ClassicHmiFoldersLogic.ValidateTreeRequest("screens", "a/../b", 4)), "hmi tree: dotted folderPath refused");

            // ---- screen objects ----
            bool O(string kind, string path, string action, string file = "", string options = "None", bool confirm = false, bool dryRun = true) => ClassicHmiFoldersLogic.ValidateObjectRequest(kind, path, action, file, options, confirm, dryRun);
            check(!O("popup", "Folder/Popup_1", "read") && !O("popup", "Popup_1", "export", xml, dryRun: false), "hmi objects: read / export never write the project");
            check(O("popup", "Popup_1", "delete", confirm: true, dryRun: false) && !O("popup", "Popup_1", "delete", confirm: true), "hmi objects: popup delete writes only for real");
            check(O("template", "Folder", "import", xml, "None", dryRun: false) && O("template", "", "import", xml, "Override", true, false), "hmi objects: template import into a folder or the system folder");
            check(!O("slidein", "Top", "read") && !O("slidein", "Left", "export", xml, dryRun: false) && O("slidein", "", "import", xml, dryRun: false), "hmi objects: slide-in by SlideinType, import into the system folder");
            check(!O("overview", "", "export", xml, dryRun: false) && O("globalElements", "", "import", xml, dryRun: false), "hmi objects: overview / global elements export and import");
            check(Fails<ArgumentException>(() => O("screen", "S", "read")), "hmi objects: plain screens refused (other tools)");
            check(Fails<ArgumentException>(() => O("popup", "", "read")), "hmi objects: read without objectPath refused");
            check(Fails<ArgumentException>(() => O("overview", "X", "read")), "hmi objects: objectPath on overview refused");
            check(Fails<ArgumentException>(() => O("overview", "", "delete", confirm: true, dryRun: false)), "hmi objects: overview delete refused");
            check(Fails<ArgumentException>(() => O("slidein", "Middle", "read")), "hmi objects: unknown SlideinType refused");
            check(Fails<ArgumentException>(() => O("slidein", "Top", "delete", confirm: true, dryRun: false)), "hmi objects: slide-in delete refused (no native Delete)");
            check(Fails<ArgumentException>(() => O("slidein", "Top", "import", xml)), "hmi objects: objectPath on slide-in import refused");
            check(Fails<ArgumentException>(() => O("popup", "Popup_1", "export")), "hmi objects: export without filePath refused");
            check(Fails<ArgumentException>(() => O("popup", "Popup_1", "export", "relative.xml")), "hmi objects: relative filePath refused");
            check(Fails<ArgumentException>(() => O("popup", "Popup_1", "read", xml)), "hmi objects: filePath on read refused");
            check(Fails<ArgumentException>(() => O("popup", "", "import", xml, "Merge")), "hmi objects: unknown importOptions refused");
            check(Fails<ArgumentException>(() => O("popup", "Popup_1", "export", xml, "Override")), "hmi objects: importOptions on export refused");
            check(Fails<ArgumentException>(() => O("popup", "Popup_1", "delete", dryRun: false)), "hmi objects: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => O("popup", "", "import", xml, "Override", false, false)), "hmi objects: real Override import without confirmDelete refused");
            check(ClassicHmiFoldersLogic.ObjectKinds.Length == 5 && ClassicHmiFoldersLogic.SlideinTypes.SequenceEqual(new[] { "Top", "Bottom", "Left", "Right" }), "hmi objects: catalogs");

            // ---- folders ----
            bool F(string kind, string path, string action, string newName = "", bool confirm = false, bool dryRun = true) => ClassicHmiFoldersLogic.ValidateFolderRequest(kind, path, action, newName, confirm, dryRun);
            foreach (var kind in ClassicHmiFoldersLogic.FolderKinds) { check(!F(kind, "", "read") && F(kind, "", "create", "Sub", dryRun: false) && F(kind, "Sub", "delete", confirm: true, dryRun: false), "hmi folders: " + kind + " read / create / delete accepted"); }
            check(!F("tags", "", "create", "Sub"), "hmi folders: create preview does not write");
            check(Fails<ArgumentException>(() => F("cycles", "", "read")), "hmi folders: unknown folderKind refused");
            check(Fails<ArgumentException>(() => F("tags", "", "create")), "hmi folders: create without newName refused");
            check(Fails<ArgumentException>(() => F("tags", "", "create", "A/B")), "hmi folders: multi-segment newName refused");
            check(Fails<ArgumentException>(() => F("tags", "", "read", "X")), "hmi folders: newName on read refused");
            check(Fails<ArgumentException>(() => F("tags", "", "delete", confirm: true, dryRun: false)), "hmi folders: system folder delete refused");
            check(Fails<ArgumentException>(() => F("tags", "Sub", "delete", dryRun: false)), "hmi folders: real delete without confirmDelete refused");

            // ---- graphics ----
            bool G(string action, string name = "", string file = "", string options = "None", bool confirm = false, bool dryRun = true) => ClassicHmiFoldersLogic.ValidateGraphicRequest(action, name, file, options, confirm, dryRun);
            check(!G("list") && !G("read", "Logo") && !G("export", "Logo", xml, dryRun: false), "hmi graphics: list / read / export never write the project");
            check(G("import", "", xml, "None", dryRun: false) && G("delete", "Logo", confirm: true, dryRun: false), "hmi graphics: import / delete write");
            check(Fails<ArgumentException>(() => G("rename", "Logo")), "hmi graphics: unknown action refused");
            check(Fails<ArgumentException>(() => G("list", "Logo")), "hmi graphics: name on list refused");
            check(Fails<ArgumentException>(() => G("export", "Logo")), "hmi graphics: export without filePath refused");
            check(Fails<ArgumentException>(() => G("import", "", xml, "Override", false, false)), "hmi graphics: real Override import without confirmDelete refused");
            check(Fails<ArgumentException>(() => G("delete", "Logo", dryRun: false)), "hmi graphics: real delete without confirmDelete refused");
        }
    }
}

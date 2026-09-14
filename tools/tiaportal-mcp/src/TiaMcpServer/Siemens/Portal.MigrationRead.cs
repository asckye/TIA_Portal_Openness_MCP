using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private readonly MigrationPages migrationPages = new MigrationPages();
        private ResponseMessage MigrationPage(string tool, string softwarePath, string expectedProject, JsonObject scope, string cursor, int pageSize, int budgetMs,
            Func<object, IEnumerable<JsonObject>> read)
        {
            scope["tool"] = tool; scope["softwarePath"] = softwarePath; scope["expectedProject"] = expectedProject;
            JsonObject result;
            try
            {
                if (CurrentProject == null) throw new InvalidOperationException("No open project.");
                var project = CurrentProject;
                var projectName = MigrationRead.Get(project, "Name")?.ToString(); scope["project"] = projectName;
                if (string.IsNullOrWhiteSpace(expectedProject) || projectName != expectedProject) throw new InvalidOperationException("Exact expectedProject mismatch; collection was not started.");
                if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("An explicit HMI software path is required.");
                var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Resolved software is not WinCC Unified HmiSoftware: " + hmi.GetType().FullName);
                result = migrationPages.Read(project, scope.ToJsonString(), cursor, pageSize, budgetMs, () => read(hmi));
            }
            catch (Exception ex)
            {
                result = new JsonObject { ["schemaVersion"] = 1, ["scope"] = scope.DeepClone(), ["readOnly"] = true,
                    ["apiCallSuccess"] = false, ["dataComplete"] = false, ["traversalComplete"] = false, ["truncated"] = true,
                    ["expectedCount"] = null, ["actualCount"] = 0, ["nextCursor"] = null,
                    ["failures"] = new JsonArray(MigrationRead.Failure(tool, "RequestFailed", ex)), ["records"] = new JsonArray() };
            }
            result["success"] = result["apiCallSuccess"]?.DeepClone(); result["operationSuccess"] = result["apiCallSuccess"]?.DeepClone();
            return new ResponseMessage { Message = "Read-only collection. apiCallSuccess and dataComplete are separate; inspect failures and nextCursor.", Meta = result };
        }
        public ResponseMessage ListUnifiedGlobalScripts(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ListUnifiedGlobalScripts", softwarePath, expectedProject, new JsonObject(), cursor, pageSize, budgetMs, UnifiedNativeRead.ListScripts);
        public ResponseMessage ReadUnifiedGlobalScript(string softwarePath, string expectedProject, string moduleName, string cursor = "", int pageSize = 50, int budgetMs = 5000)
            => MigrationPage("ReadUnifiedGlobalScript", softwarePath, expectedProject, new JsonObject { ["moduleName"] = moduleName }, cursor, pageSize, budgetMs, hmi => UnifiedNativeRead.Script(hmi, moduleName));
        public ResponseMessage ReadUnifiedTagDefinitions(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ReadUnifiedTagDefinitions", softwarePath, expectedProject, new JsonObject(), cursor, pageSize, budgetMs, UnifiedTagDefinitions.Read);
        public ResponseMessage ReadUnifiedScreenBranch(string softwarePath, string expectedProject, string screenPath, string itemName = "", string branchJson = "[]", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ReadUnifiedScreenBranch", softwarePath, expectedProject, new JsonObject { ["screenPath"] = screenPath, ["itemName"] = itemName, ["branch"] = branchJson }, cursor, pageSize, budgetMs,
                hmi => ScreenBranch(hmi, screenPath, itemName, branchJson));
        private static IEnumerable<JsonObject> ScreenBranch(object hmi, string screenPath, string itemName, string branchJson)
        {
            object root = MigrationRead.Screen(hmi, screenPath);
            var path = "/Screens" + screenPath;
            if (!string.IsNullOrEmpty(itemName)) { root = MigrationRead.Named(MigrationRead.Get(root, "ScreenItems")!, itemName); path += "/ScreenItems/" + MigrationRead.Segment(itemName); }
            var branch = MigrationRead.Branch(root, branchJson);
            yield return new JsonObject { ["path"] = path, ["kind"] = "branchSelection", ["status"] = "ok", ["branch"] = JsonNode.Parse(branchJson), ["evidenceRoot"] = path + "/$selected" };
            foreach (var row in MigrationRead.Graph(branch, path + "/$selected")) yield return row;
            if (branch != null && branch.GetType().Name == "HmiFaceplateContainer") yield return UnifiedNativeRead.InstanceLink(branch, path + "/$libraryVersion");
        }
        public ResponseMessage ReadUnifiedLibraryType(string softwarePath, string expectedProject, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ReadUnifiedLibraryType", softwarePath, expectedProject, new JsonObject { ["typePath"] = typePath, ["version"] = version }, cursor, pageSize, budgetMs,
                hmi => UnifiedNativeRead.LibraryType(MigrationRead.Get(CurrentProject!, "ProjectLibrary")!, typePath, version));
        public ResponseMessage ListUnifiedLibraryFolder(string softwarePath, string expectedProject, string folderPath = "/", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ListUnifiedLibraryFolder", softwarePath, expectedProject, new JsonObject { ["folderPath"] = folderPath }, cursor, pageSize, budgetMs,
                hmi => LibraryFolder(MigrationRead.Get(CurrentProject!, "ProjectLibrary")!, folderPath));
        private static IEnumerable<JsonObject> LibraryFolder(object library, string path)
        {
            if (!path.StartsWith("/") || (path != "/" && path.EndsWith("/"))) throw new ArgumentException("Use / or an exact URI-escaped library folder path.");
            var folder = MigrationRead.Get(library, "TypeFolder")!;
            if (path != "/") foreach (var name in path.Substring(1).Split('/')) folder = MigrationRead.Named(MigrationRead.Get(folder, "Folders")!, Uri.UnescapeDataString(name));
            foreach (var prop in new[] { "Folders", "Types" })
                foreach (var row in MigrationRead.Property(folder, prop, "/ProjectLibrary" + path.TrimEnd('/') + "/" + prop,
                    (v, p) => MigrationRead.Collection(v!, p, (item, q) => LibraryIdentity(item, q)))) yield return row;
        }
        private static IEnumerable<JsonObject> LibraryIdentity(object item, string path)
        {
            yield return new JsonObject { ["path"] = path, ["kind"] = "libraryInventory", ["status"] = "ok", ["name"] = MigrationRead.Get(item, "Name")?.ToString(), ["type"] = item.GetType().FullName };
            if (item.GetType().GetProperty("Versions") != null)
                foreach (var row in MigrationRead.Property(item, "Versions", path + "/Versions", (v, p) => MigrationRead.Collection(v!, p, VersionIdentity))) yield return row;
        }
        private static IEnumerable<JsonObject> VersionIdentity(object version, string path)
        {
            foreach (var name in new[] { "VersionNumber", "Guid", "State" })
                foreach (var row in MigrationRead.Property(version, name, path + "/" + name, (v, p) => MigrationRead.Graph(v, p))) yield return row;
        }
        public ResponseMessage ReadUnifiedFaceplateInstance(string softwarePath, string expectedProject, string screenPath, string itemName, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => MigrationPage("ReadUnifiedFaceplateInstance", softwarePath, expectedProject, new JsonObject { ["screenPath"] = screenPath, ["itemName"] = itemName, ["typePath"] = typePath, ["version"] = version }, cursor, pageSize, budgetMs,
                hmi => FaceplateInstance(hmi, MigrationRead.Get(CurrentProject!, "ProjectLibrary")!, screenPath, itemName, typePath, version));
        private static IEnumerable<JsonObject> FaceplateInstance(object hmi, object library, string screenPath, string itemName, string typePath, string version)
        {
            var item = MigrationRead.Named(MigrationRead.Get(MigrationRead.Screen(hmi, screenPath), "ScreenItems")!, itemName);
            if (item.GetType().Name != "HmiFaceplateContainer") throw new ArgumentException("Selected screen item is not a Unified faceplate container.");
            var path = "/Screens" + screenPath + "/ScreenItems/" + MigrationRead.Segment(itemName);
            var link = UnifiedNativeRead.InstanceLink(item, path + "/$libraryVersion"); yield return link;
            foreach (var row in MigrationRead.Graph(item, path)) yield return row;
            var type = UnifiedNativeRead.ExactType(library, typePath); var selected = UnifiedNativeRead.ExactVersion(type, version);
            var selectedGuid = MigrationRead.Get(selected, "Guid")?.ToString();
            bool matched = link["status"]?.ToString() == "ok" && link["versionGuid"]?.ToString() == selectedGuid;
            yield return new JsonObject { ["path"] = path + "/$trace", ["kind"] = "faceplateTrace", ["status"] = matched ? "ok" : "unsupported",
                ["instancePath"] = path, ["interfaceAssignmentsPath"] = path + "/Interface", ["typePath"] = typePath, ["version"] = version,
                ["typeVersionGuid"] = selectedGuid, ["instanceVersionVerified"] = matched,
                ["reason"] = matched ? null : "Caller-selected type/version is not verified against the instance. Do not claim this type's internals are the instance's internals.",
                ["bindingResolution"] = "Raw interface values and native internal binding paths are returned for exact-name joining; dynamic expressions require resolution." };
            if (!matched) yield break;
            foreach (var row in UnifiedNativeRead.LibraryType(library, typePath, version)) yield return row;
        }
        public ResponseMessage ReleaseUnifiedReadCursor(string cursor)
            => new ResponseMessage { Message = "Collection state released; no TIA project or process was closed.", Meta = new JsonObject { ["success"] = true, ["readOnly"] = true, ["released"] = migrationPages.Cancel(cursor) } };
    }
}

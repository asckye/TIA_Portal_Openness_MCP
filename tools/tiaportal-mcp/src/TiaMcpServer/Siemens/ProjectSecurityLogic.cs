using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/paging/tree logic for the ProjectSecurity family. No Siemens.Engineering dependency.
    internal static class ProjectSecurityLogic
    {
        internal static readonly string[] ReadCategories = { "users", "anonymousUser", "systemRoles", "customRoles", "engineeringRights", "customDeviceRights", "umcUsers", "umcUserGroups", "passwordPolicy", "deviceRights", "roleDeviceRights" };
        internal static readonly string[] DeviceCategories = { "deviceRights", "roleDeviceRights" };

        internal sealed class UserManagementRequest
        {
            public string Action = "";
            public string Target = "";          // user | role | deviceRight
            public bool NeedsPassword, NeedsRole, NeedsRight, NeedsDevice, NeedsGroup, Creates, Deletes;
        }
        private static readonly Dictionary<string, UserManagementRequest> UserActions = new[] {
            new UserManagementRequest { Action = "createUser", Target = "user", NeedsPassword = true, Creates = true },
            new UserManagementRequest { Action = "deleteUser", Target = "user", Deletes = true },
            new UserManagementRequest { Action = "setUserPassword", Target = "user", NeedsPassword = true },
            new UserManagementRequest { Action = "activateUser", Target = "user" },
            new UserManagementRequest { Action = "deactivateUser", Target = "user" },
            new UserManagementRequest { Action = "assignRole", Target = "user", NeedsRole = true },
            new UserManagementRequest { Action = "unassignRole", Target = "user", NeedsRole = true },
            new UserManagementRequest { Action = "createRole", Target = "role", Creates = true },
            new UserManagementRequest { Action = "deleteRole", Target = "role", Deletes = true },
            new UserManagementRequest { Action = "assignEngineeringRight", Target = "role", NeedsRight = true },
            new UserManagementRequest { Action = "unassignEngineeringRight", Target = "role", NeedsRight = true },
            new UserManagementRequest { Action = "assignDeviceRight", Target = "role", NeedsRight = true, NeedsDevice = true },
            new UserManagementRequest { Action = "unassignDeviceRight", Target = "role", NeedsRight = true, NeedsDevice = true },
            new UserManagementRequest { Action = "createDeviceRight", Target = "deviceRight", NeedsGroup = true, Creates = true },
            new UserManagementRequest { Action = "deleteDeviceRight", Target = "deviceRight", Deletes = true },
        }.ToDictionary(x => x.Action, StringComparer.Ordinal);
        internal static IEnumerable<string> UserActionNames => UserActions.Keys;

        internal static UserManagementRequest ValidateUserManagement(string action, string name, string password, string roleName, string rightName, string group, string devicePathJson)
        {
            if (action == null || !UserActions.TryGetValue(action, out var request)) throw new ArgumentException("action must be one of: " + string.Join(", ", UserActions.Keys));
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Exact nonempty " + request.Target + " name required (max 256 chars).");
            if (request.NeedsPassword && string.IsNullOrEmpty(password)) throw new ArgumentException("password required for " + action + "; it is passed to the API as SecureString and never logged.");
            if (!request.NeedsPassword && !string.IsNullOrEmpty(password)) throw new ArgumentException("password is not accepted for " + action + ".");
            if (request.NeedsRole && string.IsNullOrWhiteSpace(roleName)) throw new ArgumentException("Exact roleName required for " + action + ".");
            if (request.NeedsRight && string.IsNullOrWhiteSpace(rightName)) throw new ArgumentException("Exact rightName required for " + action + ".");
            if (request.NeedsGroup && string.IsNullOrWhiteSpace(group)) throw new ArgumentException("group required for createDeviceRight.");
            if (request.NeedsDevice) ValidateDevicePath(devicePathJson);
            return request;
        }
        internal static void ValidateDevicePath(string devicePathJson)
        {
            JsonArray? names;
            try { names = JsonNode.Parse(devicePathJson ?? "") as JsonArray; }
            catch (System.Text.Json.JsonException ex) { throw new ArgumentException("devicePathJson must be a JSON array of exact names: " + ex.Message); }
            if (names == null) throw new ArgumentException("devicePathJson must be a JSON array of exact names.");
            if (names.Count < 1 || names.Count > 64 || names.Any(n => n is not JsonValue || string.IsNullOrWhiteSpace(n!.GetValue<string>()))) throw new ArgumentException("devicePathJson needs 1-64 exact nonempty names.");
        }
        internal static void ValidateCategory(string category)
        {
            if (!ReadCategories.Contains(category, StringComparer.Ordinal)) throw new ArgumentException("category must be one of: " + string.Join(", ", ReadCategories));
        }
        internal static SecureString Secure(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Empty password refused.");
            var secure = new SecureString();
            foreach (var c in password) secure.AppendChar(c);
            secure.MakeReadOnly();
            return secure;
        }
        internal static void ValidatePage(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
        }
        internal static void ValidateDepth(int maxDepth)
        {
            if (maxDepth < 1 || maxDepth > 32) throw new ArgumentException("maxDepth 1..32 required.");
        }
        // Slices rows honestly: expected/actual counts, nextOffset, truncated. dataComplete stays the caller's decision.
        internal static JsonArray Page(IReadOnlyList<JsonNode?> rows, int offset, int limit, JsonObject meta)
        {
            ValidatePage(offset, limit);
            var page = new JsonArray(rows.Skip(offset).Take(limit).Select(r => r?.DeepClone()).ToArray());
            PageMeta(meta, rows.Count, offset, limit, page.Count);
            return page;
        }
        internal static void PageMeta(JsonObject meta, int total, int offset, int limit, int pageCount)
        {
            ValidatePage(offset, limit);
            if (pageCount < 0 || pageCount > limit || offset + pageCount > total) throw new ArgumentException("Page slice inconsistent with total.");
            meta["expectedCount"] = total; meta["actualCount"] = pageCount; meta["offset"] = offset; meta["limit"] = limit;
            meta["nextOffset"] = offset + pageCount < total ? offset + pageCount : (int?)null;
            meta["truncated"] = offset + pageCount < total;
        }

        internal static readonly string[] MultiuserActions = { "read", "listServerProjects", "readLockState", "listLocalSessions", "connectServer", "disconnectServer", "commit" };
        internal static readonly string[] MultiuserMutations = { "connectServer", "disconnectServer", "commit" };
        internal static bool ValidateMultiuser(string action, string serverName, string projectName, string protocol, string host, int port, string commitComment, bool confirmChange, bool dryRun)
        {
            if (!MultiuserActions.Contains(action, StringComparer.Ordinal)) throw new ArgumentException("action must be one of: " + string.Join(", ", MultiuserActions));
            bool mutation = MultiuserMutations.Contains(action, StringComparer.Ordinal);
            if (mutation && !dryRun && !confirmChange) throw new ArgumentException("Real multiuser changes require confirmChange=true together with dryRun=false.");
            if (action != "read" && action != "commit" && string.IsNullOrWhiteSpace(serverName)) throw new ArgumentException("Exact serverName (server alias) required for " + action + ".");
            if ((action == "readLockState" || action == "listLocalSessions") && string.IsNullOrWhiteSpace(projectName)) throw new ArgumentException("Exact server projectName required for " + action + ".");
            if (action == "connectServer")
            {
                if (protocol != "Https" && protocol != "Http") throw new ArgumentException("protocol must be Https or Http.");
                if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required for connectServer.");
                if (port < 1 || port > 65535) throw new ArgumentException("port 1..65535 required for connectServer.");
            }
            if (action == "commit" && string.IsNullOrWhiteSpace(commitComment)) throw new ArgumentException("Nonempty commitComment required; CloseAndCommit closes the bound local session.");
            return mutation;
        }

        internal static void ValidateLibraryPair(string leftLibraryName, string rightLibraryName)
        {
            if (string.IsNullOrEmpty(leftLibraryName) && string.IsNullOrEmpty(rightLibraryName)) throw new ArgumentException("Comparing the project library to itself is refused; name at least one open global library.");
            if (string.Equals(leftLibraryName, rightLibraryName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Left and right library must differ.");
        }
        internal static readonly string[] CompareKinds = { "software", "softwareToLibrary", "hardware" };
        internal static void ValidateCompareRequest(string kind, string softwarePath, string devicePathJson, string targetProjectName, string targetSoftwarePath, string targetDevicePathJson)
        {
            if (!CompareKinds.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException("kind must be one of: " + string.Join(", ", CompareKinds));
            if (kind == "hardware") { ValidateDevicePath(devicePathJson); ValidateDevicePath(targetDevicePathJson); }
            else if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("Exact softwarePath required.");
            if (kind == "software")
            {
                bool other = !string.IsNullOrWhiteSpace(targetProjectName);
                if (other) ValidateDevicePath(targetDevicePathJson);
                else if (string.IsNullOrWhiteSpace(targetSoftwarePath)) throw new ArgumentException("targetSoftwarePath (same project) or targetProjectName + targetDevicePathJson/targetItemPathJson (other open project) required.");
                else if (string.Equals(softwarePath, targetSoftwarePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source and target software must differ.");
            }
            if (kind == "hardware" && string.IsNullOrWhiteSpace(targetProjectName) && devicePathJson == targetDevicePathJson) throw new ArgumentException("Source and target device paths must differ unless targetProjectName names another open project (item paths are compared natively).");
        }

        private static readonly string[] IdenticalStates = { "ObjectsIdentical", "ContainerContentsIdentical", "FolderContentsIdentical", "CompareIrrelevant" };
        internal static bool IsIdentical(string? state) => state != null && IdenticalStates.Contains(state, StringComparer.Ordinal);
        // Depth-first flatten with path/depth/childCount. Bounded; sets truncated instead of silently dropping.
        internal static List<JsonObject> FlattenCompareTree(object? root, Func<object, IEnumerable<object>> children, Func<object, JsonObject> describe, int maxDepth, int maxRows, out bool truncated)
        {
            var rows = new List<JsonObject>(); truncated = false;
            if (root == null) return rows;
            var stack = new Stack<(object node, string path, int depth)>();
            stack.Push((root, "", 0));
            while (stack.Count > 0)
            {
                var (node, parentPath, depth) = stack.Pop();
                if (rows.Count >= maxRows) { truncated = true; break; }
                var row = describe(node);
                string name = row["leftName"]?.GetValue<string>() ?? "";
                if (name.Length == 0) name = row["rightName"]?.GetValue<string>() ?? "";
                string path = parentPath.Length == 0 ? name : parentPath + "/" + name;
                var kids = children(node).ToList();
                row["path"] = path; row["depth"] = depth; row["childCount"] = kids.Count;
                if (depth >= maxDepth && kids.Count > 0) { row["childrenOmitted"] = true; truncated = true; kids.Clear(); }
                rows.Add(row);
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path, depth + 1));
            }
            return rows;
        }
        internal static JsonObject Summarize(IEnumerable<JsonObject> rows)
        {
            var summary = new JsonObject();
            foreach (var group in rows.GroupBy(r => r["comparisonResult"]?.GetValue<string>() ?? "Unknown").OrderBy(g => g.Key, StringComparer.Ordinal)) summary[group.Key] = group.Count();
            return summary;
        }
    }
}

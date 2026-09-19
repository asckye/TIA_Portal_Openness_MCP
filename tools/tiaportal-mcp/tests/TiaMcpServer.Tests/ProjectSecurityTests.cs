using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class ProjectSecurityTests
    {
        private sealed class Node
        {
            public string Left = "", Right = "", State = "ObjectsDifferent";
            public List<Node> Children = new List<Node>();
        }
        internal static void Run(Action<bool,string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            bool FailsWith<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } catch { return false; } }

            // ---- UMAC request validation ----
            var request = ProjectSecurityLogic.ValidateUserManagement("createUser", "op1", "secret", "", "", "", "[]");
            check(request.Target == "user" && request.Creates && request.NeedsPassword, "createUser sentinel: user target, creates, needs password");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("createUser", "op1", "", "", "", "", "[]")), "createUser without password refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("deleteUser", "op1", "secret", "", "", "", "[]")), "password refused for actions that do not take one");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("dropEverything", "x", "", "", "", "", "[]")), "unknown UMAC action refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("deleteUser", "  ", "", "", "", "", "[]")), "blank name refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("assignRole", "op1", "", "", "", "", "[]")), "assignRole without roleName refused");
            check(ProjectSecurityLogic.ValidateUserManagement("assignRole", "op1", "", "Engineer", "", "", "[]").NeedsRole, "assignRole sentinel with roleName passes");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("assignEngineeringRight", "R1", "", "", "", "", "[]")), "assignEngineeringRight without rightName refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("assignDeviceRight", "R1", "", "", "HMI_Operate", "", "[]")), "assignDeviceRight with empty device path refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("assignDeviceRight", "R1", "", "", "HMI_Operate", "", "not json")), "assignDeviceRight with invalid JSON path refused");
            var deviceRequest = ProjectSecurityLogic.ValidateUserManagement("assignDeviceRight", "R1", "", "", "HMI_Operate", "", "[\"PLC_1\"]");
            check(deviceRequest.NeedsDevice && deviceRequest.NeedsRight && deviceRequest.Target == "role", "assignDeviceRight sentinel passes with device path");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("createDeviceRight", "Right1", "", "", "", "", "[]")), "createDeviceRight without group refused");
            check(ProjectSecurityLogic.ValidateUserManagement("deleteDeviceRight", "Right1", "", "", "", "", "[]").Deletes, "deleteDeviceRight flagged as delete");
            check(ProjectSecurityLogic.UserActionNames.Count() == 17, "seventeen UMAC actions exposed (15 + activateAnonymousUser/deactivateAnonymousUser since 2.7.32)");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCategory("secrets")), "unknown read category refused");
            check(!Fails(() => ProjectSecurityLogic.ValidateCategory("users")), "users category sentinel passes");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateDevicePath("[\"\"]")), "device path with empty name refused");

            // ---- SecureString ----
            using (var secure = ProjectSecurityLogic.Secure("pw"))
                check(secure.Length == 2 && secure.IsReadOnly(), "password converted to read-only SecureString");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.Secure("")), "empty password refused");

            // ---- Paging ----
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidatePage(-1, 10)), "negative offset refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidatePage(0, 501)), "limit above 500 refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidatePage(0, 0)), "limit 0 refused");
            var rows = Enumerable.Range(0, 7).Select(i => (JsonNode?)new JsonObject { ["i"] = i }).ToList();
            var meta = new JsonObject();
            var page = ProjectSecurityLogic.Page(rows, 5, 3, meta);
            check(page.Count == 2 && page[0]!["i"]!.GetValue<int>() == 5, "page slices from offset");
            check(meta["expectedCount"]!.GetValue<int>() == 7 && meta["actualCount"]!.GetValue<int>() == 2 && meta["nextOffset"] == null && meta["truncated"]!.GetValue<bool>() == false, "last page reports no next offset");
            meta = new JsonObject(); ProjectSecurityLogic.Page(rows, 0, 3, meta);
            check(meta["nextOffset"]!.GetValue<int>() == 3 && meta["truncated"]!.GetValue<bool>(), "first page reports next offset and truncation");
            meta = new JsonObject(); ProjectSecurityLogic.PageMeta(meta, 10, 4, 4, 4);
            check(meta["nextOffset"]!.GetValue<int>() == 8, "PageMeta computes next offset from a pre-sliced page");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.PageMeta(new JsonObject(), 10, 8, 4, 4)), "PageMeta rejects a slice that exceeds the total");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateDepth(0)) && FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateDepth(33)), "maxDepth bounds enforced");

            // ---- Multiuser validation ----
            check(!ProjectSecurityLogic.ValidateMultiuser("read", "", "", "Https", "", 0, "", false, true), "read is not a mutation");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("listServerProjects", "", "", "Https", "", 0, "", false, true)), "listServerProjects requires serverName");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("readLockState", "srv", "", "Https", "", 0, "", false, true)), "readLockState requires projectName");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Https", "host", 443, "", false, false)), "real connectServer without confirmChange refused");
            check(ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Https", "host", 443, "", true, false), "connectServer sentinel with confirmChange passes as mutation");
            check(!Fails(() => ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Https", "host", 443, "", false, true)), "connectServer preview needs no confirmChange");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Ftp", "host", 443, "", true, false)), "unknown protocol refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Https", "host", 70000, "", true, false)), "port out of range refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("connectServer", "srv", "", "Https", "", 443, "", true, false)), "empty host refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("commit", "", "", "Https", "", 0, "", true, false)), "commit without comment refused");
            check(ProjectSecurityLogic.ValidateMultiuser("commit", "", "", "Https", "", 0, "fix", true, false), "commit sentinel passes with comment and confirmation");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateMultiuser("openSession", "srv", "", "Https", "", 0, "", true, false)), "session opening is not an action of this tool");

            // ---- Library / compare selection ----
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateLibraryPair("", "")), "project library vs itself refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateLibraryPair("Lib", "lib")), "same global library (case-insensitive) refused");
            check(!Fails(() => ProjectSecurityLogic.ValidateLibraryPair("", "Lib")), "project library vs global library sentinel passes");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCompareRequest("online", "PLC_1", "[]", "", "PLC_2", "[]")), "unknown compare kind refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCompareRequest("software", "PLC_1", "[]", "", "PLC_1", "[]")), "software compared with itself refused");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCompareRequest("software", "PLC_1", "[]", "", "", "[]")), "software compare without any target refused");
            check(!Fails(() => ProjectSecurityLogic.ValidateCompareRequest("software", "PLC_1", "[]", "", "PLC_2", "[]")), "same-project software compare sentinel passes");
            check(!Fails(() => ProjectSecurityLogic.ValidateCompareRequest("software", "PLC_1", "[]", "OtherProject", "", "[\"PLC_1\"]")), "cross-project software compare sentinel passes");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCompareRequest("software", "PLC_1", "[]", "OtherProject", "", "[]")), "cross-project compare needs target device path");
            check(FailsWith<ArgumentException>(() => ProjectSecurityLogic.ValidateCompareRequest("hardware", "", "[\"Dev\"]", "", "", "[\"Dev\"]")), "hardware compared with the same device path refused");
            check(!Fails(() => ProjectSecurityLogic.ValidateCompareRequest("hardware", "", "[\"Dev\"]", "OtherProject", "", "[\"Dev\"]")), "hardware compare against the same path in another project passes");
            check(!Fails(() => ProjectSecurityLogic.ValidateCompareRequest("softwareToLibrary", "PLC_1", "[]", "", "", "[]")), "softwareToLibrary sentinel passes");

            // ---- Compare tree flattening ----
            var tree = new Node { Left = "Root", State = "FolderContentsDifferent", Children = {
                new Node { Left = "A", Right = "A", State = "ObjectsIdentical" },
                new Node { Left = "B", Right = "", State = "RightMissing", Children = { new Node { Left = "B1", State = "ObjectsDifferent", Children = { new Node { Left = "deep", State = "ObjectsDifferent" } } } } },
                new Node { Left = "", Right = "C", State = "LeftMissing" } } };
            JsonObject Describe(object o) { var n = (Node)o; return new JsonObject { ["leftName"] = n.Left, ["rightName"] = n.Right, ["comparisonResult"] = n.State }; }
            var flat = ProjectSecurityLogic.FlattenCompareTree(tree, o => ((Node)o).Children, Describe, 8, 1000, out bool truncated);
            check(flat.Count == 6 && !truncated, "tree fully flattened in depth-first order");
            check(flat[0]["path"]!.GetValue<string>() == "Root" && flat[3]["path"]!.GetValue<string>() == "Root/B/B1" && flat[4]["path"]!.GetValue<string>() == "Root/B/B1/deep", "paths concatenate names depth-first");
            check(flat[5]["path"]!.GetValue<string>() == "Root/C" && flat[5]["depth"]!.GetValue<int>() == 1, "right-only element uses right name for its path");
            check(flat[2]["childCount"]!.GetValue<int>() == 1, "childCount reported");
            var summary = ProjectSecurityLogic.Summarize(flat);
            check(summary["ObjectsDifferent"]!.GetValue<int>() == 2 && summary["ObjectsIdentical"]!.GetValue<int>() == 1 && summary["RightMissing"]!.GetValue<int>() == 1, "summary counts every state");
            var shallow = ProjectSecurityLogic.FlattenCompareTree(tree, o => ((Node)o).Children, Describe, 2, 1000, out truncated);
            check(shallow.Count == 5 && truncated && shallow[3]["childrenOmitted"]!.GetValue<bool>(), "maxDepth cuts children and reports truncation instead of silence");
            var bounded = ProjectSecurityLogic.FlattenCompareTree(tree, o => ((Node)o).Children, Describe, 8, 2, out truncated);
            check(bounded.Count == 2 && truncated, "row bound reports truncation");
            check(ProjectSecurityLogic.FlattenCompareTree(null, o => ((Node)o).Children, Describe, 8, 10, out truncated).Count == 0 && !truncated, "null root yields no rows");
            check(ProjectSecurityLogic.IsIdentical("ObjectsIdentical") && ProjectSecurityLogic.IsIdentical("CompareIrrelevant") && !ProjectSecurityLogic.IsIdentical("LeftMissing") && !ProjectSecurityLogic.IsIdentical(null), "identical filter covers identical/irrelevant states only");
        }
    }
}

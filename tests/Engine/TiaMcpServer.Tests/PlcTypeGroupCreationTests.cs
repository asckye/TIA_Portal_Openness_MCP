using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class PlcTypeGroupCreationTests
    {
        private sealed class Group
        {
            internal string Name = "";
            internal List<Group> Children = new List<Group>();
        }
        internal static void Run(Action<bool, string> check)
        {
            var root = new Group();
            int writes = 0;
            JsonObject Run(string path, bool preview, bool fail = false) => PlcTypeGroupCreation.Execute(root, path, preview,
                g => g.Children, g => g.Name, (g, n) => {
                    if (fail && n == "Fail") throw new InvalidOperationException("native failure");
                    var child = new Group { Name = n }; g.Children.Add(child); writes++; return child;
                });
            var plan = Run("Common/Motors", true);
            check(writes == 0 && plan["missingPaths"]!.AsArray().Count == 2, "type groups: preview lists missing parents without writing");
            var result = Run("Common/Motors", false);
            check(writes == 2 && result["createdCount"]!.GetValue<int>() == 2 && root.Children[0].Children[0].Name == "Motors", "type groups: creates nested parents");
            result = Run("PLC data types/Common/Motors", false);
            check(writes == 2 && result["alreadyExisted"]!.GetValue<bool>(), "type groups: repeated request is idempotent");
            result = Run(@"PLC 数据类型\Common\Valves", false);
            check(writes == 3 && result["createdPaths"]![0]!.GetValue<string>() == "Common/Valves", "type groups: localized prefix and backslash preserve parent");
            Run("+S1-K1/A.B", false);
            check(root.Children[1].Name == "+S1-K1" && root.Children[1].Children[0].Name == "A.B", "type groups: IEC and dot names are literal");
            int invalid = 0;
            foreach (var path in new[] { "", "/", "PLC data types", "PLC 数据类型", "A//B", "A/../B", "./A" })
                try { Run(path, false); } catch (PortalException) { invalid++; }
            check(invalid == 7 && writes == 5, "type groups: invalid paths fail before writes");
            root.Children.Add(new Group { Name = "Common" });
            bool ambiguous = false;
            try { Run("Common/New", false); } catch (PortalException ex) { ambiguous = ex.Message.Contains("Ambiguous"); }
            check(ambiguous && writes == 5, "type groups: ambiguous parent refuses creation");
            bool partial = false;
            try { Run("Partial/Fail", false, true); } catch (PortalException ex) { partial = ex.Message.Contains("Partial") && ex.Message.Contains("not rolled back") && ex.Message.Contains("native failure"); }
            check(partial && writes == 6, "type groups: native failure reports created parent without claiming rollback");
        }
    }
}

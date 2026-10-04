using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    internal static class EcosystemTests
    {
        private static string SourceRoot([CallerFilePath] string sourceFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", ".."));

        internal static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "tia-ecosystem-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            bool Refuses(Action action) { try { action(); return false; } catch { return true; } }
            try
            {
                string left = Path.Combine(root, "left.xml"), right = Path.Combine(root, "right.xml");
                const string aliases = "[{\"source\":[\"Input\"],\"destination\":[\"DB\",\"Output\"],\"title\":\"<script>alert(1)</script>\"}]";
                string xml = PlcAliasAlarmBuilder.Build("FC_Map", 100, aliases);
                File.WriteAllText(left, xml); File.WriteAllText(right, xml);
                var same = PlcVisualComparison.Compare(left, right, out var html);
                check(same["state"]?.GetValue<string>() == "Equal" && same["dataComplete"]!.GetValue<bool>(), "identical LAD graphical diff is complete/equal");
                check(same["graphicalLadAvailable"]!.GetValue<bool>() && html.Contains("<svg"), "LAD graph is rendered");
                check(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "LAD HTML escapes engineering labels");
                File.WriteAllText(right, xml.Replace("Name=\"Input\"", "Name=\"ChangedInput\""));
                var changed = PlcVisualComparison.Compare(left, right, out _);
                check(changed["state"]?.GetValue<string>() != "Equal", "changed operand is a real visual difference");
                var rewritten = XDocument.Parse(xml); var document = rewritten.Root!; document.Add(new XElement(document.Element("SW.Blocks.FC")!)); File.WriteAllText(right, rewritten.ToString());
                check(Refuses(() => PlcVisualComparison.Compare(left, right, out _)), "visual diff refuses silently dropping a second block");
                File.WriteAllText(right, "<!DOCTYPE Document [<!ENTITY e SYSTEM 'file:///secret'>]><Document>&e;</Document>");
                check(Refuses(() => PlcVisualComparison.Compare(left, right, out _)), "visual diff refuses external entities");
                string alarm = PlcAliasAlarmBuilder.Build("Alarm", 101, "[{\"source\":[\"Fault\"],\"destination\":[\"Alarm\"],\"acknowledge\":[\"Ack\"],\"invert\":true}]");
                var a = XDocument.Parse(alarm);
                check(a.Descendants("SW.Blocks.CompileUnit").Count() == 2 && alarm.Contains("SCoil") && alarm.Contains("RCoil") && alarm.Contains("Negated"), "latched alarm has negated condition, set and following reset networks");
                check(Refuses(() => PlcAliasAlarmBuilder.Build("FC", 1, aliases.TrimEnd(']') + "," + aliases.Substring(1))), "duplicate destination refused");
                check(Refuses(() => PlcAliasAlarmBuilder.Build("FC", 1, "[{\"source\":[\"Tag[1]\"],\"destination\":[\"Out\"]}]")), "array access not silently interpreted as a literal variable");
                File.WriteAllText(right, "<Document><Name>{{name}}</Name><Member Name=\"{{name}}\" /></Document>");
                var expanded = PlcTemplateExpansion.Expand(right, "[{\"fileName\":\"a.xml\",\"values\":{\"name\":\"a & <b>\"}}]");
                check(XDocument.Parse(expanded[0].Xml).Root!.Element("Name")!.Value == "a & <b>", "template values XML-escape without changing value");
                check(Refuses(() => PlcTemplateExpansion.Expand(right, "[{\"fileName\":\"a.xml\",\"values\":{}}]")), "missing template token refused");
                check(Refuses(() => PlcTemplateExpansion.Expand(right, "[{\"fileName\":\"../a.xml\",\"values\":{\"name\":\"b\"}}]")), "template traversal filename refused");
                File.Delete(right);
                var audit = EngineeringQualityAudit.Audit(root, "[]", "^FC_", 10);
                check(audit["blocksInspected"]!.GetValue<int>() == 1 && audit["dataComplete"]!.GetValue<bool>(), "quality audit reads exported LAD block");
                File.WriteAllText(right, "<Hardware><Firmware>0.1</Firmware></Hardware>");
                var policy = EngineeringQualityAudit.Audit(root, "[{\"id\":\"firmware\",\"files\":\"right.xml$\",\"xpath\":\"//Firmware\",\"minCount\":1,\"valuePattern\":\"^2\\.\",\"severity\":\"error\"}]".Replace("^2\\.", "^2[.]"), "", 10);
                check(policy["findings"]!.AsArray().OfType<JsonObject>().Any(f => f["rule"]?.GetValue<string>() == "firmware") && !policy["qualityPassed"]!.GetValue<bool>(), "hardware policy violation changes quality verdict");
                var unmatched = EngineeringQualityAudit.Audit(root, "[{\"id\":\"library\",\"files\":\"not-present\",\"xpath\":\"//Version\",\"minCount\":1}]", "", 10);
                check(!unmatched["dataComplete"]!.GetValue<bool>() && !unmatched["qualityPassed"]!.GetValue<bool>(), "unmatched rule is unevaluated, never passed");
                var store = new BatchPlanStore(); var now = DateTime.UtcNow;
                var plan = new BatchPlanStore.Plan { Project = "test", State = "pid:1", Operations = JsonNode.Parse("[{\"name\":\"tool\"}]")!.AsArray() };
                var token = store.Add(plan, now); plan.Operations.Clear();
                check(store.Take(token, now).Operations.Count == 1, "preview token binds a deep copy of exact operations");
                check(Refuses(() => store.Take(token, now)), "batch token is single-use");
                token = store.Add(plan, now);
                check(Refuses(() => store.Take(token, now.AddMinutes(11))), "batch token expires");
                check(BatchPlanStore.Stable(JsonNode.Parse("{\"b\":1,\"timestamp\":\"old\",\"a\":2}")) == BatchPlanStore.Stable(JsonNode.Parse("{\"a\":2,\"timestamp\":\"new\",\"b\":1}")), "preview comparison tolerates object order and timestamp only");
                check(BatchPlanStore.Stable(JsonNode.Parse("{\"a\":1}")) != BatchPlanStore.Stable(JsonNode.Parse("{\"a\":2}")), "preview comparison detects changed state");
                check(EcosystemFiles.QuoteArgument("a\"b") == "\"a\\\"b\"" && EcosystemFiles.QuoteArgument("x\\") == "\"x\\\\\"", "process argument escaping handles quotes and trailing slash");
                var repo = EcosystemFiles.RepositoryRoot("", SourceRoot());
                var guidance = EcosystemFiles.Guidance(repo, "", "", 0, 100);
                check(guidance["total"]!.GetValue<int>() >= 32 && guidance["dataComplete"]!.GetValue<bool>(), "bundled official guides indexed");
                check(Refuses(() => EcosystemFiles.Guidance(repo, "", "../../LICENSE", 0, 100)), "guidance only resolves enumerated documents");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    public sealed class EcosystemRepositoryRootTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "ecosystem root 中文 " + Guid.NewGuid().ToString("N"));

        public EcosystemRepositoryRootTests() { Directory.CreateDirectory(scratch); }

        private static string At(string root, string relative)
            => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        private static void Put(string root, string relative, string content = "fixture")
        {
            string file = At(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        private static void Bundle(string root, bool manifest = true)
        {
            if (manifest) Put(root, "manifest/package-manifest.json");
            Put(root, "scripts/ecosystem/plc_tools_bridge.py");
            Put(root, "reference/siemens-openness/skills/blocks/SKILL.md", "Fixture guidance\nSecond line");
            Put(root, "reference/siemens-openness/UPSTREAM.json", "{\"commit\":\"fixture\"}");
        }

        private void Compare(string output)
        {
            string configured = At(scratch, "override root 中文");
            // The old override requires only the companion marker, not a package manifest.
            Bundle(configured, false);
            string invalid = At(scratch, "invalid override");
            Put(invalid, "manifest/package-manifest.json");
            foreach (string suffix in new[] { "", Path.DirectorySeparatorChar.ToString(), "/" })
            foreach (string? explicitRoot in new[] { null, "", " ", configured, configured + suffix,
                configured + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar,
                invalid, At(scratch, "missing"), "relative" })
            {
                string? before = null, after = null;
                var oldError = Record.Exception(() => before = OldRepositoryRoot(output + suffix, explicitRoot));
                var newError = Record.Exception(() => after = EcosystemFiles.RepositoryRoot(output + suffix, explicitRoot));
                Assert.Equal(oldError?.GetType(), newError?.GetType());
                Assert.Equal(oldError?.Message, newError?.Message);
                Assert.Equal(before, after);
                if (before == null) continue;
                foreach (var request in new[] { (Query: "", Document: ""), (Query: "Second", Document: ""),
                    (Query: "", Document: "blocks/SKILL.md") })
                    Assert.Equal(EcosystemFiles.Guidance(before, request.Query, request.Document, 0, 100).ToJsonString(),
                        EcosystemFiles.Guidance(after!, request.Query, request.Document, 0, 100).ToJsonString());
            }
        }

        [Theory]
        [InlineData("runtime/v14sp1")]
        [InlineData("runtime/v15.1")]
        [InlineData("runtime/v16")]
        [InlineData("runtime/v17")]
        [InlineData("runtime/v18")]
        [InlineData("runtime/v19")]
        [InlineData("runtime/v20")]
        [InlineData("runtime/v21")]
        [InlineData("runtime/studio")]
        [InlineData("runtime/studio/bridge")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin/Debug/net48")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin-v20/Release/net48")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin-v20/Debug/net48")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Debug/net10.0-windows")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows/bridge")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Debug/net10.0-windows/bridge")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Bridge/bin/Release/net48")]
        [InlineData("tools/tia-openness-studio/src/TiaOpenness.Bridge/bin/Debug/net48")]
        public void Supported_anchors_match_the_original_probe(string anchor)
        {
            Bundle(scratch);
            Compare(Directory.CreateDirectory(At(scratch, anchor)).FullName);
        }

        [Theory]
        [InlineData("bundle")]
        [InlineData("repository-runtime")]
        [InlineData("worktree")]
        [InlineData("ci")]
        public void Repository_layouts_match_the_original_probe(string layout)
        {
            Bundle(scratch);
            if (layout == "repository-runtime") Directory.CreateDirectory(At(scratch, ".git"));
            if (layout == "worktree") Put(scratch, ".git");
            if (layout != "ci") Directory.CreateDirectory(At(scratch, "runtime/v21"));
            Compare(Directory.CreateDirectory(At(scratch, "tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48")).FullName);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Nested_staging_keeps_the_original_fallback(bool manifest, bool companion)
        {
            Bundle(scratch);
            string staging = At(scratch, "bin-build/staging");
            if (manifest) Put(staging, "manifest/package-manifest.json");
            if (companion) Bundle(staging, manifest);
            Compare(Directory.CreateDirectory(At(staging, "runtime/v21")).FullName);
        }

        [Theory]
        [InlineData("elsewhere/runtime/v21")]
        [InlineData("runtime/v22")]
        [InlineData("runtime/v21/plugins")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin/Custom/net48")]
        [InlineData("tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net10.0")]
        [InlineData("tools/other/bin/Release/net48")]
        public void Stray_ancestor_marker_keeps_the_original_fallback(string anchor)
        {
            Bundle(scratch);
            Compare(Directory.CreateDirectory(At(scratch, anchor)).FullName);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Runtime_only_and_missing_markers_keep_the_original_result(bool manifest, bool companion)
        {
            if (manifest) Put(scratch, "manifest/package-manifest.json");
            if (companion) Bundle(scratch, false);
            Compare(Directory.CreateDirectory(At(scratch, "runtime/v21")).FullName);
        }

        // Frozen pre-G7-3 RepositoryRoot body; only its process-global inputs are parameters.
        private static string OldRepositoryRoot(string baseDirectory, string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(Path.Combine(configured, "scripts", "ecosystem", "plc_tools_bridge.py")))
                    throw new DirectoryNotFoundException("TIA_MCP_REPOSITORY_ROOT must point to this source/distribution root.");
                return Path.GetFullPath(configured);
            }
            for (var dir = new DirectoryInfo(baseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "scripts", "ecosystem", "plc_tools_bridge.py"))) return dir.FullName;
            throw new DirectoryNotFoundException("Companion files missing. Set TIA_MCP_REPOSITORY_ROOT to the source/distribution root.");
        }

        public void Dispose()
        {
            string full = Path.GetFullPath(scratch);
            Assert.Equal(new DirectoryInfo(Path.GetTempPath()).FullName.TrimEnd(Path.DirectorySeparatorChar),
                new DirectoryInfo(full).Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar));
            Directory.Delete(full, true);
        }
    }
}

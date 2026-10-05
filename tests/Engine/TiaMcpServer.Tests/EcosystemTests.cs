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
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));

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
        private static string At(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        private static void Put(string root, string path, string text = "fixture")
        {
            string file = At(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text);
        }

        [Theory]
        [MemberData(nameof(TiaOpenness.Shared.Tests.BundleLayoutTests.Anchors), MemberType = typeof(TiaOpenness.Shared.Tests.BundleLayoutTests))]
        public void Guidance_uses_the_chosen_root_without_requiring_the_python_bridge(string anchor)
        {
            Put(scratch, "manifest/package-manifest.json");
            Put(scratch, "reference/siemens-openness/skills/blocks/SKILL.md", "Fixture guidance");
            Put(scratch, "reference/siemens-openness/UPSTREAM.json", "{\"commit\":\"fixture\"}");
            string output = Directory.CreateDirectory(At(scratch, anchor)).FullName;
            Assert.Equal(scratch, EcosystemFiles.RepositoryRoot(output, null));
            Assert.Equal(1, EcosystemFiles.Guidance(scratch, "", "", 0, 100)["total"]!.GetValue<int>());
        }

        [Theory]
        [InlineData("reference/siemens-openness/skills")]
        [InlineData("reference/siemens-openness/UPSTREAM.json")]
        public void Missing_guidance_reports_the_exact_expected_resource(string missing)
        {
            Put(scratch, "manifest/package-manifest.json");
            Put(scratch, "reference/siemens-openness/skills/blocks/SKILL.md");
            Put(scratch, "reference/siemens-openness/UPSTREAM.json", "{\"commit\":\"fixture\"}");
            if (missing.EndsWith("skills")) Directory.Delete(At(scratch, missing), true); else File.Delete(At(scratch, missing));
            Assert.Equal(At(scratch, missing), Assert.Throws<TiaOpenness.Shared.BundleResourceUnavailableException>(
                () => EcosystemFiles.Guidance(scratch, "", "", 0, 100)).Resource);
        }

        public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}

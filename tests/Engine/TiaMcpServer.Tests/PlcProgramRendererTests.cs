using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class PlcProgramRendererTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "plc-render-" + Guid.NewGuid().ToString("N"));
        public PlcProgramRendererTests() => Directory.CreateDirectory(root);
        private string Put(string name, string text) { string path = Path.Combine(root, name); File.WriteAllText(path, text); return path; }
        private string Output(string name = "page.html") => Path.Combine(root, name);

        // Synthetic namespace variants exercise parsing; they are not per-release native export evidence.
        private static string Block(string name, string language, string network, string version = "V21", int ns = 5)
            => "<Document><Engineering version=\"" + version + "\"/><SW.Blocks.FB ID=\"0\"><AttributeList><Name>" + name + "</Name><Number>42</Number><ProgrammingLanguage>" + language + "</ProgrammingLanguage>"
                + "<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Input\"><Member Name=\"Enabled\" Datatype=\"Bool\"><StartValue>true</StartValue></Member></Section></Sections></Interface></AttributeList>"
                + "<ObjectList><MultilingualText CompositionName=\"Title\"><ObjectList><MultilingualTextItem><AttributeList><Text>Block title</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>"
                + "<SW.Blocks.CompileUnit><AttributeList><ProgrammingLanguage>" + language + "</ProgrammingLanguage><NetworkSource>" + network.Replace("NS", "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v" + ns)
                + "</NetworkSource></AttributeList><ObjectList><MultilingualText CompositionName=\"Comment\"><ObjectList><MultilingualTextItem><AttributeList><Text>Network comment &amp; &lt;script&gt;</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>";

        private const string Ladder = "<FlgNet xmlns=\"NS\"><Parts><Access Scope=\"LiteralConstant\" UId=\"1\"><Constant><ConstantType>Bool</ConstantType><ConstantValue>FALSE</ConstantValue></Constant></Access>"
            + "<Access Scope=\"LiteralConstant\" UId=\"2\"><Constant><ConstantType>Time</ConstantType><ConstantValue>T#5s</ConstantValue></Constant></Access>"
            + "<Part Name=\"Contact\" UId=\"10\"/><Part Name=\"Contact\" UId=\"11\"/><Call UId=\"12\"><CallInfo Name=\"Timer\" BlockType=\"FB\"><Instance Scope=\"LocalVariable\"><Component Name=\"timer1\"/></Instance><Parameter Name=\"PT\" Section=\"Input\" Type=\"Time\"/></CallInfo></Call><Part Name=\"Coil\" UId=\"13\"/></Parts>"
            + "<Wires><Wire UId=\"20\"><IdentCon UId=\"1\"/><NameCon UId=\"10\" Name=\"operand\"/></Wire><Wire UId=\"21\"><IdentCon UId=\"2\"/><NameCon UId=\"12\" Name=\"PT\"/></Wire>"
            + "<Wire UId=\"22\"><Powerrail/><NameCon UId=\"10\" Name=\"in\"/><NameCon UId=\"11\" Name=\"in\"/></Wire><Wire UId=\"23\"><NameCon UId=\"10\" Name=\"out\"/><NameCon UId=\"11\" Name=\"out\"/><NameCon UId=\"12\" Name=\"en\"/></Wire>"
            + "<Wire UId=\"24\"><NameCon UId=\"12\" Name=\"eno\"/><NameCon UId=\"13\" Name=\"in\"/></Wire></Wires></FlgNet>";

        [Theory]
        [InlineData("14sp1", 1)][InlineData("15.1", 2)][InlineData("16", 3)][InlineData("17", 4)]
        [InlineData("18", 4)][InlineData("19", 4)][InlineData("20", 5)][InlineData("21", 5)]
        public void Shared_renderer_accepts_namespace_variants(string release, int ns)
        {
            string input = Put("block.xml", Block("Main", "LAD", Ladder, "V" + release, ns));
            var result = PlcProgramRenderer.Write(input, Output(), false, release, "test");
            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(Execution.Completed, result.Meta.Execution);
            Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
            var html = File.ReadAllText(Output());
            foreach (string expected in new[] { "Block title", "Enabled", "Bool", "true", "Network comment &amp; &lt;script&gt;", ">#timer1</text>", ">PT</text>", ">T#5s</text>", "Finding: constant open contact", "data-uid=\"13\"", "<svg", "Called by" }) Assert.Contains(expected, html);
            Assert.DoesNotContain("wired / unassigned", html);
            Assert.DoesNotContain("operand =", html);
            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("src=", html);
            var svg = XElement.Parse(html.Substring(html.IndexOf("<svg", StringComparison.Ordinal)).Split(new[] { "</svg>" }, StringSplitOptions.None)[0] + "</svg>");
            Assert.True(svg.Descendants().Count(e => e.Name.LocalName == "path") >= 4);
            var wire = McpResult.From(result);
            Assert.Equal(V4Json.Serialize(result), wire.Content[0].Text);
            Assert.Equal(release, wire.StructuredContent.GetProperty("meta").GetProperty("releaseKey").GetString());
        }

        [Fact]
        public void Atlas_links_calls_and_limits_called_by_to_included_blocks()
        {
            var exports = Directory.CreateDirectory(Path.Combine(root, "exports")).FullName;
            File.WriteAllText(Path.Combine(exports, "main.xml"), Block("Main", "FBD", Ladder));
            File.WriteAllText(Path.Combine(exports, "timer.xml"), Block("Timer", "SCL", "<StructuredText><Token Text=\"#x := 1;\"/></StructuredText>"));
            var result = PlcProgramRenderer.Write(exports, Output(), true, "21");
            Assert.True(result.Ok, result.Error?.Message);
            string html = File.ReadAllText(Output());
            Assert.Contains("FBD shown as ladder equivalent", html);
            Assert.Contains("<a href=\"#block-2\"><g data-uid=\"12\" class=\"instruction\">", html);
            Assert.Contains("1: <a href=\"#block-1\">Main</a>", html);
            Assert.Contains("<code>#x := 1;</code>", html);
            Assert.Contains("inside this atlas only", html);
        }

        [Theory]
        [InlineData("LAD", "<FlgNet xmlns=\"NS\"><Parts/><Wires/></FlgNet>")]
        [InlineData("SCL", "<StructuredText/>")]
        [InlineData("STL", "<StructuredText/>")]
        public void Empty_networks_are_explicit(string language, string network)
        {
            var input = Put("block.xml", Block("Empty", language, network));
            Assert.True(PlcProgramRenderer.Write(input, Output(), false, "21").Ok);
            Assert.Contains("Empty network.", File.ReadAllText(Output()));
            Assert.Contains("0 / 0 / 1", File.ReadAllText(Output()));
        }

        [Fact]
        public async Task Existing_output_and_concurrent_creators_cannot_overwrite()
        {
            string input = Put("block.xml", Block("Empty", "LAD", "<FlgNet xmlns=\"NS\"><Parts/><Wires/></FlgNet>"));
            Put("page.html", "preserve");
            Assert.Equal(ErrorCode.AlreadyExists, PlcProgramRenderer.Write(input, Output(), false, "21").Error!.Code);
            Assert.Equal("preserve", File.ReadAllText(Output()));
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => PlcProgramRenderer.Write(input, Output("race.html"), false, "21"))).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Single(results, r => r.Ok);
            Assert.Single(results, r => r.Error?.Code == ErrorCode.AlreadyExists);
        }

        [Fact]
        public void Repository_samples_match_generated_html_goldens()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Version.props"))) repository = repository.Parent;
            Assert.NotNull(repository);
            var goldenRoot = Path.Combine(repository!.FullName, "tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender");
            var fixtures = Directory.GetFiles(goldenRoot, "*.html");
            Assert.Equal(10, fixtures.Length);
            int index = 0;
            foreach (string golden in fixtures)
            {
                string fileName = Path.GetFileName(golden);
                string relative = fileName.StartsWith("plugin_", StringComparison.Ordinal)
                    ? "plugin/skill/lad-cookbook/" + fileName.Substring("plugin_skill_lad-cookbook_".Length)
                    : fileName.StartsWith("templates_plc_", StringComparison.Ordinal)
                    ? "templates/plc/block-xml/" + fileName.Substring("templates_plc_block-xml_".Length)
                    : "templates/mcp-full-e2e-verify/plc/blocks/" + fileName.Substring("templates_mcp-full-e2e-verify_plc_blocks_".Length);
                relative = relative.Substring(0, relative.Length - ".html".Length);
                string input = Path.GetFullPath(Path.Combine(repository.FullName, relative));
                string output = Output("golden-" + index++ + ".html");
                var result = PlcProgramRenderer.Write(input, output, false, "21");
                Assert.True(result.Ok, result.Error?.Message);
                string html = File.ReadAllText(output).Replace(System.Net.WebUtility.HtmlEncode(input), relative);
                Assert.Equal(File.ReadAllText(golden), html);
            }
        }

        [Fact]
        public void Instruction_boxes_and_disconnected_islands_keep_their_actual_names()
        {
            string graph = Ladder.Replace("</Parts>", "<Part Name=\"TON\" UId=\"90\"/><Part Name=\"SCoil\" UId=\"91\"/></Parts>");
            string input = Put("islands.xml", Block("Islands", "FBD", graph));
            Assert.True(PlcProgramRenderer.Write(input, Output(), false, "21").Ok);
            string html = File.ReadAllText(Output());
            Assert.Contains(">TON</text>", html);
            Assert.Contains(">Timer</text>", html);
            Assert.Contains("data-uid=\"90\"", html);
            Assert.Contains("data-uid=\"91\"", html);
            Assert.Contains("class=\"coil-set\"", html);
        }

        [Theory]
        [InlineData("Contact", false, "contact-no", "")]
        [InlineData("Contact", true, "contact-nc", "")]
        [InlineData("PContact", false, "contact-positive", "P")]
        [InlineData("NContact", false, "contact-negative", "N")]
        [InlineData("Coil", false, "coil", "")]
        [InlineData("Coil", true, "coil-negated", "")]
        [InlineData("SCoil", false, "coil-set", "S")]
        [InlineData("RCoil", false, "coil-reset", "R")]
        public void Ladder_symbols_are_paths_with_the_operand_above_and_no_instruction_box(string name, bool negated, string kind, string mark)
        {
            string graph = "<FlgNet xmlns=\"NS\"><Parts><Access Scope=\"LocalVariable\" UId=\"1\"><Symbol><Component Name=\"Ready\"/></Symbol></Access><Part Name=\"" + name
                + "\" UId=\"10\">" + (negated ? "<Negated Name=\"operand\"/>" : "") + "</Part></Parts><Wires><Wire><IdentCon UId=\"1\"/><NameCon UId=\"10\" Name=\"operand\"/></Wire><Wire><Powerrail/><NameCon UId=\"10\" Name=\"in\"/></Wire></Wires></FlgNet>";
            Assert.True(PlcProgramRenderer.Write(Put("symbol.xml", Block("Symbol", "LAD", graph)), Output(), false, "21").Ok);
            string html = File.ReadAllText(Output());
            var svg = XElement.Parse(html.Substring(html.IndexOf("<svg", StringComparison.Ordinal)).Split(new[] { "</svg>" }, StringSplitOptions.None)[0] + "</svg>");
            var symbol = svg.Descendants().Single(e => (string?)e.Attribute("data-uid") == "10");
            Assert.Equal(kind, (string?)symbol.Attribute("class"));
            Assert.DoesNotContain(symbol.Descendants(), e => e.Name.LocalName == "rect");
            Assert.Contains(symbol.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("class") == "symbol");
            var operand = symbol.Elements().Single(e => (string?)e.Attribute("class") == "operand");
            Assert.Equal("#Ready", operand.Value);
            int rungY = int.Parse(((string)symbol.Elements().First(e => (string?)e.Attribute("class") == "symbol").Attribute("d")!).Split(' ')[2]);
            Assert.True((int)operand.Attribute("y")! < rungY - 15);
            Assert.DoesNotContain(symbol.Elements(), e => (string?)e.Attribute("class") == "pin-name");
            if (mark.Length > 0) Assert.Equal(mark, symbol.Elements().Single(e => (string?)e.Attribute("class") == "symbol-mark").Value);
            Assert.Contains(".diagram svg{display:block;width:100%;height:auto}", html);
            Assert.Contains(svg.Elements(), e => (string?)e.Attribute("class") == "power-rail");
        }

        [Fact]
        public void Box_parameters_are_outside_the_matching_pin_and_parallel_branches_are_vertical()
        {
            string graph = Ladder.Replace("</Parts>", "<Access Scope=\"LocalVariable\" UId=\"3\"><Symbol><Component Name=\"Elapsed\"/></Symbol></Access></Parts>")
                .Replace("</CallInfo>", "<Parameter Name=\"ET\" Section=\"Output\" Type=\"Time\"/></CallInfo>")
                .Replace("</Wires>", "<Wire><IdentCon UId=\"3\"/><NameCon UId=\"12\" Name=\"ET\"/></Wire></Wires>");
            Assert.True(PlcProgramRenderer.Write(Put("pins.xml", Block("Pins", "LAD", graph)), Output(), false, "21").Ok);
            string html = File.ReadAllText(Output());
            var svg = XElement.Parse(html.Substring(html.IndexOf("<svg", StringComparison.Ordinal)).Split(new[] { "</svg>" }, StringSplitOptions.None)[0] + "</svg>");
            var box = svg.Descendants().Single(e => (string?)e.Attribute("data-uid") == "12");
            var rect = box.Elements().Single(e => e.Name.LocalName == "rect");
            var pt = box.Elements().Single(e => (string?)e.Attribute("class") == "pin-name" && e.Value == "PT");
            var value = box.Elements().Single(e => (string?)e.Attribute("class") == "pin-value" && e.Value == "T#5s");
            Assert.True((int)value.Attribute("x")! < (int)rect.Attribute("x")!);
            Assert.True((int)pt.Attribute("x")! > (int)rect.Attribute("x")!);
            Assert.Equal((int)pt.Attribute("y")!, (int)value.Attribute("y")!);
            var et = box.Elements().Single(e => (string?)e.Attribute("class") == "pin-name" && e.Value == "ET");
            var elapsed = box.Elements().Single(e => (string?)e.Attribute("class") == "pin-value" && e.Value == "#Elapsed");
            int right = (int)rect.Attribute("x")! + (int)rect.Attribute("width")!;
            Assert.True((int)elapsed.Attribute("x")! > right);
            Assert.True((int)et.Attribute("x")! < right);
            Assert.Equal((int)et.Attribute("y")!, (int)elapsed.Attribute("y")!);
            var instance = box.Elements().Single(e => (string?)e.Attribute("class") == "instance");
            Assert.True((int)instance.Attribute("y")! < (int)rect.Attribute("y")!);
            Assert.Contains(box.Elements(), e => e.Value == "EN" && (string?)e.Attribute("class") == "pin-name");
            Assert.Contains(box.Elements(), e => e.Value == "ENO" && (string?)e.Attribute("class") == "pin-name");
            Assert.Contains(svg.Elements(), e => (string?)e.Attribute("class") == "branch" && ((string?)e.Attribute("d"))!.Contains(" V "));
        }

        [Theory]
        [InlineData("EdgeParts")][InlineData("BoxParts")][InlineData("RoutingParts")][InlineData("BranchShapes")]
        public void Synthetic_part_fixtures_match_complete_html_goldens(string name)
        {
            string relative = "tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender/Parts/" + name + ".xml";
            string html = RenderFixture(relative, out string input);
            Assert.Equal(File.ReadAllText(input + ".html"), html.Replace(System.Net.WebUtility.HtmlEncode(input), relative));
            Assert.DoesNotContain(">pre</text>", html);
            Assert.DoesNotContain(">PRE</text>", html);
        }

        [Fact]
        public void Edge_parts_keep_memory_below_the_symbol_or_trigger_box()
        {
            string html = RenderFixture("tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender/Parts/EdgeParts.xml", out _);
            var svgs = Svgs(html);
            string[] names = { "PContact", "NContact", "PCoil", "NCoil", "PBox", "NBox" };
            Assert.Equal(names.Length, svgs.Length);
            for (int i = 0; i < names.Length; i++)
            {
                var node = svgs[i].Descendants().Single(e => (string?)e.Attribute("data-uid") == "10");
                var memory = node.Elements().Single(e => (string?)e.Attribute("class") == "edge-memory");
                Assert.Equal("#" + names[i] + "Memory", memory.Value);
                if (i < 4)
                {
                    Assert.DoesNotContain(node.Elements(), e => e.Name.LocalName == "rect");
                    Assert.Equal(i % 2 == 0 ? "P" : "N", node.Elements().Single(e => (string?)e.Attribute("class") == "symbol-mark").Value);
                    var operand = node.Elements().Single(e => (string?)e.Attribute("class") == "operand");
                    Assert.Equal("#" + names[i] + "Signal", operand.Value);
                    Assert.True((int)memory.Attribute("y")! > (int)operand.Attribute("y")!);
                }
                else
                {
                    Assert.Equal(i == 4 ? "P_TRIG" : "N_TRIG", node.Elements().Single(e => (string?)e.Attribute("class") == "instruction-name").Value);
                    Assert.Equal(new[] { "CLK", "Q" }, node.Elements().Where(e => (string?)e.Attribute("class") == "pin-name").Select(e => e.Value));
                    var box = node.Elements().Single(e => e.Name.LocalName == "rect");
                    Assert.True((int)memory.Attribute("y")! > (int)box.Attribute("y")! + (int)box.Attribute("height")!);
                }
            }
        }

        [Fact]
        public void Routing_parts_join_branches_without_boxes_and_pre_enters_the_visible_boolean_pin()
        {
            var svgs = Svgs(RenderFixture("tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender/Parts/RoutingParts.xml", out _));
            Assert.Equal(new[] { "10", "11", "12", "17" }, svgs[0].Descendants().Where(e => e.Attribute("data-uid") != null).Select(e => (string)e.Attribute("data-uid")!));
            Assert.DoesNotContain(svgs[0].Descendants(), e => e.Name.LocalName == "rect");
            AssertParallelGeometry(svgs[0]);
            for (int i = 1; i < svgs.Length; i++)
            {
                var node = svgs[i].Descendants().Single(e => (string?)e.Attribute("data-uid") == "10");
                var inputPin = node.Elements().Where(e => (string?)e.Attribute("class") == "pin").ElementAt(i == 3 ? 1 : 0);
                int rungY = int.Parse(((string)inputPin.Attribute("d")!).Split(' ')[2]);
                Assert.Contains(svgs[i].Elements(), e => (string?)e.Attribute("class") == "wire" && ((string?)e.Attribute("d"))!.Contains(" " + rungY + " H 24"));
                Assert.DoesNotContain(node.Elements(), e => e.Value == "pre" || e.Value == "PRE");
                if (i == 1) Assert.Contains(node.Elements(), e => (string?)e.Attribute("class") == "pin-name" && e.Value == "EN");
                else if (i == 2) Assert.Contains(node.Elements(), e => (string?)e.Attribute("class") == "pin-name" && e.Value == "IN" && (int)e.Attribute("y")! == rungY + 4);
                else Assert.DoesNotContain(node.Elements(), e => (string?)e.Attribute("class") == "pin-name" && e.Value == "START");
                if (i == 3) Assert.Contains(node.Elements(), e => (string?)e.Attribute("class") == "pin-name" && e.Value == "IN");
            }
        }

        [Theory]
        [InlineData("TON", "IN")][InlineData("TOF", "IN")][InlineData("TP", "IN")][InlineData("TONR", "IN")]
        [InlineData("CTU", "CU")][InlineData("CTUD", "CU")][InlineData("CTD", "CD")]
        [InlineData("PBox", "CLK")][InlineData("NBox", "CLK")]
        public void Internal_pre_keeps_the_known_boolean_pin_name(string name, string label)
        {
            string network = "<FlgNet xmlns=\"NS\"><Parts><Part UId=\"10\" Name=\"" + name + "\"/></Parts><Wires><Wire UId=\"20\"><Powerrail/><NameCon UId=\"10\" Name=\"pre\"/></Wire></Wires></FlgNet>";
            Assert.True(PlcProgramRenderer.Write(Put("pre.xml", Block("Pre", "LAD", network)), Output(), false, "21").Ok);
            var svg = Svgs(File.ReadAllText(Output())).Single();
            var pin = svg.Descendants().Single(e => (string?)e.Attribute("class") == "pin-name" && e.Value == label);
            int y = (int)pin.Attribute("y")! - 4;
            Assert.Contains(svg.Elements(), e => (string?)e.Attribute("class") == "wire" && ((string?)e.Attribute("d"))!.Contains(" " + y + " H 24"));
            Assert.DoesNotContain(svg.Descendants(), e => (string?)e.Attribute("class") == "pin-name" && e.Value.Equals("pre", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Routing_layout_retains_nested_groups_and_ignores_routing_part_order_and_names()
        {
            var svgs = Svgs(RenderFixture("tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender/Parts/BranchShapes.xml", out _));
            var a = SymbolBounds(svgs[0], "10"); var b = SymbolBounds(svgs[0], "11"); var c = SymbolBounds(svgs[0], "12");
            var d = SymbolBounds(svgs[0], "14"); var e = SymbolBounds(svgs[0], "15"); var coil = SymbolBounds(svgs[0], "17");
            Assert.Equal(a.left, e.left);
            Assert.Equal(b.left, c.left);
            Assert.True(a.right < b.left && b.right < d.left && d.right < coil.left);
            Assert.Equal(a.y, b.y); Assert.Equal(a.y, d.y); Assert.Equal(a.y, coil.y);
            Assert.True(c.y > b.y && e.y > c.y);
            var joins = VerticalBranches(svgs[0]);
            Assert.Contains(joins, j => j.x > Math.Max(b.right, c.right) && j.x < d.left && j.top == b.y && j.bottom == c.y);
            Assert.Contains(joins, j => j.x > Math.Max(d.right, e.right) && j.x < coil.left && j.top == d.y && j.bottom == e.y);
            var symbols = new[] { a, b, c, d, e, coil };
            Assert.All(joins, j => Assert.DoesNotContain(symbols, s => j.x >= s.left && j.x <= s.right && j.top <= s.y && j.bottom >= s.y));
            AssertParallelGeometry(svgs[1]);
            Assert.Equal(4, svgs[1].Descendants().Count(n => n.Attribute("data-uid") != null));
        }

        private static void AssertParallelGeometry(XElement svg)
        {
            var branches = new[] { "10", "11", "12" }.Select(id => SymbolBounds(svg, id)).ToArray();
            var coil = SymbolBounds(svg, "17");
            Assert.All(branches, p => Assert.Equal(branches[0].left, p.left));
            Assert.True(branches[0].y < branches[1].y && branches[1].y < branches[2].y);
            Assert.Equal(branches[0].y, coil.y);
            Assert.True(coil.left > branches.Max(p => p.right));
            var join = Assert.Single(VerticalBranches(svg), j => j.x > 24);
            Assert.True(join.x > branches.Max(p => p.right) && join.x < coil.left);
            Assert.Equal(branches[0].y, join.top); Assert.Equal(branches[2].y, join.bottom);
            foreach (var branch in branches)
            {
                Assert.Contains(svg.Elements(), n => (string?)n.Attribute("class") == "wire" && (string?)n.Attribute("d") == $"M {branch.left} {branch.y} H 24");
                Assert.Contains(svg.Elements(), n => (string?)n.Attribute("class") == "wire" && (string?)n.Attribute("d") == $"M {branch.right} {branch.y} H {join.x}");
            }
            Assert.Contains(svg.Elements(), n => (string?)n.Attribute("class") == "wire" && (string?)n.Attribute("d") == $"M {coil.left} {coil.y} H {join.x}");
        }

        private static (int left, int right, int y) SymbolBounds(XElement svg, string id)
        {
            var symbol = svg.Descendants().Single(e => (string?)e.Attribute("data-uid") == id);
            string path = (string)symbol.Elements().First(e => (string?)e.Attribute("class") == "symbol").Attribute("d")!;
            var leads = Regex.Matches(path, @"M (-?\d+) (-?\d+) H (-?\d+)").Cast<Match>().ToArray();
            Assert.Equal(2, leads.Length);
            var xs = leads.SelectMany(m => new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[3].Value) }).ToArray();
            return (xs.Min(), xs.Max(), int.Parse(leads[0].Groups[2].Value));
        }

        private static (int x, int top, int bottom)[] VerticalBranches(XElement svg)
            => svg.Elements().Where(e => (string?)e.Attribute("class") == "branch")
                .Select(e => Regex.Match((string)e.Attribute("d")!, @"^M (-?\d+) (-?\d+) V (-?\d+)$"))
                .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))).ToArray();

        [Fact]
        public void Boxes_keep_types_parameters_instances_and_unknown_part_notes()
        {
            string html = RenderFixture("tests/Engine/TiaMcpServer.Tests/Fixtures/PlcRender/Parts/BoxParts.xml", out _);
            var svgs = Svgs(html);
            Assert.Equal(12, svgs.Length);
            Assert.Contains("FBD shown as ladder equivalent", html);
            Assert.Contains("Unknown parts shown as labelled boxes: VendorMystery.", html);
            Assert.DoesNotContain("class=\"open-pin\"", html);
            for (int i = 0; i < 7; i++)
            {
                var texts = svgs[i].Descendants().Where(e => e.Name.LocalName == "text").ToArray();
                Assert.Contains(texts, e => (string?)e.Attribute("class") == "instance" && e.Value.EndsWith("Instance", StringComparison.Ordinal));
                Assert.Equal(i < 4 ? "Time" : "Int", texts.Single(e => (string?)e.Attribute("class") == "instruction-type").Value);
                Assert.Contains(texts, e => e.Value == "...");
            }
            for (int i = 7; i < 9; i++)
            {
                Assert.Contains(svgs[i].Descendants(), e => (string?)e.Attribute("class") == "instruction-name" && e.Value == (i == 7 ? "MixedCaseFB" : "MixedCaseFC"));
                Assert.Equal(i == 7 ? 1 : 0, svgs[i].Descendants().Count(e => (string?)e.Attribute("class") == "instance"));
                Assert.Contains(svgs[i].Descendants(), e => (string?)e.Attribute("class") == "pin-name" && e.Value == "Result");
                Assert.Contains(svgs[i].Descendants(), e => (string?)e.Attribute("class") == "pin-value" && e.Value.EndsWith("Result", StringComparison.Ordinal));
            }
            var movePins = svgs[9].Descendants().Where(e => (string?)e.Attribute("class") == "pin-name").Select(e => e.Value).ToArray();
            Assert.Equal(new[] { "EN", "ENO", "IN", "OUT1", "OUT2", "OUT3" }, movePins);
            Assert.DoesNotContain(svgs[9].Descendants(), e => (string?)e.Attribute("class") == "instruction-type");
            Assert.Contains(svgs[10].Descendants(), e => (string?)e.Attribute("class") == "instruction-name" && e.Value == "AND");
            Assert.Contains(svgs[10].Descendants(), e => e.Value == "IN3");
            Assert.Contains(svgs[11].Descendants(), e => (string?)e.Attribute("class") == "instruction-name" && e.Value == "VendorMystery");
        }

        [Fact]
        public void Cookbook_comparisons_not_and_math_use_their_ladder_presentation()
        {
            var drawings = new[] { "MCPVerify_FB_LAD_v3", "MCPVerify_FC_LAD", "MCPVerify_FC_LAD_v2" }
                .SelectMany(n => Svgs(RenderFixture("plugin/skill/lad-cookbook/" + n + ".xml", out _))).ToArray();
            var comparisons = drawings.SelectMany(s => s.Descendants().Where(e => (string?)e.Attribute("class") == "compare")).ToArray();
            Assert.Equal(new[] { "<", ">", "==", "<>", "<=", ">=" }, comparisons.Select(n => n.Elements().Single(e => (string?)e.Attribute("class") == "symbol-mark").Value));
            foreach (var node in comparisons)
            {
                Assert.DoesNotContain(node.Elements(), e => e.Name.LocalName == "rect" || (string?)e.Attribute("class") == "pin-name");
                var upper = node.Elements().Single(e => (string?)e.Attribute("class") == "operand");
                var lower = node.Elements().Single(e => (string?)e.Attribute("class") == "compare-operand");
                var type = node.Elements().Single(e => (string?)e.Attribute("class") == "instruction-type");
                Assert.Equal("Int", type.Value);
                Assert.True((int)upper.Attribute("y")! < (int)type.Attribute("y")! && (int)type.Attribute("y")! < (int)lower.Attribute("y")!);
            }
            var not = drawings.SelectMany(s => s.Descendants()).Single(e => (string?)e.Attribute("class") == "not");
            Assert.DoesNotContain(not.Elements(), e => e.Name.LocalName == "rect");
            Assert.Contains(not.Elements(), e => (string?)e.Attribute("class") == "symbol-mark" && e.Value == "NOT");
            var boxes = drawings.SelectMany(s => s.Descendants()).Where(e => (string?)e.Attribute("class") == "instruction").ToArray();
            foreach (string name in new[] { "MOVE", "ADD", "SUB", "MUL", "DIV", "MOD" })
            {
                var box = boxes.Single(n => n.Elements().Any(e => (string?)e.Attribute("class") == "instruction-name" && e.Value == name));
                Assert.Contains(box.Elements(), e => e.Value == "EN");
                Assert.Contains(box.Elements(), e => e.Value == "ENO");
                Assert.DoesNotContain(box.Elements(), e => (string?)e.Attribute("class") == "instruction-type" && e.Value != "Int");
            }
        }

        private string RenderFixture(string relative, out string input)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Version.props"))) repository = repository.Parent;
            Assert.NotNull(repository);
            input = Path.GetFullPath(Path.Combine(repository!.FullName, relative));
            string output = Output(Guid.NewGuid().ToString("N") + ".html");
            var result = PlcProgramRenderer.Write(input, output, false, "21");
            Assert.True(result.Ok, result.Error?.Message);
            return File.ReadAllText(output);
        }

        private static XElement[] Svgs(string html)
            => Regex.Matches(html, "<svg\\b.*?</svg>", RegexOptions.Singleline).Cast<Match>().Select(m => XElement.Parse(m.Value)).ToArray();

        [Fact]
        public void Invalid_paths_xml_and_multi_block_input_do_not_create_output()
        {
            Assert.Equal(ErrorCode.InvalidArgument, PlcProgramRenderer.Write("relative.xml", Output(), false, "21").Error!.Code);
            Assert.Equal(ErrorCode.NotFound, PlcProgramRenderer.Write(Output("missing.xml"), Output(), false, "21").Error!.Code);
            string input = Put("bad.xml", "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///missing'>]><Document>&x;</Document>");
            Assert.False(PlcProgramRenderer.Write(input, Output(), false, "21").Ok);
            Assert.False(File.Exists(Output()));
            input = Put("two.xml", "<Document><SW.Blocks.FC/><SW.Blocks.FB/></Document>");
            Assert.False(PlcProgramRenderer.Write(input, Output(), false, "21").Ok);
            Assert.False(File.Exists(Output()));
        }

        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }
}

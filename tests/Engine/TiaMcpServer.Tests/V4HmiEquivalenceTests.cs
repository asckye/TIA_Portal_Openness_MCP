using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4HmiEquivalenceTests
    {
        public static IEnumerable<object[]> Samples()
        {
            // HmiUnifiedThemeLayoutBuilder.cs:17-26,146-155 (five interpreted colors).
            foreach (string key in new[] { "Page", "Background", "Surface", "Text", "Border" })
            {
                yield return Row("theme", "{\"palette\":{\"" + key + "\":\"0xFFAabb00\"}}", true);
                yield return Row("theme", "{\"palette\":{\"" + key + "\":\"#FFAabb00\"}}", false);
            }
            yield return Row("theme", """{"Name":"T","Palette":{"Text":"0xFF000000","Surface":"0xFFFFFFFF","Accent":"custom"}}""", true);
            yield return Row("theme", """{"palette":{}}""", true);
            yield return Row("theme", """{"palette":{"Text":"0xFF123456","text":"shadowed"}}""", true);
            yield return Row("theme", """{"palette":{"Text":"0xFFFFFFFFF"}}""", false);

            // ClassicHmiValidationFixture.cs:10-34, plus builder bounds at :362-381.
            string package = ClassicHmiValidationFixture.BuildClassicHmiPackageJson("P6-05");
            var fixture = JsonNode.Parse(package)!.AsObject();
            yield return Row("classic", fixture["ScreenDesign"]!.ToJsonString(), true);
            yield return Row("tags", fixture["TagTable"]!.ToJsonString(), true);
            yield return Row("package", package, true);
            foreach (string type in new[] { "Text", "Button", "IOField", "Rectangle", "Lamp" })
            {
                var item = new JsonObject { ["Type"] = type, ["Name"] = type, ["Left"] = 520, ["Top"] = 440,
                    ["Width"] = 120, ["Height"] = 40, ["BackColor"] = "0xFF123456" };
                if (type == "Text" || type == "Button") item["Text"] = new JsonObject { ["en-US"] = "<&>", ["zh-CN"] = "测试" };
                item["Properties"] = new JsonObject { ["BorderColor"] = "1, 2, 3", ["BorderWidth"] = 0 };
                if (type == "Text" || type == "Button" || type == "IOField")
                {
                    item["Properties"]!["FontSize"] = 1;
                    item["Properties"]!["ForeColor"] = "0x123456";
                }
                if (type == "Button" || type == "IOField") item["Properties"]!["TabIndex"] = 5;
                if (type == "IOField") item["Properties"]!["Mode"] = "Output";
                var design = new JsonObject { ["Items"] = new JsonArray(item) };
                yield return Row("classic", design.ToJsonString(), true);
                item["Width"] = 121;
                yield return Row("classic", design.ToJsonString(), false);
            }
            foreach (string json in new[]
            {
                """{"items":[]}""", """{"Screen":{"Name":"S","Width":320,"Height":240,"Number":3,"BackColor":"0xFF123456"},"Items":[]}""",
                """{"items":[{"type":"Text","name":"A","text":"","properties":{"fontSize":-5}}]}""",
                """{"items":[{"type":"Text","name":"A","backColor":"0xFF123456","properties":{"backColor":"0xZZ0000"}}]}""",
                """{"items":[{"type":"Button","name":"A","actions":[{"event":"pressed","actionKind":"set-bit","targetTag":"T"}]}]}"""
            }) yield return Row("classic", json, true);
            foreach (string json in new[]
            {
                """{"screen":{"name":"S","width":319},"items":[]}""", """{"screen":{"name":"S","height":239},"items":[]}""",
                """{"items":[{"name":" ","type":"Text"}]}""", """{"items":[{"name":"A","type":"Text"},{"name":"a","type":"Text"}]}""",
                """{"items":[{"name":"A","type":"Text","left":-1}]}""", """{"items":[{"name":"A","type":"Text","width":0}]}""",
                """{"items":[{"name":"A","type":"Text","height":-1}]}""", """{"items":[{"name":"A","type":"Text","backColor":"0xZZ0000"}]}"""
            }) yield return Row("classic", json, false);

            // ClassicHmiTagTableXmlBuilder.cs:46-54,126-133,174-207.
            foreach (string dataType in new[] { "Bool", "Int", "Real", "LReal", "Custom" })
                yield return Row("tags", "{\"TableName\":\"T\",\"Tags\":[{\"Name\":\"A\",\"DataType\":\"" + dataType + "\"}]}", true);
            yield return Row("tags", """{"name":"T","tags":[{"name":"A","dataType":"Custom","length":"17"}]}""", true);
            foreach (string json in new[]
            {
                """{"name":"","tags":[{"name":"A","dataType":"Bool"}]}""", """{"name":"T","tags":[]}""",
                """{"name":"T","tags":[{"name":"","dataType":"Bool"}]}""",
                """{"name":"T","tags":[{"name":"A","dataType":"Bool"},{"name":"a","dataType":"Bool"}]}""",
                """{"name":"T","tags":[{"name":"A","dataType":"Bool","connection":"C"}]}""",
                """{"name":"T","tags":[{"name":"A","dataType":"Bool","controllerTag":"C"}]}"""
            }) yield return Row("tags", json, false);
            var missingReference = (JsonObject)fixture.DeepClone();
            missingReference["ScreenDesign"]!["Items"]![2]!["Tag"] = "NotDeclared";
            // Package readiness remains a builder finding, not a parse rejection (:260-290).
            yield return Row("package", missingReference.ToJsonString(), true);
            yield return Row("package", """{"name":"P","screenDesign":{"items":[]},"tagTable":{"name":"T","tags":[]}}""", false);
            yield return Row("package", """{"name":"P","tagTable":{"name":"T","tags":[{"name":"A","dataType":"Bool"}]}}""", false);
            yield return Row("package", """{"name":"P","screenDesign":{"items":[]}}""", false);

            // HmiUnifiedThemeLayoutBuilder.cs:47-109: defaults, clamps, snapping, spans.
            yield return Row("layout", """{"items":[]}""", true);
            yield return Row("layout", """{"grid":8,"left":4,"top":-4,"gap":7,"columns":2,"cellWidth":101,"cellHeight":33,"items":[{"name":"A","type":"Text","text":"A","font":{"Size":12}},{"name":"B","type":"Rectangle","row":2,"col":3,"rowSpan":2,"colSpan":3}]}""", true);
            yield return Row("layout", """{"grid":0,"columns":0,"cellWidth":-5,"cellHeight":0,"items":[{"name":"A","row":-1,"col":-1,"rowSpan":0,"colSpan":-1}]}""", true);
            yield return Row("layout", """{"items":[{"name":"A","type":"Button","culture":"en-US","text":"","content":{"TextHorizontalAlignment":"Center"},"padding":{"Left":4},"properties":{"BackColor":"0xFF123456"}}]}""", true);
            yield return Row("layout", """{"items":[{"name":" "}]}""", false);
            yield return Row("layout", """{"items":[1]}""", false);
            yield return Row("layout", "{}", false);

            // HardwareAmlTests.cs:15-21,32-35. Defaults added only as section 2 requires.
            yield return Row("aml", (string)typeof(HardwareAmlTests).GetField("Spec", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!, true);
            yield return Row("aml", """{"devices":[{"name":"D","typeIdentifier":"x","deviceItems":[{"name":"M"}]}]}""", false);
            yield return Row("aml", """{"devices":[]}""", false);
            yield return Row("aml", """{"devices":[{"name":"D","typeIdentifier":"x","deviceItems":[{"name":"M","role":"Module","typeIdentifier":"y"}]}]}""", false);
            yield return Row("aml", """{"devices":[{"name":"D","typeIdentifier":"x","deviceItems":[{"name":"I","role":"CommunicationInterface","nodes":[{"networkAddress":"300.1.1.1"}]}]}]}""", false);
            foreach (string role in new[] { "Rack", "DeviceItem", "CommunicationInterface", "CommunicationPort" })
                yield return Row("aml", "{\"projectName\":\"P\",\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"name\":\"I\",\"role\":\"" + role + "\",\"builtIn\":true,\"positionNumber\":-1,\"label\":\" L \",\"comment\":\"C\",\"nodes\":[{\"networkAddress\":\"123\",\"subnetName\":\"N\",\"routerAddress\":\"r\",\"pnDeviceName\":\"pn\"}]}]}],\"subnets\":[{\"name\":\"N\",\"type\":\"Profibus\"}]}", true);
            yield return Row("aml", """{"projectName":"","devices":[{"name":" D ","typeIdentifier":" x ","deviceItems":[]}]}""", true);
            yield return Row("aml", """{"devices":[{"name":" ","typeIdentifier":"x","deviceItems":[]}]}""", false);
            yield return Row("aml", """{"devices":[{"name":"D","typeIdentifier":" ","deviceItems":[]}]}""", false);
            yield return Row("aml", """{"devices":[{"name":"D","typeIdentifier":"x","deviceItems":[]}],"subnets":[{"name":" "}]}""", false);
        }

        private static object[] Row(string family, string json, bool accepted) => new object[] { family, json, accepted };

        [Theory, MemberData(nameof(Samples))]
        public void CurrentParserDecisionsAndBuilderBytesArePreserved(string family, string legacy, bool accepted)
        {
            string canonical = CanonicalInput(family, legacy);
            var type = family switch
            {
                "classic" => typeof(ClassicScreenSpec), "tags" => typeof(ClassicTagTableSpec), "package" => typeof(ClassicPackageSpec),
                "theme" => typeof(UnifiedThemeSpec), "layout" => typeof(UnifiedLayoutSpec), "aml" => typeof(DeviceAmlSpec),
                _ => throw new InvalidOperationException(family)
            };
            if (!accepted)
            {
                Assert.ThrowsAny<Exception>(() => Build(family, legacy));
                Assert.ThrowsAny<Exception>(() => V4HmiContractTests.Read(type, canonical));
                return;
            }
            var dto = V4HmiContractTests.Read(type, canonical);
            Assert.True(V4HmiContractTests.HmiSchemaAccepts(dto.GetSchema(), JsonNode.Parse(canonical)), canonical);
            string expected = Build(family, legacy);
            string actual = Build(family, dto.ToBuilderInput().ToJsonString());
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        }

        private static string Build(string family, string json)
        {
            var root = JsonNode.Parse(json)!.AsObject();
            switch (family)
            {
                case "classic": return ClassicHmiScreenXmlBuilder.BuildFromJson(json)["xml"]!.GetValue<string>();
                case "tags": return ClassicHmiTagTableXmlBuilder.BuildFromJson(json)["xml"]!.GetValue<string>();
                case "package":
                    var package = ClassicHmiMinimalPackageBuilder.BuildFromJson(json);
                    package.Remove("timestamp"); package["screen"]!.AsObject().Remove("timestamp"); package["tagTable"]!.AsObject().Remove("timestamp");
                    return package.ToJsonString();
                case "theme": return HmiUnifiedThemeLayoutBuilder.BuildThemeDesign(root).ToJsonString();
                case "layout": return HmiUnifiedThemeLayoutBuilder.BuildLayoutDesign(root).ToJsonString();
                case "aml": return StableAml(HardwareAmlLogic.Build(HardwareAmlLogic.ParseSpec(json), null, "p6-05.aml", "4.0").Document);
                default: throw new InvalidOperationException(family);
            }
        }

        private static string StableAml(XDocument document)
        {
            // HardwareAmlLogic.cs:268,287-289: each build allocates GUIDs and a wall-clock timestamp.
            // Keep and compare every link after a bijective ID substitution, never delete links.
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in document.Descendants().Attributes("ID"))
            {
                Assert.True(Guid.TryParse(id.Value, out _));
                Assert.False(ids.ContainsKey(id.Value));
                ids.Add(id.Value, "id-" + ids.Count.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var attribute in document.Descendants().Attributes())
            {
                if (attribute.Name == "ID") attribute.Value = ids[attribute.Value];
                if (attribute.Name == "RefPartnerSideA" || attribute.Name == "RefPartnerSideB") attribute.Value = ids[attribute.Value];
            }
            foreach (var element in document.Descendants("WriterProjectID"))
            {
                Assert.True(Guid.TryParse(element.Value, out _)); element.Value = "project-id";
            }
            foreach (var element in document.Descendants("LastWritingDateTime"))
            {
                Assert.True(DateTime.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)); element.Value = "timestamp";
            }
            return HardwareAmlLogic.Serialize(document);
        }

        internal static string CanonicalInput(string family, string json)
        {
            var root = Camel(JsonNode.Parse(json)!, nativeProperties: family == "layout").AsObject();
            if (family == "classic") CanonicalScreen(root);
            if (family == "tags") CanonicalTags(root);
            if (family == "package")
            {
                if (root["screenDesign"] is JsonObject screen) CanonicalScreen(screen);
                if (root["tagTable"] is JsonObject table) CanonicalTags(table);
            }
            if (family == "aml" && !root.ContainsKey("projectName")) root["projectName"] = "Project";
            return root.ToJsonString();
        }

        private static JsonNode Camel(JsonNode node, bool dictionary = false, bool nativeProperties = false)
        {
            if (node is JsonArray array) return new JsonArray(array.Select(n => n == null ? null : Camel(n, nativeProperties: nativeProperties)).ToArray());
            if (node is not JsonObject obj) return node.DeepClone();
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                string key = dictionary ? pair.Key : char.ToLowerInvariant(pair.Key[0]) + pair.Key.Substring(1);
                bool childDictionary = key == "palette" || key == "attributes" || key == "text" || key == "font" || key == "content" || key == "padding" || nativeProperties && key == "properties";
                result[key] = pair.Value == null ? null : Camel(pair.Value, childDictionary, nativeProperties);
            }
            return result;
        }

        private static void CanonicalScreen(JsonObject root)
        {
            if (root["items"] is not JsonArray items) return;
            foreach (var item in items.OfType<JsonObject>())
            {
                if (item["tag"] is JsonNode tag) { item["processValueTag"] = tag.DeepClone(); item.Remove("tag"); }
                if (item["actions"] is JsonArray actions)
                    foreach (var action in actions.OfType<JsonObject>())
                    {
                        if (action["actionKind"]?.ToString() == "set-bit") action["actionKind"] = "SetBit";
                    }
            }
        }

        private static void CanonicalTags(JsonObject root)
        {
            if (root["tableName"] is JsonNode name) { root["name"] = name.DeepClone(); root.Remove("tableName"); }
            if (root["tags"] is JsonArray tags)
                foreach (var tag in tags.OfType<JsonObject>())
                    if (tag["plcTag"] is JsonNode plc) { tag["controllerTag"] = plc.DeepClone(); tag.Remove("plcTag"); }
        }

        [Theory]
        [InlineData("Text")]
        [InlineData("Button")]
        [InlineData("IOField")]
        [InlineData("Rectangle")]
        public void UnifiedAdapterPreservesExistingTemplateBuilderDesign(string control)
        {
            // HmiTemplateDesignJsonBuilder.cs:28-109 -> UnifiedHmiService.cs:1154-1240.
            var item = new JsonObject { ["Type"] = control, ["Name"] = "A", ["Left"] = 1, ["Top"] = 2, ["Width"] = 120, ["Height"] = 40 };
            if (control == "Text" || control == "Button")
            {
                item["Text"] = new JsonObject { ["zh-CN"] = "Start" };
                item["Properties"] = new JsonObject { ["FontSize"] = 12, ["ForeColor"] = "0xFF123456" };
            }
            var template = new JsonObject { ["Screen"] = new JsonObject { ["Width"] = 800, ["Height"] = 480,
                ["Properties"] = new JsonObject { ["Name"] = "S", ["Width"] = 800, ["Height"] = 480, ["BackColor"] = "0xFF123456" } }, ["Items"] = new JsonArray(item) };
            var legacy = HmiTemplateDesignJsonBuilder.BuildApplyDesign(template, 800, 480);
            var canonical = new JsonObject { ["screen"] = new JsonObject { ["name"] = "S", ["width"] = 800, ["height"] = 480,
                ["properties"] = new JsonObject { ["BackColor"] = "0xFF123456" } }, ["items"] = legacy["items"]!.DeepClone() };
            var dto = V4Json.Deserialize<UnifiedScreenSpec>(canonical.ToJsonString());
            // width/height at the old root are template metadata, ignored by Apply.
            legacy.Remove("width"); legacy.Remove("height");
            Assert.Equal(Sorted(legacy), Sorted(dto.ToBuilderInput()));
            Assert.Equal(UnifiedMultilingualText.ReadRequest(legacy["items"]![0]!.AsObject(), out var oldText),
                UnifiedMultilingualText.ReadRequest(dto.ToBuilderInput()["items"]![0]!.AsObject(), out var newText));
            Assert.Equal(oldText, newText);
        }

        private static string Sorted(JsonNode node)
        {
            JsonNode Sort(JsonNode n) => n is JsonObject obj ? new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value == null ? null : Sort(p.Value))))
                : n is JsonArray array ? new JsonArray(array.Select(v => v == null ? null : Sort(v)).ToArray()) : n.DeepClone();
            return Sort(node).ToJsonString();
        }

        [Theory]
        [InlineData("null", false)]
        [InlineData("123", false)]
        [InlineData("{}", false)]
        [InlineData("\"\"", true)]
        [InlineData("\"Start\"", true)]
        public void UnifiedTextParserDecisionsArePreserved(string textJson, bool accepted)
        {
            // UnifiedMultilingualText.cs:15-23 is the current parser called before native writes.
            string json = "{\"type\":\"Text\",\"name\":\"A\",\"text\":" + textJson + "}";
            if (accepted)
            {
                var dto = V4Json.Deserialize<UnifiedScreenItem>(json);
                Assert.True(UnifiedMultilingualText.ReadRequest(JsonNode.Parse(json)!.AsObject(), out var oldText));
                Assert.True(UnifiedMultilingualText.ReadRequest(dto.ToBuilderInput(), out var newText));
                Assert.Equal(oldText, newText);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => UnifiedMultilingualText.ReadRequest(JsonNode.Parse(json)!.AsObject(), out _));
                Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<UnifiedScreenItem>(json));
            }
        }

        [Fact]
        public void AmlCountIsSharedAcrossDevices()
        {
            var items = Enumerable.Range(0, 2501).Select(i => new DeviceItemSpec("I" + i, builtIn: true)).ToArray();
            Assert.Throws<ArgumentException>(() => new DeviceAmlSpec("P", new[]
            {
                new AmlDeviceSpec("D1", "x", items), new AmlDeviceSpec("D2", "x", items)
            }));
            var device = new JsonObject { ["name"] = "D", ["typeIdentifier"] = "x", ["deviceItems"] = new JsonArray(items.Select(i => (JsonNode)i.ToBuilderInput()).ToArray()) };
            var json = new JsonObject { ["projectName"] = "P", ["devices"] = new JsonArray(device, device.DeepClone()) }.ToJsonString();
            Assert.Throws<ArgumentException>(() => HardwareAmlLogic.ParseSpec(json));
            Assert.Throws<ArgumentException>(() => V4Json.Deserialize<DeviceAmlSpec>(json));
        }

        [Fact]
        public void JsonDepthLimitIsRetained()
        {
            string nested = "0";
            for (int i = 0; i < 65; i++) nested = "[" + nested + "]";
            string json = "{\"palette\":{\"extension\":" + nested + "}}";
            Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(json));
            Assert.ThrowsAny<JsonException>(() => V4Json.Deserialize<UnifiedThemeSpec>(json));
        }

        [Theory]
        [InlineData(8, true)]
        [InlineData(9, false)]
        public void AmlDepthBudgetMatchesCurrentParser(int depth, bool accepted)
        {
            var item = new JsonObject { ["name"] = "I", ["builtIn"] = true };
            for (int i = 0; i < depth; i++) item = new JsonObject { ["name"] = "I", ["builtIn"] = true, ["deviceItems"] = new JsonArray(item) };
            CheckAmlBudget(new JsonArray(item), accepted);
        }

        [Theory]
        [InlineData(5000, true)]
        [InlineData(5001, false)]
        public void AmlTotalItemBudgetMatchesCurrentParser(int count, bool accepted) => CheckAmlBudget(
            new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["name"] = "I" + i, ["builtIn"] = true }).ToArray()), accepted);

        private static void CheckAmlBudget(JsonArray items, bool accepted)
        {
            string json = new JsonObject { ["projectName"] = "P", ["devices"] = new JsonArray(new JsonObject { ["name"] = "D",
                ["typeIdentifier"] = "x", ["deviceItems"] = items }) }.ToJsonString();
            if (accepted)
            {
                var old = HardwareAmlLogic.ParseSpec(json);
                var dto = V4Json.Deserialize<DeviceAmlSpec>(json);
                Assert.Equal(HardwareAmlLogic.AllItems(old.Devices[0]).Count(), HardwareAmlLogic.AllItems(HardwareAmlLogic.ParseSpec(dto.ToBuilderInput().ToJsonString()).Devices[0]).Count());
            }
            else
            {
                Assert.Throws<ArgumentException>(() => HardwareAmlLogic.ParseSpec(json));
                Assert.Throws<ArgumentException>(() => V4Json.Deserialize<DeviceAmlSpec>(json));
            }
        }

        [Fact]
        public void HFamilyDoesNotInheritUnrelatedFoundationCharacterLimits()
        {
            // B1: H has no maxLength. The 262144/4096 limits belong to Foundation B.
            string json = "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"text\":\"" + new string('x', 262145) + "\"}]}";
            var old = ClassicHmiScreenXmlBuilder.BuildFromJson(json)["xml"]!.ToString();
            var dto = V4Json.Deserialize<ClassicScreenSpec>(json);
            Assert.Equal(old, ClassicHmiScreenXmlBuilder.BuildFromJson(dto.ToBuilderInput().ToJsonString())["xml"]!.ToString());
            Assert.Equal(V4Json.Serialize(dto), V4Json.Serialize(V4Json.Deserialize<ClassicScreenSpec>(" \n " + json + " \n ")));
        }
    }
}

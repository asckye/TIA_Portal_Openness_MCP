using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

// Exercise the public construction boundary and the same builders used by the host.
// XSD checks belong to the SDK-backed tests; successful construction is not native acceptance.
internal static class PlcPatternLadderInputs
{
    internal static IReadOnlyList<XDocument> Build(string root, JsonNode variant)
    {
        var documents = new List<XDocument>();
        var inputs = new List<JsonNode>();
        foreach (var file in variant["files"]!.AsArray())
        {
            string path = Text(file!["path"]);
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, path));
            Require(Text(file["encoding"]) == "ASCII" && bytes.All(b => b < 128 && b != 13 && b != 9),
                "LAD inputs need ASCII without BOM, LF and no tabs: " + path);
            string json = System.Text.Encoding.ASCII.GetString(bytes);
            inputs.Add(JsonNode.Parse(json)!);
            ConstructionSpec spec;
            if (path.EndsWith(".rung.json", StringComparison.Ordinal))
            {
                var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation).Read(json, "ladFcBlock");
                Require(input.IsValid, "Invalid LAD rung input: " + path + " " + V4Json.Serialize(input.Error));
                spec = input.Value!;
                Require(inputs[^1]["networks"]!.AsArray().All(n => n!["rung"] != null), "Pattern needs rung networks: " + path);
            }
            else
            {
                Require(path.EndsWith(".db.json", StringComparison.Ordinal), "Unknown LAD input file: " + path);
                var input = new ConstructionInput<GlobalDbSpec>(ConstructionProfile.Foundation).Read(json, "globalDb");
                Require(input.IsValid, "Invalid LAD global DB input: " + path);
                spec = input.Value!;
            }
            // FullEngine takes the PlcBuilderToolJson.ComposeLadFcBlock/BuildGlobalDb route.
            documents.Add(XDocument.Parse(ConstructionAdapter.BuildCandidate(spec, "21", ConstructionProfile.FullEngine).Xml));
        }
        var blocks = documents.SelectMany(d => d.Root!.Elements().Where(e => e.Name.LocalName is "SW.Blocks.FC" or "SW.Blocks.GlobalDB"))
            .ToArray();
        var declared = variant["contains"]!.AsArray();
        Require(blocks.Length == declared.Count, "LAD block count differs from contains");
        for (int i = 0; i < blocks.Length; i++)
            Require(Text(declared[i]!["kind"]) == (blocks[i].Name.LocalName == "SW.Blocks.FC" ? "FC" : "DB")
                && Text(declared[i]!["name"]) == blocks[i].Element("AttributeList")!.Element("Name")!.Value
                && declared[i]!["instanceOf"] == null, "LAD block order/name differs from contains");

        var dbs = inputs.Where(n => n["dbName"] != null).ToDictionary(n => Text(n["dbName"]), StringComparer.Ordinal);
        foreach (var input in inputs.Where(n => n["networks"] != null)) Visit(input);
        return documents;

        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["global"] is JsonArray components)
                {
                    string dbName = Text(components[0]);
                    Require(dbs.TryGetValue(dbName, out var db) && components.Count >= 2,
                        "LAD global operand must refer to a declared state DB member");
                    string member = Text(components[1]);
                    Require(db!["staticMembers"]!.AsArray().Any(m => Text(m!["name"]) == member), "Unknown LAD state member: " + member);
                }
                string kind = Text(obj["kind"]);
                if (kind is "timer" or "counter")
                {
                    string instruction = Text(obj["type"]);
                    var declaration = variant["instructions"]!.AsArray().SingleOrDefault(i => Text(i!["name"]) == instruction);
                    Require(declaration != null && Set(Strings(declaration["cpuFamilies"])).SetEquals(Strings(variant["cpuFamilies"])),
                        "Undeclared LAD instruction: " + instruction);
                    var instance = obj["instance"]!["global"]!.AsArray();
                    Require(instance.Count == 2 && dbs.ContainsKey(Text(instance[0])), "Unknown LAD instance DB");
                    var db = dbs[Text(instance[0])];
                    string datatype = instruction + (kind == "timer" ? "_TIME" : "_" + Text(obj["valueType"]).ToUpperInvariant());
                    Require(db["staticMembers"]!.AsArray()
                        .Any(m => Text(m!["name"]) == Text(instance[1]) && Text(m["datatype"]) == datatype),
                        "LAD instruction instance type differs from state DB: " + instruction);
                }
                foreach (var property in obj) if (property.Value != null) Visit(property.Value);
            }
            else if (node is JsonArray array)
                foreach (var item in array) if (item != null) Visit(item);
        }
    }
}

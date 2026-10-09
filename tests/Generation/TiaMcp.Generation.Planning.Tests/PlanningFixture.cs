using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;

namespace TiaMcp.Generation.Planning.Tests
{
    internal sealed class PlanningFixture
    {
        internal static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
        public readonly Dictionary<string, JsonObject> Documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal);
        public MachineDescription Machine;
        public ProjectModel Observed;
        public GenerationPlanningOptions Options = new GenerationPlanningOptions { ArtifactRoot = "C:/tiamcp-generation" };

        public PlanningFixture(string release = "21")
        {
            Documents["naming.json"] = JsonNode.Parse("{\"rules\":[]}")!.AsObject();
            Naming("device", "device", "{{station.id}}", "^[A-Za-z][A-Za-z0-9_]*$");
            Naming("tag", "tag", "{{device.id}}_{{upper(signal.role)}}", "^[A-Z][A-Z0-9_]*$");
            Naming("tagTable", "tagTable", "Tags_{{unit.id}}", "^Tags_[A-Za-z0-9_]+$");
            Naming("idb", "instanceDb", "IDB_{{device.id}}", "^IDB_[A-Za-z0-9_]+$");
            Naming("callBlock", "FC", "FC_{{unit.id}}_Calls", "^FC_[A-Za-z0-9_]+$");
            foreach (var kind in new[] { "FB", "UDT", "DB", "OB", "blockGroup", "typeGroup", "project", "network", "alarm", "screen", "widget", "screenItem" }) Naming(kind, kind, "{{device.id}}", "^[A-Za-z0-9_]+$");
            Naming("source", "externalSource", "{{device.id}}", "^source_[a-f0-9]{16}_[a-f0-9]{16}\\.scl$");
            Documents["structure.json"] = JsonNode.Parse("{\"groups\":[],\"numberRanges\":[{\"id\":\"sequence\",\"kind\":\"DB\",\"start\":100,\"end\":999}]}")!.AsObject();
            Documents["hardware.json"] = JsonNode.Parse("""
                {"roles":[],"allocation":{"io":[
                  {"id":"inputs","direction":"DI","start":"%I0.0","end":"%I15.7"},
                  {"id":"outputs","direction":"DO","start":"%Q0.0","end":"%Q15.7"},
                  {"id":"analog","direction":"AI","start":"%IW32","end":"%IW62"}],
                  "ip":{"subnet":"192.168.1.0/24","start":"192.168.1.10","end":"192.168.1.20"}}}
                """)!.AsObject();
            Documents["library.json"] = JsonNode.Parse("""
                {"types":[{"id":"fb.motor","kind":"FB","version":"1.0.0","interface":{
                  "in":[{"name":"Feedback","type":"Bool","role":"signal.feedback"}],
                  "out":[{"name":"Run","type":"Bool","role":"signal.run"}]},
                  "implementations":[{"releases":"*","kind":"sclSource","files":["sources/motor.scl"]}]}],"libraries":[]}
                """)!.AsObject();
            Documents["rules/motor.json"] = JsonNode.Parse("""
                {"deviceType":"motor","title":{"en-US":"Synthetic motor"},"params":{"type":"object","additionalProperties":true},
                  "signals":[{"role":"feedback","dir":"DI"},{"role":"run","dir":"DO"},{"role":"overload","dir":"DI","optional":true}],
                  "emit":[{"kind":"plc.instance","type":"lib:fixture.planning/fb.motor@^1","name":"{{naming.idb(device)}}",
                    "callIn":"{{unit.callBlock}}","bind":{"signal.feedback":"{{tag(signal.feedback)}}","signal.run":"{{tag(signal.run)}}"}},
                    {"kind":"plc.tag","forEach":"signals","table":"{{unit.tagTable}}","name":"{{naming.tag(device,signal)}}","address":"{{alloc.io(signal)}}"}]}
                """)!.AsObject();
            Sources["sources/motor.scl"] = "FUNCTION_BLOCK \"FB_Motor\"\nVAR_INPUT\n    Feedback : Bool;\nEND_VAR\nVAR_OUTPUT\n    Run : Bool;\nEND_VAR\nBEGIN\n    #Run := #Feedback;\nEND_FUNCTION_BLOCK\n";
            Machine = GenerationDocuments.Load<MachineDescription>("""
                {"schema":"tiamcp.machine/1","machine":{"id":"Line","name":{"en-US":"Synthetic line"}},
                "standard":{"package":"fixture.planning","version":"^1"},"target":{"release":"21","project":{"mode":"existing","projectIdentity":"fixture-project","softwarePath":"PLC1"}},
                "stations":[{"id":"PLC1","role":"plc.main","article":"synthetic.article","firmware":"V1"}],
                "topology":[{"id":"U01","kind":"unit","name":{"en-US":"Unit"},"children":[{"id":"EM01","kind":"equipmentModule","name":{"en-US":"Equipment"}}]}],
                "devices":[{"id":"M01","type":"motor","parent":"EM01","station":"PLC1","name":{"en-US":"Motor"},"params":{},"io":{"feedback":"auto","run":"auto"}},
                  {"id":"M02","type":"motor","parent":"EM01","station":"PLC1","name":{"en-US":"Motor 2"},"params":{},"io":{"feedback":"%I0.0","run":"auto"}}],
                "options":{"languages":["en-US"]}}
                """);
            Machine.Target.Release = release;
            Observed = new ProjectModel { ProjectIdentity = "fixture-project", Devices = new List<ProjectDevice> { new ProjectDevice { Station = "PLC1", Name = "PLC1", Article = "synthetic.article", Firmware = "V1" } } };
        }

        public void Naming(string id, string kind, string template, string pattern)
            => Documents["naming.json"]["rules"]!.AsArray().Add(new JsonObject { ["id"] = id, ["kind"] = kind, ["template"] = template, ["pattern"] = pattern, ["maxLength"] = 128 });

        public StandardPackage Package(string id = "fixture.planning", PackageReference? parent = null)
        {
            var parts = new JsonObject();
            foreach (var part in new[] { "naming", "structure", "hardware", "library", "alarms", "hmi", "checks", "modes" }) if (Documents.ContainsKey(part + ".json")) parts[part] = part + ".json";
            parts["rules"] = new JsonArray(Documents.Keys.Where(p => p.StartsWith("rules/", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            var files = Documents.ToDictionary(p => p.Key, p => CanonicalJson.Encode(CanonicalJson.Parse(p.Value.ToJsonString())), StringComparer.Ordinal);
            foreach (var source in Sources) files.Add(source.Key, Encoding.UTF8.GetBytes(source.Value));
            var manifest = new JsonObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = "1.0.0", ["title"] = new JsonObject { ["en-US"] = "Synthetic planning fixture" }, ["license"] = "MIT",
                ["targets"] = new JsonObject { ["releases"] = new JsonArray(Releases.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()), ["plcFamilies"] = new JsonArray("S7-1200"), ["hmi"] = new JsonArray("WinCCUnified") },
                ["requires"] = new JsonObject { ["framework"] = "^1", ["optionalProducts"] = new JsonArray() }, ["languages"] = new JsonObject { ["required"] = new JsonArray("en-US"), ["default"] = "en-US" }, ["parts"] = parts,
                ["files"] = new JsonArray(files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["sha256"] = CanonicalJson.HashBytes(p.Value) }).ToArray()) };
            if (parent != null) manifest["extends"] = new JsonObject { ["package"] = parent.Package, ["version"] = parent.Version };
            files.Add("package.json", CanonicalJson.Encode(CanonicalJson.Parse(manifest.ToJsonString())));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var file in files) { using var entry = zip.CreateEntry(file.Key).Open(); entry.Write(file.Value, 0, file.Value.Length); }
            stream.Position = 0;
            return StandardPackageLoader.LoadZip(stream);
        }

        public GenerationPlanningResult Build() => GenerationPlanner.Build(Package(), GenerationDocuments.Canonical(Machine), Observed, Options);
        public JsonArray Emits => Documents["rules/motor.json"]["emit"]!.AsArray();
        public JsonArray Signals => Documents["rules/motor.json"]["signals"]!.AsArray();
        public JsonArray IoRanges => Documents["hardware.json"]["allocation"]!["io"]!.AsArray();
        public static ProjectModel Copy(ProjectModel model) => JsonSerializer.Deserialize<ProjectModel>(JsonSerializer.Serialize(model))!;
    }
}

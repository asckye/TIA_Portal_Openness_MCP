using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class V21EcosystemTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var temp = Path.Combine(tempRoot, "v21-ecosystem-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                var schemas = Path.Combine(temp, "Schemas"); Directory.CreateDirectory(schemas);
                var schema = Path.Combine(schemas, "SW.InterfaceSections_v5.xsd");
                const string xsd = "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema' elementFormDefault='qualified'><xs:element name='Sections'><xs:complexType><xs:sequence><xs:element name='Section' minOccurs='1'><xs:complexType><xs:attribute name='Name' use='required'/></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
                const string xml = "<Document><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Input'/></Sections></Interface></Document>";
                var source = Path.Combine(temp, "test.xml"); File.WriteAllText(schema, xsd); File.WriteAllText(source, xml);
                var valid = PlcSchemaValidation.Validate(source, schemas);
                check(valid["fragmentSchemasPassed"]!.GetValue<bool>() && !valid["wholeDocumentValidated"]!.GetValue<bool>(), "XSD fragment success never certifies whole document");
                check(valid["validatedFragments"]![0]!["schemaSha256"]!.GetValue<string>().Length == 64, "XSD evidence identifies actual schema bytes");
                File.WriteAllText(source, xml.Replace(" Name='Input'", ""));
                var invalid = PlcSchemaValidation.Validate(source, schemas);
                check(!invalid["fragmentSchemasPassed"]!.GetValue<bool>() && invalid["errorCount"]!.GetValue<int>() > 0, "XSD catches required member attributes");
                File.WriteAllText(source, xml.Replace("/v5", "/v6"));
                var missing = PlcSchemaValidation.Validate(source, schemas);
                check(!missing["fragmentSchemasPassed"]!.GetValue<bool>() && missing["skippedFragments"]!.AsArray().Count == 1, "XSD does not use a different namespace version");
                File.WriteAllText(source, "<Document/>");
                check(!PlcSchemaValidation.Validate(source, schemas)["fragmentSchemasPassed"]!.GetValue<bool>(), "No recognized XML fragments is not a pass");
                File.WriteAllText(source, "<!DOCTYPE Document [<!ENTITY e SYSTEM 'file:///not-read'>]>" + xml);
                check(Fails(() => PlcSchemaValidation.Validate(source, schemas)), "XML validator rejects entities");
                File.WriteAllText(source, xml);
                File.WriteAllText(schema, xsd.Replace("<xs:element", "<xs:include schemaLocation='https://invalid.example/no-network.xsd'/><xs:element", StringComparison.Ordinal));
                check(Fails(() => PlcSchemaValidation.Validate(source, schemas)) || !PlcSchemaValidation.Validate(source, schemas)["fragmentSchemasPassed"]!.GetValue<bool>(), "Schema resolver does not access HTTP includes");
                File.WriteAllText(schema, "<!DOCTYPE schema [<!ENTITY e SYSTEM 'file:///not-read'>]>" + xsd);
                check(Fails(() => PlcSchemaValidation.Validate(source, schemas)), "Schema input rejects entities");

                var cwc = Path.Combine(temp, "Cwc"); Directory.CreateDirectory(Path.Combine(cwc, "control"));
                File.WriteAllText(Path.Combine(cwc, "control", "index.html"), "<html><body>CWC</body></html>");
                const string manifest = "{\"mver\":\"1.2.0\",\"control\":{\"identity\":{\"name\":\"Test\",\"displayname\":\"Test\",\"version\":\"1.0\",\"type\":\"guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073\"}}}";
                var manifestPath = Path.Combine(cwc, "manifest.json"); File.WriteAllText(manifestPath, manifest);
                JsonObject Run(string action = "inspect", bool dry = true, string token = "", string output = "") => UnifiedCwcPackage.Run(cwc, action, output, dry, token);
                var preview = Run(); var fingerprint = preview["packageFingerprint"]!.GetValue<string>();
                var zip = Path.Combine(temp, preview["suggestedFileName"]!.GetValue<string>());
                check(preview["start"]!.GetValue<string>() == "control/index.html" && !preview["runtimeValidated"]!.GetValue<bool>(), "CWC follows documented default start without runtime claims");
                check(!Run("build", true, "", zip)["written"]!.GetValue<bool>() && !File.Exists(zip), "CWC build preview does not write");
                check(Fails(() => Run("build", false, "stale", zip)) && !File.Exists(zip), "CWC stale fingerprint prevents package write");
                var result = Run("build", false, fingerprint, zip);
                check(result["written"]!.GetValue<bool>() && File.Exists(zip), "CWC build writes new reviewed package");
                using (var archive = ZipFile.OpenRead(zip)) check(archive.Entries.Select(e => e.FullName).OrderBy(s => s).SequenceEqual(new[] { "control/index.html", "manifest.json" }), "CWC ZIP has required root layout, no extra parent directory");
                var original = File.ReadAllBytes(zip);
                check(Fails(() => Run("build", false, fingerprint, zip)) && File.ReadAllBytes(zip).SequenceEqual(original), "CWC never replaces an existing package");
                check(Fails(() => Run("build", false, fingerprint, Path.Combine(temp, "wrong.zip"))), "CWC output filename must match identity GUID");
                check(Fails(() => Run("build", false, fingerprint, Path.Combine(cwc, Path.GetFileName(zip)))), "CWC output cannot contaminate source folder");
                File.WriteAllText(manifestPath, manifest.Replace("\"version\":\"1.0\"", "\"version\":\"1.1\""));
                check(Run()["packageFingerprint"]!.GetValue<string>() != fingerprint, "CWC fingerprint detects changed manifest");
                File.WriteAllText(manifestPath, manifest.Replace("\"name\":\"Test\"", "\"name\":\"Test\",\"name\":\"Other\""));
                check(Fails(() => Run()), "CWC rejects duplicate manifest keys");
                File.WriteAllText(manifestPath, manifest.Replace("\"version\":\"1.0\"", "\"start\":\"./../escape.html\",\"version\":\"1.0\""));
                check(Fails(() => Run()), "CWC rejects relative reference escape");
                File.WriteAllText(manifestPath, manifest.Replace("guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073", "not-a-guid"));
                check(Fails(() => Run()), "CWC rejects invalid identity type");
                File.WriteAllText(manifestPath, manifest); File.Delete(Path.Combine(cwc, "control", "index.html"));
                check(Fails(() => Run()), "CWC requires referenced start file");
            } finally {
                if (!Path.GetFullPath(temp).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
                Directory.Delete(temp, true);
            }
        }
    }
}

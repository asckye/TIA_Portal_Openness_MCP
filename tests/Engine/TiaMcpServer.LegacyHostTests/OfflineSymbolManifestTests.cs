using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;

internal static class OfflineSymbolManifestTests
{
    internal static async Task Run(IMcpServer server, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "owned-manifest-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string tag = "<Document><Engineering version='V17'/><SW.Tags.PlcTagTable><ObjectList><SW.Tags.PlcTag><AttributeList><Name>Ready</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>";
        const string db = "<Document><Engineering version='V21'/><SW.Blocks.GlobalDB><AttributeList><Name>DB</Name><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Static'><Member Name='Motor' Datatype='&quot;MotorType&quot;'><Member Name='Run' Datatype='Bool'/></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
        JsonObject Build(params string[] paths) => OfflineSymbolManifest.Build(root, paths, OfflineSymbolManifest.ExpectedOrigin);
        void Write(string name, string content) => File.WriteAllText(Path.Combine(root, name), content, new UTF8Encoding(false));
        void Bad(string name, string content, string code)
        { Write(name, content); var result = Build(name); check(result["ok"]!.GetValue<bool>() == false && result.ToJsonString().Contains(code), "manifest rejects " + code); }
        try
        {
            Write("tags.xml", tag); Write("db.xml", db); Write("not-requested.xml", "<!DOCTYPE malicious>");
            var result = Build("tags.xml", "db.xml");
            check(result["ok"]!.GetValue<bool>() && result["symbolCount"]!.GetValue<int>() == 3, "manifest extracts exact requested files only");
            check(result["files"]![0]!["path"]!.GetValue<string>() == "db.xml", "manifest deterministic relative ordering");
            check(result["files"]![1]!["engineeringVersion"]!.GetValue<string>() == "V17", "manifest does not require V21 for metadata extraction");
            check(result["files"]![0]!["sha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db))).ToLowerInvariant(), "manifest hashes exact consumed bytes");
            check(result["symbols"]!.AsArray().Any(s => s!["symbol"]!.GetValue<string>() == "DB.Motor.Run"), "manifest nested member");
            check(result["unresolvedReferences"]!.AsArray().Count == 1 && !result["referencesResolved"]!.GetValue<bool>(), "manifest explicit unresolved UDT");
            foreach (var flag in new[] { "wholeXmlSchemaValidated", "importValidated", "programSemanticsValidated", "nativeCertified" }) check(!result[flag]!.GetValue<bool>(), "manifest no certification " + flag);
            check(!result.ToJsonString().Contains(root), "manifest no absolute root leakage");
            Write("duplicate.xml", tag.Replace("Ready", "ready").Replace("Bool", "Int"));
            result = Build("tags.xml", "duplicate.xml");
            check(!result["ok"]!.GetValue<bool>() && result["symbols"]!.AsArray().Count == 2 && result.ToJsonString().Contains("duplicate-or-case-colliding-symbol"), "manifest preserves conflicting symbols");
            Write("identical.xml", tag); check(!Build("tags.xml", "identical.xml")["ok"]!.GetValue<bool>(), "manifest identical duplicates visible");
            foreach (var path in new[] { "../secret.xml", "./tags.xml", "a//tags.xml", "a/../tags.xml", "a\\tags.xml", "tags.xml:stream", "/secret.xml", "a /tags.xml", "tags.txt" })
                check(!Build(path)["ok"]!.GetValue<bool>(), "manifest rejects path shape " + path);
            check(!Build("tags.xml", "TAGS.xml")["ok"]!.GetValue<bool>(), "manifest case-colliding paths");
            check(!Build()["ok"]!.GetValue<bool>(), "manifest empty file list");
            check(!Build(Enumerable.Range(0, 33).Select(i => i + ".xml").ToArray())["ok"]!.GetValue<bool>(), "manifest file count bounded before reads");
            check(!OfflineSymbolManifest.Build(root, new[] { "tags.xml" }, "unknown")["ok"]!.GetValue<bool>(), "manifest explicit origin required");
            result = Build("SECRET_CANARY.xml"); check(!result.ToJsonString().Contains("SECRET_CANARY") && !result.ToJsonString().Contains(root), "manifest preflight errors redacted");
            Bad("dtd.xml", "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///SECRET_CANARY'>]>" + tag.Replace("Ready", "&x;"), "invalid-or-prohibited-xml");
            Bad("network.xml", "<!DOCTYPE Document SYSTEM 'https://example.invalid/SECRET_CANARY'>" + tag, "invalid-or-prohibited-xml");
            Bad("entity.xml", "<!DOCTYPE Document [<!ENTITY a 'boom'><!ENTITY b '&a;&a;'>]>" + tag, "invalid-or-prohibited-xml");
            Bad("malformed.xml", "<Document><SECRET_CANARY>", "invalid-or-prohibited-xml");
            check(!Build("malformed.xml").ToJsonString().Contains("SECRET_CANARY"), "manifest XML exception text redacted");
            Write("future.xml", tag.Replace("V17", "V99"));
            check(Build("future.xml")["files"]![0]!["engineeringVersionStatus"]!.GetValue<string>() == "unknown-or-missing-marker", "manifest future marker reported without inferred support");
            Bad("namespace.xml", tag.Replace("<Document>", "<Document xmlns='urn:spoof'>"), "unsupported-document-root");
            Bad("unsupported.xml", db.Replace("Interface/v5", "Interface/v99"), "unsupported-interface-namespace-or-version");
            Bad("wrapped.xml", "<wrapper>" + tag + "</wrapper>", "unsupported-document-root");
            Bad("mixed.xml", tag.Replace("</Document>", "<SW.Blocks.FC/></Document>"), "unsupported-document-object");
            Bad("ambiguous.xml", tag.Replace("<Name>Ready</Name>", "<Name>Ready</Name><Name>Wrong</Name>"), "duplicate-tag-field");
            var extraTag = "<SW.Tags.PlcTag><AttributeList><Name>Hidden</Name><DataTypeName>Bool</DataTypeName></AttributeList></SW.Tags.PlcTag>";
            Bad("wrapped-tag.xml", tag.Replace("</ObjectList>", "<Wrapper>" + extraTag + "</Wrapper></ObjectList>"), "unsupported-tag-list-shape");
            Bad("hidden-metadata.xml", tag.Replace("</Document>", "<DocumentInfo>" + extraTag + "</DocumentInfo></Document>"), "unsupported-hidden-declaration");
            Bad("wrongcase-member.xml", db.Replace("</Section>", "<member Name='Hidden' Datatype='Bool'/></Section>"), "unsupported-static-member-shape");
            Bad("wrapped-member.xml", db.Replace("</Section>", "<Wrapper><Member Name='Hidden' Datatype='Bool'/></Wrapper></Section>"), "unsupported-static-member-shape");
            Bad("nested-wrapper.xml", db.Replace("<Member Name='Run' Datatype='Bool'/>", "<Wrapper><Member Name='Run' Datatype='Bool'/></Wrapper>"), "unsupported-nested-member-shape");
            Bad("extra-section.xml", db.Replace("</Sections>", "<Section Name='Input'><Member Name='Hidden' Datatype='Bool'/></Section></Sections>"), "unsupported-or-nonstatic-section");
            Bad("duplicate-address.xml", tag.Replace("</LogicalAddress>", "</LogicalAddress><LogicalAddress>%M99.0</LogicalAddress>"), "duplicate-tag-field");
            Write("raw-name.xml", tag.Replace("Ready", " &quot;Ready&quot; "));
            check(Build("raw-name.xml")["symbols"]![0]!["symbol"]!.GetValue<string>() == " \"Ready\" ", "manifest preserves raw decoded name spelling");
            Bad("nested-db-name.xml", db.Replace("<Name>DB</Name>", "<Name>D<b>B</b></Name>"), "unsupported-db-name-shape");
            Bad("attribute-case.xml", db.Replace("Datatype=", "datatype="), "missing-datatype");
            var prefixed = System.Xml.Linq.XDocument.Parse(db);
            prefixed.Root!.Add(new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "i", OfflineSymbolManifest.InterfaceNamespace));
            foreach (var declaration in prefixed.Descendants().Where(x => x != prefixed.Root).Attributes().Where(x => x.IsNamespaceDeclaration).ToArray()) declaration.Remove();
            Write("prefixed.xml", prefixed.ToString());
            check(Build("prefixed.xml")["ok"]!.GetValue<bool>(), "manifest accepts namespace prefix alias with exact URI");
            Bad("utf8-limit.xml", new string('界', OfflineSymbolManifest.MaxFileBytes / 3 + 1), "file-byte-limit");
            Bad("deep.xml", new string(' ', 1) + string.Concat(Enumerable.Repeat("<a>", 51)) + string.Concat(Enumerable.Repeat("</a>", 51)), "xml-complexity-limit");
            Bad("text.xml", tag.Replace("Ready", new string('a', 16385)), "xml-text-limit");
            Bad("field.xml", tag.Replace("Ready", new string('a', 1025)), "field-text-limit");
            Bad("large.xml", new string('x', OfflineSymbolManifest.MaxFileBytes + 1), "file-byte-limit");
            for (int i = 0; i < 9; i++) Write("total" + i + ".xml", tag + new string(' ', OfflineSymbolManifest.MaxFileBytes - Encoding.UTF8.GetByteCount(tag)));
            check(Build(Enumerable.Range(0, 9).Select(i => "total" + i + ".xml").ToArray()).ToJsonString().Contains("total-byte-limit"), "manifest total bytes bounded");
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(root, "link.xml"), Path.Combine(root, "tags.xml"));
                check(Build("link.xml").ToJsonString().Contains("symlink-or-reparse-point-rejected"), "manifest file symlink rejected");
                Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), root);
                check(Build("linkdir/tags.xml").ToJsonString().Contains("symlink-or-reparse-point-rejected"), "manifest directory symlink rejected");
                check(OfflineSymbolManifest.Build(Path.Combine(root, "linkdir"), new[] { "tags.xml" }, OfflineSymbolManifest.ExpectedOrigin).ToJsonString().Contains("symlink-or-reparse-point-rejected"), "manifest root symlink rejected");
            }
            Bad("empty.xml", "", "empty-or-special-file-rejected");
            if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/mkfifo"))
            {
                var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/mkfifo") { UseShellExecute = false };
                start.ArgumentList.Add(Path.Combine(root, "pipe.xml"));
                using var process = System.Diagnostics.Process.Start(start)!;
                check(process.WaitForExit(5000) && process.ExitCode == 0, "manifest owned FIFO fixture created");
                var fifo = await Task.Run(() => Build("pipe.xml")).WaitAsync(TimeSpan.FromSeconds(3));
                check(fifo.ToJsonString().Contains("empty-or-special-file-rejected"), "manifest zero-length FIFO rejected before open");
            }
            using var cts = new CancellationTokenSource(); cts.Cancel(); bool cancelled = false;
            try { OfflineSymbolManifest.Build(root, new[] { "tags.xml" }, OfflineSymbolManifest.ExpectedOrigin, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "manifest cancellation");
            var tool = OfflineSymbolManifestTools.Create().Single();
            check(tool.ProtocolTool.InputSchema.GetProperty("required").GetArrayLength() == 3 && !tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean(), "manifest exact SDK input contract");
            var args = new Dictionary<string, JsonElement> { ["inputRoot"] = JsonSerializer.SerializeToElement(root), ["files"] = JsonSerializer.SerializeToElement(new[] { "tags.xml" }), ["expectedOrigin"] = JsonSerializer.SerializeToElement(OfflineSymbolManifest.ExpectedOrigin) };
            var request = new RequestContext<CallToolRequestParams>(server) { Params = new() { Name = tool.ProtocolTool.Name, Arguments = args } };
            var response = await tool.InvokeAsync(request); check(response.IsError != true && JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!["symbolCount"]!.GetValue<int>() == 1, "manifest SDK functional dispatch");
            args["path"] = JsonSerializer.SerializeToElement("SECRET_CANARY"); bool rejected = false;
            try { await tool.InvokeAsync(request); } catch (McpException ex) { rejected = ex.ErrorCode == McpErrorCode.InvalidParams && !ex.Message.Contains("SECRET_CANARY"); }
            check(rejected, "manifest legacy broad path argument rejected without echo");
        }
        finally { Directory.Delete(root, true); }
    }
}

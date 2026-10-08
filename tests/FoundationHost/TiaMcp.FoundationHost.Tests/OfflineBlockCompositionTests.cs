using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;

internal static class OfflineBlockCompositionTests
{
    internal static async Task Run(IMcpServer server, Action<bool, string> check)
    {
        string Wire(string key) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
        const string basic = "{\"blockName\":\"Test\",\"blockNumber\":1,\"inputs\":[],\"outputs\":[],\"structuredText\":{\"operations\":[{\"op\":\"assignment\",\"target\":\"#Ready\",\"value\":\"TRUE\"}]}}";
        foreach (var tool in OfflineBlockCompositionTools.Create())
        {
            bool fb = tool.ProtocolTool.Name == "ComposePlcFbBlockXml";
            string parameter = fb ? "fbBlockJson" : "fcBlockJson";
            var name = tool.ProtocolTool.Name;
            Dictionary<string, JsonElement> Args(string json, string release = "21") => new() { [parameter] = JsonSerializer.SerializeToElement(json), ["outputReleaseKey"] = JsonSerializer.SerializeToElement(release) };
            RequestContext<CallToolRequestParams> Request(Dictionary<string, JsonElement> args) => new(server) { Params = new() { Name = name, Arguments = args } };
            async Task<JsonNode> Invoke(string json)
            {
                var reply = await tool.InvokeAsync(Request(Args(json)));
                check(reply.IsError != true, name + " invocation successful");
                return JsonNode.Parse(((TextContentBlock)reply.Content.Single()).Text)!;
            }
            async Task Invalid(Dictionary<string, JsonElement> args, string reason)
            {
                Exception? error = null;
                try { await tool.InvokeAsync(Request(args)); } catch (Exception ex) { error = ex; }
                check(error is McpException m && m.ErrorCode == McpErrorCode.InvalidParams, name + " rejects " + reason);
                check(error != null && !error.ToString().Contains("SECRET_CANARY"), name + " sanitizes " + reason);
            }
            var response = await Invoke(basic);
            foreach (var key in new[] { "Message", "Meta", "Ok", "Data", "Errors", "Warnings", "OutputPath", "OutputFiles", "Xml" }) check(response.AsObject().ContainsKey(Wire(key)), name + " envelope " + key);
            foreach (var flag in new[] { "schemaValidated", "importValidated", "programSemanticsValidated" }) check(response[Wire("Meta")]![flag]!.GetValue<bool>() == false && response[Wire("Data")]![flag]!.GetValue<bool>() == false, name + " honest " + flag);
            check(response[Wire("OutputPath")] == null && response[Wire("OutputFiles")] == null, name + " no file outputs");
            var xml = response[Wire("Xml")]!.GetValue<string>();
            var doc = XDocument.Parse(xml);
            check((string?)doc.Root!.Element("Engineering")!.Attribute("version") == "V21", name + " V21 marker");
            XNamespace ns = OfflineBlockCompositionBuilders.InterfaceNamespace;
            XNamespace stns = OfflineCompositionBuilders.StructuredTextNamespace;
            check(doc.Descendants(ns + "Sections").Count() == 1 && doc.Descendants(stns + "StructuredText").Count() == 1, name + " preserved namespaces");
            check(doc.Descendants("SW.Blocks.CompileUnit").Count() == 1, name + " one compile unit");
            check(doc.Descendants("SW.Blocks." + (fb ? "FB" : "FC")).Count() == 1, name + " correct block kind");
            var st = new StructuredTextXmlBuilder().Assignment("#Ready", "TRUE").BuildInnerXml();
            var empty = Array.Empty<PlcBlockMemberDefinition>();
            var expected = fb ? PlcFbBlockXmlComposer.Compose("Test", 1, empty, empty, empty, empty, empty, st) : PlcFcBlockXmlComposer.Compose("Test", 1, empty, empty, st);
            check(XNode.DeepEquals(XDocument.Parse(xml), XDocument.Parse(expected.ToString())), name + " unchanged helper structure");
            check(OfflineBlockCompositionBuilders.Build(name, "21", basic)["Xml"]!.GetValue<string>() == xml, name + " deterministic");
            var rich = JsonNode.Parse(basic)!.AsObject();
            string value = "<&\"'中文\r\n\ttext";
            rich["blockName"] = value; rich["commentZhCn"] = value; rich["titleZhCn"] = value; rich["networkCommentZhCn"] = value; rich["networkTitleZhCn"] = value;
            rich["inputs"] = new JsonArray(new JsonObject { ["name"] = value, ["datatype"] = value, ["commentZhCn"] = value });
            var richDoc = XDocument.Parse((await Invoke(rich.ToJsonString()))[Wire("Xml")]!.GetValue<string>());
            var member = richDoc.Descendants(ns + "Member").First();
            check((string?)member.Attribute("Name") == value && (string?)member.Attribute("Datatype") == value, name + " attribute fidelity");
            check(member.Descendants(ns + "MultiLanguageText").Single().Value == value, name + " member comment fidelity");
            check(richDoc.Descendants("Text").All(x => x.Value == value), name + " multilingual text fidelity");
            check(richDoc.Descendants("Name").Single().Value == value, name + " block name fidelity");
            foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "V21", "21 ", "22", "" }) await Invalid(Args(basic, release), "release " + release);
            var badArgs = Args(basic); badArgs.Remove("outputReleaseKey"); await Invalid(badArgs, "missing release");
            badArgs = Args(basic); badArgs["outputReleaseKey"] = JsonSerializer.SerializeToElement(21); await Invalid(badArgs, "numeric release");
            badArgs = Args(basic); badArgs["path"] = JsonSerializer.SerializeToElement("SECRET_CANARY"); await Invalid(badArgs, "path argument");
            foreach (var bad in new[]
            {
                "null", "[]", "{",
                basic.Replace("\"blockName\":\"Test\"", "\"blockName\":\"SECRET_CANARY\",\"blockName\":\"Other\""),
                basic.Replace("\"blockNumber\":1", "\"blockNumber\":1,\"number\":2"),
                basic.Replace("\"blockNumber\":1", "\"blockNumber\":0"),
                basic.Replace("\"blockNumber\":1", "\"blockNumber\":\"1\""),
                basic.Replace("\"inputs\":[]", "\"inputs\":null"),
                basic.Replace("\"inputs\":[]", "\"inputs\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"dataType\":\"Int\"}]"),
                basic.Replace("\"inputs\":[]", "\"inputs\":[{\"name\":\"x\",\"datatype\":\"Bool\"},{\"name\":\"X\",\"datatype\":\"Int\"}]"),
                basic.Replace("\"inputs\":[]", "\"inputs\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"comment\":\" \"}]"),
                basic.Replace("\"value\":\"TRUE\"", "\"value\":\"SECRET_CANARY\\r\""),
                basic.Replace("\"target\":\"#Ready\"", "\"target\":\"#Ready\\t\""),
                basic.Replace("\"structuredText\":", "\"structuredTextInnerXml\":\"<!DOCTYPE SECRET_CANARY>\",\"structuredText\":") }) await Invalid(Args(bad), "invalid structure");
            foreach (var field in new[] { "structuredTextInnerXml", "structuredTextXml", "sclInnerXml", "path", "outputPath" }) { var bad = JsonNode.Parse(basic)!.AsObject(); bad[field] = "SECRET_CANARY"; await Invalid(Args(bad.ToJsonString()), field); }
            var alias = JsonNode.Parse(basic)!.AsObject(); alias["name"] = alias["blockName"]!.DeepClone(); alias.Remove("blockName"); alias["number"] = alias["blockNumber"]!.DeepClone(); alias.Remove("blockNumber");
            check((await Invoke(alias.ToJsonString()))[Wire("Xml")]!.GetValue<string>() == xml, name + " aliases equivalent");
            var noArrays = JsonNode.Parse(basic)!.AsObject(); noArrays.Remove("inputs"); noArrays.Remove("outputs");
            if (fb) { await Invoke(noArrays.ToJsonString()); noArrays["inOuts"] = new JsonArray(new JsonObject { ["name"] = "io", ["dataType"] = "Bool" }); noArrays["staticMembers"] = new JsonArray(new JsonObject { ["name"] = "state", ["datatype"] = "Int" }); noArrays["temp"] = new JsonArray(new JsonObject { ["name"] = "scratch", ["datatype"] = "Int" }); var fbDoc = XDocument.Parse((await Invoke(noArrays.ToJsonString()))[Wire("Xml")]!.GetValue<string>()); check(fbDoc.Descendants(ns + "Member").Count() == 3, "FB sections composed"); noArrays["inouts"] = new JsonArray(); await Invalid(Args(noArrays.ToJsonString()), "FB array alias collision"); }
            else { await Invalid(Args(noArrays.ToJsonString()), "FC arrays required"); var reserved = JsonNode.Parse(basic)!.AsObject(); reserved["inputs"] = new JsonArray(new JsonObject { ["name"] = "ret_val", ["datatype"] = "Bool" }); await Invalid(Args(reserved.ToJsonString()), "reserved helper return name"); }
            var huge = JsonNode.Parse(basic)!.AsObject(); huge["blockName"] = new string('x', 4097); await Invalid(Args(huge.ToJsonString()), "long string");
            foreach (var invalidUnicode in new[] { "\\ud800", "\\udc00", "\\ud800x" })
            {
                await Invalid(Args(basic.Replace("Test", invalidUnicode)), "unpaired surrogate name");
                await Invalid(Args(basic.Replace("TRUE", invalidUnicode)), "unpaired surrogate nested literal");
                await Invalid(Args(basic.Replace("blockName", invalidUnicode)), "unpaired surrogate property");
            }
            var schema = tool.ProtocolTool.InputSchema;
            check(!schema.GetProperty("additionalProperties").GetBoolean() && schema.GetProperty("required").GetArrayLength() == 2, name + " closed schema");
            check(schema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum")[0].GetString() == "21", name + " exact release schema");
            huge = JsonNode.Parse(basic)!.AsObject();
            huge["inputs"] = new JsonArray(Enumerable.Range(0, 1001).Select(i => (JsonNode)new JsonObject { ["name"] = "n" + i, ["datatype"] = "Bool" }).ToArray());
            await Invalid(Args(huge.ToJsonString()), "member count bound");
            huge["inputs"] = new JsonArray(Enumerable.Range(0, 600).Select(i => (JsonNode)new JsonObject { ["name"] = "n" + i, ["datatype"] = "Bool" }).ToArray());
            huge["outputs"] = new JsonArray(Enumerable.Range(600, 401).Select(i => (JsonNode)new JsonObject { ["name"] = "n" + i, ["datatype"] = "Bool" }).ToArray());
            await Invalid(Args(huge.ToJsonString()), "combined member count bound");
            await Invalid(Args(new string(' ', OfflineCompositionBuilders.MaxJsonCharacters + 1)), "JSON size bound");
            huge = JsonNode.Parse(basic)!.AsObject();
            huge["structuredText"] = new JsonObject { ["operations"] = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)new JsonObject { ["op"] = "token", ["text"] = new string('&', 200) }).ToArray()) };
            await Invalid(Args(huge.ToJsonString()), "escaped expansion bound");
            var escaped = JsonNode.Parse(basic)!.AsObject();
            string literal = "</ConstantValue><SECRET_CANARY/>\n\t<&";
            escaped["structuredText"]!["operations"]![0]!["value"] = literal;
            var escapedDoc = XDocument.Parse((await Invoke(escaped.ToJsonString()))[Wire("Xml")]!.GetValue<string>());
            check(!escapedDoc.Descendants().Any(e => e.Name.LocalName == "SECRET_CANARY") && escapedDoc.Descendants(stns + "ConstantValue").Single().Value == literal, name + " escaped literal roundtrip and no injection");
            var cancelled = new CancellationToken(true); bool cancellation = false; try { await tool.InvokeAsync(Request(Args(basic)), cancelled); } catch (OperationCanceledException) { cancellation = true; } check(cancellation, name + " cancellation propagates");
        }
    }
}

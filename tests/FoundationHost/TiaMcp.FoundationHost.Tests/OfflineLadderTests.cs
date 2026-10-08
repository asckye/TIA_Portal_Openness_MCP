using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;

internal static class OfflineLadderTests
{
    internal static async Task Run(IMcpServer server, Action<bool, string> check)
    {
        const string basic = "{\"callName\":\"调用FC\",\"parameters\":[{\"name\":\"In\",\"section\":\"Input\",\"dataType\":\"Int\",\"sourceKind\":\"constant\",\"value\":\"42\"},{\"name\":\"Out\",\"section\":\"Output\",\"dataType\":\"Bool\",\"symbolPath\":[\"数据\",\"Ready\"]}]}";
        string Wrap(string call) => "{\"blockName\":\"Caller\",\"blockNumber\":42,\"networks\":[{\"callJson\":" + call + "}]}";
        string Wire(string key) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
        XNamespace ns = OfflineLadderBuilders.FlgNetNamespace;
        foreach (var tool in OfflineLadderTools.Create())
        {
            var name = tool.ProtocolTool.Name;
            bool compose = name == "ComposePlcLadFcBlockXml";
            var parameter = compose ? "ladFcBlockJson" : "flgNetJson";
            string Input(string call) => compose ? Wrap(call) : call;
            Dictionary<string, JsonElement> Args(string json, string release = "21") => new() { [parameter] = JsonSerializer.SerializeToElement(json), ["outputReleaseKey"] = JsonSerializer.SerializeToElement(release) };
            RequestContext<CallToolRequestParams> Request(Dictionary<string, JsonElement> args) => new(server) { Params = new() { Name = name, Arguments = args } };
            async Task<JsonNode> Invoke(string json)
            {
                var result = await tool.InvokeAsync(Request(Args(json)));
                check(result.IsError != true, name + " success");
                return JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
            }
            async Task Invalid(Dictionary<string, JsonElement> args, string reason)
            {
                Exception? error = null; try { await tool.InvokeAsync(Request(args)); } catch (Exception e) { error = e; }
                check(error is McpException m && m.ErrorCode == McpErrorCode.InvalidParams, name + " rejects " + reason);
                check(error != null && !error.ToString().Contains("SECRET_CANARY"), name + " sanitized " + reason);
            }
            var response = await Invoke(Input(basic));
            foreach (var key in new[] { "Message", "Meta", "Ok", "Data", "Errors", "Warnings", "OutputPath", "OutputFiles", "Xml" }) check(response.AsObject().ContainsKey(Wire(key)), name + " envelope " + key);
            foreach (var flag in new[] { "schemaValidated", "importValidated", "programSemanticsValidated" }) check(response[Wire("Meta")]![flag]!.GetValue<bool>() == false && response[Wire("Data")]![flag]!.GetValue<bool>() == false, name + " honest flags");
            check(response[Wire("OutputPath")] == null && response[Wire("OutputFiles")] == null && response[Wire("Meta")]!["outputReleaseKey"]!.GetValue<string>() == "21", name + " explicit candidate no files");
            var xml = response[Wire("Xml")]!.GetValue<string>();
            var doc = XDocument.Parse(xml);
            var flg = doc.Descendants(ns + "FlgNet").Single();
            var expected = FlgNetCallXmlBuilder.BuildFlgNet("调用FC", new[] { FlgNetCallParameter.Constant("In", "Input", "Int", "42"), FlgNetCallParameter.Global("Out", "Output", "Bool", "数据", "Ready") });
            check(XNode.DeepEquals(flg, XElement.Parse(expected.ToString())), name + " unchanged call helper structure");
            check(OfflineLadderBuilders.Build(name, "21", Input(basic))["Xml"]!.GetValue<string>() == xml, name + " deterministic");
            check(flg.Descendants(ns + "Wire").Count() == 3 && flg.Descendants(ns + "CallInfo").Single().Attribute("BlockType")!.Value == "FC", name + " FC wires");
            await Invoke(Input("{\"callName\":\"Empty\",\"parameters\":[]}"));
            var alias = basic.Replace("callName", "name").Replace("dataType", "type").Replace("sourceKind", "kind").Replace("\"value\"", "\"constantValue\"").Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbol\":\"数据.Ready\"");
            check((await Invoke(Input(alias)))[Wire("Xml")]!.GetValue<string>() == xml, name + " aliases preserve shape");
            string text = "SECRET_CANARY</ConstantValue><Injected/>\r\n\t<&中文😀 C:\\private\\file";
            var rich = JsonNode.Parse(basic)!; rich["parameters"]![0]!["value"] = text;
            var richDoc = XDocument.Parse((await Invoke(Input(rich.ToJsonString())))[Wire("Xml")]!.GetValue<string>());
            check(richDoc.Descendants(ns + "ConstantValue").Single().Value == text && !richDoc.Descendants().Any(e => e.Name.LocalName == "Injected"), name + " literal fidelity and no injection");
            foreach (var bad in new[] {
                "null", "[]", "{}", "{", basic.Replace("\"callName\":", "\"unknown\":0,\"callName\":"), basic.Replace("调用FC", "SECRET_CANARY/FC"),
                basic.Replace("\"callName\":", "\"callName\":\"SECRET_CANARY\",\"callName\":"), basic.Replace("\"callName\":", "\"name\":\"SECRET_CANARY\",\"callName\":"),
                basic.Replace("\"Input\"", "\"input\""), basic.Replace("\"Input\"", "\"InOut\""), basic.Replace("\"Input\"", "\"Return\""), basic.Replace("\"Input\"", "\"Output\""),
                basic.Replace("\"Int\"", "\"int\""), basic.Replace("\"Int\"", "\"SECRET_CANARY\""), basic.Replace("\"constant\"", "\"Constant\""), basic.Replace("\"constant\"", "\"unknown\""),
                basic.Replace("\"name\":\"In\"", "\"name\":\"en\""), basic.Replace("\"name\":\"In\"", "\"name\":\"ENO\""), basic.Replace("\"name\":\"Out\"", "\"name\":\"iN\""),
                basic.Replace("\"value\":\"42\"", "\"value\":42"), basic.Replace("\"value\":\"42\"", "\"value\":null"), basic.Replace("\"value\":\"42\"", "\"value\":\"42\",\"symbol\":\"x\""),
                basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbol\":\"数据..Ready\""), basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbolPath\":[\"数据\",null]"),
                basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbolPath\":[]"), basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbol\":\"C:\\\\SECRET_CANARY\""),
                basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbol\":\"http://SECRET_CANARY\""), basic.Replace("\"symbolPath\":[\"数据\",\"Ready\"]", "\"symbol\":\"\\\"数据\\\".Ready\""),
                basic.Replace("\"symbolPath\":", "\"symbol\":\"x\",\"symbolPath\":"), basic.Replace("\"section\":", "\"UId\":21,\"section\":"),
                basic.Replace("\"dataType\":", "\"datatype\":\"Bool\",\"dataType\":"), basic.Replace("\"parameters\":", "\"rawXml\":\"<!DOCTYPE SECRET_CANARY>\",\"parameters\":") }) await Invalid(Args(Input(bad)), "call contract");
            foreach (var unicode in new[] { "\\ud800", "\\udc00", "\\ud800x", "\\u0000" })
            {
                await Invalid(Args(Input(basic.Replace("调用FC", unicode))), "invalid Unicode name");
                await Invalid(Args(Input(basic.Replace("42", unicode))), "invalid Unicode literal");
                await Invalid(Args(Input(basic.Replace("callName", unicode))), "invalid Unicode key");
            }
            foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "V21", "21 ", "" }) await Invalid(Args(Input(basic), release), "unsupported output release");
            var badArgs = Args(Input(basic)); badArgs.Remove("outputReleaseKey"); await Invalid(badArgs, "missing release");
            badArgs = Args(Input(basic)); badArgs["outputReleaseKey"] = JsonSerializer.SerializeToElement(21); await Invalid(badArgs, "numeric release");
            badArgs = Args(Input(basic)); badArgs["path"] = JsonSerializer.SerializeToElement("SECRET_CANARY"); await Invalid(badArgs, "file argument");
            var huge = JsonNode.Parse(basic)!; huge["callName"] = new string('x', 4097); await Invalid(Args(Input(huge.ToJsonString())), "string size");
            huge = JsonNode.Parse(basic)!; huge["parameters"] = new JsonArray(Enumerable.Range(0,1001).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Bool", ["symbol"] = "x" }).ToArray()); await Invalid(Args(Input(huge.ToJsonString())), "parameter count");
            await Invalid(Args(new string(' ', OfflineCompositionBuilders.MaxJsonCharacters + 1)), "JSON size");
            huge = JsonNode.Parse(basic)!; huge["parameters"]![1]!["symbolPath"] = new JsonArray(Enumerable.Repeat("x",33).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()); await Invalid(Args(Input(huge.ToJsonString())), "symbol depth");
            var thousand = new JsonObject { ["callName"] = "Limit", ["parameters"] = new JsonArray(Enumerable.Range(0,1000).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Bool", ["symbol"] = "x" }).ToArray()) };
            var limitDoc = XDocument.Parse((await Invoke(Input(thousand.ToJsonString())))[Wire("Xml")]!.GetValue<string>());
            check(limitDoc.Descendants(ns + "Wire").Count() == 1001, name + " accepted parameter limit");
            var expansion = new JsonObject { ["callName"] = "Expansion", ["parameters"] = new JsonArray(Enumerable.Range(0,60).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Int", ["sourceKind"] = "constant", ["value"] = new string('&',4000) }).ToArray()) };
            var rawExpansion = expansion.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            check(Input(rawExpansion).Length < OfflineCompositionBuilders.MaxJsonCharacters, name + " output expansion test fits input bound");
            await Invalid(Args(Input(rawExpansion)), "output expansion bound");
            using (var outerUnicode = JsonDocument.Parse("\"\\ud800\"")) { badArgs = Args(Input(basic)); badArgs[parameter] = outerUnicode.RootElement.Clone(); await Invalid(badArgs, "outer Unicode argument"); }
            var schema = tool.ProtocolTool.InputSchema;
            check(!schema.GetProperty("additionalProperties").GetBoolean() && schema.GetProperty("required").GetArrayLength() == 2 && schema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum")[0].GetString() == "21", name + " closed schema");
            bool cancelled = false; try { await tool.InvokeAsync(Request(Args(Input(basic))), new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; } check(cancelled, name + " cancellation");
            if (compose)
            {
                var full = JsonNode.Parse(Wrap(basic))!;
                full["networks"]!.AsArray().Add(full["networks"]![0]!.DeepClone());
                full["commentZhCn"] = text; full["titleZhCn"] = text;
                full["inputs"] = new JsonArray(new JsonObject { ["name"] = "开始", ["datatype"] = "Bool", ["commentZhCn"] = text });
                var document = XDocument.Parse((await Invoke(full.ToJsonString()))[Wire("Xml")]!.GetValue<string>());
                check(document.Descendants(ns + "FlgNet").Count() == 2 && document.Descendants("SW.Blocks.CompileUnit").Count() == 2, "LAD multiple compile units");
                var ids = document.Descendants().Attributes("ID").Select(x => x.Value).ToArray(); check(ids.Distinct().Count() == ids.Length, "LAD document IDs unique");
                XNamespace ins = OfflineBlockCompositionBuilders.InterfaceNamespace;
                check(document.Descendants(ins + "Section").Single(x => (string?)x.Attribute("Name") == "Return").Element(ins + "Member")!.Attribute("Datatype")!.Value == "Void", "LAD fixed return interface");
                check(document.Descendants(ins + "MultiLanguageText").Single().Value == text && document.Descendants("Text").Count(x => x.Value == text) == 2, "LAD text fidelity");
                foreach (var field in new[] { "rawXml", "flgNet", "flgNetXml", "path", "outputPath" }) { var bad = JsonNode.Parse(Wrap(basic))!; bad[field] = "SECRET_CANARY"; await Invalid(Args(bad.ToJsonString()), "raw/path field"); }
                foreach (var bad in new[] { Wrap(basic).Replace("\"blockNumber\":42", "\"blockNumber\":0"), Wrap(basic).Replace("\"blockNumber\":42", "\"blockNumber\":42,\"number\":42"), "{\"blockName\":\"x\",\"blockNumber\":1,\"networks\":[]}", "{\"blockName\":\"x\",\"blockNumber\":1,\"networks\":[{\"callJson\":\"SECRET_CANARY\"}]}" }) await Invalid(Args(bad), "composer structure");
                foreach (var memberName in new[] { "ret_val", "EN/path" }) { var bad = JsonNode.Parse(Wrap(basic))!; bad["inputs"] = new JsonArray(new JsonObject { ["name"] = memberName, ["datatype"] = "Bool" }); await Invalid(Args(bad.ToJsonString()), "interface name"); }
                var many = JsonNode.Parse(Wrap(basic))!; many["networks"] = new JsonArray(Enumerable.Range(0,65).Select(_ => (JsonNode)new JsonObject { ["callJson"] = JsonNode.Parse(basic) }).ToArray()); await Invalid(Args(many.ToJsonString()), "network count");
                var combined = JsonNode.Parse(Wrap(basic))!; combined["networks"] = new JsonArray(new JsonObject { ["callJson"] = thousand.DeepClone() }, new JsonObject { ["callJson"] = JsonNode.Parse(basic) }); await Invalid(Args(combined.ToJsonString()), "combined parameter count");
                var duplicate = JsonNode.Parse(Wrap(basic))!; duplicate["inputs"] = new JsonArray(new JsonObject { ["name"] = "Ready", ["datatype"] = "Bool" }); duplicate["outputs"] = new JsonArray(new JsonObject { ["name"] = "READY", ["datatype"] = "Bool" }); await Invalid(Args(duplicate.ToJsonString()), "cross-section duplicate");
                var nestedRaw = JsonNode.Parse(Wrap(basic))!; nestedRaw["networks"]![0]!["flgNetXml"] = "SECRET_CANARY"; await Invalid(Args(nestedRaw.ToJsonString()), "nested raw XML");
                many = JsonNode.Parse(Wrap(basic))!; many["inputs"] = new JsonArray(Enumerable.Range(0,1001).Select(i => (JsonNode)new JsonObject { ["name"] = "n" + i, ["datatype"] = "Bool" }).ToArray()); await Invalid(Args(many.ToJsonString()), "member bound");
            }
        }
    }
}

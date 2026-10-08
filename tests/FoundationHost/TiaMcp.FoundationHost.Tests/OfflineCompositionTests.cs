using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;

internal static class OfflineCompositionTests
{
    internal static async Task Run(IMcpServer server, Action<bool, string> check)
    {
        const string db = "{\"dbName\":\"DB<&中文\",\"dbNumber\":42,\"staticMembers\":[{\"name\":\"Ready\",\"datatype\":\"Bool\",\"externalWritable\":false,\"commentZhCn\":\"<tag>&中文\",\"startValue\":\"TRUE\"},{\"name\":\"Counter\",\"datatype\":\"Int\"}]}";
        const string st = "{\"firstUid\":100,\"operations\":[{\"op\":\"if\",\"condition\":\"#Ready\"},{\"op\":\"assignment\",\"target\":\"#Counter\",\"value\":\"1<&\",\"indent\":2},{\"op\":\"else\"},{\"op\":\"assignment\",\"target\":\"#Counter\",\"source\":\"#Other\"},{\"op\":\"endif\"}]}";
        string Wire(string key) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
        var tools = OfflineCompositionTools.Create();
        foreach (var spec in new[] { (Name:"BuildPlcGlobalDbXml", Arg:"globalDbJson", Json:db), (Name:"BuildStructuredTextXml", Arg:"structuredTextJson", Json:st) })
        {
            var tool = tools.Single(t => t.ProtocolTool.Name == spec.Name);
            var schema = tool.ProtocolTool.InputSchema;
            check(schema.GetProperty("required").GetArrayLength() == 2 && !schema.GetProperty("additionalProperties").GetBoolean(), spec.Name + " closed explicit schema");
            check(schema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum").GetArrayLength() == (spec.Name == "BuildPlcGlobalDbXml" ? 8 : 1), spec.Name + " explicit supported output versions");
            RequestContext<CallToolRequestParams> Request(Dictionary<string, JsonElement> arguments) => new(server) { Params = new() { Name = spec.Name, Arguments = arguments } };
            Dictionary<string, JsonElement> Args(string json) => new() { [spec.Arg] = JsonSerializer.SerializeToElement(json), ["outputReleaseKey"] = JsonSerializer.SerializeToElement("21") };
            async Task<JsonNode> Invoke(Dictionary<string, JsonElement> args)
            {
                var result = await tool.InvokeAsync(Request(args));
                check(result.IsError != true, spec.Name + " SDK invocation succeeds");
                return JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
            }
            async Task Invalid(Dictionary<string, JsonElement> args, string reason)
            {
                Exception? error = null;
                try { await tool.InvokeAsync(Request(args)); } catch (Exception ex) { error = ex; }
                check(error is McpException m && m.ErrorCode == McpErrorCode.InvalidParams, spec.Name + " rejects " + reason);
                check(error != null && !error.ToString().Contains("SECRET_CANARY", StringComparison.Ordinal), spec.Name + " no input echo: " + reason);
            }
            var payload = await Invoke(Args(spec.Json));
            foreach (var key in new[] { "Message", "Meta", "Ok", "Data", "Errors", "Warnings", "OutputPath", "OutputFiles", "Xml" })
                check(payload.AsObject().ContainsKey(Wire(key)), spec.Name + " response member " + key);
            var xml = payload[Wire("Xml")]!.GetValue<string>();
            check(xml == payload[Wire("Data")]!["xml"]!.GetValue<string>(), spec.Name + " XML envelope identity");
            check(payload[Wire("OutputPath")] == null && payload[Wire("OutputFiles")] == null, spec.Name + " no file outputs");
            foreach (var flag in new[] { "schemaValidated", "importValidated", "programSemanticsValidated" })
                check(payload[Wire("Meta")]![flag]!.GetValue<bool>() == false && payload[Wire("Data")]![flag]!.GetValue<bool>() == false, spec.Name + " honest " + flag);
            check(payload[Wire("Warnings")]![0]!.GetValue<string>().Contains("Not import-ready"), spec.Name + " visible caution");
            check(xml == OfflineCompositionBuilders.Build(spec.Name, "21", spec.Json)["Xml"]!.GetValue<string>(), spec.Name + " deterministic");
            foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "V21", "21 ", "22", "" })
            {
                var args = Args(spec.Json); args["outputReleaseKey"] = JsonSerializer.SerializeToElement(release);
                if (spec.Name == "BuildPlcGlobalDbXml" && PlcDeclarationXmlFormat.ReleaseKeys.Contains(release))
                {
                    var generated = await Invoke(args);
                    check(generated[Wire("Data")]!["outputReleaseKey"]!.GetValue<string>() == release, spec.Name + " MCP accepts target format " + release);
                    continue;
                }
                await Invalid(args, "unsupported release " + release);
            }
            var missing = Args(spec.Json); missing.Remove("outputReleaseKey"); await Invalid(missing, "missing release");
            missing = Args(spec.Json); missing.Remove(spec.Arg); await Invalid(missing, "missing JSON");
            var wrong = Args(spec.Json); wrong[spec.Arg] = JsonSerializer.SerializeToElement(new { x = 1 }); await Invalid(wrong, "non-string JSON");
            wrong = Args(spec.Json); wrong["unknownSECRET_CANARY"] = JsonSerializer.SerializeToElement(1); await Invalid(wrong, "unknown argument");
            foreach (var json in new[] { "", " ", "[]", "null", "{", new string('x', 262145), "{\"SECRET_CANARY\":1}" }) await Invalid(Args(json), "invalid JSON/root");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            bool canceled = false;
            try { await tool.InvokeAsync(Request(Args(spec.Json)), cts.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, spec.Name + " cancellation honored");

            if (spec.Name == "BuildPlcGlobalDbXml")
            {
                var parsed = XDocument.Parse(xml);
                XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
                check((string?)parsed.Root!.Element("Engineering")!.Attribute("version") == "V21", "GlobalDB marker retained");
                check(parsed.Descendants(ns + "Member").Select(x => (string?)x.Attribute("Name")).SequenceEqual(new[] { "Ready", "Counter" }), "GlobalDB member order and namespace");
                check(parsed.Descendants(ns + "StartValue").Single().Value == "TRUE", "GlobalDB start value retained");
                check(parsed.Descendants(ns + "Member").First().Elements().Select(x => x.Name.LocalName).SequenceEqual(new[] { "AttributeList", "StartValue", "Comment" }), "GlobalDB documented child order with combined optional fields");
                check(parsed.Descendants(ns + "MultiLanguageText").Single().Value == "<tag>&中文", "GlobalDB XML injection escaped");
                check(parsed.Descendants(ns + "BooleanAttribute").Single().Value == "false", "GlobalDB explicit false distinct from omission");
                check(xml == PlcGlobalDbXmlBuilder.BuildXml("DB<&中文", 42, new[] { new PlcDbMemberDefinition("Ready", "Bool", false, "<tag>&中文", "TRUE"), new PlcDbMemberDefinition("Counter", "Int", null, "", "") }), "GlobalDB corrected helper byte equality");
                foreach (var invalid in new[]
                {
                    "{\"dbName\":\"A\",\"dbName\":\"SECRET_CANARY\",\"dbNumber\":1,\"members\":[{\"name\":\"x\",\"datatype\":\"Int\"}]}",
                    db.Replace("\"dbNumber\":42", "\"dbNumber\":42,\"number\":42"), db.Replace("\"dbNumber\":42", "\"dbNumber\":0"), db.Replace("\"dbNumber\":42", "\"dbNumber\":1.5"),
                    db.Replace("\"dbNumber\":42", "\"dbNumber\":\"42\""), db.Replace("\"dbNumber\":42", "\"dbNumber\":2147483648"),
                    db.Replace("\"externalWritable\":false", "\"externalWritable\":null"), db.Replace("\"externalWritable\":false", "\"externalWritable\":\"true\""),
                    db.Replace("\"Counter\"", "\"Ready\""), db.Replace("\"Int\"", "\"\""), db.Replace("\"Int\"", "\"Int\",\"dataType\":\"Int\""),
                    db.Replace("\"startValue\":\"TRUE\"", "\"startValue\":true"), db.Replace("\"Ready\"", "\"SECRET_CANARY\\u0000\""),
                    "{\"dbName\":\"D\",\"dbNumber\":1,\"members\":[]}", "{\"dbName\":\"D\",\"dbNumber\":1,\"members\":[null]}"
                }) await Invalid(Args(invalid), "GlobalDB strict field/type/member guard");
                var inner = Args(db); inner["innerOnly"] = JsonSerializer.SerializeToElement(false); await Invalid(inner, "GlobalDB innerOnly unsupported");
                var aliases = "{\"name\":\"D\",\"number\":1,\"members\":[{\"name\":\"x\",\"dataType\":\"Int\",\"comment\":\"ok\"}]}";
                await Invoke(Args(aliases));
                foreach (var key in new[] { "dbName", "commentZhCn", "startValue" })
                {
                    var value = JsonNode.Parse(db)!.AsObject();
                    if (key == "dbName") value[key] = "SECRET_CANARY\rvalue";
                    else value["staticMembers"]![0]![key] = "SECRET_CANARY\rvalue";
                    await Invalid(Args(value.ToJsonString()), "GlobalDB XML-normalized element carriage return");
                }
                foreach (var key in new[] { "commentZhCn", "startValue" })
                {
                    var value = JsonNode.Parse(db)!.AsObject();
                    value["staticMembers"]![0]![key] = " \n\t";
                    await Invalid(Args(value.ToJsonString()), "GlobalDB whitespace-only optional element");
                }
                var exactDb = JsonNode.Parse(db)!.AsObject();
                const string exactValue = "a\n\t<& 中文 b";
                exactDb["dbName"] = exactValue;
                exactDb["staticMembers"]![0]!["name"] = "a\r\n\t<&中文";
                exactDb["staticMembers"]![0]!["datatype"] = "a\r\n\t<&中文";
                exactDb["staticMembers"]![0]!["commentZhCn"] = exactValue;
                exactDb["staticMembers"]![0]!["startValue"] = exactValue;
                var exactResult = await Invoke(Args(exactDb.ToJsonString()));
                var exactXml = XDocument.Parse(exactResult[Wire("Xml")]!.GetValue<string>());
                check(exactXml.Descendants("Name").Single().Value == exactValue, "Accepted GlobalDB name parsed value is exact");
                check(exactXml.Descendants(ns + "MultiLanguageText").Single().Value == exactValue && exactXml.Descendants(ns + "StartValue").Single().Value == exactValue, "Accepted GlobalDB element LF/tab values remain exact");
                check((string?)exactXml.Descendants(ns + "Member").First().Attribute("Name") == "a\r\n\t<&中文" && (string?)exactXml.Descendants(ns + "Member").First().Attribute("Datatype") == "a\r\n\t<&中文", "GlobalDB writer entitizes attribute whitespace exactly");

            }
            else
            {
                XNamespace ns = OfflineCompositionBuilders.StructuredTextNamespace;
                var parsed = XDocument.Parse(xml);
                check(parsed.Root!.Name == ns + "StructuredText", "StructuredText namespace retained");
                check(parsed.Descendants(ns + "ConstantValue").Single().Value == "1<&", "StructuredText literal escaped");
                var uids = parsed.Descendants().Attributes("UId").Select(x => int.Parse(x.Value)).ToArray();
                check(uids.First() == 100 && uids.Distinct().Count() == uids.Length && uids.SequenceEqual(Enumerable.Range(100, uids.Length)), "StructuredText UID determinism and uniqueness");
                var expected = new StructuredTextXmlBuilder(100).IfHeader("#Ready").Assignment("#Counter", "1<&", 2).ElseLine().AssignFromSymbol("#Counter", "#Other").EndIf();
                check(xml == expected.BuildStructuredTextXml(), "StructuredText unchanged helper byte equality");
                var inner = Args(st); inner["innerOnly"] = JsonSerializer.SerializeToElement(true);
                var innerPayload = await Invoke(inner);
                check(innerPayload[Wire("Xml")]!.GetValue<string>() == expected.BuildInnerXml(), "StructuredText innerOnly exact helper bytes");
                inner["innerOnly"] = JsonSerializer.SerializeToElement("true"); await Invalid(inner, "innerOnly strict boolean");
                foreach (var invalid in new[]
                {
                    "{\"operations\":[]}", "{\"operations\":[null]}", "{\"firstUid\":0,\"operations\":[{\"op\":\"newline\"}]}",
                    "{\"firstUid\":2147483647,\"operations\":[{\"op\":\"newline\"}]}", "{\"operations\":[{\"op\":\"blank\",\"count\":0}]}",
                    "{\"operations\":[{\"op\":\"blank\",\"count\":4097}]}", "{\"operations\":[{\"op\":\"newline\",\"indent\":1}]}",
                    "{\"operations\":[{\"op\":\"token\",\"text\":\"x\",\"indent\":-1}]}", "{\"operations\":[{\"op\":\"SECRET_CANARY\"}]}",
                    "{\"operations\":[{\"op\":\"newline\",\"kind\":\"newline\"}]}", "{\"operations\":[{\"op\":\"assignment\",\"target\":\"x\",\"value\":\"1\",\"source\":\"y\"}]}",
                    "{\"operations\":[{\"op\":\"assignment\",\"target\":\"x\"}]}", "{\"operations\":[{\"op\":\"assignment\",\"target\":\"SECRET_CANARY + x\",\"value\":\"1\"}]}",
                    "{\"operations\":[{\"op\":\"symbol\",\"name\":\" a\"}]}", "{\"operations\":[{\"op\":\"symbol\",\"name\":\"\\\"DB\\\".a\"}]}",
                    "{\"operations\":[{\"op\":\"token\",\"text\":\"SECRET_CANARY\\u0000\"}]}", "{\"operations\":[{\"op\":\"line\",\"items\":[{\"sym\":\"x\",\"raw\":\";\"}]}]}",
                    "{\"operations\":[{\"op\":\"line\",\"items\":[{\"token\":false}]}]}", "{\"operations\":[{\"op\":\"line\",\"items\":[]}]}"
                }) await Invalid(Args(invalid), "StructuredText strict field/type/operation guard");
                foreach (var operation in new[]
                {
                    new { op = "symbol", name = "#a.b" }, new { op = "symbol", name = "\"DB.a\"" }, new { op = "global", name = "DB.a" }, new { op = "local", name = "a" }
                }) await Invoke(Args(JsonSerializer.Serialize(new { operations = new[] { operation } })));
                const string allOps = "{\"operations\":[{\"kind\":\"ifheader\",\"conditionVariable\":\"a\"},{\"type\":\"elseif\",\"variable\":\"b\"},{\"op\":\"else\",\"indent\":1},{\"op\":\"end_if\"},{\"op\":\"token\",\"text\":\"<Injected/>\"},{\"op\":\"blank\",\"count\":2},{\"op\":\"new_line\"},{\"op\":\"literal\",\"literalValue\":\"TRUE\"},{\"op\":\"line\",\"items\":[{\"sym\":\"a\"},{\"token\":\":=\"},{\"lit\":\"1\"},{\"raw\":\";\"}]}]}";
                var all = await Invoke(Args(allOps));
                check(!XDocument.Parse(all[Wire("Xml")]!.GetValue<string>()).Descendants().Any(x => x.Name.LocalName == "Injected"), "Token XML injection remains text");
                foreach (var whitespace in new[] { "\r", "\n", "\t" })
                {
                    var value = "SECRET_CANARY" + whitespace + "value";
                    foreach (var operation in new object[]
                    {
                        new { op = "token", text = value }, new { op = "global", name = value },
                        new { op = "local", name = value }, new { op = "symbol", name = "\"" + value + "\"" },
                        new { op = "if", condition = value }, new { op = "assignment", target = "x", source = value },
                        new { op = "assignment", target = value, value = "1" },
                        new { op = "line", items = new[] { new { raw = value } } },
                        new { op = "line", items = new[] { new { token = value } } },
                        new { op = "line", items = new[] { new { sym = value } } }
                    }) await Invalid(Args(JsonSerializer.Serialize(new { operations = new[] { operation } })), "XML-normalized attribute whitespace");
                }
                foreach (var operation in new object[]
                {
                    new { op = "literal", value = "SECRET_CANARY\rvalue" },
                    new { op = "assignment", target = "x", value = "SECRET_CANARY\rvalue" },
                    new { op = "line", items = new[] { new { lit = "SECRET_CANARY\rvalue" } } }
                }) await Invalid(Args(JsonSerializer.Serialize(new { operations = new[] { operation } })), "XML-normalized literal carriage return");
                const string attributeValue = "a <&\"' 中文 b";
                const string literalValue = "a\n\t<&\"' 中文 b";
                var preserved = await Invoke(Args(JsonSerializer.Serialize(new { operations = new object[]
                {
                    new { op = "token", text = attributeValue }, new { op = "global", name = "a <& 中文 b" },
                    new { op = "literal", value = literalValue },
                    new { op = "assignment", target = "x", value = literalValue },
                    new { op = "line", items = new[] { new { lit = literalValue } } }
                } })));
                var preservedXml = XDocument.Parse(preserved[Wire("Xml")]!.GetValue<string>());
                check((string?)preservedXml.Descendants(ns + "Token").First().Attribute("Text") == attributeValue, "Accepted ST attribute parsed value is exact");
                check((string?)preservedXml.Descendants(ns + "Component").First().Attribute("Name") == "a <& 中文 b", "Accepted ST global parsed name is exact");
                check(preservedXml.Descendants(ns + "ConstantValue").All(x => x.Value == literalValue), "Accepted ST literal LF/tab and special characters remain exact");
                var oversizedString = JsonSerializer.Serialize(new { operations = new[] { new { op = "token", text = new string('x', 4097) } } });
                await Invalid(Args(oversizedString), "oversized string");
                await Invalid(Args(JsonSerializer.Serialize(new { operations = Enumerable.Range(0, 1001).Select(_ => new { op = "newline" }) })), "oversized operations");
                // A short input may expand significantly; output cap is independent of input length.
                await Invalid(Args(JsonSerializer.Serialize(new { operations = Enumerable.Range(0, 230).Select(_ => new { op = "symbol", name = string.Join('.', Enumerable.Repeat("a", 200)) }) })), "expanded XML limit");
            }
        }
    }
}

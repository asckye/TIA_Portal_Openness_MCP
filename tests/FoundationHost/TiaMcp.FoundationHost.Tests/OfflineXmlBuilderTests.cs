using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;

internal static class OfflineXmlBuilderTests
{
    internal static async Task Run(IMcpServer server, Action<bool,string> check)
    {
        DeclarationXmlFormatTests.Run(check);
        string Wire(string key) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
        foreach(var spec in new[]
        {
            (Name:"BuildPlcUdtXml", Arg:"udtJson", Json:"{\"name\":\"UDT<&\\\"\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\",\"externalWritable\":true,\"comment\":\"<tag>&中文\"}]}"),
            (Name:"BuildPlcTagTableXml", Arg:"tagTableJson", Json:"{\"tableName\":\"Tags<&\",\"tags\":[{\"name\":\"Run\",\"dataTypeName\":\"Bool\",\"logicalAddress\":\"%Q0.0\"}]}")
        })
        {
            var tool = OfflineXmlTools.Create().Single(t=>t.ProtocolTool.Name==spec.Name);
            var schema = tool.ProtocolTool.InputSchema;
            check(schema.GetProperty("required").GetArrayLength()==2, spec.Name+" requires explicit output format");
            check(schema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum").GetArrayLength()==(spec.Name=="BuildPlcUdtXml"?8:1), spec.Name+" explicit supported output versions");
            check(!schema.GetProperty("additionalProperties").GetBoolean(),spec.Name+" closed schema");
            RequestContext<CallToolRequestParams> Request(Dictionary<string,JsonElement> args) => new(server) { Params=new() {Name=spec.Name,Arguments=args} };
            var args = new Dictionary<string,JsonElement>
            {
                [spec.Arg]=JsonSerializer.SerializeToElement(spec.Json),
                ["outputReleaseKey"]=JsonSerializer.SerializeToElement("21")
            };
            var response = await tool.InvokeAsync(Request(args));
            var payload = JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!;
            check(response.IsError != true && payload[Wire("Ok")]!.GetValue<bool>(),spec.Name+" real SDK invocation constructs XML");
            foreach(string key in new[]{"Message","Meta","Ok","Data","Errors","Warnings","OutputPath","OutputFiles","Xml"})
                check(payload.AsObject().ContainsKey(Wire(key)),spec.Name+" V17 response member "+key);
            var xml = payload[Wire("Xml")]!.GetValue<string>();
            check(xml==payload[Wire("Data")]!["xml"]!.GetValue<string>(),spec.Name+" same XML at both contract surfaces");
            check(payload[Wire("OutputPath")]==null && payload[Wire("OutputFiles")]==null,spec.Name+" no output file claims");
            check(!payload[Wire("Meta")]!["schemaValidated"]!.GetValue<bool>() && !payload[Wire("Meta")]!["importValidated"]!.GetValue<bool>(),spec.Name+" explicit unvalidated status");
            check(payload[Wire("Warnings")]![0]!.GetValue<string>().Contains("Not import-ready"),spec.Name+" visible validation warning");
            var doc=XDocument.Parse(xml);
            check((string?)doc.Root!.Element("Engineering")!.Attribute("version")=="V21",spec.Name+" actual version marker retained");
            check(doc.Root.Element("AttributeList")==null,spec.Name+" expected object hierarchy");
            var again=OfflineXmlBuilders.Build(spec.Name,"21",spec.Json);
            check(again["Xml"]!.GetValue<string>()==xml,spec.Name+" deterministic construction");

            async Task Invalid(Dictionary<string,JsonElement> values,string why)
            {
                try { await tool.InvokeAsync(Request(values)); throw new Exception("Accepted "+why); }
                catch(McpException ex) { check(ex.ErrorCode==McpErrorCode.InvalidParams,spec.Name+" rejects "+why); }
            }
            foreach(var version in new[]{"14sp1","15.1","16","17","18","19","20","V21","21 ","","22"})
            {
                var bad=new Dictionary<string,JsonElement>(args) { ["outputReleaseKey"]=JsonSerializer.SerializeToElement(version) };
                if (spec.Name=="BuildPlcUdtXml" && TiaMcpServer.ModelContextProtocol.PlcDeclarationXmlFormat.ReleaseKeys.Contains(version))
                {
                    var generated = await tool.InvokeAsync(Request(bad));
                    var generatedPayload = JsonNode.Parse(((TextContentBlock)generated.Content.Single()).Text)!;
                    check(generatedPayload[Wire("Data")]!["outputReleaseKey"]!.GetValue<string>() == version, spec.Name+" MCP accepts target format "+version);
                    continue;
                }
                await Invalid(bad,"output "+version);
            }
            foreach(var key in args.Keys)
            {
                var bad=new Dictionary<string,JsonElement>(args); bad.Remove(key);
                await Invalid(bad,"missing "+key);
                bad=new(args) { [key]=JsonSerializer.SerializeToElement(21) };
                await Invalid(bad,"non-string "+key);
            }
            await Invalid(new(args) { ["dryRun"]=JsonSerializer.SerializeToElement(false) },"unknown execution switch");
            foreach(var malformed in new[]{"", "null", "[]", "true", "{", "{\"name\":\"A\",\"name\":\"B\"}", new string('x',OfflineXmlBuilders.MaxJsonCharacters+1)})
                await Invalid(new(args) { [spec.Arg]=JsonSerializer.SerializeToElement(malformed) },"malformed JSON");
            const string secret = "SECRET-example-token-9f781";
            var sensitiveInputs = spec.Name == "BuildPlcUdtXml"
                ? new[] { "{\"name\":\"A\",\"members\":[],\"" + secret + "\":true}",
                    "{\"name\":\"A\",\"members\":[{\"name\":\"" + secret + "\",\"datatype\":\"Bool\"},{\"name\":\"" + secret + "\",\"datatype\":\"Bool\"}]}" }
                : new[] { "{\"tableName\":\"A\",\"tags\":[],\"" + secret + "\":true}",
                    "{\"tableName\":\"A\",\"tags\":[{\"name\":\"" + secret + "\",\"datatype\":\"Bool\",\"address\":\"bad\"}]}",
                    "{\"tableName\":\"A\",\"tags\":[{\"name\":\"" + secret + "\",\"datatype\":\"Bool\",\"address\":\"%I0.0\"},{\"name\":\"" + secret + "\",\"datatype\":\"Bool\",\"address\":\"%I0.1\"}]}" };
            foreach(var input in sensitiveInputs)
            {
                try { await tool.InvokeAsync(Request(new(args) { [spec.Arg]=JsonSerializer.SerializeToElement(input) })); throw new Exception("Sensitive invalid input accepted"); }
                catch(McpException ex)
                {
                    check(ex.ErrorCode==McpErrorCode.InvalidParams && !ex.ToString().Contains(secret) && ex.InnerException==null && ex.Message.Length<256,
                        spec.Name+" invalid-input response and exception chain are bounded and sanitized");
                }
            }
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            try { await tool.InvokeAsync(Request(args),cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { check(true,spec.Name+" cancellation"); }
        }
        void Bad(string name,string json,string why)
        {
            try { OfflineXmlBuilders.Build(name,"21",json); throw new Exception("Accepted "+why); }
            catch(Exception ex) when(ex is ArgumentException or JsonException or System.Xml.XmlException) { check(true,why); }
        }
        const string U="BuildPlcUdtXml", T="BuildPlcTagTableXml";
        foreach(var json in new[]
        {
            "{\"name\":\"A\",\"members\":[]}",
            "{\"name\":\"A\",\"members\":[null]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"startValue\":\"false\"}]}",
            "{\"name\":\"A\",\"udtName\":\"B\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":12,\"datatype\":\"Bool\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"externalWritable\":\"false\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\"},{\"name\":\"X\",\"datatype\":\"Bool\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"comment\":\"\\u0000\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"comment\":null}]}",
            "{\"Name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\"}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"members\":[]}]}",
            "{\"name\":\"A\",\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"datatype\":\"Int\"}]}"
        }) Bad(U,json,"UDT strict malformed/unsupported property");
        foreach(var json in new[]
        {
            "{\"tableName\":\"A\",\"tags\":[]}",
            "{\"tableName\":\"A\",\"tags\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"address\":\"I0.0\"}]}",
            "{\"tableName\":\"A\",\"tags\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"address\":\"%I0.0\",\"logicalAddress\":\"%I0.1\"}]}",
            "{\"tableName\":\"A\",\"tags\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"address\":\"%I0.0\",\"comment\":\"ignored?\"}]}",
            "{\"tableName\":\"A\",\"tags\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"address\":\"%I0.0\"},{\"name\":\"X\",\"datatype\":\"Bool\",\"address\":\"%I0.1\"}]}"
        }) Bad(T,json,"Tag strict malformed/unsupported property");
        JsonObject Udt(int count,string comment) => new() { ["name"]="A",["members"]=new JsonArray(Enumerable.Range(0,count).Select(i=>(JsonNode)new JsonObject { ["name"]="M"+i,["datatype"]="Bool",["comment"]=comment }).ToArray()) };
        Bad(U,Udt(1001,"").ToJsonString(),"member count cap");
        Bad(U,Udt(1,new string('a',4097)).ToJsonString(),"string cap");
        // Use literal ampersands to stay within the input budget but exceed the XML budget after escaping.
        var large=Udt(60,new string('&',4000)).ToJsonString(new JsonSerializerOptions { Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        check(large.Length < OfflineXmlBuilders.MaxJsonCharacters,"output amplification test is within input cap");
        Bad(U,large,"XML amplification cap");
        var bounded=OfflineXmlBuilders.Build(U,"21",Udt(1000,"").ToJsonString());
        check(XDocument.Parse(bounded["Xml"]!.GetValue<string>()).Descendants().Count(x=>x.Name.LocalName=="Member")==1000,"1000 member boundary succeeds");
        var malicious="<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>&x;";
        var escaped=OfflineXmlBuilders.Build(U,"21",Udt(1,malicious).ToJsonString());
        var tree=XDocument.Parse(escaped["Xml"]!.GetValue<string>());
        check(tree.DocumentType==null && tree.Descendants().Single(x=>x.Name.LocalName=="MultiLanguageText").Value==malicious,"DTD/entity strings remain escaped data");
        var aliases=OfflineXmlBuilders.Build(U,"21","{\"udtName\":\"Alias\",\"members\":[{\"name\":\"x\",\"dataType\":\"Bool\",\"commentZh\":\"ok\"}]}");
        check(aliases["Data"]!["summary"]!["udtName"]!.GetValue<string>()=="Alias","documented UDT aliases work");
        var tags=OfflineXmlBuilders.Build(T,"21","{\"name\":\"Alias\",\"tags\":[{\"name\":\"x\",\"dataType\":\"Bool\",\"address\":\"%I0.0\"}]}");
        check(tags["Data"]!["summary"]!["tableName"]!.GetValue<string>()=="Alias","documented tag aliases work");
    }
}

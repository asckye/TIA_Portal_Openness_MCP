using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class SecurityContractChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // Only the SDK request context is needed; no transport or TIA session is opened.
    private sealed class RequestServer : RealProxy
    {
        internal RequestServer() : base(typeof(IMcpServer)) { }
        public override IMessage Invoke(IMessage message) => new ReturnMessage(null, null, 0, null, (IMethodCallMessage)message);
    }

    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var surface = EngineSurface.For(server);
        var boundary = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        string release = (string)boundary.GetProperty("ReleaseKey", All)!.GetValue(null)!;
        var create = boundary.GetMethod("CreateTool", All)!;
        var schemaMethod = boundary.GetMethod("ToolInputSchema", All)!;
        var validate = boundary.GetMethod("ValidateV4Arguments", All)!;
        JsonElement Schema(string name) => (JsonElement)schemaMethod.Invoke(null, new object[] { name, surface.Tool(name) })!;
        CallToolResult Invoke(string name, string arguments)
        {
            var tool = (McpServerTool)create.Invoke(null, new object[] { name, surface.Tool(name) })!;
            tool = (McpServerTool)Activator.CreateInstance(server.GetType("TiaMcpServer.ModelContextProtocol.VersionPolicyTool", true)!,
                All, null, new object[] { tool }, null)!;
            var request = new RequestContext<CallToolRequestParams>((IMcpServer)new RequestServer().GetTransparentProxy()) {
                Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments) }
            };
            return tool.InvokeAsync(request).AsTask().GetAwaiter().GetResult();
        }
        JsonObject Body(CallToolResult result)
        {
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
            check(JsonNode.DeepEquals(body, result.StructuredContent) && (int?)body["schemaVersion"] == 4
                && result.IsError == !(bool)body["ok"]!, "P6-14 text, structured content and error flag agree");
            return body;
        }
        void Rejected(string name, string arguments, string code = "INVALID_ARGUMENT", bool batch = true)
        {
            var calls = new List<(string Name, string Arguments)> {
                (name, arguments),
                ("CallTool", "{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}")
            };
            if (batch) calls.Add(("PreviewToolBatch", "{\"operations\":[{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}],\"expectedProject\":\"Fixture\"}"));
            foreach (var call in calls)
            {
                if (EngineSurface.IsHostTool(server, call.Name)) continue;
                var body = Body(Invoke(call.Name, call.Arguments));
                check((string?)body["error"]?["code"] == code && (string?)body["meta"]?["outcome"] == "rejected-before-operation"
                    && (string?)body["meta"]?["execution"] == "not-started", "P6-14 " + name + " rejected through " + call.Name + ": " + code
                    + " (actual " + (string?)body["error"]?["code"] + "/" + (string?)body["meta"]?["outcome"] + "/" + (string?)body["meta"]?["execution"] + ")");
            }
        }
        const string certificate = "{\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"action\":\"create\",\"usage\":\"Tls\",\"subjectAlternativeNames\":";
        foreach (string names in new[] { "null", "\"[]\"", "[\"host\"]", "[null]", "[{\"type\":\"dns\",\"value\":\"host\"}]",
            "[{\"type\":\"Dns\",\"value\":\" \"}]", "[{\"type\":\"Dns\",\"value\":1}]", "[{\"type\":\"Dns\",\"value\":\"host\",\"extra\":true}]",
            "[{\"type\":\"Dns\",\"value\":\"host\",\"value\":\"other\"}]", "[{\"type\":\"Dns\",\"value\":\"host\"},{\"type\":\"Dns\",\"value\":\"HOST\"}]" })
            Rejected("ManagePlcCertificate", certificate + names + "}");
        Rejected("ManagePlcCertificate", certificate + JsonSerializer.Serialize(Enumerable.Range(0, 65).Select(i => new { type = "Dns", value = "host" + i })) + "}", "LIMIT_EXCEEDED");
        Rejected("ManagePlcCertificate", certificate + JsonSerializer.Serialize(new[] { new { type = "IP", value = new string('a', 256) } }) + "}", "LIMIT_EXCEEDED");
        var san = Schema("ManagePlcCertificate").GetProperty("properties").GetProperty("subjectAlternativeNames");
        check(san.GetProperty("maxItems").GetInt32() == 64 && !san.GetProperty("items").GetProperty("additionalProperties").GetBoolean()
            && san.GetProperty("items").GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32() == 255,
            "P6-14 certificate schema advertises the closed bounded SAN array");
        check(surface.Tool("ManagePlcCertificate").GetParameters().Single(p => p.Name == "subjectAlternativeNames").ParameterType.GetElementType()!.FullName
            == "TiaMcp.Logic.V4.Domain.SubjectAlternativeName", "P6-14 SAN uses the named domain DTO");

        var function = surface.Tool("ManageSafetyFunction");
        check(function.GetParameters().Single(p => p.Name == "signals").ParameterType == typeof(string[])
            && function.GetParameters().Single(p => p.Name == "signals").DefaultValue == null,
            "P6-14 Safety trace signals are optional typed names with omission preserved");
        if (release == "21")
        {
            foreach (string value in new[] { "null", "\"[]\"", "[1]", "[null]", "{}" })
                Rejected("ManageSafetyFunction", "{\"activationTest\":\"Acceptance\",\"action\":\"setTrace\",\"signals\":" + value + "}");
            Rejected("ManageSafetyFunction", "{\"activationTest\":\"Acceptance\",\"properties\":{\"signals\":[\"A\"]}}");
            // Contextual action rules are evaluated by the tool, before its service;
            // a real preview batch first requires its bound-project precondition.
            foreach (string action in new[] { "read", "create", "createFrom", "update", "resetTestResult", "checkValidity", "checkTraceValidity", "export", "import", "delete" })
                foreach (string signals in new[] { "[]", "[\"A\"]" })
                    Rejected("ManageSafetyFunction", "{\"activationTest\":\"Acceptance\",\"action\":\"" + action + "\",\"signals\":" + signals + "}", batch: false);
            Rejected("ManageSafetyFunction", "{\"activationTest\":\"Acceptance\",\"action\":\"setTrace\",\"properties\":{\"signals\":null}}", batch: false);
            foreach (string extra in new[] { "", ",\"signals\":[]", ",\"signals\":[\"A/B,exact\",\"A/B,exact\"]" })
            {
                string arguments = "{\"activationTest\":\"Acceptance\",\"action\":\"setTrace\",\"name\":\"Function\",\"properties\":{\"isTraced\":true}" + extra + "}";
                check(validate.Invoke(null, new object[] { function, JsonSerializer.Deserialize<JsonElement>(arguments), Schema("ManageSafetyFunction") }) == null,
                    "P6-14 shared admission accepts omitted, empty and ordered trace signals");
            }
        }
        else
        {
            const string arguments = "{\"activationTest\":\"Acceptance\"}";
            var direct = Invoke("ManageSafetyFunction", arguments);
            string problem = (string)boundary.GetMethod("VersionToolProblem", All)!.Invoke(null, new object[] { "ManageSafetyFunction" })!;
            var refusal = Body(direct);
            check(problem.Length > 0 && direct.IsError == true && (string?)refusal["error"]?["code"] == "UNSUPPORTED_CAPABILITY"
                && (string?)refusal["error"]?["message"] == problem && (string?)refusal["meta"]?["execution"] == "not-started",
                "P6-14 preserves the outer version refusal for the unadvertised V20 Safety function");
            foreach (var call in new[] {
                ("CallTool", "{\"name\":\"ManageSafetyFunction\",\"arguments\":" + arguments + "}"),
                ("PreviewToolBatch", "{\"operations\":[{\"name\":\"ManageSafetyFunction\",\"arguments\":" + arguments + "}],\"expectedProject\":\"Fixture\"}") })
            {
                if (EngineSurface.IsHostTool(server, call.Item1)) continue;
                var body = Body(Invoke(call.Item1, call.Item2));
                check((string?)body["error"]?["code"] == "UNSUPPORTED_CAPABILITY" && (string?)body["meta"]?["execution"] == "not-started",
                    "P6-14 preserves the V20 version gate through " + call.Item1);
            }
        }
    }
}

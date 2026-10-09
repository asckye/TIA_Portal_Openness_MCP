using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class HardwareContractsTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true };

    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var surface = EngineSurface.For(server);
        var names = new Dictionary<string, string> {
            ["AddDevice"] = "CreateDevice", ["AddDeviceWithFallback"] = "CreateHardwareDevice",
            ["AddGsdDeviceWithProbe"] = "CreateGsdDevice", ["AddHardwareCatalogDeviceWithProbe"] = "CreateHardwareCatalogDevice",
            ["DumpDeviceAttributes"] = "GetDeviceAttributes", ["GetDevices"] = "ListDevices",
            ["SetCpuCommonSettings"] = "SetPlcCpuSettings" };
        foreach (var name in new[] { "GetProjectTree", "GetDeviceInfo", "GetDeviceItemInfo", "GetDeviceItemTree",
            "SetDeviceItemAttribute", "ValidateAutomationContext", "SearchInstalledGsdDevices", "SearchHardwareCatalog",
            "GetDevicePlugLocations",
            "PlugDeviceItem", "ExportDeviceAml", "ImportDeviceAml", "BuildDeviceAmlDocument", "ManageHardwareObject" }) names[name] = name;
        var group = new[] { "DevicesTools", "HardwareDevicesTools", "HardwareAmlTools", "HardwareManagementTools", "ModulesTools" }
            .Select(n => server.GetType("TiaMcpServer.ModelContextProtocol." + n, true)!)
            .SelectMany(t => t.GetMethods(All)).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
        check(group.Length == 21, "P6-12 retains exactly 21 typed declarations across shared ports and F05");
        EngineSurface.CheckPortedHardwareRetirement(server, check);
        foreach (var pair in names)
        {
            check(group.Count(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == pair.Value) == 1,
                "P6-12 registers one V4 name: " + pair.Value);
            if (pair.Key != pair.Value) check(!group.Any(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == pair.Key),
                "P6-12 removes the alias: " + pair.Key);
            check(surface.Tool(pair.Value).ReturnType == typeof(CallToolResult), "P6-12 envelope transport: " + pair.Value);
        }
        check(surface.Tool("BuildDeviceAmlDocument").GetParameters()[0].ParameterType.Name == "DeviceAmlSpec", "P6-12 AML uses the P6-05 DTO");
        check(surface.Tool("SetPlcCpuSettings").GetParameters()[1].ParameterType.FullName == "TiaMcp.Logic.V4.Domain.CpuSettings", "P6-12 CPU uses the shared domain DTO");

        var boundary = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var schemaMethod = boundary.GetMethod("ToolInputSchema", All)!;
        var validate = boundary.GetMethod("ValidateV4Arguments", All)!;
        JsonElement Schema(string name) => (JsonElement)schemaMethod.Invoke(null, new object[] { name, surface.Tool(name) })!;
        object? Admission(string name, string input) => validate.Invoke(null, new object[] {
            surface.Tool(name), JsonSerializer.Deserialize<JsonElement>(input), Schema(name) });
        void SharedRejected(string name, string input, string code = "INVALID_ARGUMENT")
        {
            var error = Admission(name, input);
            check(error != null && string.Equals(error.GetType().GetProperty("Code")!.GetValue(error)!.ToString(),
                code.Replace("_", ""), StringComparison.OrdinalIgnoreCase),
                "P6-12 shared typed admission: " + name + "/" + code);
        }
        foreach (string settings in new[] { "null", "\"{}\"", "{}", "{\"exactAttributes\":{}}",
            "{\"exactAttributes\":{\"Name\":{}}}", "{\"exactAttributes\":{\"Name\":\"CPU\"},\"unknown\":1}" })
            SharedRejected("SetPlcCpuSettings", "{\"cpuPath\":\"CPU\",\"settings\":" + settings + "}");
        var cpuSchema = Schema("SetPlcCpuSettings").GetProperty("properties").GetProperty("settings");
        check(!cpuSchema.GetProperty("additionalProperties").GetBoolean()
            && cpuSchema.GetProperty("properties").GetProperty("exactAttributes").GetProperty("minProperties").GetInt32() == 1,
            "P6-12 advertises the closed CPU contract");

        JsonObject Body(object result) => ((CallToolResult)result).StructuredContent!.AsObject();
        object Invoke(string name, params object?[] arguments) => EngineSurface.InvokeUninitialized(surface.Tool(name), arguments)!;
        void Rejected(string name, object?[] arguments, string code = "INVALID_ARGUMENT")
        {
            var result = Body(Invoke(name, arguments));
            check((string?)result["error"]!["code"] == code && (string?)result["meta"]!["execution"] == "not-started",
                "P6-12 rejects before a service is available: " + name + "/" + code);
        }
        Rejected("ManageHardwareObject", new object?[] { new[] { "PLC_1" }, "moveItem", new[] { "CPU" }, null, null, 0, true });
        var settingsType = surface.Tool("SetPlcCpuSettings").GetParameters()[1].ParameterType;
        var tooManyAttributes = "{\"exactAttributes\":{" + string.Join(",", Enumerable.Range(0, 51).Select(i => "\"A" + i + "\":1")) + "}}";
        Rejected("SetPlcCpuSettings", new object?[] { "CPU", JsonSerializer.Deserialize(tooManyAttributes, settingsType, Json) }, "LIMIT_EXCEEDED");

        foreach (string input in new[] { "\"{}\"", "null", "{\"projectName\":\"P\",\"devices\":[]}",
            "{\"projectName\":\"P\",\"ProjectName\":\"Q\",\"devices\":[]}", "{\"projectName\":\"P\",\"projectName\":\"Q\",\"devices\":[]}",
            "{\"projectName\":\"P\",\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"System:Device.S71500\",\"deviceItems\":[],\"unknown\":1}]}" })
        {
            SharedRejected("BuildDeviceAmlDocument", "{\"spec\":" + input + ",\"outputPath\":\"candidate.aml\"}");
        }
        var valid = "{\"projectName\":\"P\",\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"System:Device.S71500\",\"deviceItems\":[]}]}";
        check(Admission("BuildDeviceAmlDocument", "{\"spec\":" + valid + ",\"outputPath\":\"candidate.aml\"}") == null,
            "P6-12 AML uses shared admission for an accepted legacy parser sample");
        check(!Schema("BuildDeviceAmlDocument").GetProperty("properties").GetProperty("spec").GetProperty("additionalProperties").GetBoolean(),
            "P6-12 advertises the closed AML contract");

        var mapper = server.GetType("TiaMcpServer.ModelContextProtocol.HardwareContract", true)!.GetMethod("Map", All)!;
        var responseType = Program.FindServerType(server, "TiaMcpServer.ModelContextProtocol.ResponseMessage");
        JsonObject Map(string name, string meta, bool write = false, bool current = false)
            => Body(mapper.Invoke(null, new object[] { name, JsonSerializer.Deserialize("{\"message\":\"fixture\",\"meta\":" + meta + "}", responseType, Json)!, write, current })!);
        foreach (var sample in new[] {
            ("PlugDeviceItem", "{\"success\":false,\"reason\":\"VerifyFailed\",\"mayHaveChanged\":true,\"verified\":false}", true, "unknown"),
            ("SetPlcCpuSettings", "{\"success\":false,\"mayHaveChanged\":true,\"applied\":[{\"attribute\":\"A\"}],\"rejected\":[{\"attribute\":\"B\"}]}", true, "partial"),
            ("SetPlcCpuSettings", "{\"success\":true,\"writeOutcomeUnknown\":true,\"applied\":[{}]}", true, "unknown"),
            ("ManageHardwareObject", "{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation") })
        {
            var result = Map(sample.Item1, sample.Item2, sample.Item3);
            check((string?)result["meta"]!["outcome"] == sample.Item4, "P6-12 maps execution evidence: " + sample.Item1 + "/" + sample.Item4);
            check((bool)result["ok"]! == (sample.Item4 == "succeeded"), "P6-12 never promotes partial/unknown to success");
        }
        var creation = Map("CreateDevice", "{\"success\":true,\"name\":\"PLC_1\"}", true, true);
        check((string?)creation["meta"]!["behaviorPolicy"] == "current" && creation["meta"]!["warnings"]!.AsArray().Count == 1,
            "P6-12 device creation discloses current native policy");
        var candidate = Map("BuildDeviceAmlDocument", "{\"success\":true,\"importVerified\":false}", true);
        check(candidate["meta"]!["warnings"]!.AsArray().Any(w => (string?)w!["code"] == "CANDIDATE_ONLY"), "P6-12 AML build remains an unverified import candidate");

        // Invoke the real tool/service bodies with an empty session, bypassing host approval entirely.
        var serviceType = server.GetType("TiaMcpServer.Siemens.Services.DevicesService", true)!;
        var service = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(serviceType);
        serviceType.GetField("_session", All)!.SetValue(service,
            System.Runtime.Serialization.FormatterServices.GetUninitializedObject(server.GetType("TiaMcpServer.Siemens.Portal", true)!));
        foreach (var sample in new[] {
            ("SearchHardwareCatalog", new object?[] { "1513", 50 }),
            ("CreateHardwareDevice", new object?[] { "6ES7 513-1AM03-0AB0", "V1.7", "PLC_2", "S7-1500" }) })
        {
            var method = surface.Tool(sample.Item1);
            var target = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(method.DeclaringType!);
            var field = method.DeclaringType!.GetField("_service", All)!;
            object injected = service;
            if (field.FieldType.Name == "HardwareDevicesService")
            {
                injected = field.FieldType.GetConstructors(All).Single().Invoke(new object?[] {
                    new Func<string, JsonObject, JsonNode?>((_, __) => throw (Exception)Activator.CreateInstance(
                        Type.GetType("TiaMcp.Adapters.Contracts.AdapterPreconditionException, TiaMcp.Adapters.Contracts", true)!,
                        "No TIA Portal is connected. Call ListPortalProcessProjects, ConnectPortal or ConnectProject first.", "session", false, null)!),
                    new Func<bool>(() => false), new Func<string>(() => ""), null });
            }
            field.SetValue(target, injected);
            var body = Body(method.Invoke(target, sample.Item2)!);
            check((string?)body["error"]?["code"] == "PRECONDITION_FAILED"
                && (string?)body["error"]?["details"]?["parameter"] == "session"
                && body["error"]!["message"]!.GetValue<string>().Contains("ListPortalProcessProjects")
                && body["error"]!["message"]!.GetValue<string>().Contains("ConnectProject")
                && (string?)body["meta"]?["outcome"] == "rejected-before-operation"
                && (string?)body["meta"]?["execution"] == "not-started"
                && (bool?)body["meta"]?["requiresSessionReset"] == false,
                "P6-70 approval-disabled real body refuses unbound catalog: " + sample.Item1);
        }
    }
}

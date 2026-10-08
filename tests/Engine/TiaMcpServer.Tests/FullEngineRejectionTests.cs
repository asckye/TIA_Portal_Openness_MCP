using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class FullEngineRejectionTests : IDisposable
    {
        private static readonly JsonArray Rows = LoadRows();
        private static readonly ToolCatalog Catalog = BuildCatalog();
        private static int calls;

        public FullEngineRejectionTests()
        {
            calls = 0;
            McpServer.ConfigureToolBridge(Catalog, () => false, new HashSet<string>());
        }
        public void Dispose() => ToolBridgeFixture.Configure();

        private static JsonArray LoadRows()
        {
            using var stream = typeof(FullEngineRejectionTests).Assembly.GetManifestResourceStream("FullEngineRejections.json")
                ?? throw new InvalidOperationException("Generated rejection data is missing from the test assembly.");
            return JsonNode.Parse(stream)!["releases"]![McpServer.ReleaseKey]!.AsArray();
        }

        // Exact generated names, parameter types and defaults; bodies are traps.
        // Source generation checks these signatures against appendix A/B. The
        // production SDK catalog, V4 binder, bridge and batches execute below.
        private static ToolCatalog BuildCatalog()
        {
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FullEngineAdmission"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Probes");
            var type = module.DefineType("FullEngineAdmissionProbes", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            foreach (var row in Rows)
            {
                string name = (string)row!["name"]!;
                var parameters = row["parameters"]!.AsObject().ToArray();
                var types = parameters.Select(p => ResolveType((string)p.Value!["type"]!)).ToArray();
                var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(CallToolResult), types);
                method.SetCustomAttribute(new CustomAttributeBuilder(typeof(McpServerToolAttribute).GetConstructor(Type.EmptyTypes)!,
                    Array.Empty<object>(), new[] { typeof(McpServerToolAttribute).GetProperty("Name")! }, new object[] { name }));
                method.SetCustomAttribute(new CustomAttributeBuilder(typeof(DescriptionAttribute).GetConstructor(new[] { typeof(string) })!,
                    new object[] { "[L2][Meta][READ] Generated admission probe." }));
                for (int i = 0; i < parameters.Length; i++)
                {
                    var declaration = parameters[i].Value!;
                    bool optional = declaration["default"] != null;
                    var parameter = method.DefineParameter(i + 1, optional ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None,
                        parameters[i].Key);
                    if (declaration["description"] != null)
                        parameter.SetCustomAttribute(new CustomAttributeBuilder(typeof(DescriptionAttribute).GetConstructor(new[] { typeof(string) })!,
                            new object[] { (string)declaration["description"]! }));
                    if (optional) parameter.SetConstant(declaration["value"] == null ? null
                        : JsonSerializer.Deserialize(declaration["value"]!.ToJsonString(), types[i]));
                }
                var body = method.GetILGenerator();
                body.Emit(OpCodes.Call, typeof(FullEngineRejectionTests).GetMethod(nameof(UnexpectedInvocation))!);
                body.Emit(OpCodes.Ret);
            }
            return new ToolCatalog(new[] { type.CreateType()! });
        }

        private static Type ResolveType(string name)
        {
            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                var inner = ResolveType(name.Substring(0, name.Length - 1));
                return inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner;
            }
            if (name.EndsWith("[]", StringComparison.Ordinal)) return ResolveType(name.Substring(0, name.Length - 2)).MakeArrayType();
            switch (name)
            {
                case "string": return typeof(string);
                case "bool": return typeof(bool);
                case "int": return typeof(int);
                case "long": return typeof(long);
                case "uint": return typeof(uint);
                case "ushort": return typeof(ushort);
                case "IMcpServer": return typeof(IMcpServer);
                case "RequestContext<CallToolRequestParams>": return typeof(RequestContext<CallToolRequestParams>);
                case "JsonObject": return typeof(JsonObject);
                case "StagedTextFile": return typeof(TiaMcp.Logic.ModelContextProtocol.StagedTextFile);
            }
            int generic = name.IndexOf('<');
            if (generic >= 0)
            {
                var arguments = name.Substring(generic + 1, name.Length - generic - 2).Split(',').Select(ResolveType).ToArray();
                var definition = name.Substring(0, generic) == "Dictionary" ? typeof(Dictionary<,>)
                    : typeof(ToolArguments).Assembly.GetTypes().Single(t => t.Name == name.Substring(0, generic) + "`" + arguments.Length);
                return definition.MakeGenericType(arguments);
            }
            if (name.Contains('.')) return typeof(ToolArguments).Assembly.GetType(name, throwOnError: true)!;
            var matches = typeof(ToolArguments).Assembly.GetTypes()
                .Where(t => t.Namespace?.StartsWith("TiaMcp.Logic.V4", StringComparison.Ordinal) == true && t.Name == name).ToArray();
            Assert.True(matches.Length == 1, name + ": generated parameter type must resolve unambiguously");
            return matches.Single();
        }

        public static CallToolResult UnexpectedInvocation()
        {
            calls++;
            throw new InvalidOperationException("Admission sweep reached a target body.");
        }

        public static IEnumerable<object[]> Renames() => Rows.SelectMany(row => row!["renamedFrom"]!.AsArray()
            .Select(old => new object[] { (string)old!, (string)row["name"]! }));

        public static IEnumerable<object[]> TypedParameters()
        {
            foreach (var row in Rows)
                foreach (var parameter in row!["typedParameters"]!.AsObject())
                {
                    yield return new object[] { (string)row["name"]!, parameter.Key, (string)parameter.Value!, false };
                    // NativeValue includes literal strings in its existing V4 contract.
                    // The separate preservation case proves they are not JSON-decoded.
                    if ((string?)row["parameters"]![(string)parameter.Value!]!["type"] != "NativeValue")
                        yield return new object[] { (string)row["name"]!, parameter.Key, (string)parameter.Value!, true };
                }
        }

        public static IEnumerable<object[]> NativeValues() => Rows.SelectMany(row => row!["typedParameters"]!.AsObject()
            .Where(p => (string?)row["parameters"]![(string)p.Value!]!["type"] == "NativeValue")
            .Select(p => new object[] { (string)row["name"]!, (string)p.Value! }));

        public static IEnumerable<object[]> WriteFamilies() => Rows.Select(row => (string)row!["name"]!)
            .Where(name => ToolMetadata.Find(name)?.Operation is "WRITE" or "ONLINE-WRITE" or "FILE" or "EXECUTE" or "SESSION")
            .Select(name => new object[] { name, ToolMetadata.Find(name)!.Domain });

        [Theory, MemberData(nameof(WriteFamilies))]
        public async Task Every_write_family_keeps_admission_with_approval_disabled(string name, string family)
        {
            using var fixture = new InfrastructureContractsTests();
            McpServer.ConfigureToolBridge(Catalog, () => false, new HashSet<string>());
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            bool context = McpServer.EnterMcpApprovalContext();
            try
            {
                new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
                Assert.False(ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled);
                Assert.False(string.IsNullOrWhiteSpace(family));
                var arguments = Rows.Single(row => (string)row!["name"]! == name)!["arguments"]!.DeepClone().AsObject();
                arguments["unexpectedAdmissionArgument"] = true;
                var input = new ToolArguments(JsonSerializer.SerializeToElement(arguments));
                var method = Catalog.Methods.Single(pair => pair.Key == name).Value;
                var direct = new VersionPolicyTool(McpServer.CreateTool(name, method));
                var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
                { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
                foreach (var result in new[] { await direct.InvokeAsync(request), McpServer.CallTool(name, input) })
                {
                    var body = McpServer.ResultBody(result)!;
                    // Version-specific operation admission can precede the unknown-argument check.
                    string code = (string)body["error"]!["code"]!;
                    Assert.Contains(code, new[] { "INVALID_ARGUMENT", "UNSUPPORTED_CAPABILITY" });
                    Reject(result, code);
                    Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
                    Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "APPROVAL_PRECHECK_REFUSED");
                }
                Assert.Equal(0, calls);
            }
            finally { McpServer.LeaveMcpApprovalContext(context); settings.Save(ApprovalSettings.SettingsPath); }
        }

        [Theory, MemberData(nameof(NativeValues))]
        public void NativeValueKeepsLiteralStringsWithoutDecoding(string name, string parameter)
        {
            var arguments = Rows.Single(r => (string)r!["name"]! == name)!["arguments"]!.DeepClone().AsObject();
            arguments[parameter] = "[]";
            Assert.Null(McpServer.BindV4Call(name, new ToolArguments(JsonSerializer.SerializeToElement(arguments)), out var method, out var values));
            int index = Array.FindIndex(method!.GetParameters(), p => p.Name == parameter);
            var value = Assert.IsType<NativeValue>(values![index]);
            Assert.Equal(JsonValueKind.String, value.Kind);
            Assert.Equal("[]", value.Json.GetString());
            Assert.Equal(0, calls);
        }

        [Fact]
        public void GeneratedCasesCoverTheFullReleaseAndOnlyV4Registrations()
        {
            var profiles = McpServer.RuntimeProfileEntries();
            Assert.Equal(profiles.Select(r => (string)r!["currentName"]!), Catalog.Methods.Select(p => p.Key));
            Assert.All(profiles, row => {
                Assert.Equal(4, (int)row!["envelopeVersion"]!);
                Assert.Equal((string)row["name"]!, (string)row["currentName"]!);
                McpServer.AssertV4Tool((string)row["currentName"]!);
            });
            Assert.Throws<InvalidOperationException>(() => McpServer.AssertV4Tool("MissingGeneratedContract"));
            Assert.NotEmpty(Renames());
            Assert.NotEmpty(TypedParameters());
        }

        [Theory, MemberData(nameof(Renames))]
        public void EveryOldNameIsAbsentAndRejectedByBridgeAndBatches(string oldName, string currentName)
        {
            Assert.DoesNotContain(Catalog.Methods, p => p.Key == oldName);
            Assert.Contains(Catalog.Methods, p => p.Key == currentName);
            var arguments = McpServer.EmptyArguments();
            Reject(McpServer.CallTool(oldName, arguments), "TOOL_NOT_FOUND");
            Reject(McpServer.ReadToolBatch(new[] { new ToolCall(oldName, arguments) }), "TOOL_NOT_FOUND");
            Reject(McpServer.PreviewToolBatch(new[] { new ToolCall(oldName, arguments) }, "Fixture"), "TOOL_NOT_FOUND");
            Assert.Equal(0, calls);
        }

        [Theory, MemberData(nameof(TypedParameters))]
        public async Task EveryTypedParameterRejectsLegacyCarriersBeforeDispatch(string name, string oldParameter, string parameter, bool encoded)
        {
            var row = Rows.Single(r => (string)r!["name"]! == name)!;
            var arguments = row["arguments"]!.DeepClone().AsObject();
            // Operation examples select a context supported by this release.
            // For example, V20 and V21 expose different device-service families.
            var operation = (string?)(arguments["action"] ?? arguments["operation"]) ?? "";
            if (operation.Length > 0 && McpServer.BindV4Call(name,
                new ToolArguments(JsonSerializer.SerializeToElement(arguments)), out _, out _) != null)
            {
                var usage = McpServer.ResultBody(new ToolUsageTools().GetToolUsage(toolName: name, operation: operation))!;
                Assert.True((bool)usage["ok"]!, name);
                arguments = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!.DeepClone().AsObject();
            }
            var baseline = new ToolArguments(JsonSerializer.SerializeToElement(arguments));
            Assert.Null(McpServer.BindV4Call(name, baseline, out var method, out _));
            var direct = new VersionPolicyTool(McpServer.CreateTool(name, method!));
            var properties = direct.ProtocolTool.InputSchema.GetProperty("properties");
            Assert.False(properties.TryGetProperty(oldParameter, out _));
            Assert.True(properties.TryGetProperty(parameter, out _));
            arguments[encoded ? parameter : oldParameter] = "[]";
            var input = new ToolArguments(JsonSerializer.SerializeToElement(arguments));
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
            Reject(await direct.InvokeAsync(request), "INVALID_ARGUMENT");
            Reject(McpServer.CallTool(name, input), "INVALID_ARGUMENT");
            Reject(McpServer.ReadToolBatch(new[] { new ToolCall(name, input) }), "INVALID_ARGUMENT");
            Reject(McpServer.PreviewToolBatch(new[] { new ToolCall(name, input) }, "Fixture"), "INVALID_ARGUMENT");
            Assert.Equal(0, calls);
        }

        private static void Reject(CallToolResult result, string code)
        {
            var body = McpServer.ResultBody(result)!;
            Assert.True(result.IsError);
            Assert.Equal(4, (int)body["schemaVersion"]!);
            Assert.False((bool)body["ok"]!);
            Assert.Equal(code, (string?)body["error"]!["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        }
    }
}

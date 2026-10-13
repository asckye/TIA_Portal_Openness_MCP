extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Optional;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;
using HostContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;
using EngineConfiguration = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration;

public sealed class OptionalEngineeringBoundaryTests
{
    private static Assembly Host => typeof(HostContract).Assembly;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [Theory]
    [InlineData("16", "Sivarc", "GetSivarcRuleTree", "{\"category\":\"screens\",\"tablePath\":\"Table_1\"}")]
    [InlineData("17", "OptionalEngineering", "ManageSivarcRule", "{\"category\":\"screens\",\"collectionPath\":[{\"property\":\"Tables\",\"name\":\"Table_1\"},{\"property\":\"Rules\"}],\"name\":\"Rule_1\",\"action\":\"update\",\"dryRun\":false}")]
    [InlineData("17", "Dcc", "ManageDccChart", "{\"devicePath\":[\"Drive_1\"],\"itemPath\":[\"Control_1\"],\"chartName\":\"Chart_1\",\"action\":\"create\",\"dryRun\":false}")]
    [InlineData("16", "SafetyManagement", "ManageSafetyGlobalSettings", "{\"action\":\"read\"}")]
    public void Capability_refusals_are_unstarted_envelopes_without_worker_calls(string release, string family, string name, string json)
    {
        using var selected = HostContract.UseRelease(release);
        using var worker = new CredentialWorker();
        var body = Body(Invoke(family, name, json, worker));
        Refusal(body);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("LibraryToolContract", "GetSivarcRuleTree", false)]
    [InlineData("OptionalPackageContract", "ManageSivarcRule", true)]
    public void Missing_Sivarc_assembly_reply_preserves_the_unstarted_envelope(string contract, string tool, bool writes)
    {
        using var selected = HostContract.UseRelease("21");
        using var worker = new CredentialWorker();
        int nativeCalls = 0;
        var reply = OptionalAssemblyAdmission.Run("21", "Siemens.Engineering.Sivarc", () => { nativeCalls++; return new HardwareAddressingReply(); });
        var responseType = Host.GetType("TiaMcpServer.ModelContextProtocol.ResponseMessage", true)!;
        object response = JsonSerializer.Deserialize(JsonSerializer.Serialize(reply), responseType)!;
        var map = Host.GetType("TiaMcpServer.ModelContextProtocol." + contract, true)!.GetMethod("Map", All)!;
        var arguments = map.GetParameters().Select(p => p.Name switch {
            "tool" => (object)tool, "response" => response, "writes" => writes, "current" => true,
            _ => p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException(p.Name)
        }).ToArray();
        Refusal(Body((CallToolResult)map.Invoke(null, arguments)!));
        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData("LibraryToolContract", "GetSivarcRuleTree", false)]
    [InlineData("OptionalPackageContract", "ManageSivarcRule", true)]
    public void Missing_optional_product_service_reply_preserves_the_unstarted_envelope(string contract, string tool, bool writes)
    {
        using var selected = HostContract.UseRelease("21");
        var reply = OptionalEngineeringCapabilities.Refusal(new PlcSoftwareException("NotSupportedOnVersion", "Synthetic missing SiVArc session service."));
        Assert.NotNull(reply);
        var responseType = Host.GetType("TiaMcpServer.ModelContextProtocol.ResponseMessage", true)!;
        object response = JsonSerializer.Deserialize(JsonSerializer.Serialize(reply), responseType)!;
        var map = Host.GetType("TiaMcpServer.ModelContextProtocol." + contract, true)!.GetMethod("Map", All)!;
        var arguments = map.GetParameters().Select(p => p.Name switch {
            "tool" => (object)tool, "response" => response, "writes" => writes, "current" => true,
            _ => p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException(p.Name)
        }).ToArray();
        Refusal(Body((CallToolResult)map.Invoke(null, arguments)!));
        Assert.Null(OptionalEngineeringCapabilities.Refusal(new PlcSoftwareException("NotFound", "Synthetic missing target.")));
    }

    [Theory]
    [InlineData("SafetyManagement", "ManagePlcSafety", "login")]
    [InlineData("SafetyManagement", "ManagePlcSafety", "setPassword")]
    [InlineData("Startdrive", "ManageDriveSecurity", "activateEncryption")]
    public void Ported_credentials_are_absent_from_envelope_journal_and_worker_reply(string family, string name, string action)
    {
        using var selected = HostContract.UseRelease("21");
        string password = "synthetic-credential-" + Guid.NewGuid().ToString("N");
        var input = new JsonObject { ["action"] = action, ["password"] = password, ["dryRun"] = false };
        if (name == "ManagePlcSafety") { input["softwarePath"] = "PLC_1"; input["confirmSafetyChange"] = true; }
        else { input["devicePath"] = new JsonArray("Drive_1"); input["itemPath"] = new JsonArray("Control_1"); }
        using var worker = new CredentialWorker();
        var journal = Host.GetType("TiaMcpServer.ModelContextProtocol.InvocationJournal", true)!;
        var sink = journal.GetField("sink", All)!; var previousSink = sink.GetValue(null);
        var rows = new List<string>();
        journal.GetMethod("ConfigureOutput", All)!.Invoke(null, new object?[] { (Action<string>)rows.Add, null });
        try
        {
            var span = (IDisposable)journal.GetMethod("Observe", All)!.Invoke(null,
                new object[] { Guid.NewGuid().ToString("N"), name, "fixture", "21", true, (Func<string>)(() => input.ToJsonString()) })!;
            using (span)
            {
                string envelope = Body(Invoke(family, name, input.ToJsonString(), worker)).ToJsonString();
                span.GetType().GetMethod("Complete", All)!.Invoke(span, new object[] { (Func<string>)(() => envelope) });
                Assert.DoesNotContain(password, envelope, StringComparison.Ordinal);
            }
            Assert.Equal(1, worker.Calls);
            Assert.Equal(password, (string?)worker.Input?["request"]?["Password"]);
            Assert.True(worker.SecureStringReadOnly);
            Assert.DoesNotContain(password, worker.ReplyEvidence!, StringComparison.Ordinal);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.DoesNotContain(password, row, StringComparison.Ordinal));
        }
        finally { sink.SetValue(null, previousSink); }
    }

    private static CallToolResult Invoke(string family, string name, string json, CredentialWorker worker)
    {
        var serviceType = Host.GetType("TiaMcpServer.Siemens.Services." + family + "Service", true)!;
        Func<string, JsonObject, JsonNode?> call = (module, arguments) => worker.Call(module, arguments, CancellationToken.None).GetAwaiter().GetResult();
        var service = Activator.CreateInstance(serviceType, All, null, new object[] { call, (Func<string>)(() => "C:/Projects/项目1.ap21") }, null)!;
        var toolType = Host.GetType("TiaMcpServer.ModelContextProtocol." + family + "Tools", true)!;
        var tool = Activator.CreateInstance(toolType, All, null, new[] { service }, null)!;
        var method = toolType.GetMethods(All).Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
        var input = JsonNode.Parse(json)!.AsObject();
        var deserialize = typeof(V4Json).GetMethods().Single(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        var arguments = method.GetParameters().Select(p => input.TryGetPropertyValue(p.Name!, out var value)
            ? deserialize.MakeGenericMethod(p.ParameterType).Invoke(null, new object[] { value!.ToJsonString() })
            : p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException("Missing fixture argument: " + p.Name)).ToArray();
        return (CallToolResult)method.Invoke(tool, arguments)!;
    }

    private static JsonObject Body(CallToolResult result)
    {
        var body = result.StructuredContent!.AsObject();
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(!(bool)body["ok"]!, result.IsError);
        return body;
    }
    private static void Refusal(JsonObject body)
    {
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
    }

    private sealed class CredentialWorker : IFoundationWorker
    {
        internal int Calls;
        internal JsonObject? Input;
        internal string? ReplyEvidence;
        internal bool SecureStringReadOnly;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++; Input = arguments.DeepClone().AsObject();
            string? password = (string?)arguments["request"]?["Password"];
            if (password != null)
            {
                using var secure = EngineeringCredentialRules.ToSecureString(password);
                SecureStringReadOnly = secure.IsReadOnly();
                Assert.Equal(password.Length, secure.Length);
            }
            var reply = new HardwareAddressingReply { Message = "Synthetic credential action completed.", Meta = new Dictionary<string, object?> {
                ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["mayHaveChanged"] = true, ["passwordProvided"] = password != null } };
            ReplyEvidence = JsonSerializer.Serialize(reply);
            return Task.FromResult(JsonNode.Parse(ReplyEvidence));
        }
        public void Dispose() { }
    }
}

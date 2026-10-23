extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;
using EngineeringServicesPortService = enginehost::TiaMcpServer.Siemens.Services.EngineeringServicesPortService;
using ProjectLifecyclePortService = enginehost::TiaMcpServer.Siemens.Services.ProjectLifecyclePortService;
using ProjectLifecycleProjectTools = enginehost::TiaMcpServer.ModelContextProtocol.ProjectLifecycleProjectTools;
using HardwareSecurityPortService = enginehost::TiaMcpServer.Siemens.Services.HardwareSecurityPortService;
using ResponseImportBatch = enginehost::TiaMcpServer.ModelContextProtocol.ResponseImportBatch;
using ResponseMessage = enginehost::TiaMcpServer.ModelContextProtocol.ResponseMessage;

namespace TiaMcp.FoundationHost.Tests;

// Family operations whose PlcWorker reply is a plain value that the shared port services read directly
// (ProjectLifecyclePortService, EngineeringServicesPortService). The host accepts exactly that shape;
// any other reply still stops the session.
public sealed class FamilyPlainReplyTests
{
    private static string Fixture => Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
        "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/bin/Release/net48/TiaMcp.PlcWorker.FaultFixture.exe");

    // SessionCallbackPrimitives.Snapshot (src/Shared/shared-native/SessionCallbackPrimitives.cs) as WorkerJson writes it.
    private static JsonObject Binding(JsonNode? identity) => new()
    {
        ["identity"] = identity, ["fault"] = null, ["instanceReserved"] = true, ["verifiedLive"] = false,
        ["note"] = "Cached binding only. Project access verifies PID/start time/full path/name; no implicit rebind."
    };

    public static TheoryData<string, string> Accepted() => new()
    {
        { "project-lifecycle.EnsureOpennessUserGroup", "true" },
        { "project-lifecycle.EnsureOpennessUserGroup", "false" },
        { "project-lifecycle.ArchiveSaveProjectCopy", "true" },
        { "project-lifecycle.ArchiveSaveProjectCopy", "false" },
        { "project-lifecycle.ReadProjectLifecycleBinding", Binding(new JsonObject { ["projectPath"] = @"C:\p\P.ap19" }).ToJsonString() },
        { "project-lifecycle.ReadProjectLifecycleBinding", Binding(null).ToJsonString() },
        { "project-lifecycle.ReadProjectLifecycleBinding", new JsonObject { ["identity"] = null, ["fault"] = "Binding lost.", ["instanceReserved"] = false,
            ["verifiedLive"] = false, ["note"] = "n" }.ToJsonString() },
        { "engineering-services.BeginEngineeringTransaction", "\"3f2a\"" },
        { "engineering-services.ReadEngineeringTransaction", "true" },
        { "engineering-services.ReadEngineeringTransaction", "false" },
        { "engineering-services.CommitEngineeringTransaction", "null" },
        { "engineering-services.DisposeEngineeringTransaction", "null" },
    };

    [Theory, MemberData(nameof(Accepted))]
    public void Documented_plain_reply_is_accepted(string operation, string reply)
    {
        Assert.True(TiaMcp.PlcWorker.WorkerOperations.IsFamilyOperation(operation));
        WorkerProtocol.ValidateExchangeResult(operation, new JsonObject(), JsonNode.Parse(reply));
    }

    public static TheoryData<string, string> Rejected()
    {
        var data = new TheoryData<string, string>();
        foreach (string operation in new[] { "project-lifecycle.EnsureOpennessUserGroup", "project-lifecycle.ArchiveSaveProjectCopy",
            "engineering-services.ReadEngineeringTransaction" })
            foreach (string reply in new[] { "\"true\"", "1", "null", "{}", "[true]", "{\"Message\":\"m\",\"RequiresSessionReset\":false,\"Meta\":{\"success\":true}}" })
                data.Add(operation, reply);
        foreach (string reply in new[] { "\"\"", "true", "null", "{}", "[\"3f2a\"]", "7" })
            data.Add("engineering-services.BeginEngineeringTransaction", reply);
        foreach (string operation in new[] { "engineering-services.CommitEngineeringTransaction", "engineering-services.DisposeEngineeringTransaction" })
            foreach (string reply in new[] { "true", "\"\"", "{}", "[]" })
                data.Add(operation, reply);
        var binding = Binding(new JsonObject { ["projectPath"] = "p" });
        JsonObject Without(string key) { var copy = binding.DeepClone().AsObject(); copy.Remove(key); return copy; }
        JsonObject With(string key, JsonNode? value) { var copy = binding.DeepClone().AsObject(); copy[key] = value; return copy; }
        foreach (var reply in new[] { Without("identity"), Without("fault"), Without("instanceReserved"), Without("verifiedLive"), Without("note"),
            With("identity", "p"), With("fault", 1), With("instanceReserved", "true"), With("verifiedLive", null), With("note", null), With("extra", 1) })
            data.Add("project-lifecycle.ReadProjectLifecycleBinding", reply.ToJsonString());
        foreach (string reply in new[] { "true", "null", "[]", "\"binding\"" })
            data.Add("project-lifecycle.ReadProjectLifecycleBinding", reply);
        // Other family operations keep their own shapes: a plain bool is still malformed for them.
        foreach (string operation in new[] { "project-lifecycle.ReadPortalInfo", "project-lifecycle.ConnectIsolatedPortal", "project-lifecycle.ArchiveSavedProject",
            "project-security.ExecuteHardwareSecurity", "engineering-services.ExecuteEngineeringServices", "hardware-devices.HardwareCreateDevice" })
            data.Add(operation, "true");
        data.Add("project-lifecycle.ReadPortalInfo", Binding(null).ToJsonString());
        return data;
    }

    [Theory, MemberData(nameof(Rejected))]
    public void Any_other_reply_stops_the_session(string operation, string reply)
    {
        var error = Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult(operation, new JsonObject(), JsonNode.Parse(reply)));
        Assert.StartsWith("Malformed hardware worker reply; session stopped: " + operation, error.Message);
        Assert.True(WorkerProtocol.RequiresSessionReset(true, error));
    }

    // Replies whose shape depends on the request (PUT/GET, scaffold primitives) or that are contract objects without step members.
    private const string SecurityRequest = "{\"request\":{\"Operation\":\"OP\"}}";
    private const string KindRequest = "{\"request\":{\"Kind\":\"KIND\"}}";
    private const string Contract = "{\"Value\":{\"State\":\"Online\"},\"Failure\":null,\"MayHaveChanged\":false,\"RequiresSessionReset\":false,\"BlockReadsAfterUncertain\":true}";
    private const string Hmi = "{\"Value\":[1],\"Notes\":null,\"Failure\":null,\"MayHaveChanged\":true,\"RequiresSessionReset\":false,\"BlockReadsAfterUncertain\":true}";

    public static TheoryData<string, string, string> AcceptedWithRequest() => new()
    {
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":false,\"message\":\"No project open.\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":false,\"device\":\"PLC_1\",\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":true,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"enabled\":false,\"rawValue\":\"\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":false,\"failureCode\":\"PROJECT_NOT_BOUND\",\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":false,\"failureCode\":\"UNSUPPORTED_CAPABILITY\",\"device\":\"PLC_1\",\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":false,\"mayHaveChanged\":true,\"writeOutcomeKnown\":false,\"device\":\"PLC_1\",\"attributeName\":\"a\",\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":false,\"mayHaveChanged\":true,\"writeOutcomeKnown\":false,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"before\":\"\",\"after\":\"\",\"note\":\"n\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "ManagePlcProtection"), "{\"Message\":\"m\",\"Meta\":{\"success\":true},\"RequiresSessionReset\":false}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "save"), "{\"Message\":\"Local project saved\",\"Meta\":{\"success\":true}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "addDevice"), "{\"Ok\":false,\"DeviceName\":\"PLC_1\",\"Family\":\"f\",\"MlfbUsed\":null,\"VersionUsed\":null,\"Attempts\":null,\"Error\":\"e\",\"Message\":\"m\",\"Meta\":{\"success\":false}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "compile"), "{\"State\":\"Error\",\"ErrorCount\":null,\"WarningCount\":2,\"Errors\":[\"e\"],\"Warnings\":[],\"Info\":[],\"RawMessages\":[],\"Message\":\"m\",\"Meta\":{}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "hmiInfo"), "{\"Name\":null,\"ProgramType\":null,\"Screens\":null,\"Message\":\"m\",\"Meta\":{}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "hmiConnect"), "{\"Meta\":{},\"Message\":\"m\",\"ObjectKind\":\"k\",\"ObjectPath\":\"p\",\"TypeName\":\"t\",\"Members\":[]}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "hmiTag"), "{\"Message\":null,\"Meta\":{}}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":null,\"Imported\":[\"S\"],\"Failed\":[{\"Path\":\"p\",\"Error\":null}],\"Meta\":{\"success\":true}}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":\"m\",\"Imported\":null,\"Failed\":null,\"Meta\":null}" },
        { "plc-online.ExecuteOnline", "{}", Contract },
        { "plc-online.ExecuteOnline", "{}", "{\"Value\":null,\"Failure\":{\"Code\":\"c\",\"Message\":\"m\"},\"MayHaveChanged\":true,\"RequiresSessionReset\":true,\"BlockReadsAfterUncertain\":true}" },
        { "hmi-generic.ExecuteHmi", "{}", Hmi },
        { "hmi-classic.ExecuteHmi", "{}", Hmi },
        { "hmi-unified.ExecuteUnifiedHmi", "{}", Hmi },
        { "hmi-unified.ExecuteUnifiedHmiMetadata", "{}", Hmi },
        { "hmi-unified-exchange.ExecuteUnified", "{}", Hmi },
        { "hmi-unified-exchange.ReleaseUnifiedReadCursor", "{}", Hmi },
        { "hmi-unified-model.ExecuteUnified", "{}", Hmi },
        { "libraries.ExecuteLibrary", "{}", "{\"Step\":{\"Message\":\"m\",\"Meta\":{},\"RequiresSessionReset\":false,\"MayHaveChanged\":false},\"Probe\":null,\"Import\":null,\"MayHaveChanged\":false,\"RequiresSessionReset\":false,\"BlockReadsAfterUncertain\":true}" },
        { "openness-reflection.ExecuteOpennessReflection", "{}", "{\"Observation\":{\"Kind\":\"k\"},\"Failure\":null,\"MayHaveChanged\":false,\"RequiresSessionReset\":false,\"BlockReadsAfterUncertain\":true}" },
        { "hardware-devices.HardwareCreateCatalogDevice", "{}", "{\"Device\":{\"Name\":\"PLC_1\"},\"Candidate\":{\"TypeIdentifier\":\"t\"},\"Candidates\":[],\"Attempts\":[\"t\"],\"Error\":null}" },
        { "hardware-devices.HardwareCreateDeviceFallback", "{}", "{\"Device\":null,\"MlfbUsed\":null,\"VersionUsed\":null,\"Attempts\":[],\"Error\":\"e\"}" },
    };

    [Theory, MemberData(nameof(AcceptedWithRequest))]
    public void Documented_reply_for_the_request_is_accepted(string operation, string arguments, string reply)
        => WorkerProtocol.ValidateExchangeResult(operation, JsonNode.Parse(arguments)!.AsObject(), JsonNode.Parse(reply));

    public static TheoryData<string, string, string> RejectedWithRequest() => new()
    {
        // A PUT/GET reply that mixes the variants, misses or adds a member, or answers the other operation.
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":true,\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":true,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"enabled\":true}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"found\":false,\"message\":\"m\",\"extra\":1}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "GetPlcPutGetAccess"), "{\"ok\":false,\"failureCode\":\"NOT_FOUND\",\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":true,\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"ok\":true,\"mayHaveChanged\":false,\"writeOutcomeKnown\":true,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"before\":\"\",\"after\":\"\",\"note\":\"n\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "SetPlcPutGetAccess"), "{\"found\":false,\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "ManagePlcProtection"), "{\"found\":false,\"message\":\"m\"}" },
        { "project-security.ExecuteHardwareSecurity", SecurityRequest.Replace("OP", "Unknown"), "{\"Message\":\"m\",\"Meta\":{\"success\":true},\"RequiresSessionReset\":false}" },
        { "project-security.ExecuteHardwareSecurity", "{}", "{\"found\":false,\"message\":\"m\"}" },
        // A scaffold primitive reply without its Meta, with another kind's members, or for no or an unknown kind.
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "connect"), "{\"Message\":\"m\"}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "connect"), "{\"Message\":\"m\",\"Meta\":{},\"Ok\":true}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "addDevice"), "{\"Message\":\"m\",\"Meta\":{}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "compile"), "{\"State\":1,\"ErrorCount\":0,\"WarningCount\":0,\"Errors\":[],\"Warnings\":[],\"Info\":[],\"RawMessages\":[],\"Message\":\"m\",\"Meta\":{}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "hmiConnect"), "{\"Message\":\"m\",\"ObjectKind\":\"k\",\"ObjectPath\":\"p\",\"TypeName\":\"t\",\"Members\":[]}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "hmiScreen"), "{\"Message\":\"m\",\"Meta\":null}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", KindRequest.Replace("KIND", "unknown"), "{\"Message\":\"m\",\"Meta\":{}}" },
        { "project-lifecycle.ExecuteProjectScaffoldPrimitive", "{}", "{\"Message\":\"m\",\"Meta\":{}}" },
        // PlcSeedHmiReply with a member missing, extra or of the wrong kind.
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":null,\"Imported\":[],\"Failed\":[]}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":null,\"Imported\":[],\"Failed\":[],\"Meta\":{},\"RequiresSessionReset\":false}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":null,\"Imported\":[1],\"Failed\":[],\"Meta\":{}}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":null,\"Imported\":[],\"Failed\":[{\"Path\":\"p\"}],\"Meta\":{}}" },
        { "plc-software.SeedReferenceHmi", "{}", "{\"Message\":\"m\",\"Meta\":{\"success\":true},\"RequiresSessionReset\":false}" },
        // Contract replies with a member missing, extra or of the wrong kind, and the step shape for them.
        { "plc-online.ExecuteOnline", "{}", Contract.Replace(",\"BlockReadsAfterUncertain\":true", "") },
        { "plc-online.ExecuteOnline", "{}", Contract.Replace("\"MayHaveChanged\":false", "\"MayHaveChanged\":\"false\"") },
        { "plc-online.ExecuteOnline", "{}", Contract.Replace("\"Failure\":null", "\"Failure\":\"f\"") },
        { "plc-online.ExecuteOnline", "{}", "{\"Message\":\"m\",\"Meta\":{\"success\":true},\"RequiresSessionReset\":false}" },
        { "hmi-generic.ExecuteHmi", "{}", Hmi.Replace("\"Notes\":null", "\"Notes\":1") },
        { "hmi-unified.ExecuteUnifiedHmi", "{}", Hmi.Replace("}", ",\"Message\":\"m\"}") },
        { "hmi-unified-exchange.ReleaseUnifiedReadCursor", "{}", Contract },
        { "libraries.ExecuteLibrary", "{}", "{\"Step\":\"s\",\"Probe\":null,\"Import\":null,\"MayHaveChanged\":false,\"RequiresSessionReset\":false,\"BlockReadsAfterUncertain\":true}" },
        { "openness-reflection.ExecuteOpennessReflection", "{}", "{\"Observation\":null,\"Failure\":null,\"MayHaveChanged\":false,\"RequiresSessionReset\":false}" },
        // Device creation replies: a full description in Device (never produced), an extra member, or a missing Attempts list.
        { "hardware-devices.HardwareCreateCatalogDevice", "{}", "{\"Device\":{\"Name\":\"PLC_1\",\"Attributes\":[]},\"Candidate\":null,\"Candidates\":[],\"Attempts\":[],\"Error\":null}" },
        { "hardware-devices.HardwareCreateGsdDevice", "{}", "{\"Device\":{\"Name\":\"PLC_1\"},\"Attempts\":[\"t\"]}" },
        { "hardware-devices.HardwareCreateDeviceFallback", "{}", "{\"Device\":{\"Name\":\"PLC_1\"},\"MlfbUsed\":null,\"VersionUsed\":null,\"Attempts\":null,\"Error\":null}" },
    };

    [Theory, MemberData(nameof(RejectedWithRequest))]
    public void Reply_outside_the_documented_shape_stops_the_session(string operation, string arguments, string reply)
    {
        var error = Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult(operation, JsonNode.Parse(arguments)!.AsObject(), JsonNode.Parse(reply)));
        Assert.Equal("Malformed hardware worker reply; session stopped: " + operation, error.Message);
        Assert.True(WorkerProtocol.RequiresSessionReset(true, error));
    }

    // The SaveProjectCopy tool (ProjectLifecycleProjectTools.SaveAsProject) through the production WorkerClient and the
    // PlcWorker fixture: binding, Save As, binding again.
    [Theory, InlineData("19"), InlineData("21")]
    public async Task Save_project_copy_runs_through_the_worker_client(string release)
    {
        using var worker = Worker(release);
        string original = @"C:\fixture\P.ap" + release, copy = @"C:\fixture\copy\P.ap" + release;
        var tools = new ProjectLifecycleProjectTools(new ProjectLifecyclePortService(Call(worker), () => true, () => original, release));
        var result = await Task.Run(() => tools.SaveAsProject(copy));
        Assert.Equal(original, result.PreviousProjectFile);
        Assert.Equal(copy, result.NewProjectFile);
        Assert.False(worker.Poisoned);
        Assert.Equal(release, (string?)(await worker.Call("ReadState", new JsonObject(), CancellationToken.None))?["ReleaseKey"]);
    }

    [Theory, InlineData("19"), InlineData("21")]
    public async Task Malformed_save_as_reply_stops_the_worker_session(string release)
    {
        using var worker = Worker(release);
        var arguments = new JsonObject { ["path"] = @"C:\fixture\malformed\P.ap" + release, ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = @"C:\fixture\P.ap" + release };
        var error = await Assert.ThrowsAsync<IOException>(() => worker.Call("project-lifecycle.ArchiveSaveProjectCopy", arguments, CancellationToken.None));
        Assert.Equal("Malformed hardware worker reply; session stopped: project-lifecycle.ArchiveSaveProjectCopy", error.Message);
        Assert.True(worker.Poisoned);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        Assert.Equal(TiaOpenness.Shared.SessionBehavior.Recovery, refused.Message);
        var tools = new ProjectLifecycleProjectTools(new ProjectLifecyclePortService(Call(worker), () => true, () => "p", release));
        Assert.ThrowsAny<Exception>(() => tools.SaveAsProject(@"C:\fixture\copy\P.ap" + release));
    }

    // RunToolTransaction's scope (EngineeringServicesPortService) through the production WorkerClient and the fixture.
    [Theory, InlineData("14sp1"), InlineData("20")]
    public async Task Transaction_scope_replies_run_through_the_worker_client(string release)
    {
        using var worker = Worker(release);
        var service = new EngineeringServicesPortService(Call(worker), () => @"C:\fixture\P.ap" + release);
        var flags = await Task.Run(() =>
        {
            using var scope = service.BeginTransaction("fixture");
            bool canCommit = scope.CanCommit, requested = scope.CommitRequested;
            scope.Commit();
            return (canCommit, requested);
        });
        Assert.True(flags.canCommit);
        Assert.True(flags.requested);
        Assert.False(worker.Poisoned);
    }

    // GetPlcPutGetAccess and SetPlcPutGetAccess (15.1-21) through HardwareSecurityPortService, WorkerClient and the fixture.
    [Theory, InlineData("16"), InlineData("21")]
    public async Task Put_get_replies_run_through_the_worker_client(string release)
    {
        using var worker = Worker(release);
        var service = new HardwareSecurityPortService(Call(worker), () => true, () => @"C:\fixture\P.ap" + release);
        var (read, written) = await Task.Run(() => (service.GetPutGetAccess("PLC_1"), service.SetPutGetAccess("PLC_1", true, dryRun: false)));
        Assert.True((bool)read["found"]!);
        Assert.True((bool)read["enabled"]!);
        Assert.Equal("PLC_1", (string?)read["device"]);
        Assert.True((bool)written["ok"]!);
        Assert.Equal("True", (string?)written["after"]);
        Assert.False(worker.Poisoned);
    }

    // SeedReferenceHmi (the PLC software port refuses it before dispatch on V14 SP1-V19) with the consumer's own read of the reply
    // (PlcArtifactPortSession.SeedHmiImport deserializes it into ResponseImportBatch).
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Seed_reference_hmi_reply_runs_through_the_worker_client(string release)
    {
        using var worker = Worker(release);
        var reply = await worker.Call("plc-software.SeedReferenceHmi", Seed("HMI_RT_1", release), CancellationToken.None);
        var batch = System.Text.Json.JsonSerializer.Deserialize<ResponseImportBatch>(reply!.ToJsonString())!;
        Assert.Equal(new[] { "Screen_1" }, batch.Imported);
        Assert.Empty(batch.Failed!);
        Assert.False(worker.Poisoned);
    }

    // The scaffold primitives (the worker serves them on V20/V21; older releases refuse them with NotSupportedException) through
    // ProjectLifecyclePortService, WorkerClient and the fixture.
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Scaffold_primitive_replies_run_through_the_worker_client(string release)
    {
        using var worker = Worker(release);
        var service = new ProjectLifecyclePortService(Call(worker), () => true, () => @"C:\fixture\P.ap" + release, release);
        var (device, compile, connected) = await Task.Run(() =>
        {
            var connected = service.Primitive<ResponseMessage>("connect");
            var device = service.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V3.0", "PLC_1", "S7-1500");
            var compile = service.CompileAndDiagnosePlc("PLC_1");
            service.ImportFromDocuments("PLC_1", "", @"C:\fixture\documents", "Main");
            service.GetHmiProgramInfo("HMI_RT_1");
            service.EnsureUnifiedHmiConnection("HMI_RT_1", "HMI_Connection_1", "PLC_1");
            service.EnsureUnifiedHmiScreen("HMI_RT_1", "Main", 800, 480);
            return (device, compile, connected);
        });
        Assert.Equal("fixture connect", connected.Message);
        Assert.True(device.Ok);
        Assert.Equal("PLC_1", device.DeviceName);
        Assert.Equal("Success", compile.State);
        Assert.Equal(0, compile.ErrorCount);
        Assert.False(worker.Poisoned);
    }

    public static TheoryData<string, string> MalformedFamilyReplies() => new()
    {
        { "16", "put-get-read" }, { "21", "put-get-read" }, { "16", "put-get-write" }, { "21", "put-get-write" },
        { "20", "seed" }, { "21", "seed" }, { "20", "scaffold" }, { "21", "scaffold" },
    };

    // The fixture leaves one member out when the request names "malformed": the host stops the session.
    [Theory, MemberData(nameof(MalformedFamilyReplies))]
    public async Task Malformed_family_reply_stops_the_worker_session(string release, string reply)
    {
        using var worker = Worker(release);
        var (operation, arguments) = reply switch
        {
            "put-get-read" => ("project-security.ExecuteHardwareSecurity", Security("GetPlcPutGetAccess", "malformed")),
            "put-get-write" => ("project-security.ExecuteHardwareSecurity", Security("SetPlcPutGetAccess", "malformed")),
            "seed" => ("plc-software.SeedReferenceHmi", Seed("malformed", release)),
            _ => ("project-lifecycle.ExecuteProjectScaffoldPrimitive", new JsonObject { ["request"] = new JsonObject { ["Kind"] = "hmiInfo",
                ["Text"] = new JsonArray("malformed"), ["Numbers"] = new JsonArray(), ["Flags"] = new JsonArray() } }),
        };
        var error = await Assert.ThrowsAsync<IOException>(() => worker.Call(operation, arguments, CancellationToken.None));
        Assert.Equal("Malformed hardware worker reply; session stopped: " + operation, error.Message);
        Assert.True(worker.Poisoned);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        Assert.Equal(TiaOpenness.Shared.SessionBehavior.Recovery, refused.Message);
    }

    private static JsonObject Security(string operation, string device) => new()
    {
        ["request"] = new JsonObject { ["Operation"] = operation, ["DeviceName"] = device, ["Enable"] = true, ["Action"] = "set", ["DryRun"] = false },
        ["dryRun"] = false,
    };

    private static JsonObject Seed(string softwarePath, string release) => new()
    {
        ["request"] = new JsonObject { ["Kind"] = "screens", ["SoftwarePath"] = softwarePath, ["FolderPath"] = "", ["Directory"] = @"C:\fixture\seed" },
        ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = @"C:\fixture\P.ap" + release,
    };

    private static Func<string, JsonObject, JsonNode?> Call(WorkerClient worker)
        => (operation, arguments) => worker.Call(operation, arguments, CancellationToken.None).GetAwaiter().GetResult();

    private static WorkerClient Worker(string release)
    {
        Assert.True(File.Exists(Fixture), Fixture);
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(Fixture)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
        return new WorkerClient(release, Fixture, Path.GetDirectoryName(Fixture)!, true) { Bundled = false };
    }
}

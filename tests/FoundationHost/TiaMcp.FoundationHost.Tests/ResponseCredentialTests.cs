extern alias toolpipeline;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.Testing;
using TiaMcp.Logic.V4;
using Xunit;
using Contracts = toolpipeline::TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost.Tests;

public sealed class ResponseCredentialTests
{
    [Theory]
    [InlineData("plc")]
    [InlineData("engineering")]
    [InlineData("hmiExchange")]
    [InlineData("hmiInspection")]
    [InlineData("library")]
    [InlineData("unifiedHmi")]
    [InlineData("runtime")]
    [InlineData("drive")]
    [InlineData("hardware")]
    [InlineData("hardwareNetwork")]
    [InlineData("classicMotion")]
    [InlineData("security")]
    [InlineData("optionalPackage")]
    [InlineData("session")]
    [InlineData("hostEvidence")]
    [InlineData("plcTestEvidence")]
    public void Evidence_hides_credentials_and_preserves_status(string family)
    {
        var fields = JsonNode.Parse("""
            {"newPassword":"planted-1","umacPassword":"planted-2","serverPassword":"planted-3",
             "serviceClientSecret":"planted-4","apiKey":"planted-5","signing_key":["planted-6","planted-7"],
             "authorization":"Operators","secret":"configured","credential":"provider","pass.phrase":["planted-8"],
             "PWD":"planted-9","s_k":"planted-10","plcAccessKey":"planted-11","privateKey":"planted-12","secret-key":"planted-13","passwd":"planted-14",
             "passwordProvided":true,"isSafetyOfflineProgramPasswordSet":false,"passwordPolicy":{"minimum":8},
             "onlinePassword":{"answered":true},"passwordReadbackPossible":false,"credentialCount":3,
             "masterSecret":{"state":"set"},"authorizationOptions":[1,"manual"],"keyword":"visible","accessKeyId":"identifier"}
            """);
        var evidence = new JsonObject { ["operationSuccess"] = true, ["observations"] = new JsonArray(fields!.DeepClone()) };
        var reply = new Contracts.ResponseMessage { Message = "Observed", Meta = evidence };
        JsonNode data;
        if (family == "hostEvidence")
        {
            HostBehavior.SanitizeEvidence(evidence);
            data = evidence;
        }
        else if (family == "plcTestEvidence")
        {
            var outcome = new JsonObject { ["data"] = new JsonObject { ["result"] = evidence } };
            data = PlcTestEvidence.Normalize(outcome, "passed")["details"]!["result"]!;
        }
        else
        {
            using var release = Contracts.HardwareContract.UseRelease("21");
            var result = Map(family, reply);
            Assert.DoesNotContain("planted-", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            data = result.StructuredContent!["data"]!;
        }
        Assert.DoesNotContain("planted-", data.ToJsonString());
        var row = (data["observations"] ?? data["evidence"]!["observations"])![0]!.AsObject();
        foreach (string name in new[] { "newPassword", "umacPassword", "serverPassword", "serviceClientSecret", "apiKey", "signing_key",
            "pass.phrase", "PWD", "s_k", "plcAccessKey", "privateKey", "secret-key", "passwd" })
            Assert.False(row.ContainsKey(name), name);
        Assert.Equal("Operators", (string?)row["authorization"]);
        Assert.Equal("configured", (string?)row["secret"]);
        Assert.Equal("provider", (string?)row["credential"]);
        Assert.Equal(3, (int)row["credentialCount"]!);
        Assert.Equal("set", (string?)row["masterSecret"]!["state"]);
        Assert.True(JsonNode.DeepEquals(fields!["authorizationOptions"], row["authorizationOptions"]));
        Assert.Equal("visible", (string?)row["keyword"]);
        Assert.Equal("identifier", (string?)row["accessKeyId"]);
        foreach (var name in new[] { "passwordProvided", "isSafetyOfflineProgramPasswordSet", "passwordPolicy", "onlinePassword", "passwordReadbackPossible" })
        {
            if (family is "optionalPackage" or "session") Assert.False(row.ContainsKey(name), name);
            else Assert.True(JsonNode.DeepEquals(fields[name], row[name]), name);
        }
    }

    [Fact]
    public void Plc_protection_preserves_master_secret_configuration_states_and_the_policy_alphabet()
    {
        var evidence = JsonNode.Parse("""
            {"before":{"masterSecret":"None"},"after":{"masterSecret":"WithPasswordAllDataProtection"},
             "expectedMasterSecret":"WithPassword|WithoutPassword","invalidPasswordCharacters":["#","%"],
             "values":{"MasterSecretConfiguration":"WithoutPassword","masterSecretPassword":"planted-password"},
             "unexpected":{"masterSecret":"future-state","expectedMasterSecret":"WithPassword|future-state","invalidPasswordCharacters":["planted"]}}
            """)!.AsObject();
        evidence["operationSuccess"] = true;
        using var release = Contracts.HardwareContract.UseRelease("21");
        var result = Contracts.HardwareToolContract.MapResult("ManagePlcProtection", new Contracts.ResponseMessage { Meta = evidence }, false, true);
        evidence = result.StructuredContent!["data"]!.AsObject();
        Assert.Equal("None", (string?)evidence["before"]!["masterSecret"]);
        Assert.Equal("WithPasswordAllDataProtection", (string?)evidence["after"]!["masterSecret"]);
        Assert.Equal("WithPassword|WithoutPassword", (string?)evidence["expectedMasterSecret"]);
        Assert.True(JsonNode.DeepEquals(new JsonArray("#", "%"), evidence["invalidPasswordCharacters"]));
        Assert.Equal("WithoutPassword", (string?)evidence["values"]!["MasterSecretConfiguration"]);
        Assert.Equal("future-state", (string?)evidence["unexpected"]!["masterSecret"]);
        Assert.Equal("WithPassword|future-state", (string?)evidence["unexpected"]!["expectedMasterSecret"]);
        Assert.False(evidence["unexpected"]!.AsObject().ContainsKey("invalidPasswordCharacters"));
        Assert.DoesNotContain("planted", evidence.ToJsonString());
    }

    [Fact]
    public void Download_preview_keeps_only_the_existing_mask_in_the_planned_password_fields()
    {
        var plan = new JsonObject();
        foreach (var name in new[] { "moduleAccessPassword", "blockBindingPassword", "masterSecretPassword" })
            plan[name] = "••••";
        var data = new JsonObject { ["plannedChanges"] = plan, ["observed"] = plan.DeepClone() };
        var reply = new CallToolResult { StructuredContent = new JsonObject { ["data"] = data }, Content = [] };
        var mapped = FoundationV4Result.ReconcileWorker(reply, null, "DownloadPlc").StructuredContent!["data"]!;
        Assert.True(JsonNode.DeepEquals(plan, mapped["plannedChanges"]));
        Assert.Empty(mapped["observed"]!.AsObject());
        foreach (var name in plan.Select(p => p.Key).ToArray()) plan[name] = "planted";
        mapped = FoundationV4Result.ReconcileWorker(reply, null, "DownloadPlc").StructuredContent!["data"]!;
        Assert.Empty(mapped["plannedChanges"]!.AsObject());
    }

    [Fact]
    public void Unified_parts_keep_authorization_in_native_scalar_values()
    {
        var evidence = JsonNode.Parse("""
            {"operationSuccess":true,
             "before":{"values":{"Name":"Button1","Authorization":"Operators","apiKey":"planted"}},
             "after":{"values":{"Name":"Button1","Authorization":"Supervisors","clientSecret":"planted"}}}
            """)!.AsObject();
        using var release = Contracts.HardwareContract.UseRelease("21");
        var result = Contracts.UnifiedHmiContract.Map("ManageUnifiedObjectParts", new Contracts.ResponseMessage { Meta = evidence }, true, true);
        var mapped = result.StructuredContent!["data"]!["evidence"]!;
        Assert.Equal("Operators", (string?)mapped["before"]!["values"]!["Authorization"]);
        Assert.Equal("Supervisors", (string?)mapped["after"]!["values"]!["Authorization"]);
        Assert.DoesNotContain("planted", mapped.ToJsonString());
    }

    [Fact]
    public void Unified_property_snapshots_keep_global_scope_persistency_authorization()
    {
        const string property = "GlobalScopePersistencyAuthorization";
        var evidence = new JsonObject {
            ["operationSuccess"] = true,
            ["before"] = new JsonObject { ["values"] = new JsonObject { [property] = "Operators" } },
            ["after"] = new JsonObject { ["values"] = new JsonObject { [property] = "Supervisors" } }
        };
        using var release = Contracts.HardwareContract.UseRelease("21");
        var result = Contracts.HmiInspectionContract.Map("SetUnifiedObjectProperties", new Contracts.ResponseMessage { Meta = evidence }, true, true);
        var data = result.StructuredContent!["data"]!;
        Assert.Equal("Operators", (string?)data["before"]!["values"]![property]);
        Assert.Equal("Supervisors", (string?)data["after"]!["values"]![property]);
    }

    [Fact]
    public void Unified_runtime_settings_keep_authorization_in_snapshots_and_requested_and_proposed_strings()
    {
        const string field = "HmiUpssRuntimeSettings.GlobalScopePersistencyAuthorization";
        var before = new JsonObject { ["path"] = "/RuntimeSettings/HmiUpssRuntimeSettings/GlobalScopePersistencyAuthorization",
            ["type"] = "System.String", ["value"] = "Operators", ["status"] = "ok", ["publicSetter"] = true };
        var after = before.DeepClone();
        after["value"] = "Supervisors";
        var evidence = new JsonObject {
            ["operationSuccess"] = true, ["before"] = new JsonObject { [field] = before }, ["readback"] = new JsonObject { [field] = after },
            ["requested"] = new JsonObject { [field] = "Supervisors" }, ["proposed"] = new JsonObject { [field] = "Supervisors" }
        };
        using var release = Contracts.HardwareContract.UseRelease("21");
        var result = Contracts.RuntimeToolContract.Map("SetUnifiedRuntimeSettings", new Contracts.ResponseMessage { Meta = evidence }, false, true);
        var mapped = result.StructuredContent!["data"]!;
        foreach (string name in new[] { "before", "readback", "requested", "proposed" })
            Assert.True(JsonNode.DeepEquals(evidence[name], mapped[name]), name);
    }

    [Fact]
    public void Parsed_open_pipe_response_hides_credentials_and_retains_protocol_data()
    {
        var reply = new Contracts.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = true, ["responseLine"] = """{"values":[{"apiKey":"planted-wire","message":"ready"}]}"""
        } };
        var result = Contracts.RuntimeToolContract.Map("InvokeUnifiedOpenPipe", reply, true, true);
        var row = result.StructuredContent!["data"]!["response"]!["values"]![0]!;
        Assert.Equal("ready", (string?)row["message"]);
        Assert.False(row.AsObject().ContainsKey("apiKey"));
    }

    private static CallToolResult Map(string family, Contracts.ResponseMessage reply) => family switch
    {
        "plc" => Contracts.PlcToolContract.Map("DescribePlcBlockLogic", reply, false, true),
        "engineering" => Contracts.EngineeringToolContract.Map("ImportTechnologyObjectsFromDirectory", reply, false),
        "hmiExchange" => Contracts.HmiExchangeContract.Map("ExportHmiTagTable", reply, false, true),
        "hmiInspection" => Contracts.HmiInspectionContract.Map("ListObjectChildren", reply, false, true),
        "library" => Contracts.LibraryToolContract.Map("GetLibraryOverview", reply, false),
        "unifiedHmi" => Contracts.UnifiedHmiContract.Map("GetUnifiedScreenBranch", reply, false, true),
        "runtime" => Contracts.RuntimeToolContract.Map("GetPlcLiveValuesS7", reply, true, true),
        "drive" => Contracts.DriveToolContract.Map("ListDriveObjects", reply, false, true),
        "hardware" => Contracts.HardwareContract.Map("GetDeviceAttributes", reply, false, true),
        "hardwareNetwork" => Contracts.HardwareToolContract.MapResult("GetPlcPutGetAccess", reply, true, true),
        "classicMotion" => Contracts.ClassicMotionToolContract.Map("ManageClassicHmiScript", reply, true, true),
        "security" => Contracts.SecurityToolContract.MapResult("GetProjectUserManagement", reply, true, true),
        "optionalPackage" => Contracts.OptionalPackageContract.Map("RunTestSuiteCase", reply, false),
        "session" => Contracts.SessionToolContract.Map("GetSessionState", reply, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}

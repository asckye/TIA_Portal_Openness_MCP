extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;
using HardwareContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;
using HardwareSecurityPortService = enginehost::TiaMcpServer.Siemens.Services.HardwareSecurityPortService;
using SecurityToolContract = enginehost::TiaMcpServer.ModelContextProtocol.SecurityToolContract;
using ResponseMessage = enginehost::TiaMcpServer.ModelContextProtocol.ResponseMessage;

namespace TiaMcp.FoundationHost.Tests;

public sealed class SecurityAuditBoundaryTests
{
    [Theory]
    [InlineData("17", "ProtectAllConfiguration")]
    [InlineData("18", " UNPROTECTALLCONFIGURATION ")]
    [InlineData("19", "protectALLConfiguration")]
    [InlineData("20", "UnprotectAllConfiguration")]
    public void Normalized_protection_gate_never_dispatches_an_unavailable_action(string release, string action)
    {
        using var selected = HardwareContract.UseRelease(release);
        int calls = 0;
        var service = new HardwareSecurityPortService((_, _) => { calls++; return new JsonObject(); }, () => true, () => "fixture.ap" + release);
        var response = service.ManagePlcProtection("[\"PLC_1\"]", "[]", action, confirmChange: true, dryRun: false);
        Assert.Equal(0, calls);
        Assert.Equal("NotSupportedOnVersion", (string?)response.Meta!["status"]);
        Assert.False((bool)response.Meta["mayHaveChanged"]!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Put_get_preview_mode_is_forwarded_and_only_real_writes_request_confirmation(bool preview)
    {
        JsonObject? sent = null;
        var service = new HardwareSecurityPortService((_, arguments) => { sent = arguments; return new JsonObject { ["ok"] = true }; },
            () => true, () => "fixture.ap21");
        service.SetPutGetAccess("PLC_1", true, preview);
        Assert.NotNull(sent);
        Assert.Equal(preview, (bool)sent["dryRun"]!);
        Assert.Equal(preview, (bool)sent["request"]!["DryRun"]!);
        Assert.Equal(!preview, sent.ContainsKey("confirm"));
        Assert.Equal(!preview, sent.ContainsKey("expectedProjectFile"));
    }

    [Fact]
    public void Password_policy_refusal_maps_to_unstarted_precondition_without_session_reset()
    {
        var response = new ResponseMessage { Message = "Policy refusal", Meta = new JsonObject {
            ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
            ["status"] = "PreconditionFailed", ["precondition"] = "password-policy", ["refusal"] = "Read ManagePasswordPolicy."
        } };
        var result = SecurityToolContract.MapResult("ManagePlcProtection", response, false, true);
        var content = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content));
        var body = JsonNode.Parse(content.Text)!;
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"failureCode\":\"UNSUPPORTED_CAPABILITY\",\"mayHaveChanged\":false,\"device\":\"PLC_1\",\"message\":\"No approved name\"}")]
    [InlineData("{\"ok\":false,\"failureCode\":\"UNSUPPORTED_CAPABILITY\",\"mayHaveChanged\":false,\"message\":\"Not Boolean\"}")]
    [InlineData("{\"ok\":true,\"dryRun\":true,\"mayHaveChanged\":false,\"writeOutcomeKnown\":true,\"device\":\"PLC_1\",\"attributeName\":\"approved\",\"before\":false,\"requestedValue\":true,\"message\":\"Preview\"}")]
    public void Put_get_wire_accepts_only_the_exact_new_refusal_and_preview_shapes(string json)
    {
        var arguments = new JsonObject { ["request"] = new JsonObject { ["Operation"] = "SetPlcPutGetAccess" } };
        var reply = JsonNode.Parse(json)!.AsObject();
        WorkerProtocol.ValidateExchangeResult("project-security.ExecuteHardwareSecurity", arguments, reply);
        reply["extra"] = true;
        Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult("project-security.ExecuteHardwareSecurity", arguments, reply));
    }
}

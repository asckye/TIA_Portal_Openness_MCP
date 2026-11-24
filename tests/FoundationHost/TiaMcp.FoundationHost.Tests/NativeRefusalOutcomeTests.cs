extern alias adaptertests;
extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;
using Native = adaptertests::TiaMcp.Adapters.Contracts.Tests.NativeRefusalProbeTests;
using Motion = adaptertests::TiaMcp.Adapters.Contracts.Tests.MotionConnectionRefusalTests;
using Xunit;

public sealed class NativeRefusalOutcomeTests
{
    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(null, false)]
    [InlineData(true, true)][InlineData(null, true)]
    public void Standalone_safety_login_retains_its_readback_across_the_worker_boundary(bool? loggedOn, bool channelFailure)
    {
        using var release = Host.HardwareContract.UseRelease("17");
        string scenario = loggedOn switch { false => "off", true => "on-cleanup", null => "unreadable-cleanup" };
        var reply = SafetyLoginFrameworkTests.RunFixture("standalone", scenario)["result"]!;
        var response = new Host.ResponseMessage { Message = (string)reply["Message"]!, Meta = reply["Meta"]!.AsObject() };
        var body = Host.SecurityToolContract.Invoke("ManagePlcSafety", false, true, () => {
            if (channelFailure)
                throw new TiaMcp.Logic.V4.HostBehavior.UncertainWorkerFailure("The write outcome is unknown.",
                    new JsonObject { ["Meta"] = response.Meta.DeepClone() });
            return response;
        }).StructuredContent!;
        Assert.Equal(loggedOn == false ? "ACCESS_DENIED" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        var evidence = loggedOn == false ? body["data"] : body["error"]?["details"]?["evidence"];
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, (string?)evidence?["nativeExceptionType"]);
        if (loggedOn != false)
        {
            Assert.Contains(loggedOn == true ? "was logged on" : "could not be read", (string?)body["error"]?["message"]);
            Assert.Contains("cleanup logoff also failed", (string?)body["error"]?["message"]);
        }
        Assert.DoesNotContain("private ", body.ToJsonString());
    }

    public static IEnumerable<object[]> Outcomes() =>
        from tool in new[] { "ManagePortInterconnection", "ManageMotionAxis", "ConfigureMotionHardwareConnection", "InvokeObject", "ManageDeviceUsers" }
        from outcome in new[] { "unchanged", "changed", "missing" }
        select new object[] { tool, outcome };

    [Theory, MemberData(nameof(Outcomes))]
    public void Fresh_proof_survives_the_tool_family_result_mapping(string tool, string outcome)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var meta = tool switch {
            "ManagePortInterconnection" => Native.PortConnect(outcome == "changed" ? "partner" : outcome),
            "InvokeObject" => Native.SetAttribute(outcome == "changed" ? "sibling" : outcome == "missing" ? "lookup" : outcome),
            "ManageDeviceUsers" => Native.DeviceUser("create", outcome == "changed" ? "added" : outcome),
            _ => Motion.MotionConnect(tool, outcome == "changed" ? "torque" : outcome == "missing" ? "lookup" : outcome)
        };
        var response = new Host.ResponseMessage { Message = "Native operation failed", Meta = JsonSerializer.SerializeToNode(meta)!.AsObject() };
        var body = (tool switch {
            "ManagePortInterconnection" => Host.HardwareToolContract.MapResult(tool, response, false, true),
            "InvokeObject" => Host.HmiInspectionContract.Map(tool, response, true, true),
            "ManageDeviceUsers" => Host.SecurityToolContract.MapResult(tool, response, false, true),
            _ => Host.ClassicMotionToolContract.Map(tool, response, false, true)
        }).StructuredContent!;
        Assert.Equal(outcome == "unchanged" ? "failed" : "unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal(outcome == "unchanged" ? "NATIVE_OPERATION_FAILED" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Empty_resolved_library_folder_has_a_public_precondition(bool preview)
    {
        var reply = Native.SynchronizeSelection(preview, "empty");
        var body = Host.LibraryToolContract.Map("SynchronizeLibrary", reply, !preview).StructuredContent!;
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("selection", (string?)body["error"]?["details"]?["parameter"]);
    }

    [Fact]
    public void SetAttribute_preview_dispatches_a_read_only_worker_request()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker));
        int requests = 0;
        var service = new Services.ReflectionPortService((_, arguments) => {
            requests++;
            Assert.Equal(true, (bool?)arguments["dryRun"]);
            Assert.Null(arguments["confirm"]);
            Assert.Equal("PreviewSetAttribute", (string?)arguments["request"]?["Operation"]);
            Assert.Equal("HeaderAuthor", (string?)arguments["request"]?["PropertyPath"]);
            return new JsonObject { ["Observation"] = new JsonObject { ["Message"] = "Read-only attribute", ["Meta"] = new JsonObject {
                ["success"] = false, ["mayHaveChanged"] = false, ["invocationAttempted"] = false, ["status"] = "PreconditionFailed",
                ["precondition"] = "attribute-not-writable", ["parameter"] = "args", ["refusal"] = "Attribute is read-only."
            } } };
        }, () => "");
        NativeValue Value(string text) => new NativeValue(JsonSerializer.SerializeToElement(text));
        var body = new Host.ReflectionTools(service).InvokeObjectV4("Block", "FC", "SetAttribute", [Value("HeaderAuthor"), Value("Author")],
            "PLC", allowWrite: true, dryRun: true).StructuredContent!;
        Assert.Equal(1, requests);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("args", (string?)body["error"]?["details"]?["parameter"]);
    }
}

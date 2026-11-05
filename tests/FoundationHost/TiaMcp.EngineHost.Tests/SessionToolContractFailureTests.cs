extern alias foundationhost;
using ModelContextProtocol;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class SessionToolContractFailureTests
{
    [Fact]
    public void Known_no_mutation_worker_refusal_keeps_its_reason_when_wrapped()
    {
        const string reason = "The worker refused Save As before changing the project.";
        var refusal = new foundationhost::TiaMcp.FoundationHost.WorkerOperationException(reason, -32603, "rejected-before-operation");
        refusal.Data["foundationKnownNoMutation"] = true;
        var exception = new McpException("Unexpected error saving the project.", refusal, McpErrorCode.InternalError);
        var body = SessionToolContract.Failure("SaveProjectCopy", exception, true, false, true).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(reason, (string?)body["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
    }

    [Fact]
    public void Internal_error_carries_the_request_diagnostic_id()
    {
        using var request = InvocationJournal.UseCorrelation("host-diagnostic-fixture");
        var body = SessionToolContract.Failure("GetPortalInfo", new InvalidOperationException("Private host detail"), false, false, false)
            .StructuredContent!;
        Assert.Equal("INTERNAL_ERROR", (string?)body["error"]?["code"]);
        Assert.Equal("host-diagnostic-fixture", (string?)body["error"]?["details"]?["diagnosticId"]);
        Assert.DoesNotContain("Private host detail", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mcp_internal_error_without_a_native_call_is_internal_error(bool writes)
    {
        var body = SessionToolContract.Failure("SaveProject", new McpException("Private detail", McpErrorCode.InternalError),
            writes, false, false).StructuredContent!;
        Assert.Equal("INTERNAL_ERROR", (string?)body["error"]?["code"]);
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
    }

    [Fact]
    public void Mcp_internal_error_after_an_issued_write_stays_unknown()
    {
        var body = SessionToolContract.Failure("SaveProjectCopy", new McpException("Write failed", McpErrorCode.InternalError),
            true, false, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.True((bool?)body["meta"]?["requiresSessionReset"]);
    }
}

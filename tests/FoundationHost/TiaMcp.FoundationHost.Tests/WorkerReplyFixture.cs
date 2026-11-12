using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;

internal sealed class WorkerReplyFixture
{
    private readonly WorkerSessionOutcomeState outcome = new();
    private readonly WorkerSessionRecovery recovery;
    internal WorkerReplyFixture() => recovery = new(outcome, () => false, () => { }, () => { }, () => { }, () => { });

    internal JsonNode Accept(string operation, JsonObject arguments, JsonNode reply)
    {
        var request = new ChannelRequest(1, "adapter." + operation, arguments.ToJsonString(), (_, _) => { });
        var response = recovery.Dispatch(request, _ => {
            if ((bool?)reply["RequiresSessionReset"] == true) outcome.MarkUncertain();
            return ChannelResponse.Success(reply.ToJsonString());
        });
        response = recovery.Synchronize(request, response);
        if (response.Failure is { } failure) throw WorkerOperationException.FromChannelFailure(failure);
        return reply;
    }
}

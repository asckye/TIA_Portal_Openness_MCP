extern alias plcsim;
using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;

namespace TiaMcp.PlcWorker;

// This module has no project/session/Openness prerequisite. It runs on the same
// net48 worker lane even when that process has no Siemens Engineering SDK loaded.
internal static class PlcSimAdvancedWorkerModule
{
    internal const string Operation = "adapter.runtime-channels.ExecutePlcSimAdvanced";
    internal static readonly plcsim::TiaMcp.PlcWorker.Runtime.PlcSimAdvancedAdapter Adapter = new plcsim::TiaMcp.PlcWorker.Runtime.PlcSimAdvancedAdapter();
    internal static ChannelResponse Dispatch(ChannelRequest request)
    {
        bool entered = false, readOnly = false;
        try
        {
            if (request.Method != Operation) throw new NotSupportedException("Unknown PLCSIM Advanced module operation.");
            var values = WorkerJson.ParseArguments(request.ArgumentsJson);
            var method = typeof(plcsim::TiaMcp.PlcWorker.Runtime.PlcSimAdvancedAdapter).GetMethod("ExecutePlcSimAdvanced")!;
            var arguments = WorkerJson.Arguments(method.GetParameters(), values);
            var input = (JsonObject)arguments[0]!;
            readOnly = (string?)input["operation"] is "ReadPlcSimAdvancedInstances" or "ReadPlcSimAdvancedTags"
                || (bool?)input["arguments"]?["dryRun"] == true;
            entered = true;
            return ChannelResponse.Success(WorkerJson.Serialize(Adapter.ExecutePlcSimAdvanced(input)));
        }
        catch (Exception error)
        {
            var cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
            var classification = WorkerFailurePolicy.Classify(cause, entered, readOnly);
            if (classification.Outcome == ChannelOutcome.Unknown)
            {
                // Simulation uncertainty belongs to the virtual controller, never
                // to the TIA project/channel recovery latch. Preserve the unknown
                // verdict without retrying any simulator call.
                var meta = JsonSerializer.SerializeToNode(TiaMcp.Adapters.Contracts.LegacyResponseMetadata.Basic(null, false))!.AsObject();
                meta["operationSuccess"] = false;
                meta["apiCallSuccess"] = false;
                meta["status"] = "ReadOrWriteFailed";
                meta["error"] = cause.Message;
                meta["mayHaveChanged"] = true;
                meta["outcomeUnknown"] = true;
                return ChannelResponse.Success(WorkerJson.Serialize(new plcsim::TiaMcp.PlcWorker.Runtime.PlcSimAdvancedReport {
                    Ok = false, Message = "PLCSIM Advanced operation outcome is unknown: " + cause.Message,
                    Data = new JsonObject(), Meta = meta
                }));
            }
            return ChannelResponse.Error(new ChannelFailureException(cause.Message, classification.Code, classification.Outcome, WorkerJson.Evidence(cause)));
        }
    }
}

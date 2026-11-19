using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace TiaMcp.Adapters.Contracts
{
    public interface IWorkerFailureEvidence
    {
        public string? EvidenceJson { get; }
    }

    public static class MutationEvidence
    {
        public static bool ProvesNoMutation(IReadOnlyDictionary<string, object?> meta)
        {
            bool Flag(string key) => meta.TryGetValue(key, out var value) && value is true;
            return meta.TryGetValue("mayHaveChanged", out var changed) && changed is false
                && !Flag("writeOutcomeUnknown") && !Flag("connectionUnavailable") && !Flag("unrestoredWrite")
                && (!meta.TryGetValue("cleanupRequired", out var cleanup) || cleanup == null || cleanup is ICollection { Count: 0 });
        }

        public static string FailureCode(Exception error)
        {
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is AdapterPreconditionException refusal)
                    return refusal.Condition == "not-found" ? "NOT_FOUND" : refusal.IsArgument ? "INVALID_ARGUMENT" : "PRECONDITION_FAILED";
                string? status = cause switch {
                    HardwareAddressingException hardware => hardware.Status,
                    PlcSoftwareException plc => plc.Code,
                    TiaMcpServer.Siemens.PortalException { Code: TiaMcpServer.Siemens.PortalErrorCode.NotFound } => "NOT_FOUND",
                    _ => null
                };
                if (status != null) return status switch {
                    "NotFound" => "NOT_FOUND", "OpennessError" or "ReadOrWriteFailed" => "NATIVE_OPERATION_FAILED",
                    "InvalidState" => "PRECONDITION_FAILED", _ => status
                };
            }
            return error.GetBaseException() switch {
                FileNotFoundException or DirectoryNotFoundException => "NOT_FOUND",
                ArgumentException or FormatException => "INVALID_ARGUMENT",
                NotSupportedException => "UNSUPPORTED_CAPABILITY",
                InvalidOperationException => "PRECONDITION_FAILED",
                _ => "NATIVE_OPERATION_FAILED"
            };
        }

        public static void RecordStepFailure(Exception error, Dictionary<string, object?> meta)
        {
            if (!ProvesNoMutation(meta)) return;
            meta["v4FailureCode"] = FailureCode(error);
            error.Data["foundationKnownNoMutation"] = true;
            if (meta.TryGetValue("phase", out var phase)) error.Data["phase"] = phase;
            if (error.GetBaseException() is AdapterPreconditionException { IsArgument: false, Condition: string condition }
                && condition != "not-found")
            {
                meta["status"] = "PreconditionFailed";
                meta["precondition"] = condition;
                meta["refusal"] = error.GetBaseException().Message;
            }
        }
    }
}

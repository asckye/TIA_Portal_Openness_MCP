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

        public static string FailureCode(Exception error) => error.GetBaseException() switch {
            AdapterPreconditionException { Condition: "not-found" } or FileNotFoundException or DirectoryNotFoundException => "NOT_FOUND",
            AdapterPreconditionException { IsArgument: false } => "PRECONDITION_FAILED",
            ArgumentException or FormatException or AdapterPreconditionException => "INVALID_ARGUMENT",
            NotSupportedException => "UNSUPPORTED_CAPABILITY",
            HardwareAddressingException hardware => hardware.Status == "InvalidState" ? "PRECONDITION_FAILED" : hardware.Status,
            PlcSoftwareException plc => plc.Code == "InvalidState" ? "PRECONDITION_FAILED" : plc.Code,
            InvalidOperationException => "PRECONDITION_FAILED",
            _ => "NATIVE_OPERATION_FAILED"
        };

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

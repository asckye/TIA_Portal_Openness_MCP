using System;
using System.IO;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public static class StepFailureStatus
    {
        public static NotSupportedException UnsupportedByTarget(string reason)
            => new NotSupportedException(reason) { Data = { ["unsupportedBy"] = "target" } };

        public static NotSupportedException UnsupportedByInstallation(string reason)
            => new NotSupportedException(reason) { Data = { ["unsupportedBy"] = "installation" } };

        public static void RecordTarget(Exception error, IDictionary<string, object?> meta)
        {
            if (!meta.TryGetValue("mayHaveChanged", out var changed) || changed is not false
                || meta.TryGetValue("mayHaveWrittenFiles", out var written) && written is true) return;
            var cause = error.GetBaseException();
            if (cause is AdapterPreconditionException { Condition: "output-parent-missing" or "service-not-provided" })
                EngineeringCredentialRules.TryRecordRefusal(cause, meta);
            if (cause.Data["unsupportedBy"] is string source && source is "target" or "installation")
                meta["unsupportedBy"] = source;
            if (cause.Data["target"] is not string target) return;
            meta["target"] = target;
            meta["candidates"] = cause.Data["candidates"];
            if (cause.Data["missingDeviceTarget"] is string missingTarget) meta["missingDeviceTarget"] = missingTarget;
        }

        public static string Of(Exception error) => error.GetBaseException() switch
        {
            PlcSoftwareException plc => plc.Code,
            HardwareAddressingException hardware => hardware.Status,
            AdapterPreconditionException { Condition: "not-found" } => "NotFound",
            FileNotFoundException _ => "NotFound",
            DirectoryNotFoundException _ => "NotFound",
            AdapterPreconditionException { IsArgument: true } => "InvalidParams",
            ArgumentException _ => "InvalidParams",
            FormatException _ => "InvalidParams",
            NotSupportedException _ => "NotSupportedOnVersion",
            _ => "ReadOrWriteFailed"
        };

        public static string? Parameter(Exception error) => error.GetBaseException() switch
        {
            PlcSoftwareException plc => plc.Data["parameter"] as string,
            AdapterPreconditionException precondition => precondition.ParamName,
            ArgumentException argument => argument.ParamName,
            _ => null
        };
    }
}

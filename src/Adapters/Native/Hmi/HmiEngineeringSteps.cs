using TiaMcp.Adapters.Native.Engineering;
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<string, object>? ResolveEngineeringHmiSoftware { get; set; }
        public Action<Dictionary<string, object?>>? HmiReadFault { get; set; }
        public Func<Dictionary<string, object?>?>? ReadEngineeringHmiReadFault { get; set; }
        public Action<string, string>? WriteEngineeringHmiTrace { get; set; }
        private Dictionary<string, object?>? hmiReadFault;
        internal HardwareAddressingReply RunHmiStep(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject, bool writing = true)
        {
            var operation = action;
            action = !writing ? operation : meta => EngineeringWriteJournal.Run(meta, () => operation(meta),
                () => EngineeringPlcSession.AcquireHmiEditAccess());
            if (EngineeringServicesStep != null)
            {
                var reply = EngineeringServicesStep(tool, requiresProject, meta => {
                    meta["mayHaveChanged"] = false;
                    return action(meta);
                });
                if (!writing)
                {
                    reply.OutcomeUnknown = false;
                    reply.Meta["writeOutcomeUnknown"] = false;
                }
                return reply;
            }
            return RunHmiStepCore(tool, action, requiresProject, writing);
        }
        private HardwareAddressingReply RunHmiStepCore(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject, bool writing = true)
        {
            var reply = new HardwareAddressingReply();
            var meta = LegacyResponseMetadata.Step(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, tool);
            reply.Meta = meta;
            bool returnedFailure = false;
            try
            {
                if (requiresProject && EngineeringPlcSession.IsProjectNull())
                {
                    meta["error"] = "Project is null"; meta["status"] = "InvalidState"; meta["operationSuccess"] = false;
                    reply.Message = "Project is null"; return reply;
                }
                meta["mayHaveChanged"] = false;
                reply.Message = action(meta);
                LegacyResponseMetadata.Complete(meta);
                returnedFailure = Equals(meta["operationSuccess"], false) && !meta.ContainsKey("rollbackOutcome")
                    && !(meta.TryGetValue("unrestoredWrite", out var unrestored) && unrestored is true)
                    && !(meta.TryGetValue("portalLost", out var lost) && lost is true)
                    && (tool == "ImportDeviceAml" && meta.TryGetValue("apiCallSuccess", out var api) && api is true
                        && meta.TryGetValue("status", out var aml) && Equals(aml, "ImportFailed")
                    || tool == "ImportPlcBlockVerified" && meta.TryGetValue("importReturned", out var imported) && imported is true
                        && meta.TryGetValue("status", out var block) && Equals(block, "DocumentReadbackMismatch"));
            }
            catch (Exception error)
            {
                for (Exception? cause = error; cause != null; cause = cause.InnerException)
                    if (cause.Data["nativeResultEvidence"] is Dictionary<string, object?> recovery)
                        foreach (var pair in recovery) meta[pair.Key] = pair.Value;
                EngineeringPlcSession.RecordExportFailure(error);
                meta["error"] = error.ToString();
                meta["nativeExceptionType"] = error.GetBaseException().GetType().FullName;
                meta["nativeMessage"] = error.GetBaseException().Message;
                meta["operationSuccess"] = false; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = StepFailureStatus.Of(error);
                meta["parameter"] = StepFailureStatus.Parameter(error);
                StepFailureStatus.RecordTarget(error, meta);
                if (tool == "ManagePlcCertificate" && meta.TryGetValue("phase", out var phase) && Equals(phase, "get-certificate-template"))
                {
                    var messages = new List<string?>();
                    for (Exception? cause = error; cause != null; cause = cause.InnerException)
                    {
                        messages.Add(cause.Message);
                        if (cause is Siemens.Engineering.EngineeringException native) RecordEngineeringDetails(native, meta, messages);
                    }
                    string chain = NativeFailureText.Compose(null, null, messages);
                    if (chain.Length != 0) meta["nativeMessage"] = chain;
                }
                else if (error.GetBaseException() is Siemens.Engineering.EngineeringException engineering)
                {
                    var messages = new List<string?>();
                    RecordEngineeringDetails(engineering, meta, messages);
                    string details = NativeFailureText.Compose(null, null, messages);
                    if (details.Length != 0) meta["nativeMessage"] = details;
                }
                if (meta["status"] as string == "ReadOrWriteFailed") EngineeringCredentialRules.TryRecordRefusal(error, meta);
                if (HmiReadSafety.ConnectionUnavailable(error)) RecordHmiFault(meta);
                MutationEvidence.RecordStepFailure(error, meta);
                reply.Message = tool + " failed";
            }
            reply.OutcomeUnknown = writing && !MutationEvidence.ProvesNoMutation(meta)
                && (meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true
                || meta.TryGetValue("connectionUnavailable", out var unavailable) && unavailable is true
                || !returnedFailure && !(meta.TryGetValue("rollbackOutcome", out var rolledBack) && rolledBack as string == "partial")
                    && meta.TryGetValue("mayHaveChanged", out var changed) && changed is true
                    && meta.TryGetValue("operationSuccess", out var success) && success is false);
            if (reply.OutcomeUnknown) { meta["writeOutcomeUnknown"] = true; }
            return reply;
        }
        private static void RecordEngineeringDetails(Siemens.Engineering.EngineeringException engineering,
            Dictionary<string, object?> meta, List<string?> messages)
        {
            try
            {
                var data = engineering.MessageData;
                string message = data.Text, detailText = data.DetailText;
                meta["messageData"] = new Dictionary<string, object?> { ["text"] = message, ["detailText"] = detailText };
                messages.Add(message);
                messages.Add(detailText);
            }
            catch { /* swallow(probe-optional): some SDK errors have only DetailMessageData */ }
            try
            {
                var details = new List<object?>();
                foreach (var detail in engineering.DetailMessageData)
                {
                    string text = detail.Text, detailMessage = detail.DetailText;
                    details.Add(new Dictionary<string, object?> { ["text"] = text, ["detailText"] = detailMessage });
                    messages.Add(text);
                    messages.Add(detailMessage);
                }
                meta["detailMessageData"] = details;
            }
            catch { /* swallow(probe-optional): retain the exception message and any available engineering details */ }
        }

        internal void RecordHmiFault(Dictionary<string, object?> meta)
        {
            if (HmiReadFault != null) { HmiReadFault(meta); return; }
            RecordHmiFaultCore(meta);
        }
        private void RecordHmiFaultCore(Dictionary<string, object?> meta)
        {
            hmiReadFault = new Dictionary<string, object?>();
            foreach (var key in new[] {
                    "timestamp", "operationId", "tool", "action", "libraryName", "typePath", "lastAttemptedProperty", "appliedProperties", "mayHaveChanged",
                        "softwarePath", "screenPath", "phase", "lastAttemptedPath", "error"
                })
                hmiReadFault[key] = meta.TryGetValue(key, out var value) && value != null ? HmiValues.DeepClone(value) : null;
            meta["status"] = "HmiConnectionUnavailable";
            meta["connectionUnavailable"] = true; meta["remoteInspectionStopped"] = true;
            meta["recovery"] =
                "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. This call failed; a later call may " +
                    "try again.";
        }
    }
}

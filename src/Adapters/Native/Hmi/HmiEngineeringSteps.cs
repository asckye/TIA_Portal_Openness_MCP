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
            if (EngineeringServicesStep != null) return EngineeringServicesStep(tool, requiresProject, meta => {
                meta["mayHaveChanged"] = false;
                return action(meta);
            });
            return RunHmiStepCore(tool, action, requiresProject);
        }
        private HardwareAddressingReply RunHmiStepCore(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject)
        {
            var reply = new HardwareAddressingReply();
            var meta = LegacyResponseMetadata.Step(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, tool);
            reply.Meta = meta;
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
                meta["status"] = error.GetBaseException() is PlcSoftwareException native ? native.Code
                    : error.GetBaseException() is HardwareAddressingException hardware ? hardware.Status : "ReadOrWriteFailed";
                if (error.GetBaseException() is Siemens.Engineering.EngineeringException engineering)
                {
                    try
                    {
                        var data = engineering.MessageData;
                        string message = data.Text, detailText = data.DetailText;
                        meta["messageData"] = new Dictionary<string, object?> { ["text"] = message, ["detailText"] = detailText };
                        var details = new List<object?>();
                        var detailTexts = new List<string?>();
                        foreach (var detail in engineering.DetailMessageData)
                        {
                            string text = detail.Text, detailMessage = detail.DetailText;
                            details.Add(new Dictionary<string, object?> { ["text"] = text, ["detailText"] = detailMessage });
                            detailTexts.Add(text);
                            detailTexts.Add(detailMessage);
                        }
                        meta["detailMessageData"] = details;
                        string composed = NativeFailureText.Compose(message, detailText, detailTexts);
                        if (composed.Length != 0) meta["nativeMessage"] = composed;
                    }
                    catch { /* swallow(probe-optional): unavailable engineering detail metadata must not replace the original exception */ }
                }
                EngineeringCredentialRules.TryRecordRefusal(error, meta);
                if (HmiReadSafety.ConnectionUnavailable(error)) RecordHmiFault(meta);
                reply.Message = tool + " failed";
            }
            reply.RequiresSessionReset = meta.TryGetValue("requiresExplicitRebind", out var blocked) && blocked is true
                || meta.TryGetValue("connectionUnavailable", out var unavailable) && unavailable is true
                || !(meta.TryGetValue("rollbackOutcome", out var rolledBack) && rolledBack as string == "partial")
                    && meta.TryGetValue("mayHaveChanged", out var changed) && changed is true
                    && (meta.TryGetValue("operationSuccess", out var success) && success is false
                        || meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true);
            return reply;
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

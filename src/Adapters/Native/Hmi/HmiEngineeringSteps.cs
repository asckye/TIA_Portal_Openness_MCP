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
        internal HardwareAddressingReply RunHmiStep(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject)
        {
            if (EngineeringServicesStep != null) return EngineeringServicesStep(tool, requiresProject, action);
            return RunHmiStepCore(tool, action, requiresProject);
        }
        private HardwareAddressingReply RunHmiStepCore(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject)
        {
            var reply = new HardwareAddressingReply();
            var meta = LegacyResponseMetadata.Step(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, tool);
            reply.Meta = meta;
            try
            {
                if (hmiReadFault != null)
                {
                    meta["status"] = "HmiReadSessionBlocked"; meta["operationSuccess"] = false;
                    meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                    meta["requiresExplicitRebind"] = true; meta["remoteInspectionStopped"] = true;
                    meta["lastFailure"] = HmiValues.DeepClone(hmiReadFault);
                    reply.Message = "HMI operation blocked after a connection failure. Inspect logs before explicitly rebinding; no TIA call attempted.";
                    reply.RequiresSessionReset = true;
                    return reply;
                }
                if (requiresProject && EngineeringPlcSession.IsProjectNull())
                {
                    meta["error"] = "Project is null"; meta["status"] = "InvalidState"; meta["operationSuccess"] = false;
                    reply.Message = "Project is null"; return reply;
                }
                reply.Message = action(meta);
                LegacyResponseMetadata.Complete(meta);
            }
            catch (Exception error)
            {
                EngineeringPlcSession.RecordExportFailure(error);
                meta["error"] = error.ToString();
                meta["operationSuccess"] = false; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = error.GetBaseException() is PlcSoftwareException native ? native.Code
                    : error.GetBaseException() is HardwareAddressingException hardware ? hardware.Status : "ReadOrWriteFailed";
                if (error.GetBaseException() is Siemens.Engineering.EngineeringException engineering)
                {
                    try
                    {
                        var data = engineering.MessageData;
                        meta["messageData"] = new Dictionary<string, object?> { ["text"] = data.Text, ["detailText"] = data.DetailText };
                        var details = new List<object?>();
                        foreach (var detail in engineering.DetailMessageData)
                            details.Add(new Dictionary<string, object?> { ["text"] = detail.Text, ["detailText"] = detail.DetailText });
                        meta["detailMessageData"] = details;
                    }
                    catch { /* swallow(probe-optional): unavailable engineering detail metadata must not replace the original exception */ }
                }
                if (HmiReadSafety.ConnectionUnavailable(error)) RecordHmiFault(meta);
                reply.Message = tool + " failed";
            }
            reply.RequiresSessionReset = meta.TryGetValue("requiresExplicitRebind", out var blocked) && blocked is true
                || meta.TryGetValue("connectionUnavailable", out var unavailable) && unavailable is true
                || meta.TryGetValue("mayHaveChanged", out var changed) && changed is true
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
            foreach (var key in new[] { "timestamp", "operationId", "tool", "action", "libraryName", "typePath", "lastAttemptedProperty", "appliedProperties", "mayHaveChanged", "softwarePath", "screenPath", "phase", "lastAttemptedPath", "error" })
                hmiReadFault[key] = meta.TryGetValue(key, out var value) && value != null ? HmiValues.DeepClone(value) : null;
            meta["status"] = "HmiConnectionUnavailable";
            meta["connectionUnavailable"] = true; meta["remoteInspectionStopped"] = true;
            meta["requiresExplicitRebind"] = true;
            meta["recovery"] = "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. Only an explicit successful project binding clears the snapshot-read block.";
        }
    }
}

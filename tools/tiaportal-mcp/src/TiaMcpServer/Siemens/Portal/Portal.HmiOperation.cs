using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private JsonObject? _hmiReadFault;
        // 2.7.40: OS process behind the bound TiaPortal (set by RememberBoundProcess in Portal.cs); pure .NET so the offline suite compiles it.
        private int? _boundProcessId;
        public JsonObject GetPortalProcessHealth()
        {
            var row = new JsonObject { ["boundProcessId"] = _boundProcessId };
            if (_boundProcessId == null) { row["processAlive"] = null; row["note"] = "no TIA Portal process bound"; return row; }
            try { using var process = System.Diagnostics.Process.GetProcessById(_boundProcessId.Value); row["processAlive"] = !process.HasExited; row["processName"] = process.ProcessName; }
            catch (ArgumentException) { row["processAlive"] = false; row["note"] = "TIA Portal process " + _boundProcessId + " is no longer running (crashed or closed): restart TIA Portal, reopen the project, then AttachToOpenProject."; }
            catch (Exception ex) { row["processAlive"] = null; row["note"] = ex.GetBaseException().Message; }
            return row;
        }
        public JsonObject GetHmiReadHealth() => new JsonObject
        {
            ["snapshotReadsBlocked"] = _hmiReadFault != null,
            ["lastFailure"] = _hmiReadFault?.DeepClone(),
            ["portalProcess"] = GetPortalProcessHealth(),
            ["meaning"] = "Portal attachment and HMI software-handle health are separate. A successful GetState does not validate every software handle."
        };
        private void ResetHmiReadHealth() { _hmiReadFault = null; }
        private void RecordHmiReadFault(JsonObject meta)
        {
            InvalidateHmiSoftwareCache();
            _hmiReadFault = new JsonObject();
            foreach (var key in new[] { "timestamp", "operationId", "softwarePath", "screenPath", "phase", "lastAttemptedPath", "error" })
                _hmiReadFault[key] = meta[key]?.DeepClone();
            meta["status"] = "HmiConnectionUnavailable";
            meta["connectionUnavailable"] = true; meta["remoteInspectionStopped"] = true;
            meta["requiresExplicitRebind"] = true;
            var process = GetPortalProcessHealth(); meta["portalProcess"] = process;
            meta["recovery"] = process["processAlive"] is JsonValue alive && alive.TryGetValue<bool>(out var running) && !running
                ? "The bound TIA Portal process is no longer running - it crashed or was closed during this call. Restart TIA Portal, reopen the project, then AttachToOpenProject; the objects created in the unsaved project are gone."
                : "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. "
                + "Only an explicit successful AttachToOpenProject clears the snapshot-read block; it does not establish root cause or fix TIA.";
        }
        private ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject = true)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["tool"] = toolName,
                ["success"] = false
            };

            try
            {
                if (_hmiReadFault != null)
                {
                    meta["status"] = "HmiReadSessionBlocked"; meta["operationSuccess"] = false;
                    meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                    meta["requiresExplicitRebind"] = true; meta["remoteInspectionStopped"] = true;
                    meta["lastFailure"] = _hmiReadFault.DeepClone();
                    return new ResponseMessage { Message = "HMI operation blocked after a connection failure. Inspect logs before explicitly rebinding; no TIA call attempted.", Meta = meta };
                }
                if (requiresProject && IsProjectNull())
                {
                    meta["error"] = ProjectNullMessage(meta);
                    meta["status"] = "InvalidState";
                    meta["operationSuccess"] = false;
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var message = action(meta);
                meta["success"] = meta["operationSuccess"]?.GetValue<bool>() ?? true;
                meta["operationSuccess"] = meta["success"]?.DeepClone();
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString();
                AddExceptionMessageData(ex, meta);
                meta["operationSuccess"] = false;
                meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = MigrationRead.Cause(ex) is PortalException pex ? pex.Code.ToString() : "ReadOrWriteFailed";
                // Any lifetime/remoting-shaped failure blocks further remote reads until an explicit AttachToOpenProject
                // (conservative by design: disposed handles preceded TIA process exits in the V21 HMI captures). Tools that
                // expect a released proxy — verification right after a native Delete — catch it locally (HmiReadSafety.DisposedObjectOnly).
                if (HmiReadSafety.ConnectionUnavailable(ex)) RecordHmiReadFault(meta);
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
        }

    }
}

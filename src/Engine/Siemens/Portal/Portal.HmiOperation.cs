using System.Collections.Generic;
using System.IO;
using System.Security;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
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
        // OS process behind the bound TiaPortal, set by RememberBoundProcess in Portal.cs.
        private int? _boundProcessId;
        private long _processStartTicks;
        public JsonObject GetPortalProcessHealth()
        {
            var row = new JsonObject { ["boundProcessId"] = _boundProcessId };
            if (_boundProcessId == null) { row["processAlive"] = null; row["note"] = "no TIA Portal process bound"; return row; }
            try { using var process = System.Diagnostics.Process.GetProcessById(_boundProcessId.Value); row["processAlive"] = !process.HasExited && (_processStartTicks == 0 || process.StartTime.ToUniversalTime().Ticks == _processStartTicks); row["processName"] = process.ProcessName; }
            catch (ArgumentException) /* swallow(env-probe): a missing process is reported as no longer alive */ { row["processAlive"] = false; row["note"] = "TIA Portal process " + _boundProcessId + " is no longer running (crashed or closed): restart TIA Portal, reopen the project, then AttachOpenProject."; }
            catch (Exception ex) { row["processAlive"] = null; row["note"] = ex.GetBaseException().Message; }
            return row;
        }
        public JsonObject GetHmiReadHealth() => new JsonObject
        {
            ["snapshotReadsBlocked"] = _hmiReadFault != null,
            ["lastFailure"] = _hmiReadFault?.DeepClone(),
            ["portalProcess"] = GetPortalProcessHealth(),
            ["meaning"] = "Portal attachment and HMI software-handle health are separate. A successful GetSessionState does not validate every software handle."
        };
        private void ResetHmiReadHealth() { _hmiReadFault = null; }
        private void RecordHmiReadFault(JsonObject meta)
        {
            InvalidateHmiSoftwareCache();
            _hmiReadFault = new JsonObject();
            foreach (var key in new[] { "timestamp", "operationId", "tool", "action", "libraryName", "typePath", "lastAttemptedProperty", "appliedProperties", "mayHaveChanged", "softwarePath", "screenPath", "phase", "lastAttemptedPath", "error" })
                _hmiReadFault[key] = meta[key]?.DeepClone();
            meta["status"] = "HmiConnectionUnavailable";
            meta["connectionUnavailable"] = true; meta["remoteInspectionStopped"] = true;
            meta["requiresExplicitRebind"] = true;
            var process = GetPortalProcessHealth(); meta["portalProcess"] = process;
            meta["recovery"] = process["processAlive"] is JsonValue alive && alive.TryGetValue<bool>(out var running) && !running
                ? "The bound TIA Portal process is no longer running - it crashed or was closed during this call. Restart TIA Portal, reopen the project, then AttachOpenProject; the objects created in the unsaved project are gone."
                : "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. "
                + "Only an explicit successful AttachOpenProject clears the snapshot-read block; it does not establish root cause or fix TIA.";
        }
        private ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject = true)
        {
            var meta = ResponseMeta.Step(toolName);

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
                ResponseMeta.Complete(meta);
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString();
                AddExceptionMessageData(ex, meta);
                ResponseMeta.Failed(meta);
                meta["status"] = MigrationRead.Cause(ex) is PortalException pex ? pex.Code.ToString() : "ReadOrWriteFailed";
                // Any lifetime/remoting-shaped failure blocks further remote reads until an explicit AttachToOpenProject
                // (conservative by design: disposed handles preceded TIA process exits in the V21 HMI captures). Tools that
                // expect a released proxy — verification right after a native Delete — catch it locally (HmiReadSafety.DisposedObjectOnly).
                if (HmiReadSafety.ConnectionUnavailable(ex)) RecordHmiReadFault(meta);
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
        }


        // ---- RunHmiStepTool hooks (the offline test project substitutes both) -------------------------------------------------
        private string ProjectNullMessage(JsonObject meta)
        {
            if (_expectedProjectName == null) return "Project is null";
            meta["expectedProject"] = _expectedProjectName;
            return "Project is null: the explicitly bound project '" + _expectedProjectName + "' is not open in any TIA Portal instance (nothing else was bound in its place); reopen it and call AttachOpenProject.";
        }
        // Openness exceptions carry structured ExceptionMessageData (Text / DetailText) beside the message.
        private static void AddExceptionMessageData(Exception ex, JsonObject meta)
        {
            if (MigrationRead.Cause(ex) is not EngineeringException engineering) return;
            try
            {
                ExceptionMessageData data = engineering.MessageData;
                meta["messageData"] = new JsonObject { ["text"] = data.Text, ["detailText"] = data.DetailText };
                meta["detailMessageData"] = new JsonArray(engineering.DetailMessageData.Select(d => (JsonNode)new JsonObject { ["text"] = d.Text, ["detailText"] = d.DetailText }).ToArray());
            }
            catch /* swallow(probe-optional): unavailable engineering detail metadata must not replace the original exception */ { }
        }

    }
}

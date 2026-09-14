using System;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private JsonObject? _hmiReadFault;
        public JsonObject GetHmiReadHealth() => new JsonObject
        {
            ["snapshotReadsBlocked"] = _hmiReadFault != null,
            ["lastFailure"] = _hmiReadFault?.DeepClone(),
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
            meta["recovery"] = "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. "
                + "Only an explicit successful AttachToOpenProject clears the snapshot-read block; it does not establish root cause or fix TIA.";
        }
        private ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action)
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
                if (IsProjectNull())
                {
                    meta["error"] = "Project is null";
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
                meta["operationSuccess"] = false;
                meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = MigrationRead.Cause(ex) is PortalException pex ? pex.Code.ToString() : "ReadOrWriteFailed";
                if (HmiReadSafety.ConnectionUnavailable(ex)) RecordHmiReadFault(meta);
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
        }

    }
}

using System;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Session double only; services, HMI access helpers and tool wrappers are production source.
    internal class FakeHmiToolSession : IHmiToolSession
    {
        internal object? FixtureRoot;
        internal Exception? FixtureResolutionError;
        internal int FixtureResolveCalls, FixtureCacheClears;
        private JsonObject? _hmiReadFault;
        public object? CurrentProject => FixtureRoot;
        public MigrationPages MigrationPages { get; } = new MigrationPages();
        public JsonObject? HmiReadFault => _hmiReadFault;
        internal void FixtureResetReadHealth() => ResetHmiReadHealth();
        private bool IsProjectNull() => FixtureRoot == null;
        private string ProjectNullMessage(JsonObject meta) => "Project is null";
        private static void AddExceptionMessageData(Exception ex, JsonObject meta) { }
        private void InvalidateHmiSoftwareCache() { FixtureCacheClears++; }
        public IDisposable AcquireHmiEditAccess() => new FixtureLease();
        private sealed class FixtureLease : IDisposable { public void Dispose() { } }
        public object ResolveHmiSoftwareOrThrow(string path)
        {
            FixtureResolveCalls++;
            if (FixtureResolutionError != null) throw FixtureResolutionError;
            return FixtureRoot ?? throw new PortalException(PortalErrorCode.NotFound, path);
        }
        private JsonObject GetPortalProcessHealth() => new JsonObject
        {
            ["boundProcessId"] = null, ["processAlive"] = null, ["note"] = "no TIA Portal process bound"
        };
        public JsonObject GetHmiReadHealth() => new JsonObject
        {
            ["snapshotReadsBlocked"] = _hmiReadFault != null,
            ["lastFailure"] = _hmiReadFault?.DeepClone(),
            ["portalProcess"] = GetPortalProcessHealth(),
            ["meaning"] = "Portal attachment and HMI software-handle health are separate. A successful GetState does not validate every software handle."
        };
        private void ResetHmiReadHealth() { _hmiReadFault = null; }
        public void RecordHmiReadFault(JsonObject meta)
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
                ? "The bound TIA Portal process is no longer running - it crashed or was closed during this call. Restart TIA Portal, reopen the project, then AttachToOpenProject; the objects created in the unsaved project are gone."
                : "Stop collection and inspect MCP/TIA logs. No automatic retry, attach, close or dispose was performed. "
                + "Only an explicit successful AttachToOpenProject clears the snapshot-read block; it does not establish root cause or fix TIA.";
        }
        public ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject = true)
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

    }
}

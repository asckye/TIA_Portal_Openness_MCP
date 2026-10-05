using System;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class RuntimeSettingsService
    {
        private readonly IHmiToolSession _session;

        public RuntimeSettingsService(IHmiToolSession session) => _session = session;

        private readonly object _runtimeSettingsGate = new object();
        private ResponseMessage RuntimeSettingsOperation(string tool, string softwarePath, string expectedProject, bool write,
            Action validateInput, Func<object, JsonObject, HmiReadTrace, string> action)
        {
            lock (_runtimeSettingsGate)
                return _session.RunHmiStepTool(tool, meta =>
                {
                    // envelope: legacy-runtime-settings: append to the HMI envelope; Failed would add operationSuccess before the action.
                    meta["softwarePath"] = softwarePath; meta["expectedProject"] = expectedProject; meta["readOnly"] = !write;
                    meta["writeAttempted"] = false; meta["mayHaveChanged"] = false;
                    meta["apiCallSuccess"] = false; meta["dataComplete"] = false; meta["verificationSuccess"] = false;
                    meta["failures"] = new JsonArray(); meta["failureCount"] = 0;
                    var trace = new HmiReadTrace(); IDisposable? exclusive = null;
                    try
                    {
                        validateInput();
                        if (string.IsNullOrWhiteSpace(softwarePath) || string.IsNullOrWhiteSpace(expectedProject))
                            throw new ArgumentException("Exact expectedProject and explicit HMI softwarePath are required.");
                        trace.Step("before", "expectedProject");
                        if (!string.Equals(MigrationRead.Get(_session.CurrentProject!, "Name") as string, expectedProject, StringComparison.Ordinal))
                            throw new InvalidOperationException("Exact expectedProject mismatch; runtime settings were not accessed.");
                        trace.Step("after", "expectedProject");
                        if (write) exclusive = _session.AcquireHmiEditAccess();
                        trace.Step("before", "resolveSoftware:" + softwarePath);
                        var hmi = _session.ResolveHmiSoftwareOrThrow(softwarePath);
                        if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware")
                            throw new NotSupportedException("Unified HmiSoftware required; this HMI/API version is unsupported: " + hmi.GetType().FullName);
                        trace.Step("after", "resolveSoftware:" + softwarePath);
                        return action(hmi, meta, trace);
                    }
                    catch (Exception ex)
                    {
                        trace.Step("failed", trace.LastAttemptedPath ?? softwarePath, ex);
                        meta["failureCount"] = 1; meta["failures"] = new JsonArray(MigrationRead.Failure(trace.LastAttemptedPath ?? "/RuntimeSettings", ex is NotSupportedException ? "Unsupported" : "RuntimeSettingsFailed", ex));
                        if (HmiReadSafety.ConnectionUnavailable(ex)) { meta["connectionUnavailable"] = true; meta["truncated"] = true; }
                        throw;
                    }
                    finally
                    {
                        trace.AddTo(meta);
                        if (exclusive != null && meta["connectionUnavailable"]?.GetValue<bool>() == true) meta["exclusiveReleaseSkipped"] = true;
                        else exclusive?.Dispose();
                    }
                });
        }
        public ResponseMessage ReadUnifiedRuntimeSettings(string softwarePath, string expectedProject, string fieldsJson = UnifiedRuntimeSettingsAccess.DefaultFields)
            => RuntimeSettingsOperation("ReadUnifiedRuntimeSettings", softwarePath, expectedProject, false,
                () => UnifiedRuntimeSettingsAccess.Fields(fieldsJson), (hmi, meta, trace) =>
                {
                    UnifiedRuntimeSettingsAccess.Read(hmi, UnifiedRuntimeSettingsAccess.Fields(fieldsJson), meta, trace);
                    return "Requested runtime settings read; inspect capabilities, failures and dataComplete. No startup setting changed.";
                });
        public ResponseMessage UpdateUnifiedRuntimeSettings(string softwarePath, string expectedProject, string changesJson, bool dryRun = true, string expectedToken = "")
            => RuntimeSettingsOperation("UpdateUnifiedRuntimeSettings", softwarePath, expectedProject, !dryRun,
                () => { UnifiedRuntimeSettingsAccess.Changes(changesJson); if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new ArgumentException("Preview expectedToken required."); },
                (hmi, meta, trace) => UnifiedRuntimeSettingsAccess.Update(hmi, expectedProject, softwarePath,
                    UnifiedRuntimeSettingsAccess.Changes(changesJson), dryRun, expectedToken, meta, trace));
    }
}

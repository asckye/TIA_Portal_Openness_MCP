#if PLC_HARDWARE_CATALOG
using System;
using System.Diagnostics;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private DeviceCreationSession? deviceCandidate;
        private DeviceCreationAdapter? deviceCandidateAdapter;

        public JsonElement CreateHardwareDeviceCandidate(string typeIdentifier, string deviceName, string family,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "",
            long bindingEpoch = 0, string requestId = "")
        {
            const string tool = "CreateHardwareDevice";
            Envelope result;
            try
            {
                Check();
                if (ReleaseKey != "19" || BehaviorCapabilities.Select(typeof(PlcFoundationEngine).Assembly, ReleaseKey, "P6-DEVICE") != BehaviorPolicy.SafeV4)
                    throw new DeviceCreationRejection(new Error("This Foundation device candidate is not selected.", new UnsupportedCapabilityDetails(ReleaseKey, "P6-DEVICE", "create")));
                deviceCandidate ??= new DeviceCreationSession();
                if (deviceCandidate.RequiresSessionReset) throw new DeviceCreationRejection(new Error("The device-create session must be rebuilt.", new SessionResetRequiredDetails("device-create-unknown")));
                PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                var candidateProject = Project();
                var candidatePortal = Portal();
                PlanIdentity Identity()
                {
                    Check();
                    if (!object.Equals(Project(), candidateProject) || !object.Equals(Portal(), candidatePortal))
                        throw new DeviceCreationRejection(new Error("The project binding changed.", new IdentityMismatchDetails("project", null, null)));
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("No attached process identity.");
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited) throw new InvalidOperationException("The attached process exited.");
                    var path = candidateProject.Path.FullName;
                    RequireProjectIdentity(path);
                    return new PlanIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), DeviceCreationSession.CanonicalProject(path), bindingEpoch, null, Array.Empty<PlanFile>());
                }
                if (deviceCandidateAdapter == null || !deviceCandidateAdapter.IsProject(candidateProject))
                    deviceCandidateAdapter = new DeviceCreationAdapter(candidateProject, candidatePortal, Identity);
                else deviceCandidateAdapter.Identity = Identity;
                result = deviceCandidate.Run(deviceCandidateAdapter, ReleaseKey, tool, requestId, typeIdentifier, deviceName, family,
                    mode, confirm, expectedPlanHash, expectedProjectFile, foundation: true);
            }
            catch (Exception ex)
            {
                var error = ex is DeviceCreationRejection rejected ? rejected.Error : new Error("Device preflight is unavailable; no Create was issued.", new PreconditionFailedDetails("device-create-preflight", null));
                result = DeviceCreationSession.Result(ReleaseKey, tool, requestId, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
            return JsonSerializer.Deserialize<JsonElement>(V4Json.Serialize(result));
        }
    }
}
#endif

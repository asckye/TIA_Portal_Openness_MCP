#if PLC_HARDWARE_CATALOG
using System;
using System.Diagnostics;
using System.Linq;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private DeviceCreationAdapter? deviceCandidateAdapter;
        public DeviceCandidateReply CreateHardwareDeviceCandidate(DeviceCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var result = new DeviceCandidateReply();
            try
            {
                Check();
                if (ReleaseKey != "19") CandidatePrimitives.Unsupported(ReleaseKey, "device-candidate");
                PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                var candidateProject = Project();
                var candidatePortal = Portal();
                CandidateIdentity Identity()
                {
                    Check();
                    if (!object.Equals(Project(), candidateProject) || !object.Equals(Portal(), candidatePortal))
                        CandidatePrimitives.Fail("identity", "project");
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("No attached process identity.");
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited) throw new InvalidOperationException("The attached process exited.");
                    var path = candidateProject.Path.FullName;
                    RequireProjectIdentity(path);
                    return new CandidateIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), CandidatePrimitives.CanonicalProject(path), bindingEpoch);
                }
                if (deviceCandidateAdapter == null || !deviceCandidateAdapter.IsProject(candidateProject))
                    deviceCandidateAdapter = new DeviceCreationAdapter(candidateProject, candidatePortal, Identity);
                else deviceCandidateAdapter.Identity = Identity;
                result.RootId = deviceCandidateAdapter.RootId;
                switch (candidate.Action)
                {
                    case "identity": result.Identity = deviceCandidateAdapter.ReadIdentity(); break;
                    case "catalog": result.Catalog = deviceCandidateAdapter.ReadCatalog(candidate.TypeIdentifier).ToArray(); break;
                    case "inventory": result.Inventory = deviceCandidateAdapter.ReadInventory().ToArray(); break;
                    case "execute":
                        if (mode != "apply" || candidate.Check == null) CandidatePrimitives.Invalid("candidate");
                        result.Attempt = CandidateExecution.Create(deviceCandidateAdapter, candidate.Check!);
                        result.RequiresSessionReset = result.Attempt.RequiresSessionReset;
                        break;
                    default: CandidatePrimitives.Invalid("candidate.action"); break;
                }
            }
            catch (Exception ex)
            { result.Fault = ex is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "device-create-preflight" }; }
            return result;
        }
        public DeviceCandidateReply HardwareDeviceCandidate(DeviceCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var result = new DeviceCandidateReply();
            try
            {
                Check();
                if (ReleaseKey != "19" && ReleaseKey != "20" && ReleaseKey != "21") CandidatePrimitives.Unsupported(ReleaseKey, "device-candidate");
                if (SharedHardwareCandidateIdentity == null) PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                var candidateProject = Project();
                var candidatePortal = Portal();
                CandidateIdentity Identity()
                {
                    if (SharedHardwareCandidateIdentity != null) return SharedHardwareCandidateIdentity(() => candidateProject.Path.FullName);
                    Check();
                    if (!object.Equals(Project(), candidateProject) || !object.Equals(Portal(), candidatePortal))
                        CandidatePrimitives.Fail("identity", "project");
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("No attached process identity.");
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited) throw new InvalidOperationException("The attached process exited.");
                    var path = candidateProject.Path.FullName;
                    RequireProjectIdentity(path);
                    return new CandidateIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), CandidatePrimitives.CanonicalProject(path), bindingEpoch);
                }
                if (deviceCandidateAdapter == null || !deviceCandidateAdapter.IsProject(candidateProject))
                    deviceCandidateAdapter = new DeviceCreationAdapter(candidateProject, candidatePortal, Identity);
                else deviceCandidateAdapter.Identity = Identity;
                result.RootId = deviceCandidateAdapter.RootId;
                switch (candidate.Action)
                {
                    case "identity": result.Identity = deviceCandidateAdapter.ReadIdentity(); break;
                    case "catalog": result.Catalog = deviceCandidateAdapter.ReadCatalog(candidate.TypeIdentifier).ToArray(); break;
                    case "inventory": result.Inventory = deviceCandidateAdapter.ReadInventory().ToArray(); break;
                    case "execute":
                        if (mode != "apply" || candidate.Check == null) CandidatePrimitives.Invalid("candidate");
                        result.Attempt = CandidateExecution.Create(deviceCandidateAdapter, candidate.Check!);
                        result.RequiresSessionReset = result.Attempt.RequiresSessionReset;
                        break;
                    default: CandidatePrimitives.Invalid("candidate.action"); break;
                }
            }
            catch (Exception ex)
            { result.Fault = ex is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "device-create-preflight" }; }
            return result;
        }
    }
}
#endif

#if !PLC_HARDWARE_CATALOG
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public TiaMcp.Adapters.Contracts.Candidates.DeviceCandidateReply HardwareDeviceCandidate(TiaMcp.Adapters.Contracts.Candidates.DeviceCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
            => throw new System.NotSupportedException("Device creation candidate requires the hardware catalog API.");
    }
}
#endif
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public System.Func<System.Func<string>, TiaMcp.Adapters.Contracts.Candidates.CandidateIdentity>? SharedHardwareCandidateIdentity { get; set; }
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
#if TIA_STUDIO_LOCAL_CONTRACTS
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
namespace TiaOpenness.Contracts.Models
#else
using TiaMcp.Adapters.Contracts.Studio;
using TiaMcp.Adapters.Contracts.Studio.Errors;
namespace TiaMcp.Adapters.Contracts.Studio
#endif
{
    /// <summary>A discovery snapshot, not an attachment or a reservation.</summary>
    public sealed class PortalProcessInfo
    {
        public int ProcessId { get; set; }
        public string ProjectPath { get; set; }
        public string Release { get; set; }
        public string UiMode { get; set; }
    }
}

#if TIA_STUDIO_LOCAL_CONTRACTS
namespace TiaOpenness.Contracts.Models.Policies
#else
namespace TiaMcp.Adapters.Contracts.Studio.Policies
#endif
{
    public static class PortalProcessSelection
    {
        public static PortalProcessInfo Select(IReadOnlyList<PortalProcessInfo> processes, int? processId,
            string release, Func<int, bool> isRunning)
        {
            if (processId.HasValue)
            {
                var selected = processes.SingleOrDefault(p => p.ProcessId == processId.Value);
                if (selected != null) return selected;
                throw new InvalidOperationException(isRunning(processId.Value)
                    ? "PID " + processId + " is running but is unavailable to TIA Portal V" + release
                        + ". It may belong to another release. No attachment or new instance was attempted."
                    : "TIA Portal PID " + processId + " has exited. No other instance was attached or started.");
            }
            if (processes.Count > 1) throw new PortalProcessSelectionRequiredException(processes);
            return processes.SingleOrDefault();
        }
    }
}

#if TIA_STUDIO_LOCAL_CONTRACTS
namespace TiaOpenness.Contracts.Models.Errors
#else
namespace TiaMcp.Adapters.Contracts.Studio.Errors
#endif
{
    public sealed class PortalProcessSelectionRequiredException : InvalidOperationException
    {
        public IReadOnlyList<PortalProcessInfo> Processes { get; }
        public PortalProcessSelectionRequiredException(IReadOnlyList<PortalProcessInfo> processes)
            : base("Several TIA Portal processes are running ("
                + string.Join("; ", processes.Select(p => "PID " + p.ProcessId + ": " + (p.ProjectPath ?? "no project")))
                + "). Choose the exact instance to connect.") { Processes = processes; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public enum BindingObservationState { Unknown, Unbound, Bound }
    public enum BindingIdentityStrength { None, WeakPidOnly, OsStartTimeAnchored }
    // Read-only observations, not connection/readiness promises or attachment authority.
    public sealed class BindingObservation
    {
        public BindingObservationState State { get; }
        public BindingIdentityStrength Strength { get; }
        public int? ProcessId { get; }
        public long? ProcessStartUtcTicks { get; }
        public string? ProjectSha256 { get; }
        public string Reason { get; }
        public bool AllowsV2Admission => State != BindingObservationState.Unknown &&
            (!ProcessId.HasValue || Strength == BindingIdentityStrength.OsStartTimeAnchored);
        internal BindingObservation(BindingObservationState state, BindingIdentityStrength strength,
            int? pid, long? ticks, string? digest, string reason)
        { State=state; Strength=strength; ProcessId=pid; ProcessStartUtcTicks=ticks; ProjectSha256=digest; Reason=reason; }
        public static BindingObservation Unknown(string reason) => new BindingObservation(BindingObservationState.Unknown, BindingIdentityStrength.None, null, null, null, reason);
        internal bool Same(BindingObservation other) => State==other.State && Strength==other.Strength &&
            ProcessId==other.ProcessId && ProcessStartUtcTicks==other.ProcessStartUtcTicks && ProjectSha256==other.ProjectSha256;
    }
    public sealed class BindingSnapshotObservation
    {
        public long Epoch { get; }
        public BindingObservation Binding { get; }
        public bool AllowsV2Admission => Binding.AllowsV2Admission;
        internal BindingSnapshotObservation(long epoch, BindingObservation binding) { Epoch=epoch; Binding=binding; }
    }
    public sealed class BindingProcessObservation
    {
        public int ProcessId { get; }
        public long StartUtcTicks { get; }
        public BindingProcessObservation(int processId, long startUtcTicks)
        {
            if(processId<=0 || startUtcTicks<=0 || startUtcTicks>DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid OS identity observation.");
            ProcessId=processId; StartUtcTicks=startUtcTicks;
        }
        internal bool Same(BindingProcessObservation? other) => other!=null && ProcessId==other.ProcessId && StartUtcTicks==other.StartUtcTicks;
    }
    public sealed class BindingCachedFields
    {
        public bool HasPortal { get; }
        public bool HasProject { get; }
        public int? ProcessId { get; }
        public string? ProjectFile { get; }
        public bool IsLocalSession { get; }
        public BindingCachedFields(bool hasPortal, bool hasProject, int? processId, string? projectFile, bool isLocalSession)
        { HasPortal=hasPortal; HasProject=hasProject; ProcessId=processId; ProjectFile=projectFile; IsLocalSession=isLocalSession; }
        internal bool Same(BindingCachedFields other) => HasPortal==other.HasPortal && HasProject==other.HasProject && ProcessId==other.ProcessId && ProjectFile==other.ProjectFile && IsLocalSession==other.IsLocalSession;
    }
    public sealed class BindingProjectObservation
    {
        public string Name { get; }
        public string File { get; }
        public BindingProjectObservation(string name, string file) { Name=name; File=file; }
    }
}

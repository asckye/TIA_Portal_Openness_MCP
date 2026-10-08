// Managed fixtures for the production adoption seam; no Openness assembly is loaded.
namespace Siemens.Engineering
{
    public sealed class TiaPortal { public int Disposes; }
    public class ProjectBase(string name)
    {
        public int NameReads, Disposes;
        public string Name { get { NameReads++; return name; } }
    }
}
namespace Siemens.Engineering.Multiuser { public sealed class LocalSession { } }
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class InvocationJournal { internal static T Native<T>(string name, Func<T> read) { NativeCallStarted(); return read(); }
        [ThreadStatic] private static NativeCallScope? current;
        internal static void NativeCallStarted() { for (var scope=current; scope!=null; scope=scope.Parent) scope.NativeCallIssued=true; }
        internal static NativeCallScope BeginNativeCallScope() => current=new NativeCallScope(current);
        internal sealed class NativeCallScope(NativeCallScope? parent) : IDisposable
        {
            internal readonly NativeCallScope? Parent=parent;
            internal bool NativeCallIssued;
            public void Dispose() => current=Parent;
        } }
}
namespace TiaMcpServer.Siemens
{
    internal static class Engineering { internal static int TiaMajorVersion = 21; }
    internal sealed class PortalProcessLease { internal int Disposes; }
    internal static class HmiExactAccess { internal static void InvalidateTokens() { } }
    public partial class Portal
    {
        private global::Siemens.Engineering.TiaPortal? _portal;
        private global::Siemens.Engineering.ProjectBase? _project;
        private global::Siemens.Engineering.Multiuser.LocalSession? _session;
        private object? _openedProject;
        private int? _boundProcessId;
        private long _processStartTicks;
        private bool _projectOpenedByUs;
        private string? _expectedProjectName, _bindingFault;
        private PortalProcessLease? _processLease;
        private ProjectBindingIdentity? _binding;
        private readonly Dictionary<string, object> _softwareContainerCache = new(), _plcResolutionCache = new();
        private void InvalidateHmiSoftwareCache() { }
        private void ResetHmiReadHealth() { }
        internal global::Siemens.Engineering.ProjectBase EngineProject() => _project ?? throw new InvalidOperationException(_bindingFault ?? "No project bound.");
        internal global::Siemens.Engineering.TiaPortal? Attachment => _portal;
        internal PortalProcessLease? Lease => _processLease;
        internal ProjectBindingIdentity? Binding => _binding;
        internal string? Fault => _bindingFault;
        internal bool OwnsProject => _projectOpenedByUs;
        internal bool LocalSession => _session != null;
        internal global::Siemens.Engineering.ProjectBase? ProjectHandle => _project;
        internal object? SessionHandle => _session;
        internal void NativeBinding(global::Siemens.Engineering.TiaPortal? tia, global::Siemens.Engineering.ProjectBase? project,
            string? path, PortalProcessLease? lease, bool ownedPortal=false, bool local=false, bool ownsProject=true)
        {
            _portal=tia; _project=project; _session=local ? new global::Siemens.Engineering.Multiuser.LocalSession() : null;
            _boundProcessId=tia==null ? null : 1234; _processStartTicks=tia==null ? 0 : 123456;
            _processLease=lease; _projectOpenedByUs=project!=null && ownsProject; sharedPortalOwned=ownedPortal;
            _binding=path==null ? null : new ProjectBindingIdentity(Engineering.TiaMajorVersion,1234,123456,path,project!.Name);
        }
    }
}

namespace TiaMcp.Versioning { internal static class TiaVersionCatalog { internal static void Get(string release) { } } }
namespace TiaMcp.Adapters { internal sealed class PlcDisconnectResult { internal int? ProcessId { get; set; } internal bool Detached { get; set; } internal string? Strategy { get; set; } internal string? LaunchMode { get; set; } } internal static class PlcFoundationPolicy { internal static void RequireName(string name) { } } }
namespace TiaOpenness.Shared { internal static class SessionBehavior { internal const string Recovery="Session reset required."; } }

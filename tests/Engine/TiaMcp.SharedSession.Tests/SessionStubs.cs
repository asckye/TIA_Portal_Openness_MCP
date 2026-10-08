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
    internal static class InvocationJournal { internal static T Native<T>(string name, Func<T> read) => read(); }
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
    }
}

// Managed fixtures for the kept lifecycle policies; no Openness assembly is loaded.
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
}

namespace TiaMcp.Versioning { internal static class TiaVersionCatalog { internal static void Get(string release) { } } }
namespace TiaMcp.Adapters { internal sealed class PlcDisconnectResult { internal int? ProcessId { get; set; } internal bool Detached { get; set; } internal string? Strategy { get; set; } internal string? LaunchMode { get; set; } } internal static class PlcFoundationPolicy { internal static void RequireName(string name) { } } }
namespace TiaOpenness.Shared { internal static class SessionBehavior { internal const string Recovery="Session reset required."; } }

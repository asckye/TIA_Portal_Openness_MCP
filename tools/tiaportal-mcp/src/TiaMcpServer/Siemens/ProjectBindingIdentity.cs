using System;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Immutable managed data; diagnostics never dereference an Openness proxy.
    internal sealed class ProjectBindingIdentity
    {
        internal int Major { get; }
        internal int ProcessId { get; }
        internal long StartUtcTicks { get; }
        internal string ProjectPath { get; }
        internal string ProjectName { get; }
        internal string Generation { get; } = Guid.NewGuid().ToString("N");
        internal ProjectBindingIdentity(int major, int pid, long startUtcTicks, string path, string name)
        {
            if (major <= 0 || pid <= 0 || startUtcTicks <= 0) throw new ArgumentException("Incomplete TIA process identity.");
            Major = major; ProcessId = pid; StartUtcTicks = startUtcTicks;
            ProjectPath = CanonicalPath(path); ProjectName = name;
        }
        internal static string CanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || (path.Length > 1 && path[1] == ':' && (path.Length < 3 || (path[2] != '\\' && path[2] != '/'))))
                throw new ArgumentException("An absolute project file path is required.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        internal void Verify(int major, int pid, long ticks, string path, string name)
        {
            if (major != Major || pid != ProcessId || ticks != StartUtcTicks ||
                !string.Equals(CanonicalPath(path), ProjectPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, ProjectName, StringComparison.Ordinal))
                throw new InvalidOperationException("The exact TIA process/project binding changed. Explicitly reconnect to the intended PID and full project path.");
        }
        internal JsonObject ToJson() => new JsonObject {
            ["tiaMajorVersion"] = Major, ["processId"] = ProcessId,
            ["processStartUtc"] = new DateTime(StartUtcTicks, DateTimeKind.Utc).ToString("O"),
            ["projectPath"] = ProjectPath, ["projectName"] = ProjectName, ["generation"] = Generation
        };
    }
}

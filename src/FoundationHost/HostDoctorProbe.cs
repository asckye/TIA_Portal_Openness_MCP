using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcp.Versioning;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;

namespace TiaMcp.FoundationHost;

// Diagnostics never connect a Portal or open a project. Only the existing managed
// worker observations can report startup state or invoke the explicit group repair.
internal sealed class HostDoctorProbe : IDisposable
{
    internal HostOptions Options { get; }
    internal int? DetectedMajor { get; }
    private readonly TiaVersionDescriptor release;
    private readonly (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) assemblies;
    private readonly string apiDirectory;
    private IDisposable? worker;
    private Func<string, JsonNode?>? observe;

    internal HostDoctorProbe(HostOptions options)
    {
        Options = options;
        release = TiaVersionCatalog.Get(options.ReleaseKey);
        Engineering.TiaMajorVersion = release.MajorVersion;
        Engineering.TiaPortalLocationOverride = options.TiaPortalLocation;
        var readiness = FoundationPassiveDiagnostics.Readiness(options.ReleaseKey, options.ApiDirectory, options.ApiDirectorySource);
        string? installed = (string?)readiness["environment"]?["tiaInstallPath"];
        apiDirectory = release.FindApiDirectory(options.ApiDirectory) ?? release.FindApiDirectory(installed!) ?? "";
        DetectedMajor = apiDirectory.Length > 0 ? release.MajorVersion : Engineering.DetectTiaMajorVersion();
        assemblies = ProbeAssemblies(release, apiDirectory);
    }

    internal static (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) ProbeAssemblies(TiaVersionDescriptor release, string apiDirectory)
    {
        if (apiDirectory.Length == 0) return Engineering.ProbeOpennessAssemblies();
        string dll = Path.Combine(apiDirectory, release.ApiAssembly);
        try
        {
            var identity = AssemblyName.GetAssemblyName(dll);
            if (identity.Version != new Version(release.ApiVersion)
                || !string.Equals(identity.Name, Path.GetFileNameWithoutExtension(release.ApiAssembly), StringComparison.Ordinal))
                return (false, apiDirectory, dll, "Openness API version mismatch: selected " + release.DisplayName + ", assembly " + identity.FullName + ".");
            return (true, apiDirectory, dll, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or System.Security.SecurityException)
        { return (false, apiDirectory, dll, error.Message); }
    }

    internal List<EnvironmentDoctor.Check> Checks()
    {
        JsonObject? startup = null;
        // The full read-only checks remain available on a damaged/missing installation.
        // A valid API additionally permits the worker to report its real startup state.
        if (assemblies.Ok && Options.NativeEnabled)
        {
            try { startup = Observe("status")?["readiness"]?.DeepClone().AsObject()
                ?? throw new InvalidDataException("Worker status did not contain readiness."); }
            catch (Exception error)
            {
                startup = new JsonObject { ["ready"] = false, ["cause"] = error.Message,
                    ["recommendedFix"] = "Repair this release's worker and Openness installation; run `tia doctor` again.",
                    ["recommendedFixZh"] = "请修复所选版本的 worker 与 Openness 安装，然后重新运行 `tia doctor`。" };
            }
        }
        string executable = Options.EngineWorkerExe ?? Options.WorkerExe;
        return EnvironmentDoctor.RunHost(Options.ReleaseKey, DetectedMajor, assemblies,
            new[] { AppContext.BaseDirectory, Path.GetDirectoryName(executable)! }, startup);
    }

    internal bool Group(bool fix)
    {
        if (fix) return (bool?)Observe("group.fix") == true;
        return observe != null ? (bool?)Observe("group") == true : EnvironmentDoctor.CurrentUserInOpennessGroup();
    }

    private JsonNode? Observe(string operation)
    {
        if (observe == null)
        {
            if (!Options.NativeEnabled) throw new InvalidOperationException("Worker observations are unavailable with --offline; rerun doctor without --offline for --fix.");
            if (Options.EngineWorkerExe != null)
            {
                var engine = new EngineWorkerClient(Options, File.Exists(Options.EngineWorkerExe) ? EngineCatalog.Hash(Options.EngineWorkerExe) : "");
                worker = engine;
                observe = name => engine.Observe(name, new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                var plc = new WorkerClient(Options.ReleaseKey, Options.WorkerExe, apiDirectory, true, Options.EngineTimeoutSeconds, Options.WorkerTimeoutConfig)
                    { Bundled = Options.BundledWorker, TiaPortalLocation = Options.TiaPortalLocation };
                worker = plc;
                observe = name => plc.Observe(name, new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        return observe(operation);
    }

    public void Dispose() => worker?.Dispose();
}

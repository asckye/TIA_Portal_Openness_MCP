using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Versioning;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;

namespace TiaMcp.FoundationHost;

// What the doctor command reads; tests substitute the installation and worker facts.
internal interface IHostDoctorProbe
{
    public string ReleaseKey { get; }
    public int? DetectedMajor { get; }
    // True when a successful repair means effective membership (PlcWorker); the engine helper returns only a bool.
    public bool FixReportsOutcome { get; }
    public string LogDirectory();
    public string DiagnosticsDirectory();
    public List<EnvironmentDoctor.Check> Checks();
    public bool Group(bool fix);
}

// Diagnostics never connect a Portal or open a project. Only the existing managed
// worker observations can report startup state or invoke the explicit group repair.
internal sealed class HostDoctorProbe : IHostDoctorProbe, IDisposable
{
    // The explicit worker repair that the EnsureOpennessUserGroup tool also uses; the PlcWorker refuses group.fix as an observation.
    internal const string PlcWorkerGroupRepair = "project-lifecycle.EnsureOpennessUserGroup";
    internal HostOptions Options { get; }
    public int? DetectedMajor { get; }
    public string ReleaseKey => Options.ReleaseKey;
    public bool FixReportsOutcome => Options.EngineWorkerExe == null;
    private readonly TiaVersionDescriptor release;
    private readonly (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) assemblies;
    private readonly string apiDirectory;
    private IDisposable? worker;
    private Func<string, JsonNode?>? observe;
    private Func<string, JsonNode?>? call;

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

    public string LogDirectory() => TiaOpenness.Shared.DataLocations.Current.LogDirectory(Options.ReleaseKey);
    public string DiagnosticsDirectory() => TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;

    public List<EnvironmentDoctor.Check> Checks()
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

    public bool Group(bool fix)
    {
        if (fix) return (bool?)Repair() == true;
        return observe != null ? (bool?)Observe("group") == true : EnvironmentDoctor.CurrentUserInOpennessGroup();
    }

    private JsonNode? Repair()
    {
        Start();
        return call != null ? call(PlcWorkerGroupRepair) : observe!("group.fix");
    }

    private JsonNode? Observe(string operation)
    {
        Start();
        return observe!(operation);
    }

    private void Start()
    {
        if (observe != null) return;
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
            call = name => plc.Call(name, new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => worker?.Dispose();
}

// The outcome of `doctor --fix` for `--json`. The PlcWorker repair fails with one of the five result codes that start
// WindowsOpennessGroup.Result.Message (approved 2026-10-18 with P8-76); a success there means effective membership.
internal static class HostDoctorGroupOutcome
{
    internal static readonly IReadOnlyDictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OPENNESS_GROUP_RELOGON_REQUIRED"] = "PendingLogon",
        ["OPENNESS_GROUP_ADDED_RELOGON_REQUIRED"] = "AddedPendingLogon",
        ["OPENNESS_GROUP_ELEVATION_DECLINED"] = "ElevationDeclined",
        ["OPENNESS_GROUP_MISSING"] = "MissingGroup",
        ["OPENNESS_GROUP_ADD_FAILED"] = "AddFailed",
    };

    // Null without --fix. "Unknown" is a failure without a result code, or the engine helper's bool, which cannot tell an
    // effective membership from one that waits for a new logon.
    internal static (string? Outcome, string? Code) Classify(bool fix, bool ok, string? failure, bool reportsOutcome)
    {
        if (!fix) return (null, null);
        if (failure == null) return (ok && reportsOutcome ? "Ready" : "Unknown", null);
        var code = Regex.Match(failure, "OPENNESS_GROUP_[A-Z_]+(?=:)");
        return code.Success && Codes.TryGetValue(code.Value, out var outcome) ? (outcome, code.Value) : ("Unknown", null);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaMcpConfigurator;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public class EnvironmentCheckService : ObservableObject, IEnvironmentCheckService
{
    private readonly Func<EnvironmentCheckContext> _context;
    private readonly Func<EnvironmentProbeSources> _sources;
    private readonly Func<string, string, DoctorRun?> _fixMembership;
    private readonly bool _allowFix;
    private int _busy;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<EnvironmentFinding> Findings { get; private set; } = [];
    public IReadOnlyList<EnvironmentGroup> Groups { get; private set; }
    public IReadOnlyList<string> LogTail { get; private set; } = [];

    public EnvironmentCheckService() : this(EnvironmentServiceContext.Current, WindowsEnvironmentSources.Create, RunDoctorFix, true) { }
    internal EnvironmentCheckService(bool allowFix) : this(EnvironmentServiceContext.Current, WindowsEnvironmentSources.Create, RunDoctorFix, allowFix) { }
    internal EnvironmentCheckService(bool allowFix, Func<string> release) : this(() => EnvironmentServiceContext.Current(release()), WindowsEnvironmentSources.Create, RunDoctorFix, allowFix) { }
    // A fix action that only receives the host path returns no doctor report; when it returns, the checks run again.
    public EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Action<string> fixMembership)
        : this(context, sources, fixMembership, true) { }
    internal EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Action<string> fixMembership, bool allowFix)
        : this(context, sources, (host, _) => { fixMembership(host); return null; }, allowFix) { }
    // runDoctor receives the host path and its release key and returns the doctor's exit code and output.
    internal EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Func<string, string, DoctorRun?> runDoctor, bool allowFix)
    {
        _allowFix = allowFix;
        _context = context; _sources = sources; _fixMembership = runDoctor;
        Groups = Group(WorkbenchEnvironmentChecks.Ids.Select(id => EnvironmentCheckCatalogue.Unchecked(id)).ToArray());
    }

    public LocalizedText Recheck()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return LocalizedText.Empty;
        Groups = Group(WorkbenchEnvironmentChecks.Ids.Select(EnvironmentCheckCatalogue.Checking).ToArray()); Raise(nameof(Groups));
        Completion = Task.Run(() =>
        {
            try
            {
                Findings = Capture();
                Groups = Group(Findings.Select(EnvironmentCheckCatalogue.Row).ToArray());
                var context = _context();
                LogTail = ReadLogTail(context.LogReadRoots ?? new[] { context.LogsDirectory });
            }
            catch (Exception ex)
            {
                Findings = WorkbenchEnvironmentChecks.Ids.Select(id => new EnvironmentFinding(id, EnvironmentFindingStatus.Unknown, "Unknown", new DiagnosticRedactor().Redact(ex.Message).Text)).ToArray();
                Groups = Group(Findings.Select(EnvironmentCheckCatalogue.Row).ToArray());
            }
            finally { Interlocked.Exchange(ref _busy, 0); Raise(nameof(Groups)); Raise(nameof(LogTail)); }
        });
        return LocalizedText.Empty;
    }

    public IReadOnlyList<EnvironmentFinding> Capture() => new WorkbenchEnvironmentChecks(_context(), _sources()).Run();

    internal static IReadOnlyList<string> ReadLogTail(params string[] logsDirectories)
    {
        var redactor = new DiagnosticRedactor();
        var rows = new List<string>();
        foreach (string logsDirectory in logsDirectories.Distinct())
        foreach (string key in TiaMcp.Versioning.TiaVersionCatalog.Runnable.Select(r => r.Key).Append("studio"))
        {
            string directory = Path.Combine(logsDirectory, key);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(2))
            {
                var tail = DiagnosticBundleService.Read(new DiagnosticInput("log", file, "", true));
                rows.Add(Path.GetFileName(file));
                rows.AddRange(redactor.Redact(tail.Text).Text.Split('\n').TakeLast(20));
            }
        }
        return rows.TakeLast(200).ToArray();
    }

    public LocalizedText Fix(string checkId)
    {
        if (!_allowFix) return LocalizedText.Key("Session.OfflineDemo");
        var finding = Findings.FirstOrDefault(f => f.Id == checkId);
        if (finding == null) return EnvironmentCheckCatalogue.Fix(checkId);
        if (checkId != "membership" || finding.Result == "NewLogon" || finding.Status != EnvironmentFindingStatus.Fail)
            return EnvironmentCheckCatalogue.Fix(checkId, finding.Result == "NewLogon");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return LocalizedText.Empty;
        Completion = Task.Run(() =>
        {
            IReadOnlyList<string> doctor = [];
            try
            {
                var context = _context();
                var release = EnvironmentBundleFiles.SelectHostRelease(context.BundleRoot, context.ReleaseKey, File.Exists);
                if (release == null) throw new FileNotFoundException(Loc.Current["Env.NoDoctorHost"]);
                var run = _fixMembership(EnvironmentBundleFiles.Host(context.BundleRoot, release), release);
                var report = run == null ? null : DoctorFixCommand.Parse(run.Output);
                if (run != null)
                {
                    LogTail = doctor = DoctorTail(run, report);
                    // Without the doctor's JSON object only the exit code is known.
                    if (report == null) RequireDoctorSuccess(run.ExitCode);
                }
                Findings = Capture();
                if (report is { } outcome) Findings = Findings.Select(f => f.Id == checkId ? FixOutcome(f, outcome) : f).ToArray();
                Groups = Group(Findings.Select(EnvironmentCheckCatalogue.Row).ToArray());
            }
            catch (Exception ex)
            {
                string error = new DiagnosticRedactor().Redact(ex.Message).Text;
                LogTail = [error, .. doctor];
                Findings = Findings.Select(f => f.Id == checkId
                    ? new EnvironmentFinding(checkId, EnvironmentFindingStatus.Fail, "FixFailed", error) : f).ToArray();
                Groups = Group(Findings.Select(EnvironmentCheckCatalogue.Row).ToArray());
            }
            finally { Interlocked.Exchange(ref _busy, 0); Raise(nameof(Groups)); Raise(nameof(LogTail)); }
        });
        return LocalizedText.Key("Env.FixStarted");
    }

    // UAC can occur only after Fix is clicked. The prompt comes from the worker's own elevated net.exe, so the hidden doctor
    // runs without a shell and its output is captured.
    private static DoctorRun RunDoctorFix(string host, string releaseKey)
    {
        using var process = Process.Start(DoctorStartInfo(host, releaseKey));
        if (process == null) throw new InvalidOperationException(Loc.Current["Env.FixFailed"]);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new DoctorRun(process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }

    internal static ProcessStartInfo DoctorStartInfo(string host, string releaseKey)
    {
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in DoctorFixCommand.Arguments(releaseKey)) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void RequireDoctorSuccess(int exitCode)
    {
        if (exitCode != 0) throw new InvalidOperationException(Loc.Current.T("Env.DoctorFailed", exitCode));
    }

    // The doctor's report lines (or its raw output without a report) and its error output, redacted.
    internal static IReadOnlyList<string> DoctorTail(DoctorRun run, DoctorFixReport? report)
    {
        var redactor = new DiagnosticRedactor();
        static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0);
        return (report?.Lines ?? Lines(run.Output)).Concat(Lines(run.Errors))
            .Select(line => redactor.Redact(line).Text).TakeLast(200).ToArray();
    }

    // Ready keeps the recheck; a join that waits for a new logon shows the sign-out guidance; anything else is a failed fix
    // with its reason.
    internal static EnvironmentFinding FixOutcome(EnvironmentFinding recheck, DoctorFixReport report)
    {
        string reason = new DiagnosticRedactor().Redact(report.Message ?? "").Text;
        return report.State switch
        {
            DoctorFixState.Ready => recheck,
            DoctorFixState.NeedsLogon => new EnvironmentFinding(recheck.Id, EnvironmentFindingStatus.Fail, "NewLogon", reason),
            _ => new EnvironmentFinding(recheck.Id, EnvironmentFindingStatus.Fail, "FixFailed",
                Loc.Current[FixFailureKey(report.Outcome)] + (reason.Length > 0 ? "\n" + reason : "")),
        };
    }

    internal static string FixFailureKey(string? outcome) => outcome switch
    {
        "ElevationDeclined" => "Env.FixDeclined",
        "MissingGroup" => "Env.FixGroupMissing",
        "AddFailed" => "Env.FixAddFailed",
        _ => "Env.FixFailed",
    };

    private static IReadOnlyList<EnvironmentGroup> Group(IReadOnlyList<EnvironmentCheck> rows) =>
    [
        new(LocalizedText.Literal("TIA Portal"), rows.Where(r => r.Id is "installations" or "membership" or "openness-readiness" or "confirmation" or "openness-allowlist").ToArray()),
        new(LocalizedText.Key("Env.Runtime"), rows.Where(r => r.Id is "framework" or "runtime").ToArray()),
        new(LocalizedText.Key("Env.Application"), rows.Where(r => r.Id.StartsWith("engine-", StringComparison.Ordinal) || r.Id == "data").ToArray()),
        new(LocalizedText.Key("Env.Network"), rows.Where(r => r.Id is "port" or "url" or "firewall").ToArray()),
    ];
}

public static class EnvironmentServiceContext
{
    public static EnvironmentCheckContext Current()
        => Current(WorkbenchReleaseSelection.DefaultKey());

    public static EnvironmentCheckContext Current(string releaseKey)
    {
        var data = DataLocations.Current;
        string root = data.WorkbenchDataDirectory;
        var context = new EnvironmentCheckContext
        {
            ReleaseKey = releaseKey,
            BundleRoot = BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory), DataRoot = root,
            UserFallback = data.Root == null, ConfigDirectory = data.ConfigDirectory, LogsDirectory = data.LogsDirectory,
            DiagnosticsDirectory = data.DiagnosticsDirectory, AuditDirectory = data.AuditDirectory,
            LogReadRoots = data.LogReadRoots, AuditReadRoots = data.AuditReadRoots,
        };
        LoadEndpoint(context);
        return context;
    }

    internal static void LoadEndpoint(EnvironmentCheckContext context)
    {
        try
        {
            var configurations = Directory.Exists(context.ConfigDirectory) ? Directory.GetFiles(context.ConfigDirectory, "http-v*.json") : [];
            var latest = configurations.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest == null) return;
            context.HttpConfigurationPath = latest;
            var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(latest));
            if (settings == null) throw new InvalidDataException("Empty HTTP configuration.");
            context.HttpPrefix = ConfigCore.Prefix(settings.Address, settings.Port);
        }
        catch (Exception ex) { context.HttpEndpointError = new DiagnosticRedactor().Redact(ex.Message).Text; }
    }
}

public static class EnvironmentCheckCatalogue
{
    public static LocalizedText Name(string id) => id.StartsWith("engine-", StringComparison.Ordinal)
        ? LocalizedText.Key("Env.ReleaseFiles", TiaMcp.Versioning.TiaVersionCatalog.Get(id[7..]).DisplayName)
        : LocalizedText.Key(id switch
        {
            "installations" => "Env.Installations", "membership" => "Env.Membership", "openness-readiness" => "Env.OpennessReadiness", "framework" => "Env.Framework", "engines" => "Env.Engines",
            "runtime" => "Env.BundledRuntime", "data" => "Env.Data", "port" => "Env.Port", "url" => "Env.Url",
            "firewall" => "Env.Firewall", "openness-allowlist" => "Env.OpennessAllowList", _ => "Env.FirstConnection",
        });

    public static LocalizedText Fix(string id, bool newLogon = false) => LocalizedText.Key(FixKey(id, newLogon));
    public static string FixKey(string id, bool newLogon = false) => id.StartsWith("engine-", StringComparison.Ordinal) ? "Env.FixFiles" : id switch
    {
        "installations" => "Env.FixInstall", "membership" => newLogon ? "Env.FixLogon" : "Env.FixMembership",
        "openness-readiness" => "Env.FixOpennessReadiness",
        "framework" => "Env.FixFramework", "runtime" => "Env.FixRuntime", "data" => "Env.FixData",
        "port" => "Env.FixPort", "url" => "Env.FixUrl", "firewall" => "Env.FixFirewall", "openness-allowlist" => "Env.FixOpennessAllowList", _ => "Env.FirstConnectionHelp",
    };

    public static EnvironmentCheck Unchecked(string id) => new(id, CheckStatus.Unchecked, Name(id), LocalizedText.Key("Env.Unchecked"), LocalizedText.Empty, LocalizedText.Empty);
    public static EnvironmentCheck Checking(string id) => Unchecked(id) with { Status = CheckStatus.Checking, Result = LocalizedText.Key("Env.Checking") };
    public static EnvironmentCheck Row(EnvironmentFinding finding)
    {
        var status = finding.Status switch { EnvironmentFindingStatus.Pass => CheckStatus.Pass, EnvironmentFindingStatus.Fail => CheckStatus.Fail, _ => CheckStatus.Warn };
        var result = LocalizedText.Key(ResultKey(finding));
        string evidence = new DiagnosticRedactor().Redact(finding.Evidence ?? "").Text;
        return new(finding.Id, status, Name(finding.Id), result,
            LocalizedText.Key(finding.Id == "membership" && finding.Result != "NewLogon" && status == CheckStatus.Fail ? "Env.FixButton" : "Env.ManualButton"),
            LocalizedText.Key("Env.CheckDetail", result, Fix(finding.Id, finding.Result == "NewLogon"), evidence));
    }

    public static string ResultKey(EnvironmentFinding finding) => finding.Result switch
    {
        "Pass" => "Env.Passed", "Fail" => "Env.Failed", "Unknown" => "Env.Unknown", "NewLogon" => "Env.NewLogon",
        "NotReady" => "Env.NotReady",
        "FixFailed" => "Env.FixFailed",
        "Fallback" => "Env.Fallback", "PortUsed" => "Env.PortUsed", "NoEndpoint" => "Env.NoEndpoint", _ => "Env.Guidance",
    };
}

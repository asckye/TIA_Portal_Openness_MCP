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

    public EnvironmentCheckService() : this(EnvironmentServiceContext.Current, ProxyConnectionInspector.Sources, RunDoctorFix, true) { }
    internal EnvironmentCheckService(bool allowFix) : this(EnvironmentServiceContext.Current, ProxyConnectionInspector.Sources, RunDoctorFix, allowFix) { }
    internal EnvironmentCheckService(bool allowFix, Func<string> release)
        : this(() => EnvironmentServiceContext.Current(release()), ProxyConnectionInspector.Sources, RunDoctorFix, allowFix) { }
    // A fix action that only receives the host path returns no doctor report; when it returns, the checks run again.
    public EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Action<string> fixMembership)
        : this(context, sources, fixMembership, true) { }
    internal EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Action<string> fixMembership, bool allowFix)
        : this(context, sources, (host, _) => { fixMembership(host); return null; }, allowFix) { }
    // runDoctor receives the host path and its release key and returns the doctor's exit code and output.
    internal EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Func<string, string, DoctorRun?> runDoctor,
            bool allowFix)
    {
        _allowFix = allowFix;
        _context = context; _sources = sources; _fixMembership = runDoctor;
        Groups = Group(PageIds.Select(EnvironmentCheckCatalogue.Unchecked).ToArray());
    }

    public LocalizedText Recheck()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return LocalizedText.Empty;
        Groups = Group(PageIds.Select(EnvironmentCheckCatalogue.Checking).ToArray()); Raise(nameof(Groups));
        Completion = Task.Run(() =>
        {
            try
            {
                Findings = Capture();
                Groups = CompletedGroups(Findings);
                var context = _context();
                LogTail = ReadLogTail(context.LogReadRoots ?? new[] { context.LogsDirectory });
            }
            catch (Exception ex)
            {
                Findings = WorkbenchEnvironmentChecks.Ids
                    .Select(id => new EnvironmentFinding(id, EnvironmentFindingStatus.Unknown, "Unknown", new DiagnosticRedactor().Redact(ex.Message).Text))
                    .ToArray();
                Groups = CompletedGroups(Findings);
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
                Groups = CompletedGroups(Findings);
            }
            catch (Exception ex)
            {
                string error = new DiagnosticRedactor().Redact(ex.Message).Text;
                LogTail = [error, .. doctor];
                Findings = Findings.Select(f => f.Id == checkId
                    ? new EnvironmentFinding(checkId, EnvironmentFindingStatus.Fail, "FixFailed", error) : f).ToArray();
                Groups = CompletedGroups(Findings);
            }
            finally { Interlocked.Exchange(ref _busy, 0); Raise(nameof(Groups)); Raise(nameof(LogTail)); }
        });
        return LocalizedText.Key("Env.FixStarted");
    }

    // UAC can occur only after Fix is clicked. The prompt comes from the worker's own elevated net.exe, so the hidden doctor
    // runs without a shell and its output is captured.
    internal static DoctorRun RunDoctorFix(string host, string releaseKey)
    {
        using var process = new Process { StartInfo = DoctorStartInfo(host, releaseKey) };
        try
        {
            if (!process.Start()) throw new InvalidOperationException(Loc.Current["Env.DoctorStartFailed"]);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(Loc.Current["Env.DoctorStartFailed"], ex);
        }
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

    private static bool WorkerFiles(string id) => id.StartsWith("engine-", StringComparison.Ordinal);
    private static bool IndividualPageCheck(string id) => id != "confirmation" && !WorkerFiles(id);
    private static IEnumerable<string> PageIds => WorkbenchEnvironmentChecks.Ids.Where(IndividualPageCheck).Prepend("engines");

    private IReadOnlyList<EnvironmentGroup> CompletedGroups(IReadOnlyList<EnvironmentFinding> findings)
    {
        // Keep Capture and Findings at release granularity for diagnostic exports.
        var problems = findings.Where(f => WorkerFiles(f.Id) && f.Status != EnvironmentFindingStatus.Pass).ToArray();
        var status = problems.Any(f => f.Status == EnvironmentFindingStatus.Fail) ? EnvironmentFindingStatus.Fail
            : problems.Length > 0 ? EnvironmentFindingStatus.Unknown : EnvironmentFindingStatus.Pass;
        var files = new EnvironmentFinding("engines", status, status.ToString(),
            string.Join("\n", problems.Select(f => TiaMcp.Versioning.TiaVersionCatalog.Get(f.Id[7..]).DisplayName + "\n" + f.Evidence)));
        return Group(findings.Where(f => IndividualPageCheck(f.Id)).Prepend(files).Select(f => EnvironmentCheckCatalogue.Row(f, _allowFix)).ToArray());
    }

    private static IReadOnlyList<EnvironmentGroup> Group(IReadOnlyList<EnvironmentCheck> rows) =>
    [
        new(LocalizedText.Literal("TIA Portal"), rows
                .Where(r => r.Id is "installations" or "membership" or "openness-readiness" or "openness-allowlist").ToArray()),
        new(LocalizedText.Key("Env.Runtime"), rows.Where(r => r.Id is "framework" or "runtime").ToArray()),
        new(LocalizedText.Key("Env.Application"), rows.Where(r => r.Id is "engines" or "data").ToArray()),
        new(LocalizedText.Key("Env.Network"), rows.Where(r => r.Id is "port" or "url" or "firewall" or "proxy-remote").ToArray()),
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
            var client = Path.Combine(context.ConfigDirectory, "client.json");
            var latest = configurations.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest != null) context.HttpConfigurationPath = latest;
            var endpoints = new List<string>();
            foreach (string path in File.Exists(client) ? configurations.Append(client) : configurations)
            {
                try
                {
                    using var record = JsonDocument.Parse(File.ReadAllText(path));
                    var settings = record.RootElement;
                    if (!settings.TryGetProperty("Address", out var address))
                    {
                        if (path == latest) throw new InvalidDataException("Missing remote address.");
                        continue;
                    }
                    string prefix = ConfigCore.EndpointPrefix(address.GetString() ?? throw new InvalidDataException("Missing remote address."),
                        settings.GetProperty("Port").GetInt32());
                    endpoints.Add(prefix + "mcp");
                    if (path == latest) context.HttpPrefix = prefix;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                    or FormatException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
                {
                    string message = new DiagnosticRedactor().Redact(ex.Message).Text;
                    if (path == latest || latest == null) context.HttpEndpointError = message;
                    else Trace.TraceWarning("Cannot read saved endpoint: " + message);
                }
            }
            context.RemoteEndpoints = endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            "installations" => "Env.Installations", "membership" => "Env.Membership", "openness-readiness" => "Env.OpennessReadiness", "framework" =>
                "Env.Framework", "engines" => "Env.Engines",
            "runtime" => "Env.BundledRuntime", "data" => "Env.Data", "port" => "Env.Port", "url" => "Env.Url",
            "firewall" => "Env.Firewall", "proxy-remote" => "Env.ProxyRemote", "openness-allowlist" => "Env.OpennessAllowList", _ => "Env.FirstConnection",
        });

    public static LocalizedText Fix(string id, bool newLogon = false) => LocalizedText.Key(FixKey(id, newLogon));
    public static string FixKey(string id, bool newLogon = false) => id.StartsWith("engine-", StringComparison.Ordinal) ? "Env.FixFiles" : id switch
    {
        "installations" => "Env.FixInstall", "membership" => newLogon ? "Env.FixLogon" : "Env.FixMembership",
        "openness-readiness" => "Env.FixOpennessReadiness",
        "engines" => "Env.FixFiles", "framework" => "Env.FixFramework", "runtime" => "Env.FixRuntime", "data" => "Env.FixData",
        "port" => "Env.FixPort", "url" => "Env.FixUrl", "firewall" => "Env.FixFirewall", "proxy-remote" => "Env.FixProxyRemote", "openness-allowlist" =>
            "Env.FixOpennessAllowList", _ => "Env.FirstConnectionHelp",
    };

    public static EnvironmentCheck Unchecked(string id) =>
        new(id, CheckStatus.Unchecked, Name(id), LocalizedText.Key("Env.Unchecked"), LocalizedText.Empty, LocalizedText.Empty);
    public static EnvironmentCheck Checking(string id) => Unchecked(id) with { Status = CheckStatus.Checking, Result = LocalizedText.Key("Env.Checking") };
    public static EnvironmentCheck Row(EnvironmentFinding finding) => Row(finding, true);

    internal static EnvironmentCheck Row(EnvironmentFinding finding, bool allowFix)
    {
        var status = finding.Status switch {
            EnvironmentFindingStatus.Pass => CheckStatus.Pass, EnvironmentFindingStatus.Fail => CheckStatus.Fail, _ => CheckStatus.Warn };
        var issue = BannerIssue(finding);
        var result = finding.Result switch
        {
            "Unknown" => LocalizedText.Key("Env.CheckUnknown", Name(finding.Id)),
            "PortUsed" when issue?.Endpoint is { } endpoint => LocalizedText.Key("Env.PortInUse", endpoint.Port),
            "Fail" when issue is { Id: "firewall", Endpoint: { } endpoint } => LocalizedText.Key("Env.FirewallPortMissing", endpoint.Port),
            _ => LocalizedText.Key(ResultKey(finding)),
        };
        string evidence = new DiagnosticRedactor().Redact(finding.Evidence ?? "").Text;
        LocalizedText? action = finding switch
        {
            { Id: "membership", Status: EnvironmentFindingStatus.Fail, Result: not "NewLogon" } => LocalizedText.Key("Env.FixButton"),
            { Id: "proxy-remote", Result: "ProxyCaptured" or "Fail" } => LocalizedText.Key("Env.ProxyFixButton"),
            _ when issue?.Endpoint != null => issue.Action,
            _ => null,
        };
        var label = status == CheckStatus.Pass ? LocalizedText.Empty : action ?? LocalizedText.Key("Env.ManualButton");
        return new(finding.Id, status, Name(finding.Id), result, label,
            LocalizedText.Key("Env.CheckDetail", result, Fix(finding.Id, finding.Result == "NewLogon"), evidence))
            { Issue = issue, CanRepair = allowFix && action.HasValue };
    }

    private static EnvironmentIssue? BannerIssue(EnvironmentFinding finding)
    {
        if (finding.Status != EnvironmentFindingStatus.Fail) return null;
        if (finding.Id == "membership" && finding.Result is "Fail" or "NewLogon")
            return new(finding.Id, null, LocalizedText.Key(finding.Result == "NewLogon" ? "Shell.GroupLogon" : "Shell.GroupMissing"),
                LocalizedText.Key(finding.Result == "NewLogon" ? "Shell.GroupLogonHelp" : "Shell.GroupHelp"), LocalizedText.Key("Shell.OpenDoctor"));
        if ((finding.Id, finding.Result) is not (("port", "PortUsed") or ("firewall", "Fail"))) return null;
        // The first evidence line is the checked endpoint. Never substitute the editable port on the MCP page.
        var lines = (finding.Evidence ?? "").Split('\n');
        if (!Uri.TryCreate(lines[0].Trim(), UriKind.Absolute, out var endpoint) || endpoint.Port <= 0) return null;
        bool port = finding.Id == "port";
        var description = port && lines.Length > 2
            ? LocalizedText.Key("Shell.PortOwners", new DiagnosticRedactor().Redact(string.Join(", ", lines.Skip(2))).Text, endpoint.Port)
            : LocalizedText.Key(port ? "Shell.PortHelp" : "Shell.FirewallHelp");
        return new(finding.Id, endpoint, LocalizedText.Key(port ? "Shell.PortUsed" : "Shell.FirewallBlocked", endpoint.Port),
            description, LocalizedText.Key(port ? "Shell.ChangePort" : "Env.ConfigureNetworkPermission"));
    }

    public static string ResultKey(EnvironmentFinding finding) => finding.Result switch
    {
        "Pass" => finding.Id == "engines" ? "Env.FilesPresent" : "Env.Passed",
        "Fail" => finding.Id switch
        {
            "membership" => "Env.GroupMissing", "installations" => "Env.InstallationMissing", "framework" => "Env.FrameworkMissing",
            "runtime" => "Env.RuntimeMissing", "data" => "Env.DataReadOnly", "url" => "Env.UrlMissing",
            "firewall" => "Env.FirewallMissing", "openness-allowlist" => "Env.AllowListMismatch", "proxy-remote" => "Env.ProxyUnreachable",
            _ when finding.Id == "engines" || finding.Id.StartsWith("engine-", StringComparison.Ordinal) => "Env.FilesMissing",
            _ => "Env.Failed",
        },
        "Unknown" => "Env.Unknown", "NewLogon" => "Env.NewLogon",
        "NotReady" => "Env.NotReady",
        "ProxyCaptured" => "Env.ProxyCaptured",
        "FixFailed" => "Env.FixFailed",
        "Fallback" => "Env.Fallback", "PortUsed" => "Env.PortUsed", "NoEndpoint" => "Env.NoEndpoint", _ => "Env.Guidance",
    };
}

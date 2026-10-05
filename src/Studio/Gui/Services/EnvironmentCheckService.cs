using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private readonly Action<string> _fixMembership;
    private int _busy;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<EnvironmentFinding> Findings { get; private set; } = [];
    public IReadOnlyList<EnvironmentGroup> Groups { get; private set; }
    public IReadOnlyList<string> LogTail { get; private set; } = [];

    public EnvironmentCheckService() : this(EnvironmentServiceContext.Current, WindowsEnvironmentSources.Create, RunDoctorFix) { }
    public EnvironmentCheckService(Func<EnvironmentCheckContext> context, Func<EnvironmentProbeSources> sources, Action<string> fixMembership)
    {
        _context = context; _sources = sources; _fixMembership = fixMembership;
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
                LogTail = [DateTimeOffset.Now.ToString("HH:mm:ss") + " — " + Findings.Count + " checks"];
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

    public LocalizedText Fix(string checkId)
    {
        var finding = Findings.FirstOrDefault(f => f.Id == checkId);
        if (finding == null) return EnvironmentCheckCatalogue.Fix(checkId);
        if (checkId != "membership" || finding.Result == "NewLogon" || finding.Status != EnvironmentFindingStatus.Fail)
            return EnvironmentCheckCatalogue.Fix(checkId, finding.Result == "NewLogon");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return LocalizedText.Empty;
        Completion = Task.Run(() =>
        {
            try
            {
                var root = _context().BundleRoot;
                var host = root == null ? null : new[] { "21", "20" }.Select(k => EnvironmentBundleFiles.Host(root, k)).FirstOrDefault(File.Exists);
                if (host == null) throw new FileNotFoundException(Loc.Current["Env.NoDoctorHost"]);
                _fixMembership(host);
                Findings = Capture(); Groups = Group(Findings.Select(EnvironmentCheckCatalogue.Row).ToArray());
            }
            catch (Exception ex) { LogTail = [new DiagnosticRedactor().Redact(ex.Message).Text]; }
            finally { Interlocked.Exchange(ref _busy, 0); Raise(nameof(Groups)); Raise(nameof(LogTail)); }
        });
        return LocalizedText.Key("Env.FixStarted");
    }

    private static void RunDoctorFix(string host)
    {
        // The existing doctor owns the only automatic fix. UAC can occur only after Fix is clicked.
        using var process = Process.Start(new ProcessStartInfo(host, "doctor --fix --logging 0")
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        if (process == null) throw new InvalidOperationException(Loc.Current["Env.FixFailed"]);
        process.WaitForExit();
    }

    private static IReadOnlyList<EnvironmentGroup> Group(IReadOnlyList<EnvironmentCheck> rows) =>
    [
        new(LocalizedText.Literal("TIA Portal"), rows.Where(r => r.Id is "installations" or "membership" or "confirmation").ToArray()),
        new(LocalizedText.Key("Env.Runtime"), rows.Where(r => r.Id is "framework" or "runtime").ToArray()),
        new(LocalizedText.Key("Env.Application"), rows.Where(r => r.Id.StartsWith("engine-", StringComparison.Ordinal) || r.Id == "data").ToArray()),
        new(LocalizedText.Key("Env.Network"), rows.Where(r => r.Id is "port" or "url" or "firewall").ToArray()),
    ];
}

public static class EnvironmentServiceContext
{
    public static EnvironmentCheckContext Current()
    {
        var data = DataLocations.Current;
        string root = data.WorkbenchDataDirectory;
        var context = new EnvironmentCheckContext
        {
            BundleRoot = BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory), DataRoot = root,
            UserFallback = data.Root == null, ConfigDirectory = data.ConfigDirectory, LogsDirectory = data.LogsDirectory,
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
            "installations" => "Env.Installations", "membership" => "Env.Membership", "framework" => "Env.Framework", "engines" => "Env.Engines",
            "runtime" => "Env.BundledRuntime", "data" => "Env.Data", "port" => "Env.Port", "url" => "Env.Url",
            "firewall" => "Env.Firewall", _ => "Env.FirstConnection",
        });

    public static LocalizedText Fix(string id, bool newLogon = false) => LocalizedText.Key(FixKey(id, newLogon));
    public static string FixKey(string id, bool newLogon = false) => id.StartsWith("engine-", StringComparison.Ordinal) ? "Env.FixFiles" : id switch
    {
        "installations" => "Env.FixInstall", "membership" => newLogon ? "Env.FixLogon" : "Env.FixMembership",
        "framework" => "Env.FixFramework", "runtime" => "Env.FixRuntime", "data" => "Env.FixData",
        "port" => "Env.FixPort", "url" => "Env.FixUrl", "firewall" => "Env.FixFirewall", _ => "Env.FirstConnectionHelp",
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
        "Fallback" => "Env.Fallback", "PortUsed" => "Env.PortUsed", "NoEndpoint" => "Env.NoEndpoint", _ => "Env.Guidance",
    };
}

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;
using CheckStatus = TiaOpenness.Contracts.Models.CheckStatus;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class EnvironmentServicesTests(WpfContext wpf)
{
    [Fact]
    public void Missing_framework_page_finding_explains_the_bilingual_install_fix()
    {
        var previous = Loc.Current.Language;
        try
        {
            var row = EnvironmentCheckCatalogue.Row(new EnvironmentFinding("framework", EnvironmentFindingStatus.Fail, "Fail", "not detected"));
            Loc.Current.Language = AppLanguage.English;
            Assert.Contains("Download and install", row.Detail.Resolve(), StringComparison.Ordinal);
            Assert.Contains(".NET Framework 4.8", row.Detail.Resolve(), StringComparison.Ordinal);
            Assert.Contains("https://dotnet.microsoft.com/download/dotnet-framework/net48", row.Detail.Resolve(), StringComparison.Ordinal);
            Loc.Current.Language = AppLanguage.Chinese;
            Assert.Contains("安装", row.Detail.Resolve(), StringComparison.Ordinal);
            Assert.Contains(".NET Framework 4.8", row.Detail.Resolve(), StringComparison.Ordinal);
        }
        finally { Loc.Current.Language = previous; }
    }

    [Fact]
    public async Task Recheck_is_read_only_and_only_a_membership_click_invokes_the_existing_doctor()
    {
        using var fixture = new Fixture();
        int fixes = 0;
        var service = new EnvironmentCheckService(() => fixture.Context, Sources, _ => fixes++);
        service.Recheck(); await service.Completion;
        Assert.Equal(0, fixes); Assert.Equal(17, service.Findings.Count);
        Assert.All(service.Groups.SelectMany(g => g.Rows), row => Assert.NotEqual(Services.CheckStatus.Unchecked, row.Status));
        service.Fix("url"); Assert.Equal(0, fixes);
        string host = EnvironmentBundleFiles.Host(fixture.Root, "21");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!); File.WriteAllText(host, "fake doctor");
        service.Fix("membership"); await service.Completion; Assert.Equal(1, fixes);
        var logon = Sources(); logon.ListedGroupMember = () => true;
        var added = new EnvironmentCheckService(() => fixture.Context, () => logon, _ => fixes++);
        added.Recheck(); await added.Completion; added.Fix("membership"); await added.Completion;
        Assert.Equal(1, fixes); Assert.Equal("NewLogon", added.Findings.Single(f => f.Id == "membership").Result);
        var unknown = Sources(); unknown.ListedGroupMember = () => null;
        var unavailable = new EnvironmentCheckService(() => fixture.Context, () => unknown, _ => fixes++);
        unavailable.Recheck(); await unavailable.Completion; unavailable.Fix("membership"); await unavailable.Completion;
        Assert.Equal(1, fixes);
    }

    [Fact]
    public void Every_check_has_translated_result_fix_action_and_detail_in_both_languages()
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language;
            try
            {
                foreach (string id in WorkbenchEnvironmentChecks.Ids)
                    foreach (string result in new[] { "Pass", "Fail", "Unknown", "NewLogon", "Fallback", "PortUsed", "NoEndpoint", "Guidance" })
                    {
                        var finding = new EnvironmentFinding(id, EnvironmentFindingStatus.Fail, result, "path");
                        var row = EnvironmentCheckCatalogue.Row(finding);
                        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
                        {
                            Loc.Current.Language = language;
                            foreach (var text in new[] { row.Name, row.Result, row.FixAction, row.Detail, EnvironmentCheckCatalogue.Fix(id) })
                            { Assert.NotEmpty(text.Resolve()); Assert.DoesNotContain("[Env.", text.Resolve()); }
                        }
                    }
            }
            finally { Loc.Current.Language = previous; }
        });
    }

    [Fact]
    public async Task Bundle_manifest_includes_checks_each_host_workbench_config_missing_files_and_redaction_counts()
    {
        using var fixture = new Fixture();
        string config = Path.Combine(fixture.Root, "server.json"), log = Path.Combine(fixture.Root, "host.log");
        File.WriteAllText(config, "{\"ProtectedKey\":\"private-config\",\"port\":8765}");
        File.WriteAllText(log, string.Join("\n", Enumerable.Range(0, 300).Select(i => "line-" + i)) + "\nAuthorization: Bearer private-auth\ndebug echo private-config");
        var input = new[]
        {
            new DiagnosticInput("config", config, "config/server.json"),
            new DiagnosticInput("workbench-log", log, "logs/workbench/current.log", true),
            new DiagnosticInput("host-log", log, "logs/hosts/21/host.log", true),
            new DiagnosticInput("host-log", Path.Combine(fixture.Root, "absent.log"), "logs/hosts/20/host.log", true),
        };
        string? opened = null;
        var service = new DiagnosticBundleService(() => fixture.Context, () => [new("data", EnvironmentFindingStatus.Pass, "Fallback", "password=private-evidence")], _ => input, folder => opened = folder);
        var progress = new System.Collections.Generic.List<DiagnosticState>();
        service.PropertyChanged += (_, _) => progress.Add(service.Progress.State);
        service.Export(); var completion = service.Completion; service.Export(); Assert.Same(completion, service.Completion);
        await service.Completion;
        Assert.Equal(DiagnosticState.Done, service.Progress.State); Assert.Equal(100, service.Progress.Percent);
        Assert.StartsWith(Path.Combine(fixture.Context.DataRoot, "reports"), service.Progress.Path);
        Assert.Contains(DiagnosticState.Running, progress); service.OpenFolder(); Assert.Equal(Path.GetDirectoryName(service.Progress.Path), opened);
        using var archive = ZipFile.OpenRead(service.Progress.Path);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open()); string content = reader.ReadToEnd();
            foreach (string secret in new[] { "private-config", "private-auth", "private-evidence" }) Assert.DoesNotContain(secret, content);
        }
        var manifest = JsonNode.Parse(Read(archive, "manifest.json"))!;
        var items = manifest["items"]!.AsArray();
        Assert.Equal(5, items.Count); Assert.Contains(items, item => item!["State"]!.GetValue<string>() == "missing");
        Assert.Contains(items, item => item!["Truncated"]!.GetValue<bool>());
        Assert.Contains(items, item => item!["Redactions"]!.AsObject().Count > 0);
        var checks = JsonNode.Parse(Read(archive, "environment/checks.json"))!;
        Assert.True(checks["userFallback"]!.GetValue<bool>());
        Assert.NotEmpty(checks["checks"]![0]!["zh"]!["fix"]!.GetValue<string>());
        Assert.DoesNotContain("line-0\n", Read(archive, "logs/hosts/21/host.log"));
    }

    [Fact]
    public async Task Open_workbench_activity_is_snapshotted_redacted_and_bounded_without_writing_a_log_file()
    {
        using var fixture = new Fixture();
        DiagnosticInput? activity = null;
        wpf.Run(() =>
        {
            var model = new ViewModels.MainViewModel(new FakeStudioClient(), new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                for (int index = 0; index < 250; index++) model.Activity.Append("line-" + index);
                model.Activity.Append("Authorization: Bearer private-workbench");
                activity = DiagnosticBundleService.Inputs(fixture.Context).Single(input => input.Content != null && input.Content.Contains("private-workbench", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
        Assert.NotNull(activity);
        var service = new DiagnosticBundleService(() => fixture.Context, () => [], _ => [activity!], _ => { });
        service.Export(); await service.Completion;
        Assert.Equal(DiagnosticState.Done, service.Progress.State);
        using var archive = ZipFile.OpenRead(service.Progress.Path);
        string log = Read(archive, activity!.Entry);
        Assert.DoesNotContain("private-workbench", log); Assert.Contains(DiagnosticRedactor.Mask, log);
        Assert.DoesNotContain("line-0\n", log.Replace("\r\n", "\n"));
        Assert.Contains("\"Truncated\": true", Read(archive, "manifest.json"));
        Assert.False(File.Exists(activity.Path));
    }

    [Fact]
    public async Task Read_only_install_falls_back_to_user_reports_without_creating_install_files()
    {
        using var fixture = new Fixture();
        string studio = Path.Combine(fixture.Root, "runtime", "studio"); Directory.CreateDirectory(studio);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "manifest")); File.WriteAllText(Path.Combine(fixture.Root, "manifest", "package-manifest.json"), "{}");
        File.WriteAllText(Path.Combine(fixture.Root, "data"), "read-only install fixture");
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        var locations = DataLocations.Resolve(studio, null, Path.Combine(fixture.Root, "user"), Path.Combine(fixture.Root, "temp"));
        Assert.Null(locations.Root);
        fixture.Context.UserFallback = true; fixture.Context.DataRoot = locations.WorkbenchDataDirectory;
        var service = new DiagnosticBundleService(() => fixture.Context, () => [], _ => [], _ => { });
        service.Export(); await service.Completion;
        Assert.Equal(DiagnosticState.Done, service.Progress.State);
        Assert.StartsWith(fixture.Context.DataRoot, service.Progress.Path);
        var after = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(Path.Combine(fixture.Root, "user"))).OrderBy(p => p).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Invalid_and_oversized_configs_are_hidden_and_export_failure_is_reported()
    {
        using var fixture = new Fixture();
        string bad = Path.Combine(fixture.Root, "bad.json"), huge = Path.Combine(fixture.Root, "huge.json");
        File.WriteAllText(bad, "{malformed private-one"); File.WriteAllText(huge, new string('x', 2097153));
        var service = new DiagnosticBundleService(() => fixture.Context, () => [], _ =>
        [new("config", bad, "config/bad.json"), new("config", huge, "config/huge.json")], _ => { });
        service.Export(); await service.Completion;
        using (var archive = ZipFile.OpenRead(service.Progress.Path))
        {
            Assert.Equal(DiagnosticRedactor.Mask, Read(archive, "config/bad.json")); Assert.Null(archive.GetEntry("config/huge.json"));
            Assert.Contains("skipped-oversized-config", Read(archive, "manifest.json"));
            Assert.Contains("unparseable-hidden", Read(archive, "manifest.json"));
        }
        fixture.Context.DataRoot = bad;
        service.Export(); await service.Completion;
        Assert.Equal(DiagnosticState.Idle, service.Progress.State); Assert.Equal("", service.Progress.Path);
        wpf.Run(() => Assert.Contains("failed", service.Progress.Step.Resolve(), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories));
    }

    private static string Read(ZipArchive archive, string entry)
    { using var reader = new StreamReader(archive.GetEntry(entry)!.Open()); return reader.ReadToEnd(); }

    [Fact]
    public void Damaged_saved_endpoint_does_not_hide_data_or_runtime_checks()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Context.ConfigDirectory);
        fixture.Context.HttpPrefix = null!;
        File.WriteAllText(Path.Combine(fixture.Context.ConfigDirectory, "http-v21.json"), "{invalid private-config");
        EnvironmentServiceContext.LoadEndpoint(fixture.Context);
        var findings = new WorkbenchEnvironmentChecks(fixture.Context, Sources()).Run();
        Assert.Equal(EnvironmentFindingStatus.Pass, findings.Single(f => f.Id == "data").Status);
        Assert.Equal(EnvironmentFindingStatus.Unknown, findings.Single(f => f.Id == "url").Status);
        Assert.DoesNotContain("private-config", fixture.Context.HttpEndpointError);
    }

    private static EnvironmentProbeSources Sources() => new()
    {
        Doctor = () => new() { Checks = [new() { Id = "ENV-NETFX", Status = CheckStatus.Pass }, new() { Id = "TIA-GROUP", Status = CheckStatus.Fail }], Installations = [new() { Version = "21" }] },
        ListedGroupMember = () => false, FileExists = _ => true, Directories = directory => [Path.Combine(directory, "10.0.12")],
        Writable = _ => true, PortOwners = _ => [], UrlReserved = _ => true, FirewallAllowed = _ => true,
    };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(FindRoot(), "bin-build", "P6-47", "fixtures", Guid.NewGuid().ToString("N"));
        public EnvironmentCheckContext Context { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Context = new() { BundleRoot = Root, DataRoot = Path.Combine(Root, "user-data"), UserFallback = true,
                ConfigDirectory = Path.Combine(Root, "config"), LogsDirectory = Path.Combine(Root, "logs"), HttpPrefix = "http://127.0.0.1:8765/" };
        }
        public void Dispose() => Directory.Delete(Root, true);
        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Version.props"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("worktree");
        }
    }
}

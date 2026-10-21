extern alias foundationhost;
extern alias enginehost;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Versioning;
using TiaMcpServer.Siemens;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;
using EnvironmentDoctor = enginehost::TiaMcpServer.Runtime.EnvironmentDoctor;
using HostCliCommands = foundationhost::TiaMcp.FoundationHost.HostCliCommands;
using HostCliOptions = foundationhost::TiaMcp.FoundationHost.HostCliOptions;
using HostDoctorGroupOutcome = foundationhost::TiaMcp.FoundationHost.HostDoctorGroupOutcome;
using IHostDoctorProbe = foundationhost::TiaMcp.FoundationHost.IHostDoctorProbe;

namespace TiaMcp.FoundationHost.Tests;

// `doctor --json` and the Workbench's environment fix: the Workbench's argument list, the JSON object it reads, and the
// group repair outcome that replaces the readiness exit code as the fix result.
public sealed class HostDoctorFixTests : IDisposable
{
    private readonly string scratch = Path.Combine(Root(), "bin-build", "doctor-fix-" + Guid.NewGuid().ToString("N"));
    private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));
    private static string HostDirectory => Path.Combine(Root(), "src/FoundationHost/bin/Release/net10.0");
    private static string Fixture => Path.Combine(Root(), "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/bin/Release/net48/TiaMcp.PlcWorker.FaultFixture.exe");
    private static readonly string[] JsonKeys = { "releaseKey", "fix", "ready", "logs", "diagnostics", "checks", "group", "clients", "report" };
    public static IEnumerable<object[]> Releases() => TiaVersionCatalog.Runnable.Select(release => new object[] { release.Key });

    [Theory, MemberData(nameof(Releases))]
    public void Workbench_fix_arguments_parse_with_the_host_options_of_every_release(string release)
    {
        // The Workbench starts the selected release's packaged host, next to that release's marker.
        Put("manifest/package-manifest.json", "fixture");
        string directory = Path.GetDirectoryName(BundleLayout.WorkbenchEnginePath(scratch, release, scratch))!;
        Put(Path.GetRelativePath(scratch, Path.Combine(directory, "release-key.txt")), release);
        var arguments = DoctorFixCommand.Arguments(release).ToArray();
        Assert.Equal(new[] { "doctor", "--fix", "--json", "--release-key", release, "--logging", "0" }, arguments);
        var parsed = HostCliOptions.Parse(arguments, directory);
        Assert.Equal(release, parsed.Host.ReleaseKey);
        Assert.Equal(new[] { "doctor", "--fix", "--json", "--logging", "0" }, parsed.Arguments);
        Assert.True(parsed.Host.NativeEnabled);
    }

    public static TheoryData<string, string?, string?, int, DoctorFixState> Outcomes() => new()
    {
        // PlcWorker repair: success means effective membership; every other outcome fails with its P8-76 result code.
        { "Ready", "Ready", null, 0, DoctorFixState.Ready },
        { "PendingLogon", "PendingLogon", "OPENNESS_GROUP_RELOGON_REQUIRED", 2, DoctorFixState.NeedsLogon },
        { "AddedPendingLogon", "AddedPendingLogon", "OPENNESS_GROUP_ADDED_RELOGON_REQUIRED", 2, DoctorFixState.NeedsLogon },
        { "ElevationDeclined", "ElevationDeclined", "OPENNESS_GROUP_ELEVATION_DECLINED", 2, DoctorFixState.Failed },
        { "MissingGroup", "MissingGroup", "OPENNESS_GROUP_MISSING", 2, DoctorFixState.Failed },
        { "AddFailed", "AddFailed", "OPENNESS_GROUP_ADD_FAILED", 2, DoctorFixState.Failed },
        // A failure without a result code, and the engine helper's bool.
        { "worker-refusal", "Unknown", null, 2, DoctorFixState.Failed },
        { "engine-true", "Unknown", null, 0, DoctorFixState.Ready },
        { "engine-false", "Unknown", null, 2, DoctorFixState.Failed },
    };

    [Theory, MemberData(nameof(Outcomes))]
    public void Json_reports_each_fix_outcome_with_the_text_report_and_the_same_exit_code(string scenario, string? outcome, string? code, int exit, DoctorFixState state)
    {
        var probe = Probe(scenario);
        var text = Doctor(probe, "en-US", "doctor", "--fix");
        var json = Doctor(probe, "en-US", "doctor", "--fix", "--json");
        Assert.Equal(exit, text.Exit);
        Assert.Equal(exit, json.Exit);
        Assert.Equal(text.Errors, json.Errors);
        var report = Report(json.Output);
        Assert.Equal(JsonKeys, report.Select(property => property.Key));
        Assert.Equal("19", (string?)report["releaseKey"]);
        Assert.True((bool)report["fix"]!);
        Assert.Equal(exit == 0, (bool)report["ready"]!);
        Assert.Equal(new[] { "release-support", "tia-install", "openness-group" }, report["checks"]!.AsArray().Select(row => (string?)row!["id"]));
        Assert.All(report["checks"]!.AsArray(), row => Assert.Equal(new[] { "id", "ok", "gating", "name", "detail", "fix" }, row!.AsObject().Select(p => p.Key)));
        var group = report["group"]!.AsObject();
        Assert.Equal(new[] { "ok", "outcome", "code", "message" }, group.Select(property => property.Key));
        Assert.Equal(outcome, (string?)group["outcome"]);
        Assert.Equal(code, (string?)group["code"]);
        Assert.Equal(scenario is "Ready" or "engine-true", (bool)group["ok"]!);
        string? message = (string?)group["message"];
        if (code != null) Assert.StartsWith(code + ": ", message);
        if (scenario == "worker-refusal") Assert.Equal("HostObserve only accepts read-only observations.", message);
        if (scenario is "Ready" or "engine-true" or "engine-false") Assert.Null(message);
        else Assert.Contains("  [FAIL] Openness user group: check failed: " + message, text.Output);
        // Without --json the same report is the output; with --json the object carries it line for line.
        Assert.Equal(Lines(text.Output), report["report"]!.AsArray().Select(line => (string)line!));
        var read = DoctorFixCommand.Parse(json.Output)!;
        Assert.Equal(state, read.State);
        Assert.Equal(outcome, read.Outcome);
        Assert.Equal(code, read.Code);
        Assert.Equal(Lines(text.Output), read.Lines);
    }

    [Fact]
    public void Json_without_fix_reports_membership_without_an_outcome_and_Chinese_reports_keep_the_codes()
    {
        var probe = Probe("AddedPendingLogon");
        var check = Doctor(probe, "en-US", "doctor", "--json");
        var group = Report(check.Output)["group"]!.AsObject();
        Assert.False((bool)Report(check.Output)["fix"]!);
        Assert.Null((string?)group["outcome"]);
        Assert.Null((string?)group["code"]);
        Assert.Null(DoctorFixCommand.Parse(check.Output)!.Outcome);
        Assert.Equal(DoctorFixState.Failed, DoctorFixCommand.Parse(check.Output)!.State);
        var zh = Doctor(probe, "zh-CN", "doctor", "--fix", "--json");
        var zhText = Doctor(probe, "zh-CN", "doctor", "--fix");
        Assert.Equal(2, zh.Exit);
        Assert.Equal("AddedPendingLogon", (string?)Report(zh.Output)["group"]!["outcome"]);
        Assert.Equal(Lines(zhText.Output), Report(zh.Output)["report"]!.AsArray().Select(line => (string)line!));
        Assert.Contains("OPENNESS_GROUP_ADDED_RELOGON_REQUIRED", zhText.Output);
        Assert.DoesNotContain("Openness user group", zhText.Output);
        Assert.Equal(DoctorFixState.NeedsLogon, DoctorFixCommand.Parse(zh.Output)!.State);
    }

    [Fact]
    public void Group_outcome_codes_are_the_PlcWorker_result_codes()
    {
        foreach (var outcome in Enum.GetValues<WindowsOpennessGroup.Outcome>().Where(o => o != WindowsOpennessGroup.Outcome.Ready))
        {
            string message = new WindowsOpennessGroup.Result(outcome, false, outcome == WindowsOpennessGroup.Outcome.MissingGroup ? null : false, 5).Message;
            var (name, code) = HostDoctorGroupOutcome.Classify(true, false, message, true);
            Assert.Equal(outcome.ToString(), name);
            Assert.StartsWith(code + ": ", message);
            Assert.Equal<(string?, string?)>(("Unknown", null), HostDoctorGroupOutcome.Classify(true, false, "worker: " + message.Replace(":", "", StringComparison.Ordinal), true));
            Assert.Equal<(string?, string?)>((outcome.ToString(), code), HostDoctorGroupOutcome.Classify(true, false, "worker: " + message, true));
        }
        Assert.Equal(5, HostDoctorGroupOutcome.Codes.Count);
        Assert.Equal<(string?, string?)>((null, null), HostDoctorGroupOutcome.Classify(false, false, "OPENNESS_GROUP_MISSING: x", true));
        Assert.Equal<(string?, string?)>(("Ready", null), HostDoctorGroupOutcome.Classify(true, true, null, true));
        Assert.Equal<(string?, string?)>(("Unknown", null), HostDoctorGroupOutcome.Classify(true, true, null, false));
    }

    [Fact]
    public void Doctor_output_that_is_not_the_report_object_is_not_a_fix_result()
    {
        foreach (string output in new[] { "", "tia doctor — environment check (fix mode)\r\n", "null", "[]", "{}", "{\"ready\":true}",
            "{\"ready\":\"yes\",\"group\":{\"ok\":true}}", "{\"ready\":true,\"group\":{\"ok\":1}}" })
            Assert.Null(DoctorFixCommand.Parse(output));
        var minimal = DoctorFixCommand.Parse("{\"ready\":false,\"group\":{\"ok\":false,\"outcome\":\"MissingGroup\"}}")!;
        Assert.Equal(DoctorFixState.Failed, minimal.State);
        Assert.Empty(minimal.Lines);
    }

    // The five failing outcomes on V19; the success reply is Plc_worker_success_reply_is_ready_on_old_and_plc_worker_releases.
    public static TheoryData<string, string, DoctorFixState> WorkerOutcomes() => new()
    {
        { "PendingLogon", "OPENNESS_GROUP_RELOGON_REQUIRED", DoctorFixState.NeedsLogon },
        { "AddedPendingLogon", "OPENNESS_GROUP_ADDED_RELOGON_REQUIRED", DoctorFixState.NeedsLogon },
        { "ElevationDeclined", "OPENNESS_GROUP_ELEVATION_DECLINED", DoctorFixState.Failed },
        { "MissingGroup", "OPENNESS_GROUP_MISSING", DoctorFixState.Failed },
        { "AddFailed", "OPENNESS_GROUP_ADD_FAILED", DoctorFixState.Failed },
    };

    // The host process, a real WorkerClient and the PlcWorker fixture, whose repair runs WindowsOpennessGroup on a fake platform.
    // The fake API file is not an assembly, so the doctor is never ready here and always exits 2. The host prints its own
    // language, so the text checks use only the result code and the report lines.
    [Theory, MemberData(nameof(WorkerOutcomes))]
    public void Plc_worker_fix_runs_the_worker_repair_and_reports_its_outcome(string outcome, string code, DoctorFixState state)
    {
        var environment = new Dictionary<string, string> { ["TIA_MCP_PLC_WORKER_GROUP_FIX"] = outcome };
        string[] args = { "doctor", "--fix", "--json", "--release-key", "19", "--worker-exe", PlcFixture(), "--public-api", FakeApi("19") };
        var result = Run(environment, args);
        Assert.Equal(2, result.Exit);
        var report = Report(result.OutText);
        Assert.Equal(JsonKeys, report.Select(property => property.Key));
        Assert.False((bool)report["ready"]!);
        var group = report["group"]!.AsObject();
        Assert.Equal(outcome, (string?)group["outcome"]);
        Assert.Equal(code, (string?)group["code"]);
        Assert.False((bool)group["ok"]!);
        string message = (string)group["message"]!;
        Assert.StartsWith(code + ": ", message);
        Assert.Contains("Token membership=False", message);
        var row = report["checks"]!.AsArray().Last()!;
        Assert.Equal("openness-group", (string?)row["id"]);
        Assert.False((bool)row["ok"]!);
        Assert.EndsWith(message, (string?)row["detail"]);
        Assert.Equal(state, DoctorFixCommand.Parse(result.OutText)!.State);
        var text = Run(environment, args.Where(arg => arg != "--json").ToArray());
        Assert.Equal(2, text.Exit);
        Assert.Contains(message + "\r\n", text.OutText);
        Assert.Equal(Lines(text.OutText), report["report"]!.AsArray().Select(line => (string)line!));
    }

    // The worker's success reply (the bool true) passes the host's reply check, so an effective membership is Ready, on an old
    // release and on V20/V21 with --plc-worker. A string instead of the bool is still a malformed reply that stops the session.
    [Theory]
    [InlineData("14sp1", false)]
    [InlineData("20", true)]
    [InlineData("21", true)]
    public void Plc_worker_success_reply_is_ready_on_old_and_plc_worker_releases(string release, bool plcWorker)
    {
        string[] args = new[] { "doctor", "--fix", "--json", "--release-key", release, "--worker-exe", PlcFixture(release), "--public-api", FakeApi(release) }
            .Concat(plcWorker ? new[] { "--plc-worker" } : Array.Empty<string>()).ToArray();
        var ready = Run(new Dictionary<string, string> { ["TIA_MCP_PLC_WORKER_GROUP_FIX"] = "Ready" }, args);
        Assert.Equal(2, ready.Exit);
        var report = Report(ready.OutText);
        Assert.Equal(release, (string?)report["releaseKey"]);
        var group = report["group"]!.AsObject();
        Assert.True((bool)group["ok"]!);
        Assert.Equal("Ready", (string?)group["outcome"]);
        Assert.Null((string?)group["code"]);
        Assert.Null((string?)group["message"]);
        Assert.True((bool)report["checks"]!.AsArray().Single(row => (string?)row!["id"] == "openness-group")!["ok"]!);
        Assert.Equal(DoctorFixState.Ready, DoctorFixCommand.Parse(ready.OutText)!.State);
        var malformed = Run(new Dictionary<string, string> { ["TIA_MCP_PLC_WORKER_GROUP_FIX"] = "malformed" }, args);
        Assert.Equal(2, malformed.Exit);
        group = Report(malformed.OutText)["group"]!.AsObject();
        Assert.False((bool)group["ok"]!);
        Assert.Equal("Unknown", (string?)group["outcome"]);
        Assert.Equal("Malformed hardware worker reply; session stopped: project-lifecycle.EnsureOpennessUserGroup", (string?)group["message"]);
        Assert.Equal(DoctorFixState.Failed, DoctorFixCommand.Parse(malformed.OutText)!.State);
    }

    [Fact]
    public void Json_needs_no_worker_without_fix_and_rejects_bad_syntax()
    {
        var result = Run(new Dictionary<string, string>(), "doctor", "--json", "--offline", "--release-key", "19", "--worker-exe", Path.Combine(scratch, "missing-worker.exe"));
        Assert.Equal(2, result.Exit);
        var report = Report(result.OutText);
        Assert.False((bool)report["fix"]!);
        Assert.Null((string?)report["group"]!["outcome"]);
        foreach (var args in new[] { new[] { "doctor", "--json", "--json" }, new[] { "config", "--print", "--json" }, new[] { "version", "--json" } })
        {
            var rejected = Run(new Dictionary<string, string>(), args);
            Assert.Equal(64, rejected.Exit);
            Assert.Empty(rejected.Stdout);
            Assert.StartsWith("ERROR: ", rejected.ErrorText);
        }
        var help = Run(new Dictionary<string, string>(), "help");
        Assert.Contains("tia doctor [--fix] [--json]", help.OutText);
        Assert.Contains("doctor --json prints one JSON object instead of the report", help.OutText);
    }

    private static JsonObject Report(string output)
    {
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return JsonNode.Parse(lines[0])!.AsObject();
    }

    private static string[] Lines(string text)
    {
        var lines = text.Split("\r\n");
        Assert.Equal("", lines[^1]);
        return lines[..^1];
    }

    private static (int Exit, string Output, string Errors) Doctor(IHostDoctorProbe probe, string culture, params string[] args)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            using var output = new StringWriter { NewLine = "\r\n" };
            using var errors = new StringWriter { NewLine = "\r\n" };
            int exit = HostCliCommands.Doctor(args, probe, output, errors);
            return (exit, output.ToString(), errors.ToString());
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static FakeProbe Probe(string scenario) => scenario switch
    {
        "worker-refusal" => new FakeProbe(_ => throw new ArgumentException("HostObserve only accepts read-only observations."), true),
        "engine-true" => new FakeProbe(_ => true, false),
        "engine-false" => new FakeProbe(_ => false, false),
        _ => new FakeProbe(fix => fix
            ? WindowsOpennessGroup.RequireEffectiveMembership(WindowsOpennessGroup.EnsureMembership(new FakePlatform(Enum.Parse<WindowsOpennessGroup.Outcome>(scenario))))
            : false, true),
    };

    private sealed class FakeProbe(Func<bool, bool> group, bool reportsOutcome) : IHostDoctorProbe
    {
        public string ReleaseKey => "19";
        public int? DetectedMajor => 19;
        public bool FixReportsOutcome => reportsOutcome;
        public string LogDirectory() => @"C:\fixture\data\logs\19";
        public string DiagnosticsDirectory() => @"C:\fixture\data\diagnostics";
        public List<EnvironmentDoctor.Check> Checks() => new()
        {
            new() { Id = "tia-install", Ok = true, NameEn = "TIA Portal installation", NameZh = "TIA Portal installation", DetailEn = "fixture", DetailZh = "fixture" },
        };
        public bool Group(bool fix) => group(fix);
    }

    // Produces the named EnsureMembership outcome, as the PlcWorker fixture does for the host process tests.
    private sealed class FakePlatform(WindowsOpennessGroup.Outcome target) : WindowsOpennessGroup.IPlatform
    {
        private bool added;
        public WindowsOpennessGroup.CurrentUser ReadCurrentUser() => new("S-1-5-21-1-2-3-1001", @"FIXTURE\user");
        public bool ReadTokenMembership() => target == WindowsOpennessGroup.Outcome.Ready;
        public bool? ReadDirectLocalMembership(string sid) => target switch
        {
            WindowsOpennessGroup.Outcome.MissingGroup => null,
            WindowsOpennessGroup.Outcome.PendingLogon => true,
            WindowsOpennessGroup.Outcome.AddedPendingLogon => added,
            _ => false,
        };
        public int AddCurrentUser(string accountName)
        {
            if (target == WindowsOpennessGroup.Outcome.ElevationDeclined) throw new Win32Exception(1223);
            added = target == WindowsOpennessGroup.Outcome.AddedPendingLogon;
            return target == WindowsOpennessGroup.Outcome.AddFailed ? 2 : 0;
        }
    }

    private string PlcFixture(string release = "19")
    {
        Assert.True(File.Exists(Fixture), Fixture);
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(Fixture)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
        return Fixture;
    }

    private string FakeApi(string release)
    {
        string api = Path.Combine(scratch, "api");
        Put(Path.Combine("api", TiaVersionCatalog.Get(release).ApiAssembly), "not an assembly");
        return api;
    }

    private void Put(string relative, string value)
    {
        string file = Path.Combine(scratch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, value);
    }

    private sealed record Result(int Exit, byte[] Stdout, byte[] Stderr)
    {
        internal string OutText => Encoding.UTF8.GetString(Stdout);
        internal string ErrorText => Encoding.UTF8.GetString(Stderr);
    }

    private Result Run(IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo(Path.Combine(HostDirectory, "TiaMcp.FoundationHost.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root() };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = scratch;
        start.Environment["LOCALAPPDATA"] = Path.Combine(scratch, "local");
        start.Environment.Remove("TIA_MCP_BUNDLE_ROOT");
        start.Environment.Remove("TiaPortalLocation");
        start.Environment.Remove("TIA_MCP_PLC_WORKER_GROUP_FIX");
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var readError = process.StandardError.BaseStream.CopyToAsync(stderr);
        Assert.True(process.WaitForExit(60000), "CLI did not exit within 60 seconds.");
        Task.WaitAll(readOut, readError);
        return new Result(process.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}

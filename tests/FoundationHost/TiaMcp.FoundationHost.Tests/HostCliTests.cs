extern alias foundationhost;
extern alias enginehost;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using HostCliOptions = foundationhost::TiaMcp.FoundationHost.HostCliOptions;
using HostCliCommands = foundationhost::TiaMcp.FoundationHost.HostCliCommands;
using HostDoctorProbe = foundationhost::TiaMcp.FoundationHost.HostDoctorProbe;
using EnvironmentDoctor = enginehost::TiaMcpServer.Runtime.EnvironmentDoctor;
using Installer = foundationhost::TiaMcpServer.Cli.McpConfigInstaller;
using TiaMcp.Versioning;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class HostCliTests : IDisposable
{
    private readonly string scratch = Path.Combine(Root(), "bin-build", "cli-test-" + Guid.NewGuid().ToString("N"));
    private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));
    private static string HostDirectory => Path.Combine(Root(), "src/FoundationHost/bin/Release/net10.0");
    public static IEnumerable<object[]> Releases() => TiaVersionCatalog.All.Select(release => new object[] { release.Key });

    [Theory, MemberData(nameof(Releases))]
    public void Kept_verbs_run_without_a_worker_on_every_release(string release)
    {
        foreach (var args in new[] {
            new[] { "help" }, new[] { "schema" }, new[] { "version" }, new[] { "config", "--print" },
            new[] { "audit", "verify", "--path", Path.Combine(scratch, "empty-chain") },
            new[] { "install-plc-tools", "--environment-path", "relative" }, new[] { "doctor", "--offline", "--tia-portal-location", Path.Combine(scratch, "V22") } })
        {
            var result = Run(args.Concat(new[] { "--release-key", release, "--worker-exe", Path.Combine(scratch, "missing-worker.exe") }).ToArray());
            Assert.Equal(args[0] is "install-plc-tools" or "doctor" ? 2 : 0, result.Exit);
            if (args[0] == "config") Assert.Contains("\"" + release + "\"", result.OutText);
            if (args[0] == "doctor")
            {
                Assert.Contains(".NET Framework 4.8", result.OutText);
                Assert.Contains("MOTW", result.OutText);
                Assert.Contains("Siemens TIA Openness", result.OutText);
                Assert.Contains("Logs: ", result.OutText);
                Assert.Contains("Diagnostics: ", result.OutText);
            }
        }
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Stable_stdout_matches_frozen_engine_bytes(string release)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/HostCli/engine-cli.json")))!;
        int compared = 0;
        foreach (var sample in fixture["samples"]!.AsArray().Where(s => (string?)s!["release"] == release))
        {
            var args = sample!["args"]!.AsArray().Select(a => Relocate((string)a!, fixture)).ToArray();
            string verb = args[0];
            // These two outputs intentionally change, and the old config without an API
            // crashes before its handler. Dedicated assertions cover the new behavior.
            if (verb is "help" or "doctor" || (long)sample["exit"]! > int.MaxValue) continue;
            var actual = Run(args);
            Assert.Equal((int)sample["exit"]!, actual.Exit);
            var expected = Encoding.UTF8.GetBytes(Accepted(verb, Relocate(Encoding.UTF8.GetString(Convert.FromBase64String((string)sample["stdout"]!)), fixture)));
            Assert.Equal(expected, actual.Stdout);
            if (verb == "install-plc-tools") Assert.Equal("IO_FAILED: --environment-path must be absolute: relative\r\n", actual.ErrorText);
            else Assert.Empty(actual.Stderr);
            compared++;
        }
        Assert.Equal(6, compared); // schema, version, audit, installer and two config profiles
    }

    // Accepted output differences, each one exact frozen line -> new line(s), applied to the frozen bytes before the full
    // byte comparison (decided 2026-10-19; listed in Fixtures/HostCli/README.md). The schema spec lines follow
    // device creation: plcMlfb is required for gen, no device is chosen from plcFamily/hmiFamily, and plcVersion, hmiMlfb and
    // hmiVersion are documented. The schema text separates its lines with LF.
    private static readonly (string Verb, string Old, string New)[] AcceptedLines =
    {
        ("schema", "  plcFamily       string  default S7-1500.",
            "  plcFamily       string  label only; no device is chosen from it."),
        ("schema", "  plcMlfb         string  exact order number (optional).",
            "  plcMlfb         string  gen: required. Exact order number or full TypeIdentifier.\n"
            + "  plcVersion      string  exact catalog version; empty with a full TypeIdentifier."),
        ("schema", "  hmiFamily       string  default WinCCUnifiedPC.",
            "  hmiFamily       string  label only; no device is chosen from it.\n"
            + "  hmiMlfb         string  gen: required with hmiName; same rules as plcMlfb.\n"
            + "  hmiVersion      string  same rules as plcVersion."),
    };

    private static string Accepted(string verb, string frozen)
    {
        foreach (var (_, old, replacement) in AcceptedLines.Where(line => line.Verb == verb))
        {
            var lines = frozen.Split('\n');
            Assert.Equal(1, lines.Count(line => line.TrimEnd('\r') == old));
            frozen = string.Join("\n", lines.Select(line => line.TrimEnd('\r') == old ? replacement + line[old.Length..] : line));
        }
        return frozen;
    }

    private string Relocate(string value, JsonNode fixture) => value
        .Replace(((string)fixture["data"]!).Replace("\\", "\\\\"), scratch.Replace("\\", "\\\\"), StringComparison.Ordinal)
        .Replace((string)fixture["data"]!, scratch, StringComparison.Ordinal)
        .Replace((string)fixture["root"]!, Root(), StringComparison.Ordinal)
        .Replace(((string)fixture["root"]!).Replace("\\", "\\\\"), Root().Replace("\\", "\\\\"), StringComparison.Ordinal);

    [Theory]
    [InlineData("gen")]
    [InlineData("patch")]
    [InlineData("describe")]
    [InlineData("compile")]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("prewarm")]
    public void Host_rejects_retired_verbs_with_guidance(string verb)
    {
        var result = Run(verb);
        Assert.Equal(64, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.Contains("MCP tools and the project generation framework", result.ErrorText);
        var help = Run("help");
        Assert.Equal(0, help.Exit);
        Assert.Contains(verb, help.OutText);
        Assert.Contains("GetProjectTree", help.OutText);
        Assert.Contains("CompilePlcDiagnostics", help.OutText);
        Assert.Contains("ApplyGenerationPlan", help.OutText);
    }

    [Theory]
    [InlineData("doctor", "--typo")]
    [InlineData("doctor", "--release-key", "14")]
    [InlineData("doctor", "--release-key", "15")]
    [InlineData("doctor", "--release-key", "22")]
    [InlineData("doctor", "--release-key", "20", "--tia-version", "21")]
    [InlineData("doctor", "--release-key")]
    [InlineData("doctor", "--fix", "--fix")]
    [InlineData("audit")]
    [InlineData("audit", "verify", "--path", "relative")]
    [InlineData("config", "--print", "--full", "--lite")]
    [InlineData("schema", "extra")]
    [InlineData("version", "--profile", "bad")]
    public void Syntax_is_rejected_before_a_worker_is_needed(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(64, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.StartsWith("ERROR: ", result.ErrorText);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_aliases_include_commands_and_existing_worker_timeout_help(string alias)
    {
        var result = Run(alias);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Stderr);
        Assert.Contains("audit verify", result.OutText);
        Assert.Contains("install-plc-tools", result.OutText);
        Assert.Contains("14sp1, 15.1, 16, 17, 18, 19, 20, 21", result.OutText);
        Assert.Contains("firstAttach per generation 300 s", result.OutText);
        Assert.Contains("2 rejected / doctor not ready / no successful config writes", result.OutText);
        Assert.Contains("install-plc-tools preserves pip's nonzero exit status.", result.OutText);
    }

    [Theory]
    [InlineData("--release-key")]
    [InlineData("--tia-version")]
    [InlineData("--tia-major-version")]
    [InlineData("-tia-major-version")]
    public void Release_aliases_keep_the_exact_old_release_key(string selector)
    {
        var result = Run("config", "--print", selector, "14sp1");
        Assert.Equal(0, result.Exit);
        Assert.Contains("\"14sp1\"", result.OutText);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void Legacy_profile_case_and_single_dash_global_options_are_preserved()
    {
        var result = Run("config", "--print", "-profile", "FULL", "-logging", "0", "-with-ui", "-tia-major-version", "21");
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Stderr);
        Assert.Contains("\"21\"", result.OutText);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Managed_doctor_keeps_every_check_and_gates_startup_for_all_releases(string release)
    {
        var selected = TiaVersionCatalog.Get(release);
        var checks = EnvironmentDoctor.RunHost(release, selected.MajorVersion, (true, "install", "api.dll", null), new[] { scratch },
            new JsonObject { ["ready"] = false, ["cause"] = "startup failed", ["recommendedFix"] = "repair", ["recommendedFixZh"] = "修复" });
        Assert.Equal(new[] { "tia-install", "openness-dll", "engine-version", "dotnet48", "motw", "openness-startup" }, checks.Select(c => c.Id));
        Assert.All(checks.Take(3), check => Assert.True(check.Ok));
        Assert.All(checks, check => Assert.True(check.Gating));
        Assert.False(checks.Last().Ok);
        Assert.Equal("startup failed", checks.Last().DetailEn);
        Assert.Equal("修复", checks.Last().FixZh);
        var failed = EnvironmentDoctor.RunHost(release, null, (false, null, null, "missing API"), new[] { scratch }, null);
        Assert.False(failed[0].Ok);
        Assert.False(failed[1].Ok);
        Assert.Contains("missing API", failed[1].DetailEn);
        Assert.Contains("V14 SP1", failed[0].FixEn);
        Assert.Contains("V15.1", failed[0].FixEn);
    }

    [Fact]
    public void Doctor_checks_actual_alternate_streams_and_caps_the_report_at_five_files()
    {
        Directory.CreateDirectory(scratch);
        for (int index = 0; index < 6; index++)
        {
            string path = Path.Combine(scratch, index + ".dll");
            File.WriteAllText(path, "fixture");
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n");
        }
        var check = EnvironmentDoctor.RunHost("21", 21, (true, null, "api.dll", null), new[] { scratch }, null).Single(c => c.Id == "motw");
        Assert.False(check.Ok);
        Assert.EndsWith(", ...", check.DetailEn);
        Assert.Contains("Unblock-File", check.FixEn);
        Assert.Contains(scratch, check.FixEn);
    }

    [Fact]
    public void Api_probe_rejects_wrong_assembly_identity_without_loading_it()
    {
        Directory.CreateDirectory(scratch);
        File.Copy(typeof(HostCliOptions).Assembly.Location, Path.Combine(scratch, "Siemens.Engineering.dll"));
        var probe = HostDoctorProbe.ProbeAssemblies(TiaVersionCatalog.Get("20"), scratch);
        Assert.False(probe.Ok);
        Assert.Contains("version mismatch", probe.Problem);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_config_write_returns_two_on_every_release(string release)
    {
        Directory.CreateDirectory(scratch);
        string blocked = Path.Combine(scratch, "blocked");
        File.WriteAllText(blocked, "fixture");
        var hosts = new[] { new Installer.Host("Fixture", Path.Combine(blocked, "mcp.json"), Installer.HostStyle.McpServers) };
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        int exit = HostCliCommands.WriteConfig(new[] { "config", "--host", "fixture" }, release, "host.exe", hosts, output, diagnostics);
        Assert.Equal(2, exit);
        Assert.Contains("  [failed] Fixture: ", diagnostics.ToString());
        Assert.Contains("No host config written.", output.ToString());
        Assert.Equal("fixture", File.ReadAllText(blocked));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Config_preserves_precise_release_keys_merge_backup_and_full_profile(string release)
    {
        Directory.CreateDirectory(scratch);
        string config = Path.Combine(scratch, "mcp.json");
        const string original = "{\"other\":true,\"mcpServers\":{\"retained\":{\"command\":\"keep\"}}}";
        File.WriteAllText(config, original);
        Installer.Apply(config, "host.exe", release, Installer.HostStyle.McpServers, true);
        Assert.Equal(original, File.ReadAllText(config + ".bak"));
        var root = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.True((bool)root["other"]!);
        Assert.Equal("keep", (string?)root["mcpServers"]?["retained"]?["command"]);
        var entry = root["mcpServers"]!["tia-portal"]!;
        Assert.Equal("host.exe", (string?)entry["command"]);
        Assert.Equal(release, (string?)entry["args"]?[1]);
        Assert.Equal("full", (string?)entry["env"]?["TIA_MCP_PROFILE"]);
        Assert.Contains("\"" + release + "\"", Installer.Snippet("host.exe", release, Installer.HostStyle.CodexToml));
    }

    [Fact]
    public void Audit_corruption_returns_three_and_missing_bundle_returns_seventy()
    {
        string audit = Path.Combine(scratch, "audit");
        Directory.CreateDirectory(audit);
        File.WriteAllText(Path.Combine(audit, "audit-20261018.jsonl"), "{invalid\n");
        var result = Run("audit", "verify", "--path", audit);
        Assert.Equal(3, result.Exit);
        Assert.Contains("false", result.OutText);
        Assert.Empty(result.Stderr);
        result = Run("version", "--bundle-root", Path.Combine(scratch, "missing"));
        Assert.Equal(70, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.Contains("RESOURCE_UNAVAILABLE", result.ErrorText);
    }

    private sealed record Result(int Exit, byte[] Stdout, byte[] Stderr)
    {
        internal string OutText => Encoding.UTF8.GetString(Stdout);
        internal string ErrorText => Encoding.UTF8.GetString(Stderr);
    }
    private Result Run(params string[] args)
    {
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo(Path.Combine(HostDirectory, "TiaMcp.FoundationHost.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root() };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = scratch;
        start.Environment["LOCALAPPDATA"] = Path.Combine(scratch, "local");
        start.Environment.Remove("TIA_MCP_BUNDLE_ROOT");
        start.Environment.Remove("TiaPortalLocation");
        using var process = Process.Start(start)!;
        using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var readError = process.StandardError.BaseStream.CopyToAsync(stderr);
        Assert.True(process.WaitForExit(30000), "CLI did not exit within 30 seconds.");
        Task.WaitAll(readOut, readError);
        return new Result(process.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}

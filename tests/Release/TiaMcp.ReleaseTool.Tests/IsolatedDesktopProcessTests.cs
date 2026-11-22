using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class IsolatedDesktopProcessTests
{
    private static string Fixture => Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";
    private static string FixtureAssembly => typeof(IsolatedDesktopProcessTests).Assembly.Location;
    private sealed record Probe(string Desktop, int Pid, string[] Arguments, string? Marker);

    public static IEnumerable<object[]> SuiteNames() => new[] { "studio-gui" }
        .Concat(DerivedSuiteGates.Supersets.Where(pair => pair.Value == "studio-gui").Select(pair => pair.Key))
        .Select(name => new object[] { name, true }).Concat(new[] { "studio-core", "foundation-api", "updater", "studio-gui-copy" }
            .Select(name => new object[] { name, false }));

    [Theory]
    [MemberData(nameof(SuiteNames))]
    public void Ordinary_and_sharded_launch_sites_isolate_only_GUI_suites(string name, bool isolated)
    {
        using var tree = new StaticTree();
        var suite = new JsonObject { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        var original = new Dictionary<string, string?> { ["fixture"] = "retained" };
        string? previous = null;
        int launches = 0;
        CommandResult Run(string executable, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            Assert.Equal("test", args[0]);
            Assert.Contains("--blame-hang", args);
            Assert.Contains(SuiteHang.Timeout, args);
            Assert.Equal(isolated, environment!.ContainsKey(GuiTestDesktop.EnvironmentKey));
            if (isolated)
            {
                string desktop = environment[GuiTestDesktop.EnvironmentKey]!;
                Assert.StartsWith("TiaMcp.Gui.", desktop);
                Assert.NotEqual(previous, desktop);
                previous = desktop;
            }
            var directory = args[Array.IndexOf(args, "--results-directory") + 1];
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, name + ".trx"), ["Passed"]);
            launches++;
            return new(0, "fixture output", "");
        }
        Assert.Equal(0, DotnetSuites.RunSuite(tree.Root, name, suite, Path.Combine(tree.Root, "ordinary"), environment: original, process: Run).ExitCode);
        Assert.False(original.ContainsKey(GuiTestDesktop.EnvironmentKey));
        Assert.Equal("retained", original["fixture"]);
        Assert.Equal(0, FastSuites.RunShard(tree.Root, name, suite, Path.Combine(tree.Root, "shard"), "dotnet", null,
            SuiteBuild.Suite(suite), 0, 1, process: Run).ExitCode);
        Assert.Equal(2, launches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void Child_and_grandchild_inherit_the_desktop_and_cleanup_includes_orphans(int exitCode)
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var report = Path.Combine(tree.Root, "报告 with spaces.json");
        string[] arguments = ["", "space value", "a\"b", "trailing\\", "你好\\\"quoted"];
        var environment = GuiTestDesktop.EnvironmentFor("studio-gui", null)!;
        string name = environment[GuiTestDesktop.EnvironmentKey]!;
        var log = Path.Combine(tree.Root, "output.log");
        var result = ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-parent", report, "orphan", exitCode.ToString(), .. arguments], tree.Root,
            environment, log, standardInput: "input: 你好", timeoutMilliseconds: TestDeadlines.HangMilliseconds,
            standardInputEncoding: Encoding.UTF8, standardOutputEncoding: Encoding.UTF8, standardErrorEncoding: Encoding.UTF8);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("DESKTOP WinSta0\\" + name, result.StandardOutput);
        Assert.Contains("input: 你好", result.StandardOutput);
        Assert.Contains("stderr: 你好", result.StandardError);
        Assert.Contains("input: 你好", File.ReadAllText(log));
        foreach (var path in new[] { report, report + ".grandchild" })
        {
            var probe = JsonSerializer.Deserialize<Probe>(File.ReadAllText(path))!;
            Assert.Equal(name, probe.Desktop);
            Assert.NotEqual(CurrentDesktop(), probe.Desktop);
            Assert.Equal(arguments, probe.Arguments);
            Assert.Null(probe.Marker);
            AssertExited(probe.Pid);
        }
        AssertDesktopClosed(name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_desktop_owned_tree_finishes_before_draining_inherited_pipes(bool useCapture)
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        string report = Path.Combine(tree.Root, "orphan.json");
        string[] args = [FixtureAssembly, "--desktop-parent", report, "orphan", "23"];
        string output;
        int exitCode;
        if (useCapture)
        {
            var start = new ProcessStartInfo(Fixture)
            {
                WorkingDirectory = tree.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            var result = ProcessDeadline.Capture(start, TestDeadlines.HangMilliseconds);
            (exitCode, output) = (result.ExitCode, result.StandardError);
        }
        else
        {
            var result = GateWatchdog.Run("merge-check", 1, () => ProcessRunner.Run(Fixture, args, tree.Root,
                timeoutMilliseconds: TestDeadlines.HangMilliseconds));
            (exitCode, output) = (result.ExitCode, result.StandardError);
        }
        Assert.Equal(23, exitCode);
        Assert.Contains("stderr:", output);
        foreach (string path in new[] { report, report + ".grandchild" })
            AssertExited(JsonSerializer.Deserialize<Probe>(File.ReadAllText(path))!.Pid);
    }

    [Fact]
    public void Explicit_stream_encodings_round_trip_UTF8_without_an_isolated_desktop()
    {
        using var tree = new StaticTree();
        var log = Path.Combine(tree.Root, "output.log");
        var result = ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-echo"], tree.Root,
            logPath: log, standardInput: "input: 你好", timeoutMilliseconds: TestDeadlines.HangMilliseconds,
            standardInputEncoding: Encoding.UTF8, standardOutputEncoding: Encoding.UTF8, standardErrorEncoding: Encoding.UTF8);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("input: 你好", result.StandardOutput);
        Assert.Contains("stderr: 你好", result.StandardError);
        Assert.Contains("input: 你好", File.ReadAllText(log));
        Assert.Contains("stderr: 你好", File.ReadAllText(log));
    }

    [Fact]
    public async Task A_running_parent_and_grandchild_are_joined_before_the_desktop_closes()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var report = Path.Combine(tree.Root, "waiting.json");
        string name = GuiTestDesktop.EnvironmentFor("studio-gui", null)![GuiTestDesktop.EnvironmentKey]!;
        var start = new ProcessStartInfo(Fixture) { WorkingDirectory = tree.Root };
        foreach (var arg in new[] { FixtureAssembly, "--desktop-parent", report, "wait", "0" }) start.ArgumentList.Add(arg);
        using (var child = IsolatedDesktopProcess.Start(start, name))
        {
            child.StandardInput.Close();
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TestDeadlines.Hang));
            Assert.False(child.WaitForExit(0));
            child.Finish();
            foreach (string path in new[] { report, report + ".grandchild" })
                AssertExited(JsonSerializer.Deserialize<Probe>(File.ReadAllText(path))!.Pid);
        }
        AssertDesktopClosed(name);
    }

    [Fact]
    public void Expired_process_budget_closes_the_desktop_and_retains_the_timeout_error()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var environment = GuiTestDesktop.EnvironmentFor("studio-gui", null)!;
        var error = Assert.Throws<ReleaseException>(() => ProcessRunner.Run(Fixture,
            [FixtureAssembly, "--desktop-parent", Path.Combine(tree.Root, "timeout.json"), "wait", "0"], tree.Root, environment, timeoutMilliseconds: 0));
        Assert.Contains("timed out after 0 ms", error.Message);
        AssertDesktopClosed(environment[GuiTestDesktop.EnvironmentKey]!);
    }

    [Fact]
    public void Creation_failure_fails_the_suite_without_starting_on_the_interactive_desktop()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var report = Path.Combine(tree.Root, "must-not-exist.json");
        var suite = new JsonObject { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        var result = DotnetSuites.RunSuite(tree.Root, "studio-gui", suite, tree.Root, process: (_, _, root, environment) =>
        {
            environment![GuiTestDesktop.EnvironmentKey] = "invalid\\desktop";
            return ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-parent", report, "orphan", "0"], root, environment);
        });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cannot create isolated GUI test desktop", result.StandardOutput);
        Assert.Contains("No interactive-desktop fallback", result.StandardOutput);
        Assert.False(File.Exists(report));
        Assert.Contains("Cannot create isolated GUI test desktop", File.ReadAllText(Path.Combine(tree.Root, "studio-gui.json")));
    }

    [Fact]
    public void Process_creation_failure_also_closes_the_created_desktop()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var environment = GuiTestDesktop.EnvironmentFor("studio-gui", null)!;
        var error = Assert.Throws<ReleaseException>(() => ProcessRunner.Run(Path.Combine(tree.Root, "missing.exe"), [], tree.Root, environment));
        Assert.Contains("Cannot start GUI tests on isolated desktop", error.Message);
        AssertDesktopClosed(environment[GuiTestDesktop.EnvironmentKey]!);
    }

    [Fact]
    public async Task Concurrent_launches_keep_their_desktops_and_inherited_handles_separate()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        var names = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            var environment = GuiTestDesktop.EnvironmentFor("studio-gui", null)!;
            string name = environment[GuiTestDesktop.EnvironmentKey]!;
            string report = Path.Combine(tree.Root, index + ".json");
            var result = ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-parent", report, "orphan", "0"], tree.Root,
                environment, timeoutMilliseconds: TestDeadlines.HangMilliseconds);
            Assert.Equal(0, result.ExitCode);
            foreach (string path in new[] { report, report + ".grandchild" })
            {
                var probe = JsonSerializer.Deserialize<Probe>(File.ReadAllText(path))!;
                Assert.Equal(name, probe.Desktop);
                AssertExited(probe.Pid);
            }
            AssertDesktopClosed(name);
            return name;
        })));
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_concurrent_managed_process_cannot_keep_the_GUI_output_pipe_open()
    {
        if (!OperatingSystem.IsWindows()) { AssertWindowsRequired(); return; }
        using var tree = new StaticTree();
        string scope = "Local\\TiaMcp.Desktop." + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, scope + ".release");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, scope + ".ready");
        using var start = new Barrier(2);
        var environment = GuiTestDesktop.EnvironmentFor("studio-gui", null)!;
        var managed = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TestDeadlines.Hang));
            return ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-hold", scope], tree.Root,
                timeoutMilliseconds: TestDeadlines.HangMilliseconds);
        });
        var native = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TestDeadlines.Hang));
            return ProcessRunner.Run(Fixture, [FixtureAssembly, "--desktop-parent", Path.Combine(tree.Root, "mixed.json"), "orphan", "0"],
                tree.Root, environment, timeoutMilliseconds: TestDeadlines.HangMilliseconds);
        });
        try
        {
            Assert.Equal(0, (await native.WaitAsync(TestDeadlines.Hang)).ExitCode);
            Assert.True(ready.WaitOne(TestDeadlines.Hang));
            Assert.False(managed.IsCompleted);
            AssertDesktopClosed(environment[GuiTestDesktop.EnvironmentKey]!);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(managed, native);
        }
        Assert.Equal(0, (await managed).ExitCode);
    }

    private static void AssertWindowsRequired() => Assert.Contains("require Windows",
        Assert.Throws<ReleaseException>(() => IsolatedDesktopProcess.Start(new ProcessStartInfo(Fixture), "fixture")).Message);

    private static void AssertExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, "Owned fixture process is still alive: " + pid);
        }
        catch (ArgumentException) { }
    }

    private static void AssertDesktopClosed(string name)
    {
        var handle = OpenDesktop(name, 0, false, 1 /* READOBJECTS */);
        try { Assert.Equal(IntPtr.Zero, handle); }
        finally { if (handle != IntPtr.Zero) CloseDesktop(handle); }
    }

    private static string CurrentDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * sizeof(char), out _))
            throw new InvalidOperationException("Cannot read fixture desktop: " + Marshal.GetLastWin32Error());
        return name.ToString();
    }

    internal static int RunFixture(string[] args)
    {
        if (args[0] == "--desktop-hold")
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("The event fixture requires Windows.");
            using var release = EventWaitHandle.OpenExisting(args[1] + ".release");
            using var ready = EventWaitHandle.OpenExisting(args[1] + ".ready");
            ready.Set();
            release.WaitOne();
            return 0;
        }
        if (args[0] == "--desktop-run")
        {
            var result = ProcessRunner.Run(args[1], args.Skip(2), Repository.FindRoot(AppContext.BaseDirectory),
                GuiTestDesktop.EnvironmentFor("studio-gui", null), forwardOutput: true);
            Console.WriteLine(result.StandardOutput.Split('\n')[0]);
            return result.ExitCode;
        }
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        if (args[0] == "--desktop-echo")
        {
            Console.WriteLine(Console.In.ReadToEnd());
            Console.Error.WriteLine("stderr: 你好");
            return 0;
        }
        string report = args[1], mode = args[2];
        string[] forwarded = args.Skip(4).ToArray();
        File.WriteAllText(report, JsonSerializer.Serialize(new Probe(CurrentDesktop(), Environment.ProcessId, forwarded,
            Environment.GetEnvironmentVariable(GuiTestDesktop.EnvironmentKey))));
        if (args[0] == "--desktop-grandchild")
        {
            Console.WriteLine("ready");
            if (mode != "normal") new ManualResetEvent(false).WaitOne();
            return 0;
        }
        var start = new ProcessStartInfo(Fixture) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { FixtureAssembly, "--desktop-grandchild", report + ".grandchild", mode, "0" }.Concat(forwarded)) start.ArgumentList.Add(arg);
        using var grandchild = Process.Start(start) ?? throw new InvalidOperationException("Could not start desktop grandchild");
        if (grandchild.StandardOutput.ReadLine() != "ready") throw new InvalidOperationException("Grandchild did not report its desktop");
        if (mode == "wait")
        {
            Console.WriteLine("ready");
            new ManualResetEvent(false).WaitOne();
        }
        Console.WriteLine(Console.In.ReadToEnd());
        Console.Error.WriteLine("stderr: 你好");
        if (mode == "normal") grandchild.WaitForExit();
        return int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();
}

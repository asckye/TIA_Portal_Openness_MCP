using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PrepareChecksTests
{
    [Theory]
    [InlineData("check")]
    [InlineData("plc")]
    public void FailureRunsLaterJobsAndNeverWritesCandidate(string failure)
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var events = new System.Collections.Concurrent.ConcurrentBag<string>();
        PrepareCheckJob Job(string name) => new(name, () =>
        {
            events.Add(name);
            if (name == failure) throw new ReleaseException("failed " + name);
            return new(name, 0, "passed", "");
        });
        var plan = new PrepareCheckPlan([Job("check"), Job("plc"), Job("later")]);
        var error = Assert.Throws<ReleaseException>(() => Prepare.Complete(context, package, Options(), Environment(tree.Root),
            () => throw new InvalidOperationException("clean-tree must not run after failure"),
            () => PrepareChecks.Execute(context, package, plan, runtimeCheck: () => events.Add("runtime-bytes")),
            _ => throw new InvalidOperationException("upload must not run")));
        Assert.Contains(failure, error.Message);
        foreach (var name in new[] { "check", "plc", "later", "runtime-bytes" }) Assert.Contains(name, events);
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, "candidate.json")));
    }

    [Fact]
    public void NoComposedCommandStartsASuite()
    {
        using var tree = new StaticTree();
        var calls = new List<(string Exe, string[] Args)>();
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), Path.Combine(tree.Root, "bundle"), Environment(tree.Root),
            (exe, arguments, _, _, _) =>
            {
                calls.Add((exe, arguments.ToArray()));
                return new(1, "", "injected process failure");
            });
        foreach (var job in plan.Jobs) Assert.Throws<ReleaseException>(() => job.Run());
        // The write guard checks both the denied write and the allowed read.
        Assert.Equal(plan.Jobs.Length + 1, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.NotEqual("test-suites", call.Exe);
            Assert.DoesNotContain("test-suites", call.Args);
            Assert.DoesNotContain("test", call.Args);
            Assert.DoesNotContain("build", call.Args);
        });
    }

    [Fact]
    public async Task CandidateRoundTripsAndUploadFailureKeepsIt()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var clean = false;
        var uploaded = false;
        var checks = new PrepareCheckResults(["product-smoke"]);
        Assert.Equal(1, Prepare.Complete(context, package, Options(), Environment(tree.Root), () => clean = true,
            () => checks, path => { uploaded = true; Assert.True(clean); Assert.True(File.Exists(path)); throw new IOException("offline fixture"); }));
        Assert.True(uploaded);
        var read = PreparedCandidate.Read(tree.Root, Path.Combine(context.OutputDirectory, "candidate.json"), context.Version);
        Assert.Equal(package.ZipPath, read.Zip);
        Assert.Equal(package.Sha256, read.Digest);
        Assert.Equal(package.Size, read.Size);
        Assert.Equal(package.FileCount, (int)read.Candidate["fileCount"]!);
        Assert.False(read.Candidate.ContainsKey("suites"));
        Assert.Equal("product-smoke", (string?)read.Candidate["checks"]![0]);
        Assert.True(DateTimeOffset.TryParse((string?)read.Candidate["finishedUtc"], out _));
        await read.VerifyAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildOnlyAndNoDraftUploadRespectTheirBoundaries(bool buildOnly)
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var checks = 0;
        var clean = 0;
        var options = Options(buildOnly ? "-BuildOnly" : "-NoDraftUpload");
        Assert.Equal(0, Prepare.Complete(context, package, options, Environment(tree.Root), () => clean++,
            () => { checks++; return new([]); }, _ => throw new InvalidOperationException("No upload expected")));
        Assert.Equal(buildOnly ? 0 : 1, checks);
        Assert.Equal(buildOnly ? 0 : 1, clean);
        Assert.Equal(!buildOnly, File.Exists(Path.Combine(context.OutputDirectory, "candidate.json")));
    }

    [Fact]
    public void DirtyTreeAndChangedArchiveLeaveNoCandidate()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var checks = new PrepareCheckResults([]);
        Assert.Throws<ReleaseException>(() => Prepare.Complete(context, package, Options("-NoDraftUpload"), Environment(tree.Root),
            () => throw new ReleaseException("dirty"), () => checks));
        File.AppendAllText(package.ZipPath, "changed");
        Assert.Throws<ReleaseException>(() => PrepareChecks.WriteCandidate(context, package, checks, new string('a', 40)));
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, "candidate.json")));
    }

    [Fact]
    public void UpdaterComparisonUsesInstalledBytes()
    {
        using var tree = new StaticTree();
        var runtime = tree.Write("runtime/tools/TiaMcp.Updater.exe", "shipped");
        tree.Write("bin-build/updater/TiaMcp.Updater.exe", "suite rebuild");
        var inventory = new JsonObject { ["runtime/tools/TiaMcp.Updater.exe"] = FileHash.Sha256(runtime) };
        ReleaseCommands.RequireRuntimeFiles(tree.Root, inventory);
        File.WriteAllText(runtime, "changed");
    }

    [Fact]
    public void ComposedInventoryContainsExactlyTheRetainedChecks()
    {
        using var tree = new StaticTree();
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), Path.Combine(tree.Root, "bundle"), Environment(tree.Root));
        var expected = new[] { "product-smoke", "relocated-bundle", "bundled-runtime-foundation-host", "bundled-runtime-studio",
            "bundle-write-guard", "foundation-approval", "approval-v20-bundle", "approval-v21-bundle", "native-mcp-session-self-test", "nativelifecycle-self-test" }
            .Concat(PlcWorkerFixtures.Keys.SelectMany(key => new[] { "weave-v" + key + "-bundle", "contracts-v" + key }));
        Assert.Equal(expected.Order(), All(plan).Select(job => job.Name).Order());
        Assert.Equal(38, DotnetSuites.LoadCatalog(Path.Combine(Repository.FindRoot(), "tests/test-suites.json"), Repository.FindRoot()).Count);
    }

    [Fact]
    public void FailedPrepareEndsWithEveryFailedJobAfterSlowestFirstTimings()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var savedOut = Console.Out;
        var savedError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            var plan = new PrepareCheckPlan(
                [new("producer", () => new("producer", 1, "producer reason\nproducer details", "")),
                 new("check", () => throw new ReleaseException("check reason\ncheck details"))]);
            Assert.Equal(1, Prepare.RunWithTimings(() =>
            {
                PrepareTimings.Record("slow", 9);
                PrepareTimings.Record("fast", 1);
                try
                {
                    PrepareChecks.Execute(context, package, plan,
                        runtimeCheck: () => throw new IOException("runtime reason\nruntime details"));
                    return 0;
                }
                finally { Console.WriteLine("cleanup complete"); }
            }));
        }
        finally { Console.SetOut(savedOut); Console.SetError(savedError); }
        var lines = output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var timingLines = lines.Where(line => line.StartsWith("PREPARE timing ", StringComparison.Ordinal)).ToArray();
        Assert.Contains("PREPARE timing slow: 9.0 s", timingLines);
        Assert.Contains("PREPARE timing fast: 1.0 s", timingLines);
        Assert.True(Array.IndexOf(timingLines, "PREPARE timing slow: 9.0 s") < Array.IndexOf(timingLines, "PREPARE timing fast: 1.0 s"));
        Assert.True(Array.IndexOf(lines, "cleanup complete") < Array.IndexOf(lines, timingLines[0]));
        Assert.Equal(new[] { "PREPARE failed jobs:", "FAIL check: TiaMcp.ReleaseTool.ReleaseException: check reason",
            "FAIL producer: producer reason", "FAIL runtime-bytes: System.IO.IOException: runtime reason" }, lines.TakeLast(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimingsArePrintedForSuccessfulAndFailedPrepare(bool fail)
    {
        var savedOut = Console.Out;
        var savedError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            Assert.Equal(fail ? 64 : 0, Prepare.RunWithTimings(() => Prepare.Timed("fixture", () =>
            {
                if (fail) throw new ReleaseException("fixture reason\nfull details", 64);
                return 0;
            })));
        }
        finally { Console.SetOut(savedOut); Console.SetError(savedError); }
        Assert.Contains("PREPARE timing fixture:", output.ToString());
        if (fail)
        {
            Assert.Contains("full details", output.ToString());
            Assert.EndsWith("PREPARE failed jobs:" + System.Environment.NewLine + "FAIL fixture: fixture reason" + System.Environment.NewLine, output.ToString());
        }
        else Assert.DoesNotContain("PREPARE failed jobs:", output.ToString());
    }

    [Fact]
    public void EveryPreparedVerificationUsesTheFrozenWeaver()
    {
        using var tree = new StaticTree();
        var calls = new List<string[]>();
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), Path.Combine(tree.Root, "bundle"), Environment(tree.Root),
            (_, arguments, _, _, _) =>
            {
                calls.Add(arguments.ToArray());
                return new(1, "", "stop at the fake process boundary");
            });
        foreach (var job in All(plan).Where(job => job.Name.StartsWith("weave-", StringComparison.Ordinal)
            || job.Name.StartsWith("native-coverage-", StringComparison.Ordinal)
            || job.Name.StartsWith("adapter-native-coverage-", StringComparison.Ordinal)
            || job.Name.StartsWith("jit-adapter-coverage-", StringComparison.Ordinal)))
            Assert.Throws<ReleaseException>(() => job.Run());
        Assert.Equal(8, calls.Count);
        Assert.All(calls, args =>
        {
            Assert.Equal("verify", args[1]);
            Assert.Equal(Path.Combine(tree.Root, "runtime/verification/NativeCallWeaver.dll"), args[0]);
            Assert.DoesNotContain("bin/Release", args[0]);
        });

    }

    [Fact]
    public void OnlyTheExpectedJobsExecuteFromTheBundle()
    {
        using var tree = new StaticTree();
        var bundle = Path.Combine(tree.Root, "bundle");
        var current = "";
        var bundled = new HashSet<string>(StringComparer.Ordinal);
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), bundle, Environment(tree.Root), (_, _, cwd, _, _) =>
        {
            if (cwd == bundle) bundled.Add(current);
            return new(1, "", "stop at the fake process boundary");
        });
        foreach (var job in plan.Jobs)
        {
            current = job.Name;
            Assert.Throws<ReleaseException>(() => job.Run());
        }
        var expected = new[] { "product-smoke", "relocated-bundle", "bundle-write-guard", "bundled-runtime-foundation-host", "bundled-runtime-studio",
            "foundation-approval", "approval-v20-bundle", "approval-v21-bundle" }
            .Concat(PlcWorkerFixtures.Keys.SelectMany(key => new[] { "weave-v" + key + "-bundle", "contracts-v" + key }));
        Assert.Equal(expected.Order(), bundled.Order());
    }

    [Fact]
    public void BundleCommandsUseExtractedHostsAndRunTemporaryEnvironment()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var bundle = Path.Combine(tree.Root, "extracted/package");
        foreach (var key in PlcWorkerFixtures.Keys) tree.Write($"manifest/contracts/v4/baseline/{key}.json", "{}\n");
        var calls = new List<(string Exe, string[] Args, string Cwd, IDictionary<string, string?> Env, string? Input)>();
        CommandResult Process(string exe, IEnumerable<string> arguments, string cwd, IDictionary<string, string?> env, string? input)
        {
            calls.Add((exe, arguments.ToArray(), cwd, new Dictionary<string, string?>(env), input));
            if (env.TryGetValue("COREHOST_TRACEFILE", out var trace))
                File.WriteAllText(trace!, "Resolved fxr [" + Path.GetFullPath(Path.Combine(bundle, "runtime/dotnet/host/fxr")) + Path.DirectorySeparatorChar + "10.0/hostfxr.dll]");
            if (input is not null)
                return new(0, input.Contains("UnknownV4Tool", StringComparison.Ordinal) ? "{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\"}}" : "", "");
            var args = arguments.ToArray();
            if (args.Contains("capture"))
            {
                Assert.Equal(bundle, args[Array.IndexOf(args, "--repo-root") + 1]);
                var key = args[Array.IndexOf(args, "--releases") + 1];
                var output = args[Array.IndexOf(args, "--output") + 1];
                Directory.CreateDirectory(output);
                File.Copy(Path.Combine(tree.Root, $"manifest/contracts/v4/baseline/{key}.json"), Path.Combine(output, key + ".json"));
            }
            return new(0, "", "");
        }
        var plan = ReleaseCommands.PrepareCheckJobs(context, bundle, Environment(tree.Root), Process);
        var bundled = plan.Jobs.Where(job => job.Name is "product-smoke" or "relocated-bundle" or "bundle-write-guard" or "bundled-runtime-foundation-host" or "bundled-runtime-studio"
            || job.Name.StartsWith("weave-", StringComparison.Ordinal) || job.Name.StartsWith("contracts-v", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(new[] { "product-smoke", "relocated-bundle", "bundle-write-guard", "bundled-runtime-foundation-host", "bundled-runtime-studio" }
            .Concat(PlcWorkerFixtures.Keys.SelectMany(key => new[] { "weave-v" + key + "-bundle", "contracts-v" + key })).Order(), bundled.Select(job => job.Name).Order());
        foreach (var job in bundled)
            Assert.Equal(0, job.Run().ExitCode);
        foreach (var call in calls.Where(call => call.Cwd == bundle))
        {
            Assert.Equal(bundle, call.Env["TIA_MCP_BUNDLE_ROOT"]);
            Assert.Equal(bundle, call.Env["CLAUDE_PLUGIN_ROOT"]);
            Assert.Equal(context.CompanionPython, call.Env["TIA_MCP_PLC_TOOLS_PYTHON"]);
        }
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            Assert.Contains(calls, call => call.Args.Contains(Path.Combine(bundle, $"runtime/v{key}/worker/TiaMcp.Adapter.{key}.dll")));
            Assert.Contains(calls, call => call.Args.Contains(key + "=" + Path.Combine(bundle, $"runtime/v{key}/TiaMcp.FoundationHost.exe")));
        }
        Assert.Equal(8, calls.Count(call => call.Args.Contains("capture")));
        Assert.Equal(8, calls.Count(call => call.Args.Contains("compare")));
    }

    [Theory]
    [InlineData("native-mcp-session-self-test", "COMPLETE: 7 native MCP safety checks passed")]
    [InlineData("nativelifecycle-self-test", "COMPLETE: 0 native supervisor checks passed; live TIA tests NOT RUN")]
    public void NativeSelfTestCountsAreNotOnlyExitCodeChecks(string name, string output)
    {
        using var tree = new StaticTree();
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), Path.Combine(tree.Root, "bundle"), Environment(tree.Root),
            (_, _, _, _, _) => new(0, output, ""));
        Assert.Throws<ReleaseException>(() => plan.Jobs.Single(job => job.Name == name).Run());
    }

    [Fact]
    public void WriteGuardFailsWhenDenyDisappearsOrReadIsDenied()
    {
        var denied = new CommandResult(0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\"}}", "");
        var empty = new CommandResult(0, "", "");
        PrepareChecks.RequireWriteGuard(denied, empty);
        Assert.ThrowsAny<Exception>(() => PrepareChecks.RequireWriteGuard(empty, empty));
        Assert.Throws<ReleaseException>(() => PrepareChecks.RequireWriteGuard(denied, denied));
    }

    [Fact]
    public void RetiredHostParityJobsAreNotScheduled()
    {
        using var tree = new StaticTree();
        var plan = ReleaseCommands.PrepareCheckJobs(Context(tree.Root), Path.Combine(tree.Root, "bundle"), Environment(tree.Root));
        Assert.DoesNotContain(plan.Jobs, job => job.Name is "host-parity-build" or "host-behavior-parity");
    }

    [Fact]
    public void ApprovalUsesAllBundleHostsAndPreparedWorkers()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var bundle = Path.Combine(tree.Root, "bundle");
        var calls = new List<(string[] Args, IDictionary<string, string?> Env)>();
        var plan = ReleaseCommands.PrepareCheckJobs(context, bundle, Environment(tree.Root), (exe, args, cwd, env, input) =>
        {
            calls.Add((args.ToArray(), new Dictionary<string, string?>(env)));
            return new(1, "", "stop before fixture validation");
        });
        foreach (var name in new[] { "foundation-approval", "approval-v20-bundle", "approval-v21-bundle" })
            Assert.Throws<ReleaseException>(() => plan.Jobs.Single(job => job.Name == name).Run());
        var foundation = calls[0];
        Assert.Contains("foundation", foundation.Args);
        Assert.Contains(Path.Combine(bundle, "runtime"), foundation.Args);
        foreach (var key in new[] { "20", "21" })
        {
            var call = calls.Single(call => call.Args.Contains(key));
            Assert.Contains(Path.Combine(bundle, $"runtime/v{key}/TiaMcp.FoundationHost.exe"), call.Args);
            Assert.Contains("--plc-worker", call.Args);
            Assert.Contains("--readiness-worker", call.Args);
            Assert.Contains(Path.Combine(bundle, $"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe"), call.Args);
            Assert.Contains(call.Args, arg => arg.StartsWith(Path.Combine(context.Logs, "plc-fixtures"), StringComparison.Ordinal));
            Assert.Equal(bundle, call.Env["TIA_MCP_BUNDLE_ROOT"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApprovalReadinessSelectsThePackagedWorkerOrTheExistingDefault(bool packaged)
    {
        using var tree = new StaticTree();
        var script = Path.Combine(Repository.FindRoot(), "scripts/checks/Test-ReleaseApprovalGate.py");
        const string code = """
            import contextlib, importlib.util, sys
            from pathlib import Path
            from types import SimpleNamespace
            from unittest.mock import patch
            script, directory, packaged = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3] == 'True'
            sys.path.insert(0, str(script.parent))
            spec = importlib.util.spec_from_file_location('approval_fixture', script)
            gate = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(gate)
            exe = directory / 'host.exe'
            exe.touch()
            worker = directory / 'bundle/runtime/v21/worker/TiaMcp.PlcWorker.21.exe'
            args = SimpleNamespace(exe=exe, portal_root=directory, public_api=directory,
                host_harness=None, plc_worker=directory / 'sdk-worker.exe', major=21,
                temp_root=directory, readiness_worker=worker if packaged else None)
            calls = []
            def rpc(method, *args, **kwargs):
                if method == 'tools/list':
                    return {'result': {'tools': [{'name': gate.ENGINE_WRITE[0]}, {'name': gate.CREATE_DEVICE[0]}, {'name': 'CallTool'}]}}
                return {'result': {}}
            @contextlib.contextmanager
            def server(*args, **kwargs):
                calls.append(kwargs)
                yield rpc, None, []
            with patch.multiple(gate, WINDOWS=True,
                    fresh_data_root=lambda *a, **k: (directory, {}),
                    retained_on_failure=contextlib.nullcontext,
                    assert_refused=lambda *a: None, assert_engine_read=lambda *a: None,
                    assert_create_device_stopped=lambda *a: None,
                    assert_readiness_refused=lambda *a: None), \
                 patch.object(gate.resources, 'server', server), \
                 patch.object(gate.resources, 'sdk_only_installation', lambda *a: directory):
                report = gate.run_engine(args)
            assert report['checksPassed'] == report['checksExpected'] == 8
            expected = worker if packaged else script.parents[2] / 'src/PlcWorker/bin/21/Release/net48/TiaMcp.PlcWorker.21.exe'
            assert calls[-1]['plc_worker'] == expected, (calls[-1]['plc_worker'], expected)
            assert calls[-1]['sdk_ready'] is False
            assert all(call['plc_worker'] == args.plc_worker for call in calls[:2])
            print('Readiness routing passed')
            """;
        var result = ProcessRunner.Run("python", ["-c", code, script, tree.Root, packaged.ToString()], tree.Root);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Contains("Readiness routing passed", result.StandardOutput);
    }

    [Fact]
    public void NonbreakingBundleContractDriftStillFailsExactBytes()
    {
        using var tree = new StaticTree();
        tree.Write("manifest/contracts/v4/baseline/21.json", "{\"description\":\"original\"}\n");
        var context = Context(tree.Root);
        var plan = ReleaseCommands.PrepareCheckJobs(context, Path.Combine(tree.Root, "bundle"), Environment(tree.Root),
            (_, arguments, _, _, _) =>
            {
                var args = arguments.ToArray();
                if (args.Contains("capture"))
                {
                    var output = args[Array.IndexOf(args, "--output") + 1];
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "21.json"), "{\"description\":\"changed\"}\n");
                }
                return new(0, "No breaking changes", "");
            });
        Assert.Throws<ReleaseException>(() => plan.Jobs.Single(job => job.Name == "contracts-v21").Run());
    }

    [Fact]
    public void PackageChecksDoNotReadOrStartSuitesAndLocalOnlyNamesUploadCommand()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        var package = Package(context);
        var saved = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Prepare.Complete(context, package, Options("-NoDraftUpload"), Environment(tree.Root), () => { },
                () => PrepareChecks.Execute(context, package, new([new("fixture", () => new("fixture", 0, "passed", ""))]), runtimeCheck: () => { })));
        }
        finally { Console.SetOut(saved); }
        Assert.Contains("dotnet run --no-build --project build-tools/release -- upload-draft -Version " + context.Version, output.ToString());
        Assert.DoesNotContain("suite-", output.ToString());
        Assert.DoesNotContain("publish-prepared", output.ToString());
    }

    private static IEnumerable<PrepareCheckJob> All(PrepareCheckPlan plan) => plan.Jobs;
    private static PrepareContext Context(string root) => new(root, "4.0.0-beta.4", Path.Combine(root, "sdk"), null,
        "python", "companion-python", Path.Combine(root, "prepared"), new(2026, 11, 3), 1);
    private static Dictionary<string, string?> Environment(string root) => new() { ["TEMP"] = Path.Combine(root, "temp") };
    private static Options Options(params string[] flags) => TiaMcp.ReleaseTool.Options.Parse(["-Version", "4.0.0-beta.4", .. flags],
        new HashSet<string> { "BuildOnly", "NoDraftUpload" });
    private static JsonObject Suite() => new() { ["project"] = "fixture.csproj", ["minimumPassed"] = 2, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray() };
    private static PreparedPackage Package(PrepareContext context)
    {
        Directory.CreateDirectory(context.OutputDirectory);
        var zip = Path.Combine(context.OutputDirectory, context.PackageName + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(context.PackageName + "/runtime/test.dll").Open())) writer.Write("test");
        var hash = FileHash.Sha256(zip);
        File.WriteAllText(Path.ChangeExtension(zip, ".sha256"), hash + "  " + Path.GetFileName(zip));
        return new(zip, hash, 1, new FileInfo(zip).Length, context.StageDirectory);
    }
    private static void WriteTrx(string path, int passed, int skipped)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var cases = Enumerable.Range(0, passed + skipped).ToArray();
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", cases.Select(index => new XElement(ns + "UnitTestResult",
            new XAttribute("testId", "test" + index), new XAttribute("executionId", "run" + index), new XAttribute("outcome", index < passed ? "Passed" : "NotExecuted")))),
            new XElement(ns + "TestDefinitions", cases.Select(index => new XElement(ns + "UnitTest", new XAttribute("id", "test" + index),
                new XElement(ns + "TestMethod", new XAttribute("className", "Fixture"), new XAttribute("name", "Test" + index))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters", new XAttribute("total", passed + skipped),
                new XAttribute("executed", passed), new XAttribute("passed", passed), new XAttribute("failed", 0), new XAttribute("notExecuted", skipped))))).Save(path);
    }

    [Fact]
    public void CandidateInventoryIncludesAllRuntimeBytesAndRootExecutables()
    {
        using var tree = new StaticTree();
        var zip = Path.Combine(tree.Root, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var path in new[] { "runtime/host.exe", "runtime/dependency.dll", "runtime/release-key.txt", "launcher.exe", "docs/readme.md" })
            {
                using var writer = new StreamWriter(archive.CreateEntry("bundle/" + path).Open()); writer.Write(path);
            }
        var rows = ReleaseCommands.PackageInventory(zip);
        Assert.Equal(4, rows.Count); Assert.False(rows.ContainsKey("docs/readme.md"));
        Assert.All(rows, row => Assert.Matches("^[0-9a-f]{64}$", (string)row.Value!));
    }

    [Fact]
    public void CandidateChecksCannotUseDifferentRuntimeBytesAndUpdaterMapsToItsBuildOutput()
    {
        using var tree = new StaticTree();
        var host = tree.Write("runtime/v20/host.exe", "host");
        var updater = tree.Write("runtime/tools/TiaMcp.Updater.exe", "updater");
        var hashes = new JsonObject { ["runtime/v20/host.exe"] = FileHash.Sha256(host), ["runtime/tools/TiaMcp.Updater.exe"] = FileHash.Sha256(updater) };
        ReleaseCommands.RequireRuntimeFiles(tree.Root, hashes);
        File.AppendAllText(host, "changed"); Assert.Throws<ReleaseException>(() => ReleaseCommands.RequireRuntimeFiles(tree.Root, hashes));
        File.WriteAllText(host, "host"); File.AppendAllText(updater, "changed");
        Assert.Throws<ReleaseException>(() => ReleaseCommands.RequireRuntimeFiles(tree.Root, hashes));
    }
}

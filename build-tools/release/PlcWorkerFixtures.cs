using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.WorkerChannel;

namespace TiaMcp.ReleaseTool;

internal sealed record PlcWorkerFixtureBuild(string Release, string Variant)
{
    internal string Root(string output) => Path.Combine(output, Variant);
    internal string Worker(string output) => Path.Combine(Root(output), "bin", Release, "Release/net48", $"TiaMcp.PlcWorker.{Release}.exe");
    internal string[] Properties(string output) => ["-p:TiaMcpPlcWorkerSdkFixture=true",
        "-p:TiaMcpPlcWorkerFixtureRoot=" + Root(output)];
}

internal static class PlcWorkerFixtures
{
    internal static readonly string[] Keys = CatalogJson.Keys.ToArray();
    internal static PlcWorkerFixtureBuild Sdk(string key)
    {
        if (!Keys.Contains(key)) throw new ReleaseException("Unknown PlcWorker release: " + key);
        return new(key, "sdk");
    }
    internal static IEnumerable<PlcWorkerFixtureBuild> Builds(IEnumerable<string> keys) => keys.Select(Sdk);

    internal static async Task<string> Smoke(string worker, string api, string key, string? fault = null, string? environmentFault = null)
    {
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var start = new ProcessStartInfo(Path.GetFullPath(worker)) { WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false) };
        foreach (string argument in new[] { "--native-session", key, Path.GetFullPath(api), nonce }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_PLC_WORKER_FAULT"] = fault ?? environmentFault;
        start.Environment["TIA_MCP_PLC_WORKER_SDK_READY"] = "1";
        var job = GateWatchdog.ActiveJob;
        job?.RequireTime();
        var tree = WindowsProcessTree.Start(start, name: null, expectedImagePath: start.FileName);
        Exception? failure = null;
        try
        {
            void Stop() => StopWorker(tree, start.FileName);
            using var registration = job?.TrackTree(checked((int)tree.ProcessId), Stop);
            return await SmokeStarted(tree, worker, key, nonce, fault, environmentFault, Stop);
        }
        catch (Exception error) { failure = error; throw; }
        finally { tree.Dispose(failure); }
    }

    internal static void StopWorker(WindowsProcessTree tree, string imagePath)
    {
        try { Console.Error.WriteLine($"Stopping owned PlcWorker pid={tree.ProcessId} image={imagePath}"); }
        finally { tree.Finish(ProcessDeadline.CleanupTimeout); }
    }

    private static async Task<string> SmokeStarted(WindowsProcessTree tree, string worker, string key, string nonce,
        string? fault, string? environmentFault, Action stop)
    {
        var stderr = tree.StandardError.ReadToEndAsync();
        using var channel = new ChannelClient(tree.StandardOutput.BaseStream, tree.StandardInput.BaseStream,
            new ChannelIdentity(key, FileHash.Sha256(worker), FileHash.Sha256(Path.Combine(Path.GetDirectoryName(worker)!, $"TiaMcp.Adapter.{key}.dll")),
                checked((int)tree.ProcessId), nonce));
        string result = "";
        Exception? failure = null;
        try
        {
            await channel.ConnectAsync(TimeSpan.FromSeconds(30));
            if (fault == null)
            {
                string raw = await channel.CallAsync("adapter.ReadState", "{}", BindingChange.None, true, TimeSpan.FromSeconds(30));
                using var state = JsonDocument.Parse(raw);
                if (state.RootElement.GetProperty("ReleaseKey").GetString() != key || state.RootElement.GetProperty("IsAttached").GetBoolean())
                    throw new ReleaseException("PlcWorker smoke returned an unexpected cached state: " + raw);
                result = raw;
            }
            else try
            {
                await channel.CallAsync("adapter.ReadState", "{}", BindingChange.None, true, TimeSpan.FromSeconds(1));
                throw new ReleaseException("Fixture fault was not injected: " + fault);
            }
            catch (ChannelFailureException error) when (fault is "tia-lost" or "background-tia-exit")
            {
                if (error.Outcome != ChannelOutcome.Unknown || tree.WaitForExit(0)) throw;
                result = error.Message;
            }
            catch (ChannelFaultException error) when (fault is not ("tia-lost" or "background-tia-exit"))
            {
                if (!channel.OutcomeUnknown) throw;
                result = error.Message;
            }
        }
        catch (Exception error) { failure = error; }
        try
        {
            tree.StandardInput.Close();
            channel.Dispose();
            if (!tree.WaitForExit(5000)) stop();
            else tree.Finish();
            int exitCode = tree.GetExitCode();
            string diagnostics = await stderr;
            if (fault == null && environmentFault == null) result += "\n" + diagnostics;
            if (fault == "background-tia-exit" && !diagnostics.Contains("terminating=False", StringComparison.Ordinal))
                throw new ReleaseException("Missing CLR non-terminating background exception evidence: " + diagnostics);
            if (exitCode == 86 || fault == null && exitCode != 0)
                throw new ReleaseException("PlcWorker smoke " + TiaMcp.Engine.Tests.Shared.FixtureExit.Describe(exitCode, diagnostics));
        }
        catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}

internal static partial class ReleaseCommands
{
    private static int BuildPlcWorkerFixtures(Options options)
    {
        EnsureWindows("build-plc-worker-fixtures");
        string apiRoot = Path.GetFullPath(options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required"));
        string output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/plc-worker-fixtures")));
        var keys = options.All("ReleaseKeys").ToArray();
        if (keys.Length == 0) keys = PlcWorkerFixtures.Keys;
        string project = Path.Combine(Root, "src/PlcWorker/TiaMcp.PlcWorker.csproj");
        string weaver = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
        var results = new List<object>();
        foreach (var unit in PlcWorkerFixtures.Builds(keys))
        {
            string api = Path.Combine(apiRoot, PlcWorkerApi(unit.Release).Directory);
            Directory.CreateDirectory(unit.Root(output));
            string[] properties = ["-p:TiaReleaseKey=" + unit.Release, "-p:SiemensEngineeringDirectory=" + api,
                "-p:NativeCallWeaverPath=" + weaver, "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", .. unit.Properties(output)];
            List<string> restore = ["restore", project, "-m:1", "-nodeReuse:false", .. properties];
            AddNugetConfig(restore, options.Get("NuGetConfig"));
            var restored = ProcessRunner.Run(Dotnet, restore, Root);
            WriteLog(Path.Combine(unit.Root(output), "restore-" + unit.Release + ".log"), restored);
            ProcessRunner.RequireSuccess(restored, "PlcWorker fixture restore");
            var built = ProcessRunner.Run(Dotnet, ["build", project, "-c", "Release", "--no-restore", "-m:1", "-nodeReuse:false", .. properties], Root);
            WriteLog(Path.Combine(unit.Root(output), "build-" + unit.Release + ".log"), built);
            ProcessRunner.RequireSuccess(built, "PlcWorker fixture build");
            string worker = unit.Worker(output);
            var metadata = WorkerAssemblyMetadata.Read(worker);
            if (metadata.GetValueOrDefault("TiaMcpPlcWorkerSdkFixture") != "true"
                || metadata.GetValueOrDefault("TiaReleaseKey") != unit.Release
                || metadata.GetValueOrDefault("TiaMcpTestPolicy") != null)
                throw new ReleaseException("PlcWorker fixture metadata differs from its build unit: " + worker);
            var verified = ProcessRunner.Run(Dotnet, [weaver, "verify", Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter." + unit.Release + ".dll")], Root);
            WriteLog(Path.Combine(unit.Root(output), "verify-" + unit.Release + ".log"), verified);
            ProcessRunner.RequireSuccess(verified, "PlcWorker fixture adapter weave verify");
            string startupState = PlcWorkerFixtures.Smoke(worker, api, unit.Release).GetAwaiter().GetResult();
            results.Add(new { unit.Release, unit.Variant, worker, workerSha256 = FileHash.Sha256(worker), startupState });
            Console.WriteLine($"PASS PlcWorker fixture {unit.Release}/{unit.Variant}");
        }
        WriteJson(Path.Combine(output, "summary.json"), new { results, nativeAcceptance = "NOT RUN" });
        return 0;
    }

    private static int CheckPlcWorkers(Options options)
    {
        EnsureWindows("check-plc-workers");
        string apiRoot = Path.GetFullPath(options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required"));
        string? fixtureRoot = options.Get("FixtureRoot");
        var rows = new List<object>();
        int passed = 0, failed = 0;
        void Check(string key, string label, Func<string> action)
        {
            try
            {
                string evidence = action(); passed++;
                rows.Add(new { key, check = label, passed = true, evidence });
                Console.WriteLine("PASS PlcWorker " + key + "/" + label);
            }
            catch (Exception error)
            {
                failed++; rows.Add(new { key, check = label, passed = false, error = error.ToString() });
                Console.WriteLine("FAIL PlcWorker " + key + "/" + label + ": " + error.Message);
            }
        }
        foreach (string key in PlcWorkerFixtures.Keys)
        {
            string worker = fixtureRoot == null ? Path.Combine(Root, $"src/PlcWorker/bin/{key}/Release/net48/TiaMcp.PlcWorker.{key}.exe")
                : new PlcWorkerFixtureBuild(key, "sdk").Worker(Path.GetFullPath(fixtureRoot));
            string api = Path.Combine(apiRoot, PlcWorkerApi(key).Directory);
            if (!Path.GetFullPath(worker).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ReleaseException("PlcWorker smoke may launch only outputs in this worktree.");
            Check(key, "hello-ReadState-D6-config", () => {
                var metadata = WorkerAssemblyMetadata.Read(worker);
                if (metadata.GetValueOrDefault("TiaReleaseKey") != key
                    || (metadata.GetValueOrDefault("TiaMcpPlcWorkerSdkFixture") == "true") != (fixtureRoot != null)
                    || metadata.GetValueOrDefault("TiaMcpTestPolicy") != null)
                    throw new ReleaseException("PlcWorker smoke selected an unexpected build flavor: " + worker);
                var config = XDocument.Load(worker + ".config");
                if ((string?)config.Root?.Element("runtime")?.Element("legacyUnhandledExceptionPolicy")?.Attribute("enabled") != "1")
                    throw new ReleaseException("PlcWorker is missing D6: " + worker);
                return PlcWorkerFixtures.Smoke(worker, api, key).GetAwaiter().GetResult();
            });
            if (fixtureRoot == null)
                foreach (string mode in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost", "background-tia-exit" })
                {
                    Check(key, "production-blocks-" + mode, () => PlcWorkerFixtures.Smoke(worker, api, key, environmentFault: mode).GetAwaiter().GetResult());
                }
            if (fixtureRoot != null)
                foreach (string fault in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost", "background-tia-exit" })
                {
                    Check(key, "fault-" + fault, () => PlcWorkerFixtures.Smoke(worker, api, key, fault).GetAwaiter().GetResult());
                }
        }
        WriteJson(Path.GetFullPath(options.Get("Output", Path.Combine(Root, "bin-build/plc-worker-smoke.json"))), new { passed, failed, rows, nativeAcceptance = "NOT RUN" });
        return failed == 0 ? 0 : 1;
    }
}

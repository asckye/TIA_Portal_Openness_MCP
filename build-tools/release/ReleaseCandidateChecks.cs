using System.IO.Compression;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static void RunProductionCandidateChecks(ReleaseCheckPlan plan, Options options, string api, string logs,
        string output, string temp, string python)
    {
        var activeLog = new AsyncLocal<string?>();
        void Step(string name, Action action)
        {
            activeLog.Value = Path.Combine(logs, name + ".log");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try { action(); }
            finally
            {
                File.AppendAllText(activeLog.Value, $"Elapsed: {stopwatch.Elapsed.TotalSeconds:F3}s{Environment.NewLine}");
                activeLog.Value = null;
            }
        }
        CommandResult Isolated(string name, string executable, IReadOnlyList<string> args, bool defaults, string? cwd)
        {
            var root = Path.Combine(temp, "rc", Guid.NewGuid().ToString("N")[..8]);
            foreach (var child in new[] { "config", "temp", "local-app-data", "app-data" }) Directory.CreateDirectory(Path.Combine(root, child));
            var settings = ReleaseValidation.HostApprovalSettings(defaults);
            if (settings is not null) File.WriteAllText(Path.Combine(root, "config/approval.settings"), settings, new System.Text.UTF8Encoding(false));
            var env = new Dictionary<string, string?>
            {
                ["TIA_MCP_DATA_DIRECTORY"] = root, ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(root, "diagnostics"),
                ["LOCALAPPDATA"] = Path.Combine(root, "local-app-data"), ["APPDATA"] = Path.Combine(root, "app-data"),
                ["TEMP"] = Path.Combine(root, "temp"), ["TMP"] = Path.Combine(root, "temp"),
                ["TIA_MCP_RELEASE_TEMP_ROOT"] = temp, ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api,
                ["RestoreConfigFile"] = options.Get("NuGetConfig"), ["NuGetAudit"] = "false"
            };
            var result = ProcessRunner.Run(executable, args, cwd ?? Root, env);
            if (activeLog.Value is { } log) File.AppendAllText(log, result.StandardOutput + result.StandardError, new System.Text.UTF8Encoding(false));
            if (result.ExitCode == 0) Directory.Delete(root, true);
            else Console.Error.WriteLine($"Retained failed release check data ({name}): {root}");
            return result;
        }
        void Python(string script, string[] args, bool defaults)
        {
            var result = Isolated(script, python, [Path.Combine(Root, script), .. args], defaults, null);
            Console.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            ProcessRunner.RequireSuccess(result, script);
        }
        RunCandidateChecks(plan, api, logs, output, temp, Dotnet, python, GetMaxParallelism(options), Step,
            (script, args) => Python(script, args, false), (script, args) => Python(script, args, true), Isolated);
    }

    private static void RunCandidateChecks(ReleaseCheckPlan plan, string api, string logs, string output, string releaseTemp,
        string dotnet, string python, int maxParallelism, Action<string, Action> runStep,
        Action<string, string[]> runPython, Action<string, string[]> runDefaults,
        Func<string, string, IReadOnlyList<string>, bool, string?, CommandResult> runIsolated)
    {
        var snapshotArgs = new[] { "--repo-root", Root, "--public-api-root", api };
        var harness = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe");
        void RequireFile(string path, string explanation)
        {
            if (!File.Exists(path)) throw new ReleaseException(explanation + ": " + path);
        }
        string[] CaptureArgs(string release) => [.. snapshotArgs, "--releases", release, "--exe", release + "=" + Path.Combine(Root, release is "20" or "21" ? $"runtime/v{release}/TiaMcp.Engine.V{release}.exe" : $"runtime/v{release}/TiaMcp.FoundationHost.exe")];
        void CaptureSnapshots(string script, string name, bool responses, string[] releases)
        {
            var combined = Path.Combine(logs, name);
            Directory.CreateDirectory(combined);
            foreach (var release in releases)
            {
                var perRelease = Path.Combine(logs, name + "-v" + release);
                var args = new List<string> { "capture" };
                args.AddRange(CaptureArgs(release));
                if (responses && release is "20" or "21") args.AddRange(["--harness", harness]);
                if (responses && release is not ("20" or "21")) args.AddRange(["--dotnet-root", Path.Combine(Root, "runtime/dotnet")]);
                args.AddRange(["--output", perRelease]);
                if (responses) args.AddRange(["--temp-root", Path.Combine(releaseTemp, "response-temp-v" + release)]);
                runDefaults(script, [.. args]);
                var captured = Path.Combine(perRelease, release + ".json");
                RequireFile(captured, $"V{release} {name} snapshot missing");
                File.Copy(captured, Path.Combine(combined, release + ".json"), true);
            }
        }
        var postValidation = new (string Name, Func<PipelineResult> Run)[]
        {
            ("prompt-registration", () =>
            {
                runStep("05-prompt-registration", () => runPython("scripts/checks/Test-DotnetSuites.py", ["--suite", "prompt-registration", "--dotnet", dotnet, "--results-directory", Path.Combine(logs, "prompt-registration-results")]));
                return new PipelineResult("prompt-registration", 0, "Prompt registration passed." + Environment.NewLine, "");
            }),
            ("contracts", () =>
            {
                runStep("06-v4-contracts-capture", () => CaptureSnapshots("scripts/checks/Snapshot-ToolContracts.py", "contracts", responses: false, ["20", "21"]));
                runStep("07-v4-contracts-compare", () => runPython("scripts/checks/Snapshot-ToolContracts.py", ["compare", "--baseline", "manifest/contracts/v4/baseline", "--current", Path.Combine(logs, "contracts"), "--releases", "20", "21"]));
                return new PipelineResult("contracts", 0, "Contract capture and comparison passed." + Environment.NewLine, "");
            }),
            ("responses", () =>
            {
                runStep("08-v4-responses-capture", () => CaptureSnapshots("scripts/checks/Snapshot-ToolResponses.py", "responses", responses: true, ["20", "21"]));
                runStep("09-v4-responses-compare", () => runPython("scripts/checks/Snapshot-ToolResponses.py", ["compare", "--baseline", "manifest/contracts/v4/responses", "--current", Path.Combine(logs, "responses"), "--releases", "20", "21"]));
                return new PipelineResult("responses", 0, "Response capture and comparison passed." + Environment.NewLine, "");
            }),
            ("relocated-bundle", () =>
            {
                runStep("10-relocated-bundle", () =>
                {
                    var packagePath = Path.Combine(output, "package-result.json");
                    RequireFile(packagePath, "Local packaging result missing");
                    using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
                    var zip = package.RootElement.GetProperty("path").GetString() ?? throw new ReleaseException("Package result has no path");
                    var relocationInput = Path.Combine(releaseTemp, "relocation-input-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(relocationInput);
                        ZipFile.ExtractToDirectory(zip, relocationInput);
                        var bundle = Path.Combine(relocationInput, Path.GetFileNameWithoutExtension(zip));
                        if (!Directory.Exists(bundle)) throw new ReleaseException("Extracted candidate package root is missing: " + bundle);
                        var args = new[] { Path.Combine(Root, "scripts/checks/Test-RelocatedBundle.py"), "--bundle-root", bundle, "--public-api-root", api };
                        var result = runIsolated("review-relocated-bundle", python, args, false, relocationInput);
                        Console.Write(result.StandardOutput);
                        Console.Error.Write(result.StandardError);
                        if (result.ExitCode != 0) throw new ReleaseException($"Relocated bundle check exited {result.ExitCode}: {Tail(result.StandardOutput + result.StandardError)}");
                    }
                    finally
                    {
                        try { if (Directory.Exists(relocationInput)) Directory.Delete(relocationInput, true); }
                        catch (Exception ex) { Console.Error.WriteLine($"warning: relocation input remains at {relocationInput}: {ex.Message}"); }
                    }
                });
                return new PipelineResult("relocated-bundle", 0, "Relocated bundle check passed." + Environment.NewLine, "");
            })
        };

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt-registration"] = "prompt-registration", ["contracts"] = "engine-contracts",
            ["responses"] = "engine-responses", ["relocated-bundle"] = "relocation"
        };
        var jobs = postValidation.Where(job => plan.Includes(names[job.Name])).ToList();
        jobs.Add(("product-smoke", () =>
        {
            runStep("11-product-smoke", () => runPython("scripts/checks/Test-ReleaseSmoke.py",
                ["--runtime-root", Path.Combine(Root, "runtime"), "--public-api-root", api, "--temp-root", releaseTemp,
                 "--output", Path.Combine(logs, "smoke.json")]));
            return new PipelineResult("product-smoke", 0, "All product smoke checks passed.\n", "");
        }));
        if (plan.Includes("foundation-responses")) jobs.Add(("foundation-responses", () =>
        {
            var keys = new[] { "14sp1", "15.1", "16", "17", "18", "19" };
            runStep("12-foundation-responses", () =>
            {
                CaptureSnapshots("scripts/checks/Snapshot-ToolResponses.py", "foundation-responses", responses: true, keys);
                runPython("scripts/checks/Snapshot-ToolResponses.py", ["compare", "--baseline", "manifest/contracts/v4/responses",
                    "--current", Path.Combine(logs, "foundation-responses"), "--releases", .. keys]);
            });
            return new PipelineResult("foundation-responses", 0, "Foundation response capture and comparison passed.\n", "");
        }));
        foreach (var test in ReleaseCheckPolicy.Load(Root).SelfTests.Where(test => plan.Includes(test.Check)))
        {
            jobs.Add((test.Check, () =>
            {
                runStep(test.Check, () => runPython(test.Script, test.Arguments.Select(argument => argument == "{output}"
                    ? Path.Combine(logs, test.Check + "-" + Guid.NewGuid().ToString("N")) : argument).ToArray()));
                return new PipelineResult(test.Check, 0, "Check self-test passed.\n", "");
            }));
        }
        ParallelPipeline.Run(jobs, Path.Combine(logs, "post-validation"), maxParallelism);

    }
}

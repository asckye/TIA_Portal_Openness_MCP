using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static int RunReleaseProduction(Options options)
    {
        var version = options.Get("Version")!;
        var date = options.Get("ReleaseDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        if (!DateTime.TryParseExact(date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ReleaseException("-ReleaseDate must be a valid yyyyMMdd date");
        if (options.Has("NoReuse") && options.Has("SkipBuild")) throw new ReleaseException("-NoReuse and -SkipBuild cannot be combined");
        var git = options.Get("Git", "git");
        var python = options.Get("Python", Py);
        var parallelism = GetMaxParallelism(options).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var publicApiRoot = ResolveReleaseApiRoot(options);
        var nugetConfig = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG") ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        var v20 = ResolveReleaseApi(options.Get("V20ReferenceRoot"), publicApiRoot, 20);
        var v21 = ResolveReleaseApi(options.Get("V21ReferenceRoot"), publicApiRoot, 21);

        if (options.Has("EarlyGatesOnly"))
        {
            var releaseTemp = CreateReleaseTempRoot();
            RunReleaseEarlyGates(version, python, v21, publicApiRoot, releaseTemp, nugetConfig);
            return 0;
        }

        // Prerequisites are deliberately the first operation that can contact an external service.
        Prerequisites(options);
        var tokenValue = options.Get("Token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
        var token = ReleasePrerequisites.GetToken(tokenValue, git, Root, localOnly: false);
        ReleasePrerequisites.RequireToken(token);
        var branch = GitText(git, ["rev-parse", "--abbrev-ref", "HEAD"]);
        if (branch != "master") throw new ReleaseException("current branch is " + branch + ", releases go from master");
        RunGitChecked(git, ["fetch", "origin", "--quiet"], "fetch origin");
        var behind = GitText(git, ["rev-list", "--count", "master..origin/master"]);
        if (behind != "0") throw new ReleaseException("origin/master has " + behind + " commit(s) this checkout lacks; pull first");
        RefuseOrStopStrayEngines(options.Has("KillStrayEngine"));

        var changelog = File.ReadAllText(Path.Combine(Root, "CHANGELOG.md"));
        var entry = Regex.Match(changelog, "(?m)^## \\[(\\d+\\.\\d+\\.\\d+)\\][^\\r\\n]*\\r?\\n");
        if (!entry.Success) throw new ReleaseException("CHANGELOG.md has no \"## [x.y.z]\" entry");
        if (entry.Groups[1].Value != version) throw new ReleaseException("newest CHANGELOG entry is " + entry.Groups[1].Value + ", not " + version + " - write the entry first");
        if (!File.Exists(Path.Combine(Root, "docs/releases", "v" + version + ".md"))) throw new ReleaseException("docs/releases/v" + version + ".md is missing - write the release note first");
        var summary = options.Get("Summary", "");
        if (summary.Length == 0)
        {
            var after = changelog[(entry.Index + entry.Length)..];
            summary = after.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.Trim().Length != 0) ?? "";
            summary = Regex.Replace(summary, "\\[([^\\]]+)\\]\\([^)]*\\)", "$1").Trim();
            if (summary.Length > 300) summary = summary[..300];
        }
        Console.WriteLine("Summary = " + summary);

        var runTemp = CreateReleaseTempRoot();
        var logRoot = Path.Combine(Root, "bin-build/release-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(logRoot);
        // Publication never trusts cached outputs or prior validation records, including -Resume.
        var archiveRecord = TryReadBuildRecord("manifest/release-build.json");
        var archiveOutput = ReleaseRecords.MovePreviousReleaseOutput(Root, version);
        if (archiveOutput is not null)
        {
            Console.WriteLine("Preserved earlier release output: " + archiveOutput);
            if (archiveRecord is { } old && GetJsonString(old, "release") == version && ReleaseRecords.AuditEvidenceReason(Root, version, old) == "")
                ReleaseRecords.RestoreArchivedAuditEvidence(Root, version, archiveOutput, old);
        }

        BumpReleaseVersion(version);
        if (!File.ReadAllText(Path.Combine(Root, "docs/reference/capabilities.md")).Contains(version, StringComparison.Ordinal))
            Console.WriteLine("WARNING docs/reference/capabilities.md does not mention " + version + " - add the release entry by hand");
        if (!File.ReadAllText(Path.Combine(Root, "docs/development/roadmap.md")).Contains(version, StringComparison.Ordinal))
            Console.WriteLine("WARNING docs/development/roadmap.md does not mention " + version + " - add the roadmap entry by hand");
        RunReleaseEarlyGates(version, python, v21, publicApiRoot, runTemp, nugetConfig);

        var stages = new ReleaseBuildStages(
            Prepare: () =>
            {
                Console.WriteLine("Preparing build-multi-version: publication requires a cold rebuild");
                var args = new List<string> { "build-multi-version", "-PublicApiRoot", publicApiRoot, "-Python", python, "-PrepareOnly", "-Test", "-Offline", "-MaxParallelism", parallelism };
                if (nugetConfig is not null) args.AddRange(["-NuGetConfig", nugetConfig]);
                RunReleaseCommand(args.ToArray(), "multi-version preparation", logRoot, runTemp, publicApiRoot);
            },
            BuildEngines: () =>
            {
                var args = new List<string> { "build-release", "-V20ReferenceRoot", v20, "-V21ReferenceRoot", v21, "-Python", python, "-ReleaseDate", date, "-MaxParallelism", parallelism };
                if (nugetConfig is not null) args.AddRange(["-NuGetConfig", nugetConfig]);
                RunReleaseCommand(args.ToArray(), "build-release", logRoot, runTemp, publicApiRoot);
            },
            Complete: () =>
            {
                var args = new List<string> { "build-multi-version", "-PublicApiRoot", publicApiRoot, "-Python", python, "-CompleteOnly", "-Test", "-Offline", "-MaxParallelism", parallelism };
                if (nugetConfig is not null) args.AddRange(["-NuGetConfig", nugetConfig]);
                RunReleaseCommand(args.ToArray(), "multi-version completion", logRoot, runTemp, publicApiRoot);
            });
        ReleaseStageRunner.Invoke(stages);

        RunReleaseCommand(["preflight"], "release preflight", logRoot, runTemp, publicApiRoot);
        RunReleaseCommand(["validate-bundle", "-Strict"], "strict delivery validation", logRoot, runTemp, publicApiRoot);
        var deliveryPath = Path.Combine(Root, "manifest/delivery.json");
        using (var delivery = JsonDocument.Parse(File.ReadAllText(deliveryPath)))
            if (string.IsNullOrWhiteSpace(GetJsonString(delivery.RootElement, "multiVersionBuildSha256")))
                throw new ReleaseException("A tested eight-version build is required for publication; run build-multi-version -Test");

        var plan = ReleasePlan(options);
        WriteTierRecord(plan, passed: false);
        var candidateOutput = Path.Combine(logRoot, "candidate");
        RunReleaseExternal(python, [Path.Combine(Root, "scripts/build/Package-Release.py"), "--local", "--output-directory", candidateOutput],
            "candidate-package", logRoot, runTemp, publicApiRoot);
        RunProductionCandidateChecks(plan, options, publicApiRoot, logRoot, candidateOutput, runTemp, python);
        WriteTierRecord(plan, passed: true);

        var paths = EnumerateReleaseChanges(Root, git);
        if (!options.Has("Resume") || paths.Length != 0)
        {
            var head = CommitReleaseChanges(Root, git, "Release " + version + ": " + summary);
            Console.WriteLine("Release commit created: " + head);
        }
        var gitExecutable = ResolveApplication(git);
        var packageDir = Path.Combine(Root, "bin-build/releases", "v" + version);
        var packageName = ReadDeliveryPackage(deliveryPath);
        RunReleaseExternal(python, [Path.Combine(Root, "scripts/build/Package-Release.py"), "--git", gitExecutable], "package", logRoot, runTemp, publicApiRoot);
        ReleaseCheckPolicy.Load(Root).RequireFullPackage(Path.Combine(packageDir, packageName + ".zip"));
        RunReleaseExternal(python, [Path.Combine(Root, "scripts/checks/Verify-ReleaseAsset.py"), Path.Combine(packageDir, packageName + ".zip"), "--git", gitExecutable], "verify-package", logRoot, runTemp, publicApiRoot);
        if (options.Has("NoPush")) { Console.WriteLine("stopped before push (-NoPush)"); return 0; }
        RunGitChecked(git, ["push", "origin", "master"], "push master");
        var headSha = GitText(git, ["rev-parse", "HEAD"]);
        var apiBase = options.Get("ApiBaseUrl", "https://api.github.com");
        if (!options.Has("NoWait")) WaitForWorkflows(["validate-bundle", "offline-checks"], headSha, apiBase, token,
            options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), int.Parse(options.Get("CiTimeoutMinutes", "30"), System.Globalization.CultureInfo.InvariantCulture));
        else Console.WriteLine("not waiting for CI (-NoWait)");
        if (options.Has("NoTag")) { Console.WriteLine("stopped before tag (-NoTag)"); return 0; }

        var tag = "v" + version;
        var existingTag = GitText(git, ["tag", "-l", tag], allowEmpty: true);
        if (existingTag.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains(tag, StringComparer.Ordinal))
        {
            var taggedHead = GitText(git, ["rev-list", "-n", "1", tag]);
            if (taggedHead != headSha) throw new ReleaseException("local tag " + tag + " points at " + taggedHead + ", not HEAD " + headSha);
            Console.WriteLine("tag " + tag + " already exists on HEAD");
        }
        else
        {
            var tagMessage = Path.Combine(runTemp, "release-tag-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(tagMessage, tag + ": " + summary, new UTF8Encoding(false));
            try { RunGitChecked(git, ["tag", "-a", tag, "-F", tagMessage, headSha], "create annotated release tag"); }
            finally { if (File.Exists(tagMessage)) File.Delete(tagMessage); }
        }
        RunGitChecked(git, ["push", "origin", tag], "push release tag");
        var publish = new PublishOptions(version, token, options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), apiBase,
            DraftOnly: false, DeleteDraft: false, GitExecutable: git);
        ReleasePublisher.PublishAsync(Root, publish).GetAwaiter().GetResult();
        var publishedPath = Path.Combine(packageDir, "published-release.json");
        if (!File.Exists(publishedPath)) throw new ReleaseException("Release publisher left no published-release.json");
        using (var published = JsonDocument.Parse(File.ReadAllText(publishedPath))) Console.WriteLine("published: " + GetJsonString(published.RootElement, "html_url"));
        if (!options.Has("NoWait")) WaitForWorkflows(["Verify " + tag], headSha, apiBase, token,
            options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), int.Parse(options.Get("CiTimeoutMinutes", "30"), System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"DONE {tag}. Record publication in docs/development/handoff.md and handoff-checklist.md, then commit that separately.");
        _ = paths;
        return 0;
    }

    internal static string[] EnumerateReleaseChanges(string root, string git)
    {
        var tracked = ProcessRunner.Run(git, ["-C", root, "diff", "--name-only"], root);
        ProcessRunner.RequireSuccess(tracked, "Could not enumerate tracked release changes");
        var roots = new[] { "docs", "src", "tests", "third_party", "build-tools", "plugin", "scripts", "templates", "hooks", "manifest", "reference", ".claude-plugin", ".github" };
        var untracked = ProcessRunner.Run(git, ["-C", root, "ls-files", "--others", "--exclude-standard", "--", .. roots], root);
        ProcessRunner.RequireSuccess(untracked, "Could not enumerate new release files");
        return (tracked.StandardOutput + "\n" + untracked.StandardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string CommitReleaseChanges(string root, string git, string message)
    {
        var stagedBefore = ProcessRunner.Run(git, ["-C", root, "diff", "--cached", "--name-only"], root);
        ProcessRunner.RequireSuccess(stagedBefore, "Could not inspect Git index");
        if (stagedBefore.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length != 0)
            throw new ReleaseException("Git index must be clean before release staging");
        var paths = EnumerateReleaseChanges(root, git);
        if (paths.Length == 0) throw new ReleaseException("nothing to commit - did the version bump run?");
        RunGitAtRoot(root, git, ["add", "--", .. paths], "stage release files");
        var staged = ProcessRunner.Run(git, ["-C", root, "diff", "--cached", "--name-only"], root);
        ProcessRunner.RequireSuccess(staged, "Could not inspect staged release files");
        var stagedPaths = staged.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var binaries = stagedPaths.Where(path => (path.StartsWith("runtime/", StringComparison.Ordinal) && path != "runtime/README.md") || path == "TiaOpenness.exe").ToArray();
        if (binaries.Length != 0)
        {
            RunGitAtRoot(root, git, ["reset", "-q"], "clear rejected release staging");
            throw new ReleaseException("binaries must not be committed: " + string.Join(", ", binaries));
        }
        if (stagedPaths.Length == 0) throw new ReleaseException("nothing to commit - did the version bump run?");
        var status = ProcessRunner.Run(git, ["-C", root, "status", "--porcelain"], root);
        ProcessRunner.RequireSuccess(status, "Could not verify the release worktree");
        var statusRows = status.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("??", StringComparison.Ordinal)).ToArray();
        if (statusRows.Length != stagedPaths.Length || statusRows.Any(line => line.Length < 2 || line[1] != ' ')) throw new ReleaseException("Worktree contains unstaged changes after release staging");
        var messageFile = Path.Combine(Path.GetTempPath(), "release-msg-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(messageFile, message, new UTF8Encoding(false));
        try { RunGitAtRoot(root, git, ["commit", "-q", "-F", messageFile], "release commit"); }
        finally { if (File.Exists(messageFile)) File.Delete(messageFile); }
        var dirty = ProcessRunner.Run(git, ["-C", root, "status", "--porcelain"], root);
        ProcessRunner.RequireSuccess(dirty, "Could not verify post-commit worktree");
        if (dirty.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("??", StringComparison.Ordinal)))
            throw new ReleaseException("working tree still dirty after the release commit");
        return GitAtRootText(root, git, ["rev-parse", "HEAD"]);
    }

    private static void RunReleaseEarlyGates(string version, string python, string v21Api, string apiRoot, string runTemp, string? nugetConfig)
    {
        TestReleaseDocumentation(version, requireNewest: true);
        var outputDirectory = Path.Combine(Root, "bin-build/release-early-gates", Guid.NewGuid().ToString("N"));
        var cliHome = Path.Combine(outputDirectory, "dotnet-home");
        Directory.CreateDirectory(cliHome);
        RunReleaseSpec("repository, links, bundle layout and delivery set", python, [Path.Combine(Root, "scripts/checks/Check-Repository.py"), "--no-binaries"], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseSpec("dead tool references", python, [Path.Combine(Root, "scripts/checks/Check-DeadToolReferences.py")], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseCommand(["validate-bundle", "-Strict", "-NoBinaries", "-SkipSourceHashes", "-PendingRelease", version], "repository bundle validation before build", outputDirectory, runTemp, apiRoot);
        RunReleaseSpec("bundle layout self-test", python, [Path.Combine(Root, "scripts/checks/Check-BundleLayout.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseSpec("tool usage catalog", python, [Path.Combine(Root, "scripts/generate/Generate-ToolUsage.py"), "--check"], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseSpec("version catalog wiring", python, [Path.Combine(Root, "scripts/checks/Test-VersionCatalogWiring.py")], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseSpec("native supervisor safety", python, [Path.Combine(Root, "scripts/checks/Test-NativeLifecycle.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        RunReleaseSpec("native MCP safety", python, [Path.Combine(Root, "scripts/checks/Test-NativeMcpSession.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        RunSuiteForReleaseGates("write-guard", python, outputDirectory, runTemp, cliHome, apiRoot, nugetConfig);
        RunSuiteForReleaseGates("crash-evidence", python, outputDirectory, runTemp, cliHome, apiRoot, nugetConfig);
        RunReleaseSpec("release approval gate self-test", python, [Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var harness = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe");
        if (!File.Exists(harness))
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";
            var project = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj");
            RunReleaseRaw(dotnet, ["build", project, "-c", "Release", "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"], Path.Combine(outputDirectory, "build-harness.log"), runTemp, cliHome, apiRoot, null, "HTTP harness build");
        }
        var engine = Path.Combine(Root, "runtime/v21/TiaMcp.Engine.V21.exe");
        if (File.Exists(engine))
        {
            RunReleaseTableStep("download-route", harness, [engine, "test-download-route", v21Api], outputDirectory, runTemp, cliHome, apiRoot);
            RunReleaseTableStep("match-plc-name", harness, [engine, "test-match-plc-name"], outputDirectory, runTemp, cliHome, apiRoot);
        }
        else Console.WriteLine("Existing V21 engine unavailable; production assembly fixtures run in build-release after compilation.");
    }

    private static void RunSuiteForReleaseGates(string suite, string python, string output, string runTemp, string cliHome, string apiRoot, string? nugetConfig)
    {
        var results = Path.Combine(output, "suites");
        Directory.CreateDirectory(results);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";
        if (string.IsNullOrWhiteSpace(nugetConfig)) throw new ReleaseException("release checks require an offline NuGet config; set TIA_MCP_OFFLINE_NUGET_CONFIG");
        var project = suite == "write-guard"
            ? Path.Combine(Root, "tests/Tools/TiaMcp.ShippedTools.Tests/TiaMcp.ShippedTools.Tests.csproj")
            : Path.Combine(Root, "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj");
        RunReleaseRaw(dotnet, ["restore", project, "--configfile", Path.GetFullPath(nugetConfig), "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"],
            Path.Combine(output, "restore-" + suite + ".log"), runTemp, cliHome, apiRoot, nugetConfig, "restore " + suite + " suite");
        var args = new[] { Path.Combine(Root, "scripts/checks/Test-DotnetSuites.py"), "--suite", suite, "--dotnet", dotnet, "--no-restore", "--results-directory", results };
        var result = RunBuildIsolated(python, args, Path.Combine(output, suite + ".log"), runTemp, cliHome, apiRoot, null, false, suite);
        ProcessRunner.RequireSuccess(result, suite + " suite failed");
        _ = ReadSuitePassed(results, suite);
    }

    private static void RunReleaseSpec(string name, string executable, IEnumerable<string> args, string output, string runTemp, string cliHome, string apiRoot)
    {
        var result = RunBuildIsolated(executable, args, Path.Combine(output, Regex.Replace(name, "[^A-Za-z0-9._-]", "_") + ".log"), runTemp, cliHome, apiRoot, null, false, name);
        ProcessRunner.RequireSuccess(result, name + " failed");
    }

    private static void RunReleaseTableStep(string name, string executable, IEnumerable<string> args, string output, string runTemp, string cliHome, string apiRoot)
    {
        var spec = ReleaseCommandTable.Get(name);
        var result = RunBuildIsolated(executable, args, Path.Combine(output, spec.LogFile), runTemp, cliHome, apiRoot, null, false, name);
        ProcessRunner.RequireSuccess(result, name + " failed");
    }

    private static void RunReleaseCommand(string[] args, string description, string output, string runTemp, string apiRoot)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";
        var command = new List<string> { typeof(ReleaseCommands).Assembly.Location };
        command.AddRange(args);
        var cliHome = Path.Combine(output, "dotnet-home");
        Directory.CreateDirectory(cliHome);
        RunReleaseRaw(dotnet, command, Path.Combine(output, Regex.Replace(description, "[^A-Za-z0-9._-]", "_") + ".log"), runTemp, cliHome, apiRoot,
            Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG"), description);
    }

    private static void RunReleaseExternal(string executable, string[] args, string name, string output, string runTemp, string apiRoot)
    {
        var cliHome = Path.Combine(output, "external-home");
        Directory.CreateDirectory(cliHome);
        var result = RunBuildIsolated(executable, args, Path.Combine(output, name + ".log"), runTemp, cliHome, apiRoot, null, false, name);
        ProcessRunner.RequireSuccess(result, name + " failed");
    }

    private static void RunReleaseRaw(string executable, IEnumerable<string> args, string log, string runTemp, string cliHome, string apiRoot, string? nuget, string description)
    {
        var result = RunBuildIsolated(executable, args, log, runTemp, cliHome, apiRoot, nuget, false, description);
        ProcessRunner.RequireSuccess(result, description + " failed; see " + log);
    }

    private static string ResolveReleaseApiRoot(Options options)
    {
        var explicitRoot = options.Get("PublicApiRoot");
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);
        if (options.Get("V20ReferenceRoot") is { } v20 && Directory.Exists(v20)) return Directory.GetParent(Directory.GetParent(Path.GetFullPath(v20))!.FullName)!.FullName;
        if (options.Get("V21ReferenceRoot") is { } v21 && Directory.Exists(v21)) return Directory.GetParent(Directory.GetParent(Directory.GetParent(Path.GetFullPath(v21))!.FullName)!.FullName)!.FullName;
        var siblingSdk = Path.Combine(Directory.GetParent(Root)!.FullName, "sdk");
        return Directory.Exists(siblingSdk) ? siblingSdk : Directory.GetParent(Root)!.FullName;
    }

    private static string ResolveReleaseApi(string? supplied, string apiRoot, int major)
    {
        var path = supplied ?? Path.Combine(apiRoot, major == 20 ? "TIA_V20_PublicAPI/V20" : "TIA_V21_PublicAPI/V21/net48");
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new ReleaseException($"Missing V{major} PublicAPI directory: {path}");
        return path;
    }

    private static string CreateReleaseTempRoot()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT") ?? Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        var repo = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(repo, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release temp root must be outside the repository: " + root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool IsResumableRelease(string git, string version)
    {
        var log = ProcessRunner.Run(git, ["-C", Root, "log", "--pretty=%H %s", "-20"], Root);
        ProcessRunner.RequireSuccess(log, "Could not inspect release history");
        var commit = log.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains(" Release " + version + ":", StringComparison.Ordinal) || line.Contains(" Release " + version + " (3/3)", StringComparison.Ordinal));
        if (commit is null) throw new ReleaseException("-Resume needs the Release " + version + " commit within the last 20 commits");
        var status = ProcessRunner.Run(git, ["-C", Root, "status", "--porcelain"], Root);
        ProcessRunner.RequireSuccess(status, "Could not inspect release worktree");
        if (status.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("??", StringComparison.Ordinal)))
            throw new ReleaseException("-Resume needs a clean tree");
        var engine = BuildEngineReuseReason(version);
        var multi = BuildMultiReuseReason(version);
        if (engine.Length != 0 || multi.Length != 0) { Console.WriteLine("Resume rebuild required; engine: " + engine + "; multi-version: " + multi); return false; }
        Console.WriteLine("resuming after the release commit (" + commit[..7] + "); the tag goes on HEAD");
        return true;
    }

    private static string BuildEngineReuseReason(string version)
    {
        var path = Path.Combine(Root, "manifest/release-build.json");
        if (!File.Exists(path)) return "release build record missing";
        using var record = JsonDocument.Parse(File.ReadAllText(path));
        return ReleaseRecords.EngineReuseReason(Root, version, record.RootElement, ReleaseRecords.GetSources(Root, "engine"));
    }

    private static string BuildMultiReuseReason(string version)
    {
        var path = Path.Combine(Root, "manifest/multi-version-build.json");
        if (!File.Exists(path)) return "multi-version build record missing";
        using var record = JsonDocument.Parse(File.ReadAllText(path));
        if (GetJsonString(record.RootElement, "tier") != "full") return "full multi-version checks are required";
        return ReleaseRecords.ReuseReason(Root, record.RootElement, version, ReleaseRecords.GetSources(Root, "multi"), "files");
    }

    private static JsonElement? TryReadBuildRecord(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static void BindReusedMultiVersionToDelivery(string version)
    {
        var reason = BuildMultiReuseReason(version);
        if (reason.Length != 0) throw new ReleaseException("Reused multi-version build changed during delivery preparation: " + reason);
        var recordPath = Path.Combine(Root, "manifest/multi-version-build.json");
        var deliveryPath = Path.Combine(Root, "manifest/delivery.json");
        var delivery = JsonNode.Parse(File.ReadAllText(deliveryPath))?.AsObject() ?? throw new ReleaseException("manifest/delivery.json is invalid");
        delivery["multiVersionBuildSha256"] = ReleaseRecords.HashFile(recordPath);
        delivery["releaseKeys"] = new JsonArray("14sp1", "15.1", "16", "17", "18", "19", "20", "21");
        WriteJson(deliveryPath, delivery);
    }

    private static void BumpReleaseVersion(string version)
    {
        var props = Path.Combine(Root, "Version.props");
        var previous = Regex.Match(File.ReadAllText(props), "<TiaMcpRelease>(\\d+\\.\\d+\\.\\d+)</TiaMcpRelease>").Groups[1].Value;
        if (previous.Length == 0) throw new ReleaseException("Cannot read TiaMcpRelease from Version.props");
        if (previous == version) Console.WriteLine("version already " + version + " in Version.props; bump skipped");
        else
        {
            ReplaceReleaseOnce(Path.Combine(Root, ".claude-plugin/plugin.json"), "\"version\": \"" + previous + "\"", "\"version\": \"" + version + "\"", "plugin.json");
            ReplaceReleaseOnce(props, "<TiaMcpRelease>" + previous + "</TiaMcpRelease>", "<TiaMcpRelease>" + version + "</TiaMcpRelease>", "Version.props");
            ReplaceReleaseOnce(Path.Combine(Root, "docs/README.md"), "[当前版本说明](releases/v" + previous + ".md)", "[当前版本说明](releases/v" + version + ".md)", "docs/README.md current release link");
            var roadmapPath = Path.Combine(Root, "docs/development/roadmap.md");
            var roadmap = File.ReadAllText(roadmapPath);
            var title = Regex.Match(roadmap, "(?m)^# 路线图与待办（[^）]*，" + Regex.Escape(previous) + " 更新）");
            if (!title.Success) title = Regex.Match(roadmap, "(?m)^# 路线图与待办（v" + Regex.Escape(previous) + "）");
            if (title.Success) ReplaceReleaseOnce(roadmapPath, title.Value, title.Value.Replace(previous + " 更新", version + " 更新", StringComparison.Ordinal).Replace("（v" + previous + "）", "（v" + version + "）", StringComparison.Ordinal), "roadmap title");
            else if (!roadmap.Contains(version + " 更新）", StringComparison.Ordinal)) Console.WriteLine("WARNING: roadmap title not bumped (pattern not found) - fix by hand");
        }
    }

    private static void ReplaceReleaseOnce(string path, string oldValue, string newValue, string description)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = File.ReadAllText(path);
        var count = Regex.Matches(text, Regex.Escape(oldValue)).Count;
        if (count == 0 && text.Contains(newValue, StringComparison.Ordinal)) { Console.WriteLine("  already applied: " + description); return; }
        if (count != 1) throw new ReleaseException("expected exactly one occurrence for " + description + " in " + path + ", found " + count);
        var encoding = new UTF8Encoding(bom);
        var normalizedOld = oldValue;
        var normalizedNew = newValue;
        if (text.Contains("\r\n", StringComparison.Ordinal)) { normalizedOld = oldValue.Replace("\n", "\r\n", StringComparison.Ordinal); normalizedNew = newValue.Replace("\n", "\r\n", StringComparison.Ordinal); }
        File.WriteAllText(path, text.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal), encoding);
        Console.WriteLine("  bumped: " + description);
    }

    private static void TestReleaseDocumentation(string version, bool requireNewest)
    {
        var props = XDocument.Load(Path.Combine(Root, "Version.props"));
        var actual = (string?)props.Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (actual != version) throw new ReleaseException("Early gates require Version.props to match -Version (run after the mechanical bump)");
        var entry = Regex.Match(File.ReadAllText(Path.Combine(Root, "CHANGELOG.md")), "(?m)^## \\[(\\d+\\.\\d+\\.\\d+)\\]");
        if (!entry.Success || !Version.TryParse(entry.Groups[1].Value, out var changelog) || !Version.TryParse(version, out var requested) || changelog < requested || (requireNewest && entry.Groups[1].Value != version))
            throw new ReleaseException("Newest CHANGELOG entry differs from the requested release");
        if (!File.Exists(Path.Combine(Root, "docs/releases", "v" + entry.Groups[1].Value + ".md"))) throw new ReleaseException("Matching release note missing");
        if (!File.ReadAllText(Path.Combine(Root, "docs/README.md")).Contains("[当前版本说明](releases/v" + version + ".md)", StringComparison.Ordinal)) throw new ReleaseException("docs/README.md current release link is stale");
        if (!Regex.IsMatch(File.ReadAllText(Path.Combine(Root, "docs/development/roadmap.md")), "(?m)^# .*" + Regex.Escape(version))) throw new ReleaseException("Roadmap title is stale");
    }

    private static void RefuseOrStopStrayEngines(bool kill)
    {
        var names = new[] { "TiaMcp.Engine.V20", "TiaMcp.Engine.V21", "TiaMcp.FoundationHost" };
        var processes = names.SelectMany(Process.GetProcessesByName).ToArray();
        if (processes.Length == 0) return;
        if (!kill) throw new ReleaseException("TiaMcp.Engine.V20.exe / TiaMcp.Engine.V21.exe / TiaMcp.FoundationHost.exe is running (PID " + string.Join(',', processes.Select(process => process.Id)) + ") and would lock runtime/v21; stop it or pass -KillStrayEngine");
        foreach (var process in processes) process.Kill(true);
        foreach (var process in processes) process.WaitForExit(2000);
        Console.WriteLine("killed stray engine and FoundationHost processes");
    }

    private static void WaitForWorkflows(string[] workflows, string sha, string apiBase, string token, string repository, int timeoutMinutes)
    {
        if (timeoutMinutes < 1) throw new ReleaseException("CiTimeoutMinutes must be at least 1");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMcp-Release");
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        while (true)
        {
            var states = workflows.ToDictionary(name => name, name => WorkflowState(client, apiBase, repository, sha, name), StringComparer.Ordinal);
            var status = string.Join(" | ", workflows.Select(name => name + "=" + states[name]));
            Console.WriteLine("  CI: " + status);
            if (states.Values.Any(state => state.StartsWith("failed", StringComparison.Ordinal))) throw new ReleaseException("CI failed: " + status);
            if (states.Values.All(state => state == "success")) return;
            if (DateTime.UtcNow >= deadline) throw new ReleaseException($"CI did not finish within {timeoutMinutes} min: {status}");
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private static string WorkflowState(HttpClient client, string apiBase, string repository, string sha, string workflow)
    {
        try
        {
            var uri = apiBase.TrimEnd('/') + "/repos/" + repository.Trim('/') + "/actions/runs?head_sha=" + Uri.EscapeDataString(sha) + "&per_page=50";
            using var response = client.GetAsync(uri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return "api unreachable: HTTP " + (int)response.StatusCode;
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var runs = document.RootElement.GetProperty("workflow_runs").EnumerateArray()
                .Where(run => GetJsonString(run, "name") == workflow).OrderByDescending(run => GetJsonInt(run, "run_number")).ToArray();
            if (runs.Length == 0) return "not listed yet";
            var latest = runs[0];
            if (GetJsonString(latest, "status") != "completed") return GetJsonString(latest, "status") + " (run " + GetJsonInt(latest, "run_number") + ")";
            return GetJsonString(latest, "conclusion") == "success" ? "success" : "failed: " + GetJsonString(latest, "conclusion") + " " + GetJsonString(latest, "html_url");
        }
        catch (Exception ex) { return "api unreachable: " + ex.Message; }
    }

    private static string GitText(string git, string[] args, bool allowEmpty = false)
    {
        var result = ProcessRunner.Run(git, ["-C", Root, .. args], Root);
        if (!allowEmpty) ProcessRunner.RequireSuccess(result, "Git " + string.Join(' ', args));
        else if (result.ExitCode != 0) return "";
        return result.StandardOutput.Trim();
    }

    private static string GitAtRootText(string root, string git, string[] args)
    {
        var result = ProcessRunner.Run(git, ["-C", root, .. args], root);
        ProcessRunner.RequireSuccess(result, "Git " + string.Join(' ', args));
        return result.StandardOutput.Trim();
    }

    private static void RunGitChecked(string git, string[] args, string description) => RunGitAtRoot(Root, git, args, description);
    private static void RunGitAtRoot(string root, string git, string[] args, string description)
    {
        var result = ProcessRunner.Run(git, ["-C", root, .. args], root);
        ProcessRunner.RequireSuccess(result, description);
    }

    private static string ReadDeliveryPackage(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var package = GetJsonString(document.RootElement, "package");
        if (package.Length == 0) throw new ReleaseException("manifest/delivery.json has no package name");
        return package;
    }
}

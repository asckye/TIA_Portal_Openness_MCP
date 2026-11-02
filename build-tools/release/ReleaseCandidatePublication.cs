using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class CandidatePublicationGate
{
    internal static void RequireNightly(JsonObject latest, Func<string, bool> isAncestor)
    {
        var revision = ReleaseEvidence.Text(latest["rev"]);
        if (revision.Length == 0) throw new ReleaseException("Nightly release gate lacks its revision");
        if (!isAncestor(revision)) return;
        var passed = ReleaseEvidence.Text(latest["status"]) == "passed" || ReleaseEvidence.Bool(latest["passed"]);
        if (!passed || !ReleaseEvidence.Bool(latest["underThirtyMinutes"]))
            throw new ReleaseException("Latest relevant nightly failed or timed out; a later passing nightly is required (no override)");
    }

    internal static void RequirePublication(IEnumerable<JsonObject> records)
    {
        foreach (var record in records)
            if (ReleaseEvidence.Text(record["status"]) == "failed")
                throw new ReleaseException("Post-publication verification failed for " + ReleaseEvidence.Text(record["version"]) + "; resolve it and rerun release-publication-check");
    }

    internal static string WorkflowStatus(JsonArray runs, string commit, string name, string? title = null)
    {
        var matches = runs.OfType<JsonObject>().Where(run => ReleaseEvidence.Text(run["head_sha"]) == commit &&
            ReleaseEvidence.Text(run["name"]) == name && (title is null || ReleaseEvidence.Text(run["display_title"]) == title))
            .OrderByDescending(run => ReleaseEvidence.Int(run["run_number"])).ThenByDescending(run => ReleaseEvidence.Int(run["run_attempt"])).ToArray();
        if (matches.Length == 0 || ReleaseEvidence.Text(matches[0]["status"]) != "completed") return "pending";
        return ReleaseEvidence.Text(matches[0]["conclusion"]) == "success" ? "passed" : "failed";
    }

    internal static bool JobsPassed(JsonArray jobs) => jobs.Count != 0 && jobs.OfType<JsonObject>().Count() == jobs.Count &&
        jobs.All(job => ReleaseEvidence.Text(job?["status"]) == "completed" && ReleaseEvidence.Text(job?["conclusion"]) == "success");

    internal static JsonObject PublicationFacts(string version, string commit, JsonObject published, JsonObject package)
    {
        var release = ReleaseVersions.Require(version, "Publication version missing");
        var time = ReleaseEvidence.Text(published["published_at"]);
        if (!DateTimeOffset.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ReleaseException("Published release response lacks published_at");
        return new JsonObject
        {
            ["release"] = release.Tag, ["url"] = published["html_url"]?.DeepClone(), ["sourceCommit"] = commit, ["publishedAtUtc"] = time,
            ["formalRelease"] = !release.IsPrerelease, ["prerelease"] = release.IsPrerelease, ["latest"] = !release.IsPrerelease,
            ["package"] = new JsonObject
            {
                ["name"] = Path.GetFileName(ReleaseEvidence.Text(package["path"])), ["bytes"] = package["size"]?.DeepClone(),
                ["sha256"] = package["sha256"]?.DeepClone(), ["files"] = package["files"]?.DeepClone()
            },
            ["assets"] = new JsonArray((published["assets"] as JsonArray ?? []).OfType<JsonObject>().Select(asset => (JsonNode?)new JsonObject
            {
                ["name"] = asset["name"]?.DeepClone(), ["bytes"] = asset["size"]?.DeepClone(), ["digest"] = asset["digest"]?.DeepClone(),
                ["state"] = asset["state"]?.DeepClone(), ["downloadUrl"] = asset["browser_download_url"]?.DeepClone()
            }).ToArray())
        };
    }
}

internal static partial class ReleaseCommands
{
    private static void RequireCandidateReleaseGates(string git)
    {
        var latest = Path.Combine(Root, "bin-build/nightly/latest.json");
        if (!File.Exists(latest)) throw new ReleaseException("Nightly release gate missing: " + latest);
        CandidatePublicationGate.RequireNightly(ReleaseCandidate.Read(latest), revision =>
        {
            var result = ProcessRunner.Run(git, ["merge-base", "--is-ancestor", revision, "HEAD"], Root);
            if (result.ExitCode is not (0 or 1)) ProcessRunner.RequireSuccess(result, "Nightly ancestry");
            return result.ExitCode == 0;
        });
        var directory = Path.Combine(Root, "bin-build/publication-verification");
        CandidatePublicationGate.RequirePublication(Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json").Select(ReleaseCandidate.Read) : []);
        CandidatePublicationGate.RequirePublication(Directory.GetFiles(Path.Combine(Root, "manifest"), "publication-v*.json")
            .Select(ReleaseCandidate.Read).Where(row => row["postPublication"] is JsonObject)
            .Select(row => row["postPublication"]!.AsObject()));
    }

    private static int ReleaseFromCandidate(Options options)
    {
        if (options.Has("SkipBuild") || options.Has("Resume") || options.Has("BumpOnly") || options.Has("EarlyGatesOnly") ||
            options.Has("AdoptEvidence") || options.Has("KillStrayEngine") || ReleaseCheckPolicy.Tier(options.Get("Tier")) != "full")
            throw new ReleaseException("-FromCandidate accepts only verification and publication options");
        var path = Path.GetFullPath(options.Get("FromCandidate")!);
        RequireCheckAllOwnedPath(Root, path);
        var candidate = ReleaseCandidate.Read(path);
        var git = options.Get("Git", "git");
        // A rebuild of this launcher at the records commit may have a new revision stamp.
        // Hand over to the verified private tool copy, after verifying every file, without rebuilding it.
        var toolRows = candidate["toolFiles"] as JsonArray ?? throw new ReleaseException("Candidate tool files missing");
        foreach (var row in toolRows) ReleaseCandidate.VerifyBinding(Root, row);
        var boundTool = toolRows.Single(row => Path.GetFileName(ReleaseEvidence.Text(row?["path"])) == "TiaMcp.ReleaseTool.dll")!;
        if (ReleaseRecords.HashFile(typeof(ReleaseCommands).Assembly.Location) != ReleaseEvidence.Text(boundTool["sha256"]))
        {
            var result = ProcessRunner.Run(Dotnet,
                [ReleaseCandidate.Resolve(Root, ReleaseEvidence.Text(boundTool["path"])), "release", .. options.Arguments()], Root, forwardOutput: true);
            ProcessRunner.RequireSuccess(result, "Verified publication tool"); return 0;
        }
        var directory = Path.Combine(Root, "bin-build/release-verification", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var clock = new ReleaseStageClock(directory);
        clock.Run("candidate-record-bindings", () => ReleaseCandidate.Verify(Root, git, candidate, requireBranch: !options.Has("DryRun")));
        var version = ReleaseVersions.Require(ReleaseEvidence.Text(candidate["version"]), "Candidate version missing");
        if (options.Get("Version") is { } requested && requested != version.Label) throw new ReleaseException("Requested version differs from candidate");
        clock.Run("compare-release-records", () =>
        {
            if (CompareReleaseRecords(Options.Parse([], new HashSet<string>())) != 0) throw new ReleaseException("Candidate release records differ");
        });
        clock.Run("nightly-and-publication-block", () => RequireCandidateReleaseGates(git));
        if (options.Has("DryRun"))
        {
            Console.WriteLine("DRY RUN: candidate, adopted evidence, ZIP, records and publication blocks verified; no network requests or Git writes.");
            clock.Print(); return 0;
        }
        if (!options.Has("ConfirmPublication")) throw new ReleaseException("Maintainer publication confirmation is required: -ConfirmPublication");
        if (options.Has("NoPush") || options.Has("NoTag")) { Console.WriteLine("Verified; stopped before publication (-NoPush/-NoTag)."); return 0; }
        var token = ReleasePrerequisites.GetToken(options.Get("Token", ""), git, Root, localOnly: false);
        ReleasePrerequisites.RequireToken(token);
        RunGitChecked(git, ["fetch", "origin", "--quiet"], "fetch publication branch");
        var commit = ReleaseEvidence.Text(candidate["commit"]);
        if (GitText(git, ["rev-parse", "origin/master"]) != commit) throw new ReleaseException("origin/master differs from the verified candidate commit");
        var tag = version.Tag;
        var existing = GitText(git, ["tag", "-l", tag], allowEmpty: true);
        if (existing.Length != 0)
        {
            if (GitText(git, ["cat-file", "-t", tag]) != "tag" || GitText(git, ["rev-list", "-n", "1", tag]) != commit)
                throw new ReleaseException("Existing tag is not an annotated tag of the verified candidate");
        }
        else
        {
            var message = Path.Combine(directory, "tag-message.txt");
            File.WriteAllText(message, tag + ": verified release candidate", new UTF8Encoding(false));
            RunGitChecked(git, ["tag", "-a", tag, "-F", message, commit], "tag verified candidate");
        }
        RunGitChecked(git, ["push", "origin", tag], "push verified candidate tag");
        clock.Run("upload", () => ReleasePublisher.PublishAsync(Root, new(version.Label, token,
            options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), options.Get("ApiBaseUrl", "https://api.github.com"),
            DraftOnly: false, DeleteDraft: false, GitExecutable: git, CandidatePath: path)).GetAwaiter().GetResult());
        WritePublicationState(version.Label, commit, candidate, Path.GetDirectoryName(path)!);
        Console.WriteLine("Published " + tag + "; run release-publication-check -Version " + version.Label + " afterwards (CI is asynchronous).");
        clock.Print(); return 0;
    }

    private static void WritePublicationState(string version, string commit, JsonObject? candidate, string output)
    {
        var directory = Path.Combine(Root, "bin-build/publication-verification"); Directory.CreateDirectory(directory);
        WriteJson(Path.Combine(directory, "v" + version + ".json"), new JsonObject
        {
            ["version"] = version, ["commit"] = commit, ["status"] = "pending", ["candidate"] = candidate?.DeepClone(),
            ["recordOnlyPreflight"] = ReleaseEvidence.Strings(ReleaseCandidate.RecordChecks), ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
        });
        var published = ReleaseCandidate.Read(Path.Combine(output, "published-release.json"));
        var package = ReleaseCandidate.Read(candidate is null ? Path.Combine(output, "package-result.json")
            : ReleaseCandidate.Resolve(Root, ReleaseEvidence.Text(candidate["packageResult"]?["path"])));
        UpdatePublicationManifest(version, new JsonObject { ["version"] = version, ["commit"] = commit, ["status"] = "pending" }, candidate,
            CandidatePublicationGate.PublicationFacts(version, commit, published, package));
    }

    private static void UpdatePublicationManifest(string version, JsonObject verification, JsonObject? candidate = null, JsonObject? facts = null)
    {
        var path = Path.Combine(Root, "manifest", "publication-v" + version + ".json");
        var record = File.Exists(path) ? ReleaseCandidate.Read(path) : new JsonObject { ["version"] = version, ["commit"] = verification["commit"]?.DeepClone() };
        if (facts is not null) foreach (var (name, value) in facts) record[name] = value?.DeepClone();
        record["postPublication"] = verification.DeepClone();
        if (candidate is not null)
        {
            var adoption = ReleaseCandidate.Read(ReleaseCandidate.Resolve(Root, ReleaseEvidence.Text(candidate["adoption"]?["path"])));
            record["verifiedCandidate"] = new JsonObject
            {
                ["commit"] = candidate["commit"]!.DeepClone(), ["tree"] = candidate["tree"]!.DeepClone(),
                ["packageSha256"] = candidate["package"]!["sha256"]!.DeepClone(),
                ["evidence"] = adoption["record"]!["evidence"]!.DeepClone(),
                ["binaryChecks"] = adoption["record"]?["binaryChecks"]?.DeepClone(),
                ["localChecks"] = adoption["record"]?["localChecks"]?.DeepClone(),
                ["candidateChecks"] = candidate["checks"]!.DeepClone()
            };
        }
        WriteJson(path, record);
    }

    private static int CheckReleasePublication(Options options)
    {
        var version = ReleaseVersions.Require(options.Get("Version"), "release-publication-check requires -Version");
        var path = Path.Combine(Root, "bin-build/publication-verification", version.Tag + ".json");
        var manifest = Path.Combine(Root, "manifest/publication-" + version.Tag + ".json");
        JsonObject record;
        if (File.Exists(path)) record = ReleaseCandidate.Read(path);
        else if (File.Exists(manifest))
        {
            var saved = ReleaseCandidate.Read(manifest);
            record = saved["postPublication"] is JsonObject savedVerification ? savedVerification.DeepClone().AsObject()
                : new JsonObject { ["version"] = version.Label, ["commit"] = saved["sourceCommit"]?.DeepClone(), ["status"] = "pending" };
        }
        else throw new ReleaseException("Publication record missing: " + path);
        var commit = ReleaseEvidence.Text(record["commit"]);
        var token = ReleasePrerequisites.GetToken(options.Get("Token", ""), options.Get("Git", "git"), Root, localOnly: false);
        ReleasePrerequisites.RequireToken(token);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMcp-Release");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var repository = options.Get("Repository", "asckye/TIA_Portal_Openness_MCP");
        var api = options.Get("ApiBaseUrl", "https://api.github.com").TrimEnd('/') + "/repos/" + repository;
        JsonArray Pages(string url, string property)
        {
            var rows = new JsonArray();
            for (var page = 1; page <= 20; page++)
            {
                using var response = client.GetAsync(url + "&page=" + page).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
                var body = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                var items = body[property]!.AsArray();
                foreach (var row in items) rows.Add(row!.DeepClone());
                if (items.Count == 0 || rows.Count >= ReleaseEvidence.Int(body["total_count"])) return rows;
            }
            throw new ReleaseException("Publication workflow listing exceeds twenty pages");
        }
        var runs = Pages(api + "/actions/runs?head_sha=" + Uri.EscapeDataString(commit) + "&per_page=100", "workflow_runs");
        var checks = new JsonObject();
        var proof = new JsonArray();
        foreach (var name in ReleaseEvidence.PushWorkflows.Append("Verify published release"))
        {
            var title = name == "Verify published release" ? "Verify " + version.Tag : null;
            var status = CandidatePublicationGate.WorkflowStatus(runs, commit, name, title);
            var latest = runs.OfType<JsonObject>().Where(run => ReleaseEvidence.Text(run["head_sha"]) == commit && ReleaseEvidence.Text(run["name"]) == name &&
                (title is null || ReleaseEvidence.Text(run["display_title"]) == title))
                .OrderByDescending(run => ReleaseEvidence.Int(run["run_number"])).ThenByDescending(run => ReleaseEvidence.Int(run["run_attempt"])).FirstOrDefault();
            if (status == "passed" && latest is not null)
            {
                var id = ((long?)latest["id"] ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var attempt = Math.Max(1, ReleaseEvidence.Int(latest["run_attempt"]));
                var jobs = Pages(api + "/actions/runs/" + id + "/attempts/" + attempt + "/jobs?per_page=100", "jobs");
                if (!CandidatePublicationGate.JobsPassed(jobs)) status = "failed";
                proof.Add(new JsonObject { ["workflow"] = name, ["runId"] = latest["id"]?.DeepClone(), ["runAttempt"] = attempt, ["jobs"] = jobs });
            }
            checks[title ?? name] = status;
        }
        record["checks"] = checks;
        record["workflowRuns"] = proof;
        var wasFailed = ReleaseEvidence.Text(record["status"]) == "failed";
        record["status"] = checks.Any(pair => ReleaseEvidence.Text(pair.Value) == "failed") ? "failed"
            : checks.All(pair => ReleaseEvidence.Text(pair.Value) == "passed") ? "passed" : "pending";
        if (wasFailed && ReleaseEvidence.Text(record["status"]) == "pending") record["status"] = "failed";
        record["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); WriteJson(path, record);
        var verification = record.DeepClone().AsObject(); verification.Remove("candidate");
        UpdatePublicationManifest(version.Label, verification);
        Console.WriteLine("Post-publication verification: " + ReleaseEvidence.Text(record["status"]) + "; " + path);
        return ReleaseEvidence.Text(record["status"]) == "failed" ? 1 : 0;
    }
}

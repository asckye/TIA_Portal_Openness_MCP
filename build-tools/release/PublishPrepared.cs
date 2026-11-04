using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class PublishPrepared
{
    private static readonly long ProcessStart = Stopwatch.GetTimestamp() -
        (long)((DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds * Stopwatch.Frequency);

    internal static async Task<int> RunAsync(string root, PublishOptions options, HttpClient? client = null,
        Func<string[], int, CommandResult>? git = null, Func<TimeSpan>? elapsed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var budget = new PublicationBudget(elapsed ?? (() => Stopwatch.GetElapsedTime(ProcessStart)), delay ?? Task.Delay);
        try
        {
            budget.RemainingMilliseconds();
            RequirePrerelease(options.Version);
            var package = PreparedCandidate.Read(root, Path.Combine(root, "bin-build/prepared", options.Version, "candidate.json"), options.Version);
            git ??= (args, timeout) => ProcessRunner.Run(options.GitExecutable, args, root,
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never" }, timeoutMilliseconds: timeout);
            var head = git(["rev-parse", "HEAD"], budget.RemainingMilliseconds());
            ProcessRunner.RequireSuccess(head, "git rev-parse HEAD");
            if (!head.StandardOutput.Trim().Equals(package.Commit, StringComparison.Ordinal)) throw new ReleaseException("HEAD differs from candidate commit");
            var status = git(["status", "--porcelain", "--untracked-files=all"], budget.RemainingMilliseconds());
            ProcessRunner.RequireSuccess(status, "git status");
            if (!string.IsNullOrWhiteSpace(status.StandardOutput)) throw new ReleaseException("dirty tree; prepare and commit the source first");
            using var deadline = budget.BeginAttempt();
            await package.VerifyAsync(deadline.Token);
            if (package.ReleaseId <= 0 || package.AssetId <= 0) throw new ReleaseException("no draft recorded in candidate.json; upload the draft first");
            using var ownedClient = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
            var http = client ?? ownedClient!;
            var api = ReleasePublisher.PreparedApi(root, options, http, budget.RemainingMilliseconds());
            var release = await ReleasePublisher.ReadPreparedReleaseAsync(api, package, deadline.Token);
            if (release.Public) budget.State = PublicationBudget.PublicState;
            var tagExists = await ReleasePublisher.PreparedTagExistsAsync(api, package, deadline.Token);
            if (release.Public)
            {
                if (!tagExists) throw new ReleaseException("public release tag is missing");
                await ReleasePublisher.ConfirmPreparedDownloadAsync(http, release.ZipAsset, budget);
            }
            else
            {
                if (tagExists) budget.State = PublicationBudget.TagState;
                ProcessRunner.RequireSuccess(git(["push", "origin", package.Commit + ":refs/heads/master"], budget.RemainingMilliseconds()), "git push master");
                if (budget.Elapsed.TotalSeconds > 150) throw new ReleaseException("150-second tag-start budget exceeded");
                budget.State = PublicationBudget.TagState;
                if (!tagExists) await EnsureTagAsync(api, package, budget);
                await ReleasePublisher.PublishPreparedDraftAsync(api, package, http, budget);
            }
            Console.WriteLine("PUBLISHED v" + options.Version);
            return 0;
        }
        catch (Exception error) when (error is ReleaseException or HttpRequestException or OperationCanceledException)
        {
            throw new ReleaseException(budget.State + ": " + error.Message);
        }
        finally { Console.WriteLine("Total seconds: " + budget.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)); }
    }

    internal static void RequirePrerelease(string version)
    {
        if (!ReleaseVersions.Require(version, "-Version must be X.Y.Z-beta.N or X.Y.Z-rc.N").IsPrerelease)
            throw new ReleaseException("publish-prepared publishes pre-releases only; upload-draft also requires a pre-release");
    }

    private static async Task EnsureTagAsync(ReleasePublisher.GitHubApi api, PreparedCandidate package, PublicationBudget budget)
    {
        using var attempt = budget.BeginAttempt();
        try
        {
            using var created = await api.SendJsonRequiredAsync(HttpMethod.Post, "repos/" + api.Repository + "/git/refs",
                new { @ref = "refs/tags/v" + package.Version, sha = package.Commit }, attempt.Token);
        }
        catch (Exception error) when (error is ReleaseException or HttpRequestException or OperationCanceledException)
        {
            // A failed response does not prove the write failed. Read the ref before deciding whether to stop.
            using var recovery = budget.BeginAttempt();
            if (!await ReleasePublisher.PreparedTagExistsAsync(api, package, recovery.Token)) throw;
        }
    }
}

internal sealed class PublicationBudget(Func<TimeSpan> elapsed, Func<TimeSpan, CancellationToken, Task> delay)
{
    internal const string TagState = "tag exists or creation is uncertain; state unknown or draft not public, rerun this command";
    internal const string PublicState = "public, download not confirmed; rerun this command";
    internal string State { get; set; } = "nothing published";
    internal TimeSpan Elapsed => elapsed();
    internal int RemainingMilliseconds()
    {
        var remaining = TimeSpan.FromSeconds(300) - Elapsed;
        if (remaining <= TimeSpan.Zero) throw new ReleaseException("300-second budget exhausted");
        return (int)Math.Ceiling(remaining.TotalMilliseconds);
    }
    internal CancellationTokenSource BeginAttempt(bool firstFlip = false) =>
        new(firstFlip ? 30000 : RemainingMilliseconds());
    internal async Task DelayAsync(TimeSpan duration)
    {
        using var remaining = BeginAttempt();
        var capped = TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds, RemainingMilliseconds()));
        await delay(capped, remaining.Token);
        RemainingMilliseconds();
    }
}

internal sealed record PreparedCandidate(string CandidatePath, JsonObject Candidate, string Zip, string Digest,
    long Size, string Commit, string Version, long ReleaseId, long AssetId)
{
    internal string Sidecar => Path.ChangeExtension(Zip, ".sha256");

    internal static PreparedCandidate Read(string root, string path, string version)
    {
        if (!File.Exists(path)) throw new ReleaseException("no candidate: " + path);
        var candidate = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject() ?? throw new ReleaseException("Invalid candidate.json");
        string Text(string name) => candidate[name]?.GetValue<string>() ?? "";
        long Number(string name) => candidate[name]?.GetValue<long>() ?? 0;
        if (Text("version") != version) throw new ReleaseException("candidate version differs from -Version");
        var zip = Text("zip");
        if (!Path.IsPathRooted(zip))
        {
            var beside = Path.Combine(Path.GetDirectoryName(path)!, zip);
            zip = File.Exists(beside) ? beside : Path.Combine(root, zip);
        }
        if (Text("commit").Length != 40 || Text("commit").Any(c => !char.IsAsciiHexDigit(c))) throw new ReleaseException("Invalid candidate commit");
        return new(path, candidate, Path.GetFullPath(zip), Text("zipSha256"), Number("zipSize"), Text("commit"), version,
            Number("draftReleaseId"), Number("draftAssetId"));
    }

    internal async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Zip) || new FileInfo(Zip).Length != Size) throw new ReleaseException("ZIP hash differs from candidate (missing file or size)");
        await using var stream = File.OpenRead(Zip);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!digest.Equals(Digest, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("ZIP hash differs from candidate");
        var expected = Digest + "  " + Path.GetFileName(Zip);
        if (!File.Exists(Sidecar) || !string.Equals((await File.ReadAllTextAsync(Sidecar, cancellationToken)).Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("ZIP hash differs from .sha256 sidecar");
    }
}

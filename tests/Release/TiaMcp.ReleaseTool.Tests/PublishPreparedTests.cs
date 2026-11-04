using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PublishPreparedTests
{
    [Theory]
    [InlineData("missing", "no candidate")]
    [InlineData("head", "HEAD differs")]
    [InlineData("dirty", "dirty tree")]
    [InlineData("hash", "ZIP hash differs")]
    [InlineData("local-sidecar", "sidecar")]
    [InlineData("no-draft", "no draft")]
    [InlineData("404", "no draft")]
    [InlineData("size", "asset size or digest differs")]
    [InlineData("digest", "asset size or digest differs")]
    [InlineData("sidecar", "asset size or digest differs")]
    [InlineData("duplicate", "asset size or digest differs")]
    [InlineData("asset-id", "asset size or digest differs")]
    [InlineData("state", "asset size or digest differs")]
    public async Task RefusalsNeverPushTagOrPublish(string fault, string message)
    {
        using var f = new Fixture { Fault = fault };
        if (fault == "missing") File.Delete(f.CandidatePath);
        if (fault == "hash") { var bytes = File.ReadAllBytes(f.ZipPath); bytes[0] ^= 1; File.WriteAllBytes(f.ZipPath, bytes); }
        if (fault == "local-sidecar") File.WriteAllText(f.Sidecar, new string('0', 64) + "  pkg.zip\n");
        if (fault == "no-draft") f.SaveCandidate(false);
        var error = await Assert.ThrowsAsync<ReleaseException>(f.Publish);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("push", f.Events);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
    }

    [Fact]
    public async Task VerifiesBeforePushAndUsesThePublishedUrlNeverTheDeadDraftUrl()
    {
        using var f = new Fixture();
        Assert.Equal(0, await f.Publish());
        Assert.Equal(new[] { "head", "status", "release", "ref", "push", "tag", "publish", "reachable" }, f.Events);
        Assert.Equal("refs/tags/v4.0.0-beta.3", f.Tag!["ref"]!.GetValue<string>());
        Assert.Equal(Fixture.Commit, f.Tag["sha"]!.GetValue<string>());
        Assert.False(f.Patch!["draft"]!.GetValue<bool>());
        Assert.True(f.Patch["prerelease"]!.GetValue<bool>());
        Assert.Equal("false", f.Patch["make_latest"]!.GetValue<string>());
        Assert.DoesNotContain("dead-draft-url", f.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentTagCommitRefusesBeforePushIncludingAnnotatedTags(bool annotated)
    {
        using var f = new Fixture { TagCommit = "different", Annotated = annotated };
        Assert.Contains("tag", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("push", f.Events);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPreparedTagContinuesWithoutAnotherPost(bool annotated)
    {
        using var f = new Fixture { TagCommit = Fixture.Commit, Annotated = annotated };
        Assert.Equal(0, await f.Publish());
        Assert.DoesNotContain("tag", f.Events);
        Assert.Contains("publish", f.Events);
    }

    [Fact]
    public async Task LostTagResponseReadsTheRefAndContinues()
    {
        using var f = new Fixture { Fault = "lost-tag" };
        Assert.Equal(0, await f.Publish());
        Assert.Equal(2, f.Events.Count(e => e == "ref"));
        Assert.Single(f.Events, e => e == "tag");
        Assert.Contains("publish", f.Events);
    }

    [Fact]
    public async Task FailedTagCreationWithNoRefReportsRecoveryStateAndDoesNotFlip()
    {
        using var f = new Fixture { Fault = "tag-fail" };
        Assert.Contains("state unknown or draft not public", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Equal(2, f.Events.Count(e => e == "ref"));
        Assert.DoesNotContain("publish", f.Events);
    }

    [Fact]
    public async Task FailedFlipCanBeCompletedByRerunningTheSameCommand()
    {
        using var f = new Fixture { FlipFailures = 3 };
        var error = await Assert.ThrowsAsync<ReleaseException>(f.Publish);
        Assert.Contains("state unknown or draft not public, rerun this command", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, f.Events.Count(e => e == "publish"));
        Assert.Equal(new[] { 5d, 5d }, f.Delays.Select(d => d.TotalSeconds));
        Assert.Equal(0, await f.Publish());
        Assert.Single(f.Events, e => e == "tag");
    }

    [Fact]
    public async Task FlipRetriesTwiceThenSucceeds()
    {
        using var f = new Fixture { FlipFailures = 2 };
        Assert.Equal(0, await f.Publish());
        Assert.Equal(3, f.Events.Count(e => e == "publish"));
        Assert.Equal(new[] { 5d, 5d }, f.Delays.Select(d => d.TotalSeconds));
    }

    [Theory]
    [InlineData("published")]
    [InlineData("lost-flip")]
    public async Task PublicRerunOnlyConfirmsCurrentAssetsAndTheirUrl(string mode)
    {
        using var f = new Fixture { Public = mode == "published", TagCommit = Fixture.Commit };
        if (mode == "lost-flip")
        {
            f.Fault = mode;
            f.ExpireAt = "publish";
            await Assert.ThrowsAsync<ReleaseException>(f.Publish);
            Assert.True(f.Public);
            f.Events.Clear(); f.Fault = ""; f.ExpireAt = ""; f.Seconds = 0;
        }
        Assert.Equal(0, await f.Publish());
        Assert.Equal(new[] { "head", "status", "release", "ref", "reachable" }, f.Events);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("different-tag")]
    [InlineData("missing-tag")]
    public async Task PublicReleaseWithDifferentAssetsOrTagRefusesWithoutWrites(string fault)
    {
        using var f = new Fixture { Public = true, TagCommit = fault == "missing-tag" ? null : fault == "different-tag" ? "different" : Fixture.Commit, Fault = fault };
        await Assert.ThrowsAsync<ReleaseException>(f.Publish);
        Assert.DoesNotContain("push", f.Events);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
        Assert.DoesNotContain("reachable", f.Events);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("release")]
    [InlineData("push")]
    public async Task ExpiredBudgetBeforeTagNeverPublishes(string expireAt)
    {
        using var f = new Fixture { ExpireAt = expireAt };
        Assert.Contains("budget", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(151, false)]
    public async Task TagStartsOnlyWithinThe150SecondWindow(int seconds, bool allowed)
    {
        using var f = new Fixture { Seconds = seconds };
        if (allowed) Assert.Equal(0, await f.Publish());
        else Assert.Contains("150-second", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Equal(allowed, f.Events.Contains("tag"));
    }

    [Fact]
    public async Task FirstFlipStillRunsAfterTagButExpiredBudgetPreventsReachability()
    {
        using var f = new Fixture { ExpireAt = "tag" };
        var error = await Assert.ThrowsAsync<ReleaseException>(f.Publish);
        Assert.Single(f.Events, e => e == "publish");
        Assert.DoesNotContain("reachable", f.Events);
        Assert.Contains("public, download not confirmed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredBudgetAfterFailedFirstFlipPreventsRetryAndNamesUnknownState()
    {
        using var f = new Fixture { FlipFailures = 3, ExpireAt = "publish" };
        Assert.Contains("state unknown or draft not public", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Single(f.Events, e => e == "publish");
        Assert.Empty(f.Delays);
    }

    [Fact]
    public async Task RetryDelayIsCappedAtTheTimeRemaining()
    {
        using var f = new Fixture { FlipFailures = 3, FirstFlipSeconds = 298 };
        Assert.Contains("state unknown or draft not public", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(f.Delays));
        Assert.Equal(300, f.Seconds);
        Assert.Single(f.Events, e => e == "publish");
    }

    [Fact]
    public async Task DownloadRetriesUsePublishedUrlAndThreeSecondDelays()
    {
        using var f = new Fixture { DownloadFailures = 4 };
        Assert.Equal(0, await f.Publish());
        Assert.Equal(5, f.Events.Count(e => e == "reachable"));
        Assert.Equal(new[] { 3d, 3d, 3d, 3d }, f.Delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task UnconfirmedDownloadReportsLiveReleaseAndCanBeRerun()
    {
        using var f = new Fixture { DownloadFailures = 5 };
        Assert.Contains("public, download not confirmed", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Equal(5, f.Events.Count(e => e == "reachable"));
        f.Events.Clear();
        Assert.Equal(0, await f.Publish());
        Assert.DoesNotContain("push", f.Events);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
    }

    [Fact]
    public async Task ReachabilityStopsAtTheHardDeadline()
    {
        using var f = new Fixture { DownloadFailures = 5, FirstFlipSeconds = 299 };
        Assert.Contains("public, download not confirmed", (await Assert.ThrowsAsync<ReleaseException>(f.Publish)).Message, StringComparison.Ordinal);
        Assert.Equal(300, f.Seconds);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(f.Delays));
        Assert.Single(f.Events, e => e == "reachable");
    }

    [Fact]
    public async Task FailedPushDoesNotCreateTag()
    {
        using var f = new Fixture { Fault = "push" };
        await Assert.ThrowsAsync<ReleaseException>(f.Publish);
        Assert.DoesNotContain("tag", f.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadCreatesOrReusesADraftAndRemovesStaleDuplicates(bool reuse)
    {
        using var f = new Fixture { Reuse = reuse };
        f.Assets.Add(f.Asset(10, "pkg.zip", [1]));
        f.Assets.Add(f.Asset(11, "pkg.zip", [2]));
        f.Assets.Add(f.Asset(12, "unrelated.txt", [3]));
        await f.Upload();
        Assert.Equal(new[] { "pkg.zip", "pkg.sha256" }, f.Assets.Select(a => a["name"]!.GetValue<string>()));
        Assert.Equal("master", f.DraftBody!["target_commitish"]!.GetValue<string>());
        Assert.True(f.DraftBody["draft"]!.GetValue<bool>());
        Assert.Equal("false", f.DraftBody["make_latest"]!.GetValue<string>());
        Assert.Contains("https://fixture.invalid/acme/project/blob/" + Fixture.Commit + "/docs/releases/../guides/check.md", f.DraftBody["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(Fixture.Hash(File.ReadAllBytes(f.ZipPath)), f.DraftBody["body"]!.GetValue<string>(), StringComparison.Ordinal);
        var candidate = JsonNode.Parse(File.ReadAllText(f.CandidatePath))!;
        Assert.Equal(42, candidate["draftReleaseId"]!.GetValue<int>());
        Assert.Equal(f.Assets[0]["id"]!.GetValue<long>(), candidate["draftAssetId"]!.GetValue<long>());
        Assert.Equal(123, candidate["fileCount"]!.GetValue<int>());
        Assert.DoesNotContain("push", f.Events);
        Assert.DoesNotContain("tag", f.Events);
        Assert.DoesNotContain("publish", f.Events);
    }

    [Fact]
    public async Task FailedUploadRetriesAndVerifiedZipIsNotUploadedAgain()
    {
        using var f = new Fixture { Reuse = true, UploadFailures = 1 };
        f.Assets.Add(f.Asset(50, "pkg.zip", File.ReadAllBytes(f.ZipPath)));
        await f.Upload();
        Assert.DoesNotContain("upload:pkg.zip", f.Events);
        Assert.Equal(2, f.Events.Count(e => e == "upload:pkg.sha256"));
        Assert.Equal(2, f.Assets.Count);
        Assert.All(f.Assets, asset => Assert.Equal("uploaded", asset["state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task FirstZipUploadFailureIsRetriedAndBothAssetsAreVerified()
    {
        using var f = new Fixture { UploadFailures = 1 };
        await f.Upload();
        Assert.Equal(2, f.Events.Count(e => e == "upload:pkg.zip"));
        Assert.Single(f.Events, e => e == "upload:pkg.sha256");
        Assert.Equal(2, f.Assets.Count);
        Assert.All(f.Assets, asset => Assert.Equal("uploaded", asset["state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task VerifiedDuplicatesAreRemovedWithoutReuploadingTheVerifiedFiles()
    {
        using var f = new Fixture { Reuse = true };
        f.Assets.Add(f.Asset(50, "pkg.zip", File.ReadAllBytes(f.ZipPath)));
        f.Assets.Add(f.Asset(51, "pkg.zip", File.ReadAllBytes(f.ZipPath)));
        f.Assets.Add(f.Asset(52, "pkg.sha256", File.ReadAllBytes(f.Sidecar)));
        await f.Upload();
        Assert.Equal(2, f.Assets.Count);
        Assert.DoesNotContain(f.Events, e => e.StartsWith("upload:", StringComparison.Ordinal));
        Assert.Equal(50, f.Assets[0]["id"]!.GetValue<long>());
    }

    [Fact]
    public async Task MissingGeneratedNotesRefusesBeforeCredentialsOrRequests()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Notes, "# No generated section\n");
        var options = f.Options with { Token = "", GitExecutable = "must-not-run" };
        Assert.Contains("Not verified on a real TIA Portal", (await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.UploadDraftAsync(f.Root, f.CandidatePath, options, f.Client))).Message, StringComparison.Ordinal);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task PlainVersionsAreRejectedByBothCommandsBeforeAnyIo()
    {
        using var f = new Fixture();
        var options = f.Options with { Version = "4.0.0" };
        Assert.Contains("pre-releases only", (await Assert.ThrowsAsync<ReleaseException>(() => PublishPrepared.RunAsync(f.Root, options, f.Client, elapsed: () => TimeSpan.Zero))).Message, StringComparison.Ordinal);
        Assert.Contains("pre-releases only", (await Assert.ThrowsAsync<ReleaseException>(() => ReleasePublisher.UploadDraftAsync(f.Root, f.CandidatePath, options, f.Client))).Message, StringComparison.Ordinal);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task OnlyExpectedGitCommandsCanStartAndThereIsNoBuildTestOrPackagingProcess()
    {
        using var f = new Fixture();
        Assert.Equal(0, await f.Publish());
        Assert.Equal(new[] { "rev-parse HEAD", "status --porcelain --untracked-files=all", "push origin " + Fixture.Commit + ":refs/heads/master" }, f.GitCommands);
        var source = File.ReadAllText(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "build-tools/release/PublishPrepared.cs"));
        Assert.Equal(2, source.Split("ProcessRunner.Run", StringSplitOptions.None).Length);
        Assert.Contains("git ??=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dotnet\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Package-Release", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryOutcomePrintsTotalSeconds(bool fail)
    {
        using var f = new Fixture { Fault = fail ? "head" : "" };
        var saved = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try { Console.SetOut(output); if (fail) await Assert.ThrowsAsync<ReleaseException>(f.Publish); else Assert.Equal(0, await f.Publish()); }
        finally { Console.SetOut(saved); }
        Assert.Contains("Total seconds:", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal const string Commit = "0123456789abcdef0123456789abcdef01234567";
        internal string Root { get; } = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build/refactor", "publish-prepared-" + Guid.NewGuid().ToString("N"));
        internal string CandidatePath => Path.Combine(Root, "bin-build/prepared/4.0.0-beta.3/candidate.json");
        internal string ZipPath => Path.Combine(Path.GetDirectoryName(CandidatePath)!, "pkg.zip");
        internal string Sidecar => Path.ChangeExtension(ZipPath, ".sha256");
        internal string Notes => Path.Combine(Root, "docs/releases/v4.0.0-beta.3.md");
        internal PublishOptions Options { get; } = new("4.0.0-beta.3", "fake-token", "acme/project", "https://fixture.invalid", false, false, RetryDelaySeconds: 0);
        internal HttpClient Client { get; }
        internal List<string> Events { get; } = [];
        internal List<string> GitCommands { get; } = [];
        internal List<TimeSpan> Delays { get; } = [];
        internal List<JsonObject> Assets { get; } = [];
        internal JsonObject? Tag { get; private set; }
        internal JsonObject? Patch { get; private set; }
        internal JsonObject? DraftBody { get; private set; }
        internal string Fault { get; set; } = "";
        internal string ExpireAt { get; set; } = "";
        internal string? TagCommit { get; set; }
        internal bool Annotated { get; set; }
        internal bool Public { get; set; }
        internal bool Reuse { get; set; }
        internal int FlipFailures { get; set; }
        internal int DownloadFailures { get; set; }
        internal int UploadFailures { get; set; }
        internal double Seconds { get; set; }
        internal double FirstFlipSeconds { get; set; }
        private bool uploading;

        internal Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CandidatePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Notes)!);
            File.WriteAllText(Notes, "# Release\n[Checklist](../guides/check.md)\n" + PrereleaseNotes.Begin + "\n" + PrereleaseNotes.Heading + "\n\n1. Not run.\n" + PrereleaseNotes.End + "\n");
            File.WriteAllBytes(ZipPath, [0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            File.WriteAllText(Sidecar, Hash(File.ReadAllBytes(ZipPath)) + "  pkg.zip\n");
            SaveCandidate(true);
            Client = new HttpClient(this);
        }

        internal void SaveCandidate(bool draft) => File.WriteAllText(CandidatePath, JsonSerializer.Serialize(new
        {
            version = Options.Version, commit = Commit, zip = "pkg.zip", zipSha256 = Hash(File.ReadAllBytes(ZipPath)), zipSize = new FileInfo(ZipPath).Length, fileCount = 123,
            suites = new { unit = new { passed = 10, skipped = 0 } }, checks = new[] { "preflight" }, finishedUtc = "2026-11-03T00:00:00Z",
            draftReleaseId = draft ? 42 : (int?)null, draftAssetId = draft ? 50 : (int?)null
        }));
        internal Task<int> Publish() => PublishPrepared.RunAsync(Root, Options, Client, Git, () => TimeSpan.FromSeconds(ExpireAt == "start" ? 301 : Seconds), Delay);
        internal Task Upload() => ReleasePublisher.UploadDraftAsync(Root, CandidatePath, Options, Client);
        private Task Delay(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Delays.Add(delay); Seconds += delay.TotalSeconds; return Task.CompletedTask; }
        private CommandResult Git(string[] args, int timeout)
        {
            Assert.True(timeout is > 0 and <= 300000); GitCommands.Add(string.Join(" ", args));
            if (args[0] == "rev-parse") { Record("head"); return new(0, Fault == "head" ? "wrong" : Commit, ""); }
            if (args[0] == "status") { Record("status"); return new(0, Fault == "dirty" ? " M file" : "", ""); }
            Assert.Equal(new[] { "push", "origin", Commit + ":refs/heads/master" }, args); Record("push");
            return new(Fault == "push" ? 1 : 0, "", "");
        }
        private void Record(string action) { Events.Add(action); if (ExpireAt == action) Seconds = 301; }
        internal JsonObject Asset(long id, string name, byte[] bytes) => new()
        {
            ["id"] = id, ["name"] = name, ["state"] = "uploaded", ["size"] = bytes.LongLength, ["digest"] = "sha256:" + Hash(bytes),
            ["browser_download_url"] = "https://fixture.invalid/download/untagged-" + name
        };
        private void EnsureAssets()
        {
            if (Assets.Count != 0 || uploading) return;
            Assets.Add(Asset(50, "pkg.zip", File.ReadAllBytes(ZipPath))); Assets.Add(Asset(51, "pkg.sha256", File.ReadAllBytes(Sidecar)));
            if (Fault == "size") Assets[0]["size"] = 999;
            if (Fault == "digest") Assets[0]["digest"] = "sha256:bad";
            if (Fault == "sidecar") Assets[1]["digest"] = "sha256:bad";
            if (Fault == "duplicate") Assets.Add((JsonObject)Assets[0].DeepClone());
            if (Fault == "asset-id") Assets[0]["id"] = 99;
            if (Fault == "state") Assets[0]["state"] = "starter";
        }
        private object Release()
        {
            var assets = Assets.Select(a => (JsonObject)a.DeepClone()).ToArray();
            if (Public) foreach (var a in assets) a["browser_download_url"] = "https://fixture.invalid/download/published-" + a["name"]!.GetValue<string>();
            return new { id = 42, tag_name = "v4.0.0-beta.3", draft = !Public, prerelease = true, upload_url = "https://fixture.invalid/upload{?name,label}", assets };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; var method = request.Method.Method;
            if (path.StartsWith("/download/untagged-", StringComparison.Ordinal)) { Record("dead-draft-url"); return Reply(new { }, HttpStatusCode.NotFound); }
            if (path == "/download/published-pkg.zip") { Record("reachable"); Assert.True(token.CanBeCanceled); return Reply(new { }, DownloadFailures-- > 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK); }
            if (path.Contains("/git/ref/tags/", StringComparison.Ordinal)) { Record("ref"); return TagCommit == null ? Reply(new { }, HttpStatusCode.NotFound) : Reply(new { @object = new { type = Annotated ? "tag" : "commit", sha = Annotated ? "annotated-object" : TagCommit } }); }
            if (path.EndsWith("/git/tags/annotated-object", StringComparison.Ordinal)) return Reply(new { @object = new { type = "commit", sha = TagCommit } });
            if (path.EndsWith("/git/refs", StringComparison.Ordinal))
            {
                Record("tag"); Tag = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
                if (Fault == "tag-fail") return Reply(new { }, HttpStatusCode.ServiceUnavailable);
                TagCommit = Commit;
                if (Fault == "lost-tag") throw new HttpRequestException("lost tag response");
                return Reply(new { });
            }
            if (method == "DELETE") { var id = long.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture); Assets.RemoveAll(a => a["id"]!.GetValue<long>() == id); return Reply(new { }); }
            if (path == "/upload")
            {
                var name = Uri.UnescapeDataString(request.RequestUri.Query["?name=".Length..]); Record("upload:" + name);
                if (UploadFailures-- > 0) return Reply(new { }, HttpStatusCode.ServiceUnavailable);
                var asset = Asset(100 + Assets.Count, name, await request.Content!.ReadAsByteArrayAsync(token)); Assets.Add(asset); return Reply(asset);
            }
            if (path.Contains("/releases/assets/", StringComparison.Ordinal)) return Reply(Assets.Single(a => a["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture) == path.Split('/')[^1]));
            if (path.EndsWith("/assets", StringComparison.Ordinal)) { EnsureAssets(); return Reply(Assets); }
            if (path.EndsWith("/releases", StringComparison.Ordinal) && method == "GET") { uploading = true; return Reply(Reuse ? new[] { Release() } : []); }
            if (method is "POST" or "PATCH")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
                if (body["draft"]!.GetValue<bool>()) DraftBody = body;
                else
                {
                    Patch = body; Record("publish"); if (FirstFlipSeconds > 0) Seconds = FirstFlipSeconds;
                    if (FlipFailures-- > 0) return Reply(new { }, HttpStatusCode.ServiceUnavailable);
                    Public = true;
                    if (Fault == "lost-flip") throw new HttpRequestException("lost flip response");
                }
                return Reply(Release());
            }
            Record("release"); if (Fault == "404") return Reply(new { }, HttpStatusCode.NotFound);
            EnsureAssets(); return Reply(Release());
        }
        private static HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        protected override void Dispose(bool disposing) { if (disposing) Directory.Delete(Root, true); base.Dispose(disposing); }
    }
}

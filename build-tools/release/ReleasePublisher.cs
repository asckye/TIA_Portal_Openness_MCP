using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record PublishOptions(string Version, string Token, string Repository, string ApiBaseUrl,
    bool DraftOnly, bool DeleteDraft, int RetryCount = 6, int RetryDelaySeconds = 15, string GitExecutable = "git", string? CandidatePath = null);

// The JSON bodies of the two release writes. A plain release is drafted with prerelease=false and make_latest="false", then
// published as the latest release. A pre-release (X.Y.Z-beta.N, X.Y.Z-rc.N) carries prerelease=true and make_latest="false" in both
// writes, so GitHub's "latest release" (and the updater of every plain install) keeps pointing at the newest plain release.
internal static class ReleaseRequestBodies
{
    internal static object Draft(string tag, string commit, string body, bool prerelease) =>
        new { tag_name = tag, target_commitish = commit, name = tag, body, draft = true, prerelease, make_latest = "false" };

    internal static object Publish(string tag, string body, bool prerelease) =>
        new { name = tag, body, draft = false, prerelease, make_latest = prerelease ? "false" : "true" };
}

internal static class ReleasePublisher
{
    internal static GitHubApi PreparedApi(string root, PublishOptions options, HttpClient http, int? timeout = null)
    {
        var token = ReleasePrerequisites.GetToken(options.Token, options.GitExecutable, root, localOnly: false, timeoutMilliseconds: timeout);
        if (string.IsNullOrWhiteSpace(token)) throw new ReleaseException("No GitHub token: pass -Token, set GITHUB_TOKEN, or sign in with Git Credential Manager; token value is never printed");
        return new(http, new Uri(options.ApiBaseUrl.TrimEnd('/') + "/"), "repos/" + options.Repository.Trim('/'), token);
    }

    internal static async Task UploadDraftAsync(string root, string candidatePath, PublishOptions options,
        HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        PublishPrepared.RequirePrerelease(options.Version);
        var version = ReleaseVersions.Require(options.Version, "Invalid prepared version");
        var package = PreparedPackage.Read(root, candidatePath, options.Version);
        await package.VerifyAsync(cancellationToken);
        var notes = await File.ReadAllTextAsync(Path.Combine(root, "docs/releases", version.Tag + ".md"), cancellationToken);
        if (!PrereleaseNotes.HasSection(notes))
            throw new ReleaseException(version.Tag + " needs the generated Not verified on a real TIA Portal section (generate-prerelease-notes)");
        using var ownedClient = client is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        var api = PreparedApi(root, options, client ?? ownedClient!);
        var releases = "repos/" + api.Repository + "/releases";
        JsonElement? existing = null;
        for (var page = 1; ; page++)
        {
            using var list = await api.GetRequiredAsync(releases + "?per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture), cancellationToken);
            existing = FindRelease(list.RootElement, version.Tag);
            if (existing is not null || list.RootElement.GetArrayLength() < 100) break;
        }
        if (existing is { } published && !JsonBool(published, "draft")) throw new ReleaseException("release is already published; published releases are never changed");
        var webBase = options.ApiBaseUrl.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com" : options.ApiBaseUrl.TrimEnd('/');
        var notesBody = ComposeBody(notes, webBase, options.Repository, package.Commit, Path.GetFileName(package.Zip),
            package.Candidate["fileCount"]!.GetValue<long>(), package.Size, package.Digest);
        // The prepared SHA need not exist on origin yet. A draft targets the existing master branch.
        var body = ReleaseRequestBodies.Draft(version.Tag, "master", notesBody, prerelease: true);
        using var draft = await api.SendJsonRequiredAsync(existing is null ? HttpMethod.Post : HttpMethod.Patch,
            releases + (existing is null ? "" : "/" + JsonLong(existing.Value, "id").ToString(CultureInfo.InvariantCulture)), body, cancellationToken);
        var id = JsonLong(draft.RootElement, "id");
        var assetsPath = releases + "/" + id.ToString(CultureInfo.InvariantCulture) + "/assets?per_page=100";
        var unexpected = new List<long>();
        for (var page = 1; ; page++)
        {
            using var assets = await api.GetRequiredAsync(assetsPath + "&page=" + page.ToString(CultureInfo.InvariantCulture), cancellationToken);
            unexpected.AddRange(assets.RootElement.EnumerateArray()
                .Where(asset => JsonString(asset, "name") != Path.GetFileName(package.Zip) && JsonString(asset, "name") != Path.GetFileName(package.Sidecar))
                .Select(asset => JsonLong(asset, "id")));
            if (assets.RootElement.GetArrayLength() < 100) break;
        }
        foreach (var assetId in unexpected)
            await api.DeleteAsync(releases + "/assets/" + assetId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var upload = JsonString(draft.RootElement, "upload_url").Replace("{?name,label}", "", StringComparison.Ordinal);
        await UploadAndVerifyAsync(api, assetsPath, upload, await File.ReadAllBytesAsync(package.Zip, cancellationToken),
            Path.GetFileName(package.Zip), "application/zip", options, cancellationToken);
        await UploadAndVerifyAsync(api, assetsPath, upload, await File.ReadAllBytesAsync(package.Sidecar, cancellationToken),
            Path.GetFileName(package.Sidecar), "text/plain", options, cancellationToken);
        using var finalAssets = await api.GetRequiredAsync(assetsPath, cancellationToken);
        var zipId = JsonLong(FindAssets(finalAssets.RootElement, Path.GetFileName(package.Zip)).Single(), "id");
        await ReadPreparedReleaseAsync(api, package with { ReleaseId = id, AssetId = zipId }, cancellationToken);
        package.Candidate["draftReleaseId"] = id;
        package.Candidate["draftAssetId"] = zipId;
        await File.WriteAllTextAsync(candidatePath, package.Candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false), cancellationToken);
    }

    internal sealed record PreparedRelease(bool Public, JsonElement ZipAsset);

    internal static async Task<PreparedRelease> ReadPreparedReleaseAsync(GitHubApi api, PreparedPackage package, CancellationToken cancellationToken)
    {
        var path = "repos/" + api.Repository + "/releases/" + package.ReleaseId.ToString(CultureInfo.InvariantCulture);
        using var release = await api.GetReleaseByTagOrNullAsync(path, cancellationToken);
        if (release is null || JsonString(release.RootElement, "tag_name") != "v" + package.Version)
            throw new ReleaseException("no draft or public release for prepared version");
        var assets = release.RootElement.GetProperty("assets");
        var zip = FindAssets(assets, Path.GetFileName(package.Zip)).ToArray();
        var sidecar = FindAssets(assets, Path.GetFileName(package.Sidecar)).ToArray();
        var sidecarBytes = await File.ReadAllBytesAsync(package.Sidecar, cancellationToken);
        bool Matches(JsonElement asset, long size, string digest) => JsonString(asset, "state") == "uploaded" &&
            JsonLong(asset, "size") == size && JsonString(asset, "digest").Equals("sha256:" + digest, StringComparison.OrdinalIgnoreCase);
        if (assets.GetArrayLength() != 2 || zip.Length != 1 || sidecar.Length != 1 || JsonLong(zip[0], "id") != package.AssetId ||
            !Matches(zip[0], package.Size, package.Digest) || !Matches(sidecar[0], sidecarBytes.LongLength, Sha256Digest(sidecarBytes)))
            throw new ReleaseException("release asset size or digest differs from prepared ZIP or .sha256");
        return new(!JsonBool(release.RootElement, "draft"), zip[0].Clone());
    }

    internal static async Task<bool> PreparedTagExistsAsync(GitHubApi api, PreparedPackage package, CancellationToken cancellationToken)
    {
        using var reference = await api.GetReleaseByTagOrNullAsync("repos/" + api.Repository + "/git/ref/tags/" + Uri.EscapeDataString("v" + package.Version), cancellationToken);
        if (reference is null) return false;
        var target = reference.RootElement.GetProperty("object");
        if (JsonString(target, "type") == "tag")
        {
            using var annotated = await api.GetRequiredAsync("repos/" + api.Repository + "/git/tags/" + Uri.EscapeDataString(JsonString(target, "sha")), cancellationToken);
            target = annotated.RootElement.GetProperty("object").Clone();
        }
        if (JsonString(target, "type") != "commit" || JsonString(target, "sha") != package.Commit)
            throw new ReleaseException("tag does not point at the prepared commit");
        return true;
    }

    internal static async Task PublishPreparedDraftAsync(GitHubApi api, PreparedPackage package, HttpClient http, PublicationBudget budget)
    {
        JsonElement published = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // The first flip always runs after tagging; the 150-second entry limit leaves room for it.
                using var deadline = budget.BeginAttempt(firstFlip: attempt == 1);
                using var response = await api.SendJsonRequiredAsync(HttpMethod.Patch,
                    "repos/" + api.Repository + "/releases/" + package.ReleaseId.ToString(CultureInfo.InvariantCulture),
                    new { draft = false, prerelease = true, make_latest = "false", target_commitish = package.Commit }, deadline.Token);
                if (JsonBool(response.RootElement, "draft") || !JsonBool(response.RootElement, "prerelease"))
                    throw new ReleaseException("draft did not become a published pre-release");
                budget.State = PublicationBudget.PublicState;
                published = response.RootElement.Clone();
                break;
            }
            catch (Exception error) when (attempt < 3 && error is ReleaseException or HttpRequestException or OperationCanceledException)
            {
                await budget.DelayAsync(TimeSpan.FromSeconds(5));
            }
        }
        var zip = FindAssets(published.GetProperty("assets"), Path.GetFileName(package.Zip))
            .SingleOrDefault(asset => JsonLong(asset, "id") == package.AssetId);
        if (zip.ValueKind == JsonValueKind.Undefined) throw new ReleaseException("published response has no prepared ZIP asset");
        await ConfirmPreparedDownloadAsync(http, zip, budget);
    }

    internal static async Task ConfirmPreparedDownloadAsync(HttpClient http, JsonElement zipAsset, PublicationBudget budget)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var deadline = budget.BeginAttempt();
                using var request = new HttpRequestMessage(HttpMethod.Get, JsonString(zipAsset, "browser_download_url"));
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) throw new ReleaseException("download returned " + (int)response.StatusCode);
                budget.RemainingMilliseconds();
                return;
            }
            catch (Exception error) when (attempt < 5 && error is ReleaseException or HttpRequestException or OperationCanceledException)
            {
                await budget.DelayAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    internal static async Task<int> PublishAsync(string root, PublishOptions options, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        var version = ReleaseVersions.Require(options.Version, "-Version must be X.Y.Z, X.Y.Z-beta.N or X.Y.Z-rc.N");
        if (options.RetryCount < 1 || options.RetryDelaySeconds < 0) throw new ReleaseException("RetryCount must be positive and RetryDelaySeconds cannot be negative");
        using var ownedClient = client is null ? new HttpClient() : null;
        var http = client ?? ownedClient!;
        http.Timeout = Timeout.InfiniteTimeSpan;

        var repoPath = "repos/" + options.Repository.Trim('/');


        var tag = version.Tag;
        var prepared = options.CandidatePath is null ? null : ReleaseCandidate.Read(options.CandidatePath);
        if (prepared is not null) ReleaseCandidate.Verify(root, options.GitExecutable, prepared);
        var output = options.CandidatePath is null ? Path.Combine(root, "bin-build/releases", tag) : Path.GetDirectoryName(options.CandidatePath)!;
        var resultPath = prepared is null ? Path.Combine(output, "package-result.json") : ReleaseCandidate.Resolve(root, ReleaseEvidence.Text(prepared["packageResult"]?["path"]));
        if (!File.Exists(resultPath)) throw new ReleaseException("no package-result.json in " + output + " - run Package-Release.py first");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        var resultRoot = result.RootElement;
        if (prepared is null) ReleaseCheckPolicy.RequireFullRecord(resultRoot);
        var zip = JsonString(resultRoot, "path");
        if (zip.Length == 0) throw new ReleaseException("package-result.json is missing its path");
        zip = Path.GetFullPath(Path.IsPathRooted(zip) ? zip : Path.Combine(root, zip));
        var sidecar = Path.ChangeExtension(zip, ".sha256");
        if (!File.Exists(zip) || !File.Exists(sidecar)) throw new ReleaseException("ZIP or .sha256 missing: " + zip);
        var zipInfo = new FileInfo(zip);
        var digest = HashFile(zip);
        if (digest != JsonString(resultRoot, "sha256") || zipInfo.Length != JsonLong(resultRoot, "size"))
            throw new ReleaseException("ZIP differs from package-result.json (size or sha256)");
        if (prepared is null) ReleaseCheckPolicy.Load(root).RequireFullPackage(zip, requireColdBuild: true);
        else ReleaseCandidate.RequirePackage(root, zip);
        // Pre-release only: the notes must list what was not verified on a real TIA Portal (checked before credentials or requests).
        var releaseNote = Path.Combine(root, "docs/releases", tag + ".md");
        if (version.IsPrerelease && (!File.Exists(releaseNote) || !PrereleaseNotes.HasSection(File.ReadAllText(releaseNote, Encoding.UTF8))))
            throw new ReleaseException(tag + " is a pre-release; docs/releases/" + tag + ".md needs the generated \"Not verified on a real TIA Portal\" section (generate-prerelease-notes)");
        var token =ReleasePrerequisites.GetToken(options.Token, options.GitExecutable, root, localOnly: false);
        if (string.IsNullOrWhiteSpace(token)) throw new ReleaseException("No GitHub token: pass -Token, set GITHUB_TOKEN, or sign in with Git Credential Manager; token value is never printed");
        var baseUri = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
        var api = new GitHubApi(http, baseUri, repoPath, token);
        var commit = prepared is null ? JsonString(resultRoot, "sourceCommit") : ReleaseEvidence.Text(prepared["commit"]);
        if (commit.Length == 0) throw new ReleaseException("package-result.json is missing sourceCommit");
        var deliveryPath = Path.Combine(root, "manifest/delivery.json");
        using var delivery = JsonDocument.Parse(File.ReadAllText(deliveryPath));
        var packageName = JsonString(delivery.RootElement, "package");
        if (JsonString(delivery.RootElement, "release") != options.Version || packageName + ".zip" != Path.GetFileName(zip))
            throw new ReleaseException("manifest/delivery.json does not match the requested package/version");

        using (var user = await api.GetRequiredAsync("user", cancellationToken))
            Console.WriteLine($"GitHub token for {JsonString(user.RootElement, "login")} ({options.Repository})");

        using (var branch = await api.GetRequiredAsync(repoPath + "/branches/master", cancellationToken))
        {
            if (JsonString(branch.RootElement.GetProperty("commit"), "sha") != commit)
                throw new ReleaseException("master HEAD differs from the packaged sourceCommit");
        }
        using var gitRef = await api.GetRequiredAsync(repoPath + "/git/ref/tags/" + Uri.EscapeDataString(tag), cancellationToken);
        var tagObject = gitRef.RootElement.GetProperty("object");
        var targetType = JsonString(tagObject, "type");
        var targetSha = JsonString(tagObject, "sha");
        if (targetType == "tag")
        {
            using var annotated = await api.GetRequiredAsync(repoPath + "/git/tags/" + Uri.EscapeDataString(targetSha), cancellationToken);
            var target = annotated.RootElement.GetProperty("object");
            targetType = JsonString(target, "type");
            targetSha = JsonString(target, "sha");
        }
        if (targetType != "commit" || targetSha != commit) throw new ReleaseException($"Tag {tag} does not point at the packaged commit");

        using var existing = await api.GetReleaseByTagOrNullAsync(repoPath + "/releases/tags/" + Uri.EscapeDataString(tag), cancellationToken);
        using var listed = existing is null ? await api.GetRequiredAsync(repoPath + "/releases?per_page=100", cancellationToken) : null;
        JsonElement? releaseElement = existing is not null ? existing.RootElement.Clone() : FindRelease(listed!.RootElement, tag);
        if (options.DeleteDraft)
        {
            if (releaseElement is not { } draft || !JsonBool(draft, "draft")) throw new ReleaseException("no draft release for " + tag);
            await api.DeleteAsync(repoPath + "/releases/" + JsonLong(draft, "id"), cancellationToken);
            Console.WriteLine("draft " + tag + " deleted");
            return 0;
        }
        if (releaseElement is { } published && !JsonBool(published, "draft"))
            throw new ReleaseException("release " + tag + " is already published; published releases are never changed");

        var notePath = Path.Combine(root, "docs/releases", tag + ".md");
        var notes = File.ReadAllText(notePath, Encoding.UTF8);
        var webBase = options.ApiBaseUrl.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com"
            : options.ApiBaseUrl.TrimEnd('/');
        var body = ComposeBody(notes, webBase, options.Repository, commit, Path.GetFileName(zip), JsonLong(resultRoot, "files"), zipInfo.Length, digest);
        JsonDocument release;
        if (releaseElement is { } draftRelease)
        {
            release = JsonDocument.Parse(draftRelease.GetRawText());
            Console.WriteLine("reusing draft release " + JsonLong(draftRelease, "id"));
        }
        else
        {
            release = await api.SendJsonRequiredAsync(HttpMethod.Post, repoPath + "/releases", ReleaseRequestBodies.Draft(tag, commit, body, version.IsPrerelease), cancellationToken);
            Console.WriteLine("draft release created: " + JsonLong(release.RootElement, "id"));
        }

        try
        {
            var releaseId = JsonLong(release.RootElement, "id");
            var uploadBase = JsonString(release.RootElement, "upload_url").Replace("{?name,label}", "", StringComparison.Ordinal);
            if (uploadBase.Length == 0) throw new ReleaseException("Draft release has no upload_url");
            var assetsPath = repoPath + "/releases/" + releaseId + "/assets?per_page=100";
            var zipBytes = await File.ReadAllBytesAsync(zip, cancellationToken);
            var sidecarBytes = await File.ReadAllBytesAsync(sidecar, cancellationToken);
            await UploadAndVerifyAsync(api, assetsPath, uploadBase, zipBytes, Path.GetFileName(zip), "application/zip", options, cancellationToken);
            await UploadAndVerifyAsync(api, assetsPath, uploadBase, sidecarBytes, Path.GetFileName(sidecar), "text/plain", options, cancellationToken);
            if (options.DraftOnly)
            {
                Console.WriteLine("draft " + tag + " ready with both verified assets");
                return 0;
            }
            using var publishedResponse = await api.SendJsonRequiredAsync(HttpMethod.Patch, repoPath + "/releases/" + releaseId,
                ReleaseRequestBodies.Publish(tag, body, version.IsPrerelease), cancellationToken);
            if (JsonBool(publishedResponse.RootElement, "draft")) throw new ReleaseException("release is still a draft after publishing");
            using var finalAssets = await api.GetRequiredAsync(assetsPath, cancellationToken);
            AssertUploaded(finalAssets.RootElement, Path.GetFileName(zip), zipBytes.LongLength, Sha256Digest(zipBytes));
            AssertUploaded(finalAssets.RootElement, Path.GetFileName(sidecar), sidecarBytes.LongLength, Sha256Digest(sidecarBytes));
            var outputJson = Path.Combine(output, "published-release.json");
            File.WriteAllText(outputJson, publishedResponse.RootElement.GetRawText().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            Console.WriteLine("PUBLISHED " + tag + ": " + JsonString(publishedResponse.RootElement, "html_url"));
            return 0;
        }
        finally { release.Dispose(); }
    }

    /// <summary>The release body: the notes with relative links resolved at the source commit, then the package facts.</summary>
    internal static string ComposeBody(string notes, string webBase, string repository, string commit, string zipName, long fileCount, long zipLength, string digest)
    {
        notes = Regex.Replace(notes, @"\]\((\.\./[^)]+)\)", match => "](" + webBase + "/" + repository + "/blob/" + commit + "/docs/releases/" + match.Groups[1].Value + ")");
        return notes + $"\n\nPackage: `{zipName}` ({fileCount} files, {zipLength} bytes).\n\nSource commit: [{commit}]({webBase}/{repository}/commit/{commit}).\n\nZIP SHA-256:\n\n```text\n{digest}\n```\n";
    }

    private static async Task UploadAndVerifyAsync(GitHubApi api, string assetsPath, string uploadBase, byte[] bytes, string name,
        string contentType, PublishOptions options, CancellationToken cancellationToken)
    {
        var expected = "sha256:" + Sha256Digest(bytes);
        for (var attempt = 1; attempt <= options.RetryCount; attempt++)
        {
            using var assets = await api.GetRequiredAsync(assetsPath, cancellationToken);
            JsonElement? verified = null;
            foreach (var stale in FindAssets(assets.RootElement, name).ToArray())
            {
                if (verified is null && JsonString(stale, "state") == "uploaded" && JsonLong(stale, "size") == bytes.LongLength && JsonString(stale, "digest") == expected)
                    verified = stale;
                else
                    await api.DeleteAsync("repos/" + api.Repository + "/releases/assets/" + JsonLong(stale, "id"), cancellationToken);
            }
            if (verified is not null) return;
            JsonDocument uploaded;
            try
            {
                uploaded = await api.UploadRequiredAsync(uploadBase + "?name=" + Uri.EscapeDataString(name), bytes, contentType, cancellationToken);
            }
            catch (Exception) when (attempt < options.RetryCount)
            {
                if (options.RetryDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds * attempt), cancellationToken);
                continue;
            }
            using (uploaded)
            {
                var assetId = JsonLong(uploaded.RootElement, "id");
                using var check = await api.GetRequiredAsync("repos/" + api.Repository + "/releases/assets/" + assetId, cancellationToken);
                if (JsonString(check.RootElement, "state") == "uploaded" && JsonLong(check.RootElement, "size") == bytes.LongLength && JsonString(check.RootElement, "digest") == expected)
                    return;
            }
            if (attempt < options.RetryCount && options.RetryDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds * attempt), cancellationToken);
        }
        throw new ReleaseException(name + $": could not upload and verify after {options.RetryCount} attempts; the draft is kept for inspection");
    }

    private static IEnumerable<JsonElement> FindAssets(JsonElement assets, string name) =>
        assets.ValueKind == JsonValueKind.Array ? assets.EnumerateArray().Where(asset => JsonString(asset, "name") == name) : [];

    private static JsonElement? FindRelease(JsonElement releases, string tag) =>
        releases.ValueKind == JsonValueKind.Array ? releases.EnumerateArray().FirstOrDefault(release => JsonString(release, "tag_name") == tag) is var found && found.ValueKind != JsonValueKind.Undefined ? found.Clone() : null : null;

    private static void AssertUploaded(JsonElement assets, string name, long size, string digest)
    {
        var matches = FindAssets(assets, name).Where(asset => JsonString(asset, "state") == "uploaded").ToArray();
        if (matches.Length != 1 || JsonLong(matches[0], "size") != size || JsonString(matches[0], "digest") != "sha256:" + digest)
            throw new ReleaseException("published release does not list exactly one verified asset: " + name);
    }

    private static string JsonString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long JsonLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static bool JsonBool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Sha256Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal sealed class GitHubApi(HttpClient client, Uri apiBase, string repositoryPath, string token)
    {
        internal string Repository => repositoryPath["repos/".Length..];

        internal async Task<JsonDocument> GetRequiredAsync(string path, CancellationToken cancellationToken) =>
            await SendJsonRequiredAsync(HttpMethod.Get, path, null, cancellationToken);

        internal async Task<JsonDocument?> GetReleaseByTagOrNullAsync(string path, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Get, path, null, null, null, cancellationToken);
            if (response.Status == HttpStatusCode.NotFound) return null;
            AssertSuccess(response, HttpMethod.Get, path);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task<JsonDocument> SendJsonRequiredAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            var response = await SendAsync(method, path, body, null, null, cancellationToken);
            AssertSuccess(response, method, path);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task<JsonDocument> UploadRequiredAsync(string url, byte[] bytes, string contentType, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Post, url, null, bytes, contentType, cancellationToken);
            AssertSuccess(response, HttpMethod.Post, url);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Delete, path, null, null, null, cancellationToken);
            AssertSuccess(response, HttpMethod.Delete, path);
        }

        private async Task<ApiResponse> SendAsync(HttpMethod method, string pathOrUrl, object? json, byte[]? bytes, string? contentType, CancellationToken cancellationToken)
        {
            var uri = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) ? absolute : new Uri(apiBase, pathOrUrl.TrimStart('/'));
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("User-Agent", "TiaMcp-Release");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
            }
            else if (json is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            return new ApiResponse(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        private static void AssertSuccess(ApiResponse response, HttpMethod method, string path)
        {
            if ((int)response.Status is >= 200 and < 300) return;
            throw new ReleaseException($"GitHub API {method} {path} returned {(int)response.Status}: {response.Body}");
        }

        private sealed record ApiResponse(HttpStatusCode Status, string Body);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Versioning;

namespace TiaMcp.ReleaseTool;

internal sealed record PublishOptions(string Version, string Token, string Repository, string ApiBaseUrl,
    bool DraftOnly, bool DeleteDraft, int RetryCount = 6, int RetryDelaySeconds = 15, string GitExecutable = "git", string? CandidatePath = null);

internal static class ReleaseRequestBodies
{
    // A private draft cannot be Latest. Promote the final release in the same request that makes it public.
    internal static object Draft(ProductReleaseVersion version, string commit, string body) =>
        new { tag_name = version.Tag, target_commitish = commit, name = version.Tag, body, draft = true,
            prerelease = version.IsPrerelease, make_latest = "false" };

    internal static object Publish(ProductReleaseVersion version, string commit) =>
        new { draft = false, prerelease = version.IsPrerelease, make_latest = version.IsPrerelease ? "false" : "true", target_commitish = commit };
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
        var package = PreparedCandidate.Read(root, candidatePath, options.Version);
        var version = package.Version;
        await package.VerifyAsync(cancellationToken);
        var notes = await ReadNotesAsync(root, version, cancellationToken);
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
        var body = ReleaseRequestBodies.Draft(version, "master", notesBody);
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
        await UploadAndVerifyAsync(api, assetsPath, upload, package.Zip, package.Digest, "application/zip", options, TimeSpan.FromSeconds(60),
                cancellationToken);
        await UploadAndVerifyAsync(api, assetsPath, upload, package.Sidecar, FileHash.Sha256(package.Sidecar), "text/plain", options, TimeSpan.FromSeconds(60),
                cancellationToken);
        using var finalAssets = await api.GetRequiredAsync(assetsPath, cancellationToken);
        var zipId = JsonLong(FindAssets(finalAssets.RootElement, Path.GetFileName(package.Zip)).Single(), "id");
        await ReadPreparedReleaseAsync(api, package with { ReleaseId = id, AssetId = zipId }, cancellationToken);
        package.Candidate["draftReleaseId"] = id;
        package.Candidate["draftAssetId"] = zipId;
        await File.WriteAllTextAsync(candidatePath, package.Candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false), cancellationToken);
    }

    internal sealed record PreparedRelease(bool Public, JsonElement ZipAsset);

    internal static async Task<string> ReadNotesAsync(string root, ProductReleaseVersion version, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, PrereleaseNotes.NotesPath(version.Label));
        if (!File.Exists(path)) throw new ReleaseException("Missing release notes: " + path);
        var notes = await File.ReadAllTextAsync(path, cancellationToken);
        RequireNotes(version, notes);
        return notes;
    }

    private static void RequireNotes(ProductReleaseVersion version, string notes)
    {
        if (!version.IsPrerelease) PrereleaseNotes.RequireLanguages(notes);
        else if (!PrereleaseNotes.HasBilingualSections(notes))
            throw new ReleaseException(version.Tag + " needs the generated Not verified on a real TIA Portal section in Chinese and English"
                + " (generate-prerelease-notes); run upload-draft to refresh the draft.");
    }

    internal static async Task<PreparedRelease> ReadPreparedReleaseAsync(GitHubApi api, PreparedCandidate package, CancellationToken cancellationToken)
    {
        var path = "repos/" + api.Repository + "/releases/" + package.ReleaseId.ToString(CultureInfo.InvariantCulture);
        using var release = await api.GetOrNullAsync(path, cancellationToken);
        if (release is null || JsonString(release.RootElement, "tag_name") != package.Version.Tag)
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
        bool draft = JsonBool(release.RootElement, "draft");
        if (draft) RequireNotes(package.Version, JsonString(release.RootElement, "body"));
        else RequirePublishedVersion(release.RootElement, package.Version);
        return new(!draft, zip[0].Clone());
    }

    internal static async Task<bool> PreparedTagExistsAsync(GitHubApi api, PreparedCandidate package, CancellationToken cancellationToken)
    {
        using var reference = await api.GetOrNullAsync("repos/" + api.Repository + "/git/ref/tags/" + Uri.EscapeDataString(package.Version.Tag), cancellationToken);
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

    internal static async Task PublishPreparedDraftAsync(GitHubApi api, PreparedCandidate package, HttpClient http, PublicationBudget budget)
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
                    ReleaseRequestBodies.Publish(package.Version, package.Commit), deadline.Token);
                RequirePublishedVersion(response.RootElement, package.Version);
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

    private static void RequirePublishedVersion(JsonElement release, ProductReleaseVersion version)
    {
        if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("prerelease", out var prerelease)
            || prerelease.ValueKind != (version.IsPrerelease ? JsonValueKind.True : JsonValueKind.False))
            throw new ReleaseException("Published release flags do not match " + version.Tag);
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
                await budget.DelayAsync(TimeSpan.FromSeconds(10 * (1 << (attempt - 1))));
            }
        }
    }

    /// <summary>The release body: the notes with relative links resolved at the source commit, then the package facts.</summary>
    internal static string ComposeBody(string notes, string webBase, string repository, string commit, string zipName, long fileCount, long zipLength, string digest)
    {
        notes = Regex.Replace(notes, @"\]\((\.\./[^)]+)\)", match => "](" + webBase + "/" + repository + "/blob/" + commit + "/docs/releases/" + match.Groups[1].Value + ")");
        int split = PrereleaseNotes.RequireLanguages(notes);
        var link = $"[{commit}]({webBase}/{repository}/commit/{commit})";
        var sha = $"\n\n```text\n{digest}\n```\n";
        return notes[..split].TrimEnd() + $"\n\n交付包：`{zipName}`（{fileCount} 个文件，{zipLength} 字节）。\n\n源提交：{link}。\n\nZIP SHA-256：" + sha
            + "\n" + notes[split..].TrimEnd() + $"\n\nPackage: `{zipName}` ({fileCount} files, {zipLength} bytes).\n\nSource commit: {link}.\n\nZIP SHA-256:"
                + sha;
    }

    internal static async Task UploadAndVerifyAsync(GitHubApi api, string assetsPath, string uploadBase, string file, string digest,
        string contentType, PublishOptions options, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file);
        var size = new FileInfo(file).Length;
        var expected = "sha256:" + digest;
        for (var attempt = 1; attempt <= options.RetryCount; attempt++)
        {
            using var assets = await api.GetRequiredAsync(assetsPath, cancellationToken);
            JsonElement? verified = null;
            foreach (var stale in FindAssets(assets.RootElement, name).ToArray())
            {
                if (verified is null && JsonString(stale, "state") == "uploaded" && JsonLong(stale, "size") == size && JsonString(stale, "digest") == expected)
                    verified = stale;
                else
                    await api.DeleteAsync("repos/" + api.Repository + "/releases/assets/" + JsonLong(stale, "id"), cancellationToken);
            }
            if (verified is not null) return;
            JsonDocument uploaded;
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(stallTimeout);
                using var content = new UploadContent(file, size, attemptTimeout, stallTimeout);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                uploaded = await api.UploadRequiredAsync(uploadBase + "?name=" + Uri.EscapeDataString(name), content, attemptTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == options.RetryCount)
            {
                throw new ReleaseException($"{name}: upload stalled after {attempt} attempts; the draft is kept for inspection");
            }
            catch (Exception error) when (attempt < options.RetryCount && !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"{name}: upload attempt {attempt} failed ({error.GetType().Name}), retrying.");
                if (options.RetryDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds * attempt), cancellationToken);
                continue;
            }
            using (uploaded)
            {
                var assetId = JsonLong(uploaded.RootElement, "id");
                using var check = await api.GetRequiredAsync("repos/" + api.Repository + "/releases/assets/" + assetId, cancellationToken);
                if (JsonString(check.RootElement, "state") == "uploaded" && JsonLong(check.RootElement, "size") == size
                        && JsonString(check.RootElement, "digest") == expected)
                    return;
            }
            if (attempt < options.RetryCount && options.RetryDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds * attempt), cancellationToken);
        }
        throw new ReleaseException(name + $": could not upload and verify after {options.RetryCount} attempts; the draft is kept for inspection");
    }

    private sealed class UploadContent(string path, long size, CancellationTokenSource watchdog, TimeSpan limit) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, watchdog.Token);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await using var file = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            var clock = Stopwatch.StartNew();
            long sent = 0;
            var next = TimeSpan.FromSeconds(10);
            int count;
            while ((count = await file.ReadAsync(buffer, token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, count), token);
                watchdog.CancelAfter(limit);
                sent += count;
                if (clock.Elapsed < next) continue;
                Console.WriteLine($"{Path.GetFileName(path)}: {sent}/{size} bytes sent, {sent / clock.Elapsed.TotalSeconds:F0} bytes/s");
                next = clock.Elapsed + TimeSpan.FromSeconds(10);
            }
        }
    }

    private static IEnumerable<JsonElement> FindAssets(JsonElement assets, string name) =>
        assets.ValueKind == JsonValueKind.Array ? assets.EnumerateArray().Where(asset => JsonString(asset, "name") == name) : [];

    private static string JsonString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long JsonLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static bool JsonBool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Sha256Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal sealed class GitHubApi(HttpClient client, Uri apiBase, string repositoryPath, string token)
    {
        internal string Repository => repositoryPath["repos/".Length..];

        internal async Task<JsonDocument> GetRequiredAsync(string path, CancellationToken cancellationToken) =>
            await SendJsonRequiredAsync(HttpMethod.Get, path, null, cancellationToken);

        internal async Task<JsonDocument?> GetOrNullAsync(string path, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Get, path, null, null, cancellationToken);
            if (response.Status == HttpStatusCode.NotFound) return null;
            AssertSuccess(response, HttpMethod.Get, path);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task<JsonDocument> SendJsonRequiredAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            var response = await SendAsync(method, path, body, null, cancellationToken);
            AssertSuccess(response, method, path);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task<JsonDocument> UploadRequiredAsync(string url, HttpContent content, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Post, url, null, content, cancellationToken);
            AssertSuccess(response, HttpMethod.Post, url);
            return JsonDocument.Parse(response.Body);
        }

        internal async Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Delete, path, null, null, cancellationToken);
            AssertSuccess(response, HttpMethod.Delete, path);
        }

        private async Task<ApiResponse> SendAsync(HttpMethod method, string pathOrUrl, object? json, HttpContent? content, CancellationToken cancellationToken)
        {
            var uri = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) ? absolute : new Uri(apiBase, pathOrUrl.TrimStart('/'));
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("User-Agent", "TiaMcp-Release");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (content is not null) request.Content = content;
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

    private static JsonElement? FindRelease(JsonElement releases, string tag) =>
        releases.ValueKind == JsonValueKind.Array ? releases.EnumerateArray().FirstOrDefault(release => JsonString(release, "tag_name") == tag) is var found && found.ValueKind != JsonValueKind.Undefined ? found.Clone() : null : null;
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record PublishOptions(string Version, string Token, string Repository, string ApiBaseUrl,
    bool DraftOnly, bool DeleteDraft, int RetryCount = 6, int RetryDelaySeconds = 15, string GitExecutable = "git");

internal static class ReleasePublisher
{
    internal static async Task<int> PublishAsync(string root, PublishOptions options, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(options.Version, "^\\d+\\.\\d+\\.\\d+$")) throw new ReleaseException("-Version must be X.Y.Z");
        if (options.RetryCount < 1 || options.RetryDelaySeconds < 0) throw new ReleaseException("RetryCount must be positive and RetryDelaySeconds cannot be negative");
        using var ownedClient = client is null ? new HttpClient() : null;
        var http = client ?? ownedClient!;
        http.Timeout = Timeout.InfiniteTimeSpan;

        var repoPath = "repos/" + options.Repository.Trim('/');


        var tag = "v" + options.Version;
        var output = Path.Combine(root, "bin-build/releases", tag);
        var resultPath = Path.Combine(output, "package-result.json");
        if (!File.Exists(resultPath)) throw new ReleaseException("no package-result.json in " + output + " - run Package-Release.py first");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        var resultRoot = result.RootElement;
        ReleaseCheckPolicy.RequireFullRecord(resultRoot);
        var zip = JsonString(resultRoot, "path");
        if (zip.Length == 0) throw new ReleaseException("package-result.json is missing its path");
        zip = Path.GetFullPath(Path.IsPathRooted(zip) ? zip : Path.Combine(root, zip));
        var sidecar = Path.ChangeExtension(zip, ".sha256");
        if (!File.Exists(zip) || !File.Exists(sidecar)) throw new ReleaseException("ZIP or .sha256 missing: " + zip);
        var zipInfo = new FileInfo(zip);
        var digest = HashFile(zip);
        if (digest != JsonString(resultRoot, "sha256") || zipInfo.Length != JsonLong(resultRoot, "size"))
            throw new ReleaseException("ZIP differs from package-result.json (size or sha256)");
        ReleaseCheckPolicy.Load(root).RequireFullPackage(zip, requireColdBuild: true);
        var token = ReleasePrerequisites.GetToken(options.Token, options.GitExecutable, root, localOnly: false);
        if (string.IsNullOrWhiteSpace(token)) throw new ReleaseException("No GitHub token: pass -Token, set GITHUB_TOKEN, or sign in with Git Credential Manager; token value is never printed");
        var baseUri = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
        var api = new GitHubApi(http, baseUri, repoPath, token);
        var commit = JsonString(resultRoot, "sourceCommit");
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
        notes = Regex.Replace(notes, @"\]\((\.\./[^)]+)\)", match => "](" + webBase + "/" + options.Repository + "/blob/" + commit + "/docs/releases/" + match.Groups[1].Value + ")");
        var fileCount = JsonLong(resultRoot, "files");
        var body = notes + $"\n\nPackage: `{Path.GetFileName(zip)}` ({fileCount} files, {zipInfo.Length} bytes).\n\nSource commit: [{commit}]({webBase}/{options.Repository}/commit/{commit}).\n\nZIP SHA-256:\n\n```text\n{digest}\n```\n";
        JsonDocument release;
        if (releaseElement is { } draftRelease)
        {
            release = JsonDocument.Parse(draftRelease.GetRawText());
            Console.WriteLine("reusing draft release " + JsonLong(draftRelease, "id"));
        }
        else
        {
            release = await api.SendJsonRequiredAsync(HttpMethod.Post, repoPath + "/releases", new
            {
                tag_name = tag, target_commitish = commit, name = tag, body, draft = true, prerelease = false, make_latest = "false"
            }, cancellationToken);
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
                new { name = tag, body, draft = false, prerelease = false, make_latest = "true" }, cancellationToken);
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

    private static async Task UploadAndVerifyAsync(GitHubApi api, string assetsPath, string uploadBase, byte[] bytes, string name,
        string contentType, PublishOptions options, CancellationToken cancellationToken)
    {
        var expected = "sha256:" + Sha256Digest(bytes);
        for (var attempt = 1; attempt <= options.RetryCount; attempt++)
        {
            using var assets = await api.GetRequiredAsync(assetsPath, cancellationToken);
            foreach (var stale in FindAssets(assets.RootElement, name).ToArray())
            {
                if (JsonString(stale, "state") == "uploaded" && JsonLong(stale, "size") == bytes.LongLength && JsonString(stale, "digest") == expected)
                    return;
                await api.DeleteAsync("repos/" + api.Repository + "/releases/assets/" + JsonLong(stale, "id"), cancellationToken);
            }
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

    private sealed class GitHubApi(HttpClient client, Uri apiBase, string repositoryPath, string token)
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

using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.57: engine maintenance - the session state PreflightToolCall reads, and CheckForUpdate.
    // This file is NOT linked into the offline suite (it needs the portal and the network); the logic it
    // relies on (UpdateLogic, PreflightLogic) is.
    public static partial class McpServer
    {
        static partial void ReadSessionState(ref bool? connected, ref string? project)
        {
            try
            {
                var state = Portal.GetState();
                connected = state?.IsConnected;
                project = state?.Project;
            }
            catch
            {
                connected = null; project = null;
            }
        }

        private static readonly HttpClient UpdateHttp = CreateUpdateClient();

        private static HttpClient CreateUpdateClient()
        {
            // net48 defaults to TLS 1.0/1.1 on older Windows; GitHub requires 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMcpServer/" + (typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        /// <summary>The delivery root (the folder holding manifest/delivery.json), walking up from the engine's directory; null when not found.</summary>
        internal static string? FindInstallRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "manifest", "delivery.json"))) return dir.FullName;
            }
            catch { }
            return null;
        }

        [McpServerTool(Name = "CheckForUpdate"), Description(
            "[L0][Diagnostics][SESSION] Read-only update check: compares this engine's version with the latest GitHub release of the project and reports the delivery ZIP " +
            "(name, size, download URL, .sha256 sidecar) plus the exact steps to update. The engine never replaces its own files: the update is scripts/operations/Update-Engine.ps1, " +
            "run by the maintainer with every TiaMcpServer.exe stopped (it refuses while one runs; -Rollback restores the previous install). " +
            "The TIA machine needs access to github.com; without it the tool reports the release page URL. Nothing touches TIA Portal.")]
        public static async Task<ResponseStringList> CheckForUpdate(
            [Description("repository: GitHub owner/name to query (default the project's repository).")] string repository = UpdateLogic.DefaultRepository,
            [Description("timeoutSeconds: HTTP timeout for the GitHub API call (default 15).")] int timeoutSeconds = 15)
        {
            var meta = new JsonObject { ["timestamp"] = DateTime.Now };
            string current = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            meta["currentVersion"] = current;
            meta["currentFileVersion"] = typeof(McpServer).Assembly.GetName().Version?.ToString();
            var root = FindInstallRoot();
            var updater = root != null ? Path.Combine(root, UpdateLogic.UpdaterRelativePath) : null;
            bool updaterPresent = updater != null && File.Exists(updater);
            meta["installRoot"] = root;
            meta["updaterScript"] = updaterPresent ? updater : null;
            meta["howToUpdate"] = new JsonArray(UpdateLogic.HowToUpdate(updater, updaterPresent).Select(x => (JsonNode)x).ToArray());
            var lines = UpdateLogic.HowToUpdate(updater, updaterPresent).ToList();

            if (!UpdateLogic.IsValidRepository(repository))
            {
                meta["success"] = false;
                return new ResponseStringList { Message = "repository must be 'owner/name', e.g. " + UpdateLogic.DefaultRepository + ".", Items = lines, Meta = meta };
            }
            if (timeoutSeconds < 1 || timeoutSeconds > 120) timeoutSeconds = 15;
            string url = UpdateLogic.LatestReleaseUrl(repository);
            meta["releaseApiUrl"] = url;
            meta["releasePageUrl"] = UpdateLogic.ReleasePageUrl(repository);

            UpdateLogic.Release? release = null;
            string? apiProblem = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await UpdateHttp.GetAsync(url, cts.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    try { release = UpdateLogic.ParseRelease(json); meta["source"] = "api"; }
                    catch (Exception px) { apiProblem = "the GitHub answer could not be parsed: " + px.Message; }
                }
                else
                {
                    meta["httpStatus"] = (int)response.StatusCode;
                    apiProblem = response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429
                        ? "GitHub API rate limit reached from this address (60 unauthenticated calls per hour)"
                        : response.StatusCode == HttpStatusCode.NotFound ? "no published release found for " + repository : "GitHub API answered " + (int)response.StatusCode;
                }
            }
            catch (Exception ex)
            {
                meta["error"] = ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message);
                meta["success"] = false;
                return new ResponseStringList
                {
                    Message = "Engine " + current + "; GitHub could not be reached from this machine (" + (ex.InnerException?.Message ?? ex.Message) + "). " +
                              "The updater needs access to github.com from the TIA machine; check the connection / proxy, or open " + UpdateLogic.ReleasePageUrl(repository) + " to see the latest release.",
                    Items = lines, Meta = meta,
                };
            }

            if (release == null)
            {
                // The release page is not subject to the API quota: the /releases/latest redirect names the tag and the
                // expanded-assets fragment lists the download links (sizes approximate).
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    using var page = await UpdateHttp.GetAsync(UpdateLogic.LatestPageUrl(repository), cts.Token).ConfigureAwait(false);
                    var tag = UpdateLogic.TagFromReleaseUrl(page.RequestMessage?.RequestUri?.ToString());
                    if (tag != null)
                    {
                        using var fragment = await UpdateHttp.GetAsync(UpdateLogic.ExpandedAssetsUrl(repository, tag), cts.Token).ConfigureAwait(false);
                        var html = fragment.IsSuccessStatusCode ? await fragment.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        release = UpdateLogic.ParseReleasePage(repository, tag, html);
                        meta["source"] = "releasePage"; meta["apiProblem"] = apiProblem;
                    }
                }
                catch (Exception ex) { meta["releasePageError"] = ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message); }
            }
            if (release == null)
            {
                meta["success"] = false;
                return new ResponseStringList { Message = "Engine " + current + "; the latest release could not be read: " + apiProblem + ". Release page: " + UpdateLogic.ReleasePageUrl(repository), Items = lines, Meta = meta };
            }
            var comparison = UpdateLogic.Compare(current, release.Version);
            meta["latestVersion"] = release.Version; meta["latestTag"] = release.Tag; meta["latestName"] = release.Name;
            meta["publishedAt"] = release.PublishedAt; meta["releaseUrl"] = release.Url; meta["prerelease"] = release.Prerelease;
            meta["updateAvailable"] = comparison.HasValue && comparison < 0;
            var zip = release.Zip; var sha = release.Sha256;
            meta["zip"] = zip == null ? null : new JsonObject { ["name"] = zip.Name, ["url"] = zip.Url, ["size"] = zip.Size, ["digest"] = zip.Digest.Length > 0 ? zip.Digest : null };
            meta["sha256"] = sha == null ? null : new JsonObject { ["name"] = sha.Name, ["url"] = sha.Url };
            if (zip == null) lines.Insert(0, "The latest release carries no TIA_MCP_Delivery ZIP asset - check the release page before updating.");
            else lines.Insert(0, "Latest delivery: " + zip.Name + " (" + (zip.Size / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB) " + zip.Url + (sha != null ? " + " + sha.Name : " (no .sha256 asset)"));
            meta["success"] = true;
            return new ResponseStringList { Message = UpdateLogic.Summary(current, release, comparison), Items = lines, Meta = meta };
        }
    }
}

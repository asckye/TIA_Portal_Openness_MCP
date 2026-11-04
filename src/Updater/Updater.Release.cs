using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TiaMcp.Versioning;

namespace TiaMcp.Updater
{
    public static partial class UpdaterEngine
    {

        /// <summary>X.Y.Z (or X.Y.Z-beta.N / X.Y.Z-rc.N) of a tag or version with an optional leading v.</summary>
        public static string NormalizeVersion(string value)
        {
            ProductReleaseVersion version;
            if (!ProductReleaseVersion.TryParseTag(value, out version) || version == null)
                throw new InvalidOperationException("Release tag is not a version: " + value);
            return version.Label;
        }

        /// <summary>Semantic Versioning precedence: numeric versions compare as numbers, and a pre-release sorts below its plain release.</summary>
        public static int CompareVersions(string left, string right)
        {
            ProductReleaseVersion a, b;
            if (!ProductReleaseVersion.TryParseTag(NormalizeVersion(left), out a) || !ProductReleaseVersion.TryParseTag(NormalizeVersion(right), out b)
                    || a == null || b == null)
                throw new InvalidOperationException("Version comparison received an invalid version.");
            return Math.Sign(a.CompareTo(b));
        }

        public static bool IsVersion(string value) {
            ProductReleaseVersion version; return ProductReleaseVersion.TryParseTag(value, out version) && version != null; }
        public static bool IsPrerelease(string value) { return ProductReleaseVersion.IsPrereleaseText(value); }

        /// <summary>An install whose own version is a pre-release reads the releases list; a plain install reads releases/latest.</summary>
        public static bool UsesPrereleaseChannel(string installed) { return IsPrerelease(installed); }

        /// <summary>The newest published release with both delivery assets by Semantic Versioning precedence; drafts and tags that are not
        /// versions are ignored. Null when none qualifies.</summary>
        internal static ReleaseInfo SelectNewest(IEnumerable<ReleaseInfo> releases)
        {
            ReleaseInfo best = null;
            foreach (ReleaseInfo release in releases ?? Enumerable.Empty<ReleaseInfo>())
            {
                if (release == null || release.Draft || !IsVersion(release.Tag) || SelectAsset(release, true) == null || SelectAsset(release, false) == null)
                    continue;
                if (best == null || CompareVersions(release.Tag, best.Tag) > 0) best = release;
            }
            return best;
        }

        private static ReleaseInfo FindRelease(UpdaterOptions options, string installed, Action<string> say)
        {
            string api = options.ApiBaseUrl ?? "https://api.github.com";
            string tag = string.IsNullOrEmpty(options.Version) ? "" : "v" + options.Version.TrimStart('v', 'V');
            bool prereleaseChannel = tag.Length == 0 && UsesPrereleaseChannel(installed);
            string url = api.TrimEnd('/') + "/repos/" + options.Repository
                + (prereleaseChannel ? "/releases?per_page=100" : "/releases/" + (tag.Length == 0 ? "latest" : "tags/" + tag));
            try
            {
                ReleaseInfo release;
                if (prereleaseChannel)
                {
                    release = SelectNewest(GetJson<List<ReleaseInfo>>(url, "application/vnd.github+json", options.TimeoutSeconds, options));
                    if (release == null) throw new InvalidOperationException("The releases list has no published release with both delivery assets.");
                    say(UpdaterText.Bilingual("PrereleaseChannel", "pre-release install: newest release from the releases list") + ": " + release.Tag);
                }
                else release = GetJson<ReleaseInfo>(url, "application/vnd.github+json", options.TimeoutSeconds, options);
                NormalizeVersion(release.Tag);
                if (SelectAsset(release, true) == null || SelectAsset(release, false) == null)
                    throw new InvalidOperationException("The release does not contain both delivery ZIP and .sha256 assets.");
                say(UpdaterText.Bilingual("GitHubApi", "GitHub API") + ": " + release.Tag + " (" + (release.PublishedAt ?? "") + ")");
                return release;
            }
            catch (Exception apiError)
            {
                say(UpdaterText.Bilingual("ApiFallback", "GitHub API unavailable; using the release page") + ": " + apiError.GetBaseException().Message);
                if (prereleaseChannel)
                    say(UpdaterText
                        .Bilingual("PrereleaseFallback",
                            "the release page shows only the latest plain release; newer pre-releases appear once the GitHub API answers again"));
                string pageBase = options.ReleasePageBaseUrl ?? ("https://github.com/" + options.Repository);
                if (tag.Length == 0)
                {
                    string location = GetRedirect(pageBase.TrimEnd('/') + "/releases/latest", options.TimeoutSeconds, options);
                    Match redirect = Regex.Match(location ?? "", @"/releases/tag/([^/?#]+)$");
                    if (!redirect.Success) throw new InvalidOperationException("Could not determine the latest release tag from " + location);
                    tag = Uri.UnescapeDataString(redirect.Groups[1].Value);
                }
                var fallback = new ReleaseInfo { Tag = tag, Assets = new List<ReleaseAsset>() };
                string html = GetText(pageBase.TrimEnd('/') + "/releases/expanded_assets/" + Uri.EscapeDataString(tag), options.TimeoutSeconds, options);
                foreach (Match asset in Regex
                        .Matches(html, @"/releases/download/" + Regex.Escape(tag)
                            + @"/(?<name>TIA_MCP_Delivery_v\d+\.\d+\.\d+(?:-(?:beta|rc)\.\d+)?_\d{8}\.(?:zip|sha256))"))
                {
                    string name = Uri.UnescapeDataString(asset.Groups["name"].Value);
                    if (!fallback.Assets.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                        fallback.Assets.Add(new ReleaseAsset { Name = name, Url = pageBase.TrimEnd('/') + asset.Value });
                }
                SelectAsset(fallback, true);
                SelectAsset(fallback, false);
                say(UpdaterText.Bilingual("ReleasePage", "release page") + ": " + tag);
                return fallback;
            }
        }

        private static ReleaseAsset SelectAsset(ReleaseInfo release, bool zip)
        {
            if (release == null || release.Assets == null) return null;
            return release.Assets
                .FirstOrDefault(a => a != null && a.Name != null
                    && (zip ? ZipPattern.IsMatch(a.Name) : ZipPattern.IsMatch(a.Name.Replace(".sha256", ".zip")) && a.Name
                        .EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)));
        }

        private static void Download(string url, string destination, int timeoutSeconds, UpdaterOptions options)
        {
#if UPDATER_TEST_SOURCE
            string fixture = Environment.GetEnvironmentVariable("TIA_MCP_UPDATER_TEST_SOURCE");
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                string asset = Path.GetFileName(new Uri(url).AbsolutePath);
                string source = Path.Combine(fixture, asset);
                if (string.IsNullOrWhiteSpace(asset) || !File.Exists(source)) throw new FileNotFoundException("Test release asset was not found.", source);
                File.Copy(source, Prefix(destination), true);
                return;
            }
#endif
            if (options.ReadBytesForTest != null)
            {
                byte[] bytes = options.ReadBytesForTest(url, timeoutSeconds);
                using (var output = new FileStream(Prefix(destination), FileMode.Create, FileAccess.Write, FileShare.None)) output
                    .Write(bytes, 0, bytes.Length);
                return;
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1";
            request.Accept = "application/vnd.github+json";
            request.Timeout = checked(timeoutSeconds * 1000);
            request.ReadWriteTimeout = request.Timeout;
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(Prefix(destination), FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        }

        private static string GetText(string url, int timeoutSeconds, UpdaterOptions options)
        {
            if (options.ReadTextForTest != null) return options.ReadTextForTest(url, timeoutSeconds);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.Timeout = timeoutSeconds * 1000; request.ReadWriteTimeout = request.Timeout;
            using (WebResponse response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        private static string GetRedirect(string url, int timeoutSeconds, UpdaterOptions options)
        {
            if (options.ReadRedirectForTest != null) return options.ReadRedirectForTest(url, timeoutSeconds);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.AllowAutoRedirect = false; request.Timeout = timeoutSeconds * 1000;
            try { using (WebResponse response = request.GetResponse()) return response.Headers[HttpResponseHeader.Location]; }
            catch (WebException error) when (error.Response != null) { using (error.Response) return error.Response.Headers[HttpResponseHeader.Location]; }
        }

        private static T GetJson<T>(string url, string accept, int timeoutSeconds, UpdaterOptions options) where T : class
        {
#if UPDATER_TEST_SOURCE
            string fixture = Environment.GetEnvironmentVariable("TIA_MCP_UPDATER_TEST_SOURCE");
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                using (Stream stream = File.OpenRead(Path.Combine(fixture, "latest.json"))) return (T)JsonSerializer(typeof(T)).ReadObject(stream);
            }
#endif
            if (options.ReadTextForTest != null)
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(options.ReadTextForTest(url, timeoutSeconds)))) return (T)JsonSerializer(typeof(T))
                    .ReadObject(stream);
            }
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.Accept = accept; request.Timeout = timeoutSeconds * 1000; request.ReadWriteTimeout = request
                .Timeout;
            using (WebResponse response = request.GetResponse()) using (Stream stream = response.GetResponseStream())
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static bool IsValidRepository(string value) {
            return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$") && !value
                .Contains("..");
        }
    }
}

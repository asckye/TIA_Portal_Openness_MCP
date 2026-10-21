extern alias PlcRendering;
using System;
using TiaOpenness.Gui.Localization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using ProductReleaseVersion = PlcRendering::TiaMcp.Versioning.ProductReleaseVersion;

namespace TiaMcpConfigurator
{
    // the update band of the configurator. Everything that can be tested without network or UI lives here;
    // the .NET Framework updater performs the stop, download + verify, backup and replacement outside the installed files.
    public sealed class UpdateInfo
    {
        public string Installed { get; set; }
        public string Latest { get; set; }
        public string Tag { get; set; }
        public string ReleaseUrl { get; set; }
        public string ZipName { get; set; }
        public long ZipSize { get; set; }
        public bool HasSha256 { get; set; }
        public string Source { get; set; }      // api / page
        public bool Prerelease { get; set; }    // GitHub marks the release a pre-release
        // A plain install is never offered a pre-release; an install whose own version is a pre-release is offered newer releases.
        public bool UpdateAvailable { get { return UpdateCheck.Compare(Latest, Installed) > 0 && UpdateCheck.MayOffer(Latest, Prerelease, Installed); } }
        public string ZipSizeText { get { return ZipSize <= 0 ? "" : (ZipSize / 1048576.0).ToString("0.0") + " MB"; } }
    }

    public static class UpdateCheck
    {
        public const string Repository = "asckye/TIA_Portal_Openness_MCP";
        public const string UpdaterRelativePath = @"runtime/tools/TiaMcp.Updater.exe";
        public static string ReleasePageUrl(string repository) { return "https://github.com/" + repository + "/releases/latest"; }
        public static string ReleaseApiUrl(string repository) { return "https://api.github.com/repos/" + repository + "/releases/latest"; }
        public static string ReleasesListUrl(string repository) { return "https://api.github.com/repos/" + repository + "/releases?per_page=100"; }
        // The endpoint for an install of this version: releases/latest for a plain install, the releases list for a pre-release install.
        public static string UpdateApiUrl(string repository, string installed) { return UsesPrereleaseChannel(installed) ? ReleasesListUrl(repository) : ReleaseApiUrl(repository); }
        public static bool UsesPrereleaseChannel(string installed) { return IsPrerelease(installed); }
        public static bool IsPrerelease(string version) { var release = Release(version); return release != null && release.IsPrerelease; }
        public static bool MayOffer(string latest, bool markedPrerelease, string installed) { return !(markedPrerelease || IsPrerelease(latest)) || UsesPrereleaseChannel(installed); }
        private static ProductReleaseVersion Release(string text)
        {
            ProductReleaseVersion release;
            return text != null && ProductReleaseVersion.TryParseTag(text.Trim(), out release) ? release : null;
        }

        // Installed version = manifest\delivery.json next to the executable (null when this is not an unpacked delivery).
        public static string Installed(string root) { return DeliveryField(root, "release"); }
        public static string InstalledPackage(string root) { return DeliveryField(root, "package"); }
        private static string DeliveryField(string root, string name)
        {
            root = TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, root);
            string path = TiaOpenness.Shared.BundleLayout.RequirePath(root, "manifest/delivery.json");
            try
            {
                var json = ConfigCore.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                object value;
                return json != null && json.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : null;
            }
            catch /* swallow(parse-fallback): an unreadable or malformed delivery manifest leaves the installed release or package unknown */ { return null; }
        }
        public static string UpdaterPath(string root)
        {
            root = TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, root);
            return TiaOpenness.Shared.BundleLayout.RequirePath(root, UpdaterRelativePath.Replace('\\', '/'));
        }
        // The source repository also carries manifest\delivery.json and the updater; updating there would overwrite
        // tracked files, so the button is disabled for both a checkout and a worktree.
        public static bool IsSourceRepository(string root)
        {
            return TiaOpenness.Shared.BundleLayout.IsSourceCheckout(root);
        }

        // Numeric MAJOR.MINOR.PATCH comparison; a leading "v" is ignored; unparsable or empty sorts lowest. When either side is a
        // pre-release (X.Y.Z-beta.N, X.Y.Z-rc.N) the Semantic Versioning precedence applies: the pre-release sorts below its plain release.
        public static int Compare(string a, string b)
        {
            var ra = Release(a); var rb = Release(b);
            if (ra != null && rb != null && (ra.IsPrerelease || rb.IsPrerelease)) return Math.Sign(ra.CompareTo(rb));
            var va = Parse(a); var vb = Parse(b);
            if (va == null && vb == null) return 0;
            if (va == null) return -1;
            if (vb == null) return 1;
            return va.CompareTo(vb);
        }
        public static Version Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
        }
        public static string TagFromLocation(string location)
        {
            if (string.IsNullOrEmpty(location)) return null;
            var m = Regex.Match(location, @"/releases/tag/([^/?#]+)$");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        // GitHub API JSON -> UpdateInfo (tag, ZIP asset name / size, .sha256 sidecar present). Pure, so it is tested offline.
        public static UpdateInfo ParseRelease(string json, string installed, string repository)
        {
            return ParseRelease(ConfigCore.Json().Deserialize<Dictionary<string, object>>(json), installed, repository);
        }

        // GitHub API releases list -> the newest published release with a delivery ZIP and .sha256 (Semantic Versioning precedence).
        public static UpdateInfo ParseReleaseList(string json, string installed, string repository)
        {
            var releases = ConfigCore.Json().Deserialize<object[]>(json);
            if (releases == null) throw new InvalidOperationException(Loc.Current["Config.InvalidReleaseJson"]);
            UpdateInfo best = null;
            foreach (var release in releases.OfType<Dictionary<string, object>>())
            {
                object draft;
                if (release.TryGetValue("draft", out draft) && draft is bool && (bool)draft) continue;
                object tagValue;
                if (!release.TryGetValue("tag_name", out tagValue) || Parse(Convert.ToString(tagValue)) == null) continue;
                var info = ParseRelease(release, installed, repository);
                if (info.ZipName == null || !info.HasSha256) continue;
                if (best == null || Compare(info.Latest, best.Latest) > 0) best = info;
            }
            if (best == null) throw new InvalidOperationException(Loc.Current["Config.InvalidReleaseJson"]);
            return best;
        }

        private static UpdateInfo ParseRelease(Dictionary<string, object> release, string installed, string repository)
        {
            if (release == null) throw new InvalidOperationException(Loc.Current["Config.InvalidReleaseJson"]);
            object tagValue;
            string tag = release.TryGetValue("tag_name", out tagValue) ? Convert.ToString(tagValue) : null;
            if (Parse(tag) == null) throw new InvalidOperationException(Loc.Current.T("Config.InvalidReleaseTag", tag));
            var label = Release(tag);
            object prerelease;
            var info = new UpdateInfo { Installed = installed, Tag = tag, Latest = label != null ? label.Label : Parse(tag).ToString(), Source = "api",
                Prerelease = release.TryGetValue("prerelease", out prerelease) && prerelease is bool && (bool)prerelease };
            object url;
            info.ReleaseUrl = release.TryGetValue("html_url", out url) && url != null ? Convert.ToString(url) : "https://github.com/" + repository + "/releases/tag/" + tag;
            object assetsValue;
            // JavaScriptSerializer hands nested arrays back as ArrayList (Deserialize<T>) or object[] (DeserializeObject).
            var assets = release.TryGetValue("assets", out assetsValue) ? assetsValue as System.Collections.IEnumerable : null;
            if (assets != null)
                foreach (var asset in assets.OfType<Dictionary<string, object>>())
                {
                    object nameValue; string name = asset.TryGetValue("name", out nameValue) ? Convert.ToString(nameValue) : "";
                    if (name.StartsWith("TIA_MCP_Delivery_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ZipName = name;
                        object size; info.ZipSize = asset.TryGetValue("size", out size) && size != null ? Convert.ToInt64(size) : 0;
                    }
                    else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) info.HasSha256 = true;
                }
            return info;
        }

        // Network: the API first (rate-limited to 60 calls per hour per address), then the release page redirect.
        public static UpdateInfo Latest(string installed, string repository)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string apiError;
            try
            {
                return UsesPrereleaseChannel(installed)
                    ? ParseReleaseList(Download(ReleasesListUrl(repository), "application/vnd.github+json"), installed, repository)
                    : ParseRelease(Download(ReleaseApiUrl(repository), "application/vnd.github+json"), installed, repository);
            }
            catch (Exception ex) { apiError = ex.GetBaseException().Message; }
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ReleasePageUrl(repository));
                request.UserAgent = UserAgent(); request.Timeout = 15000; request.ReadWriteTimeout = 15000; request.AllowAutoRedirect = false;
                string tag;
                using (var response = (HttpWebResponse)request.GetResponse()) tag = TagFromLocation(response.Headers[HttpResponseHeader.Location]);
                if (Parse(tag) == null) throw new InvalidOperationException(Loc.Current.T("Config.MissingReleaseTag", apiError));
                var label = Release(tag);
                return new UpdateInfo { Installed = installed, Tag = tag, Latest = label != null ? label.Label : Parse(tag).ToString(), Source = "page", ReleaseUrl = "https://github.com/" + repository + "/releases/tag/" + tag };
            }
            catch (Exception ex) { throw new InvalidOperationException(Loc.Current.T("Config.UpdateUnreachable", ex.GetBaseException().Message), ex); }
        }
        private static string Download(string url, string accept)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent(); request.Accept = accept; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }
        private static string UserAgent() { return "TiaMcpConfigurator/" + Assembly.GetExecutingAssembly().GetName().Version; }

        // Engines that would make the updater refuse (it never kills them); the configurator itself is closed before launch.
        public static List<string> RunningEngines(string root = null)
        {
            var list = new List<string>();
            var names = TiaMcp.Versioning.TiaVersionCatalog.Runnable
                .Select(release => Path.GetFileNameWithoutExtension(TiaOpenness.Shared.BundleLayout.GetProduct(release.Key).Executable)).Distinct().ToList();
            names.AddRange(TiaMcp.Versioning.TiaVersionCatalog.Runnable.Select(release => Path.GetFileNameWithoutExtension(TiaOpenness.Shared.BundleLayout.GetProduct(release.Key).WorkerExecutable)).Distinct());
            string boundary = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (string name in names.Distinct())
            foreach (var p in Process.GetProcessesByName(name))
            {
                string path = ""; try { path = p.MainModule.FileName; } catch /* swallow(env-probe): an unavailable process module path still leaves the PID visible in the updater check */ { }
                if (boundary != null && path.Length > 0 && !path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(name + ".exe PID " + p.Id + (path.Length > 0 ? "（" + path + "）" : ""));
            }
            return list;
        }

        // The console updater waits for this configurator to exit before its running-process check, then reopens it.
        public static string LaunchArguments(string updaterPath, string root, int waitForPid)
        {
            return "-InstallRoot " + ConfigCore.Quote(root.TrimEnd('\\', '/')) + " -WaitForPid " + waitForPid + " -RelaunchConfigurator";
        }
        public static ProcessStartInfo Launch(string root, int waitForPid)
        {
            root = TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, root);
            if (IsSourceRepository(root)) throw new InvalidOperationException(Loc.Current["Config.CannotUpdateSource"]);
            return new ProcessStartInfo(UpdaterPath(root), LaunchArguments(UpdaterPath(root), root, waitForPid)) { UseShellExecute = true, WorkingDirectory = root };
        }
    }
}

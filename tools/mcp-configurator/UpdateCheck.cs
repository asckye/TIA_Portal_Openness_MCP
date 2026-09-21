using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TiaMcpConfigurator
{
    // 2.8.0: the update band of the configurator. Everything that can be tested without network or UI lives here;
    // the update itself stays in scripts\operations\Update-Engine.ps1 (stop, download + verify, back up, replace),
    // which the configurator launches in its own PowerShell window after closing itself - the updater refuses while
    // TiaMcpConfigurator.exe runs because it replaces that file too.
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
        public bool UpdateAvailable { get { return UpdateCheck.Compare(Latest, Installed) > 0; } }
        public string ZipSizeText { get { return ZipSize <= 0 ? "" : (ZipSize / 1048576.0).ToString("0.0") + " MB"; } }
    }

    public static class UpdateCheck
    {
        public const string Repository = "asckye/TIA_Portal_Openness_MCP";
        public const string UpdaterRelativePath = @"scripts\operations\Update-Engine.ps1";
        public static string ReleasePageUrl(string repository) { return "https://github.com/" + repository + "/releases/latest"; }
        public static string ReleaseApiUrl(string repository) { return "https://api.github.com/repos/" + repository + "/releases/latest"; }

        // Installed version = manifest\delivery.json next to the executable (null when this is not an unpacked delivery).
        public static string Installed(string root) { return DeliveryField(root, "release"); }
        public static string InstalledPackage(string root) { return DeliveryField(root, "package"); }
        private static string DeliveryField(string root, string name)
        {
            try
            {
                string path = Path.Combine(root, "manifest", "delivery.json");
                if (!File.Exists(path)) return null;
                var json = ConfigCore.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                object value;
                return json != null && json.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : null;
            }
            catch { return null; }
        }
        public static string UpdaterPath(string root) { return Path.Combine(root, UpdaterRelativePath); }
        // The source repository also carries manifest\delivery.json and the updater; updating there would overwrite
        // tracked files, so the button is disabled when a .git folder sits at the root.
        public static bool IsSourceRepository(string root) { return Directory.Exists(Path.Combine(root, ".git")); }

        // Numeric MAJOR.MINOR.PATCH comparison; a leading "v" is ignored; unparsable or empty sorts lowest.
        public static int Compare(string a, string b)
        {
            var va = Parse(a); var vb = Parse(b);
            if (va == null && vb == null) return 0;
            if (va == null) return -1;
            if (vb == null) return 1;
            return va.CompareTo(vb);
        }
        public static Version Parse(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(Int32.Parse(m.Groups[1].Value), Int32.Parse(m.Groups[2].Value), Int32.Parse(m.Groups[3].Value)) : null;
        }
        public static string TagFromLocation(string location)
        {
            if (String.IsNullOrEmpty(location)) return null;
            var m = Regex.Match(location, @"/releases/tag/([^/?#]+)$");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        // GitHub API JSON -> UpdateInfo (tag, ZIP asset name / size, .sha256 sidecar present). Pure, so it is tested offline.
        public static UpdateInfo ParseRelease(string json, string installed, string repository)
        {
            var release = ConfigCore.Json().Deserialize<Dictionary<string, object>>(json);
            if (release == null) throw new InvalidOperationException("GitHub 返回的不是 release JSON。");
            object tagValue;
            string tag = release.TryGetValue("tag_name", out tagValue) ? Convert.ToString(tagValue) : null;
            if (Parse(tag) == null) throw new InvalidOperationException("release 的 tag 不是版本号：" + tag);
            var info = new UpdateInfo { Installed = installed, Tag = tag, Latest = Parse(tag).ToString(), Source = "api" };
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
            try { return ParseRelease(Download(ReleaseApiUrl(repository), "application/vnd.github+json"), installed, repository); }
            catch (Exception ex) { apiError = ex.GetBaseException().Message; }
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ReleasePageUrl(repository));
                request.UserAgent = UserAgent(); request.Timeout = 15000; request.ReadWriteTimeout = 15000; request.AllowAutoRedirect = false;
                string tag;
                using (var response = (HttpWebResponse)request.GetResponse()) tag = TagFromLocation(response.Headers[HttpResponseHeader.Location]);
                if (Parse(tag) == null) throw new InvalidOperationException("发布页没有给出最新版本的 tag（API：" + apiError + "）");
                return new UpdateInfo { Installed = installed, Tag = tag, Latest = Parse(tag).ToString(), Source = "page", ReleaseUrl = "https://github.com/" + repository + "/releases/tag/" + tag };
            }
            catch (Exception ex) { throw new InvalidOperationException("这台机器访问不到 github.com：" + ex.GetBaseException().Message + "。检查网络或代理后再点“检查更新”。", ex); }
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
        public static List<string> RunningEngines()
        {
            var list = new List<string>();
            foreach (var p in Process.GetProcessesByName("TiaMcpServer"))
            {
                string path = ""; try { path = p.MainModule.FileName; } catch { }
                list.Add("TiaMcpServer.exe PID " + p.Id + (path.Length > 0 ? "（" + path + "）" : ""));
            }
            return list;
        }

        // powershell -NoExit keeps the updater window open so its log (or FAIL line) stays readable; -WaitForPid lets the
        // script wait for this configurator to exit before its running-process check; -RelaunchConfigurator reopens it.
        public static string LaunchArguments(string updaterPath, string root, int waitForPid)
        {
            // A trailing backslash inside quotes would escape the closing quote on PowerShell's command line.
            return "-NoProfile -ExecutionPolicy Bypass -NoExit -File " + ConfigCore.Quote(updaterPath) + " -InstallRoot " + ConfigCore.Quote(root.TrimEnd('\\', '/')) + " -WaitForPid " + waitForPid + " -RelaunchConfigurator";
        }
        public static ProcessStartInfo Launch(string root, int waitForPid)
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            return new ProcessStartInfo(shell, LaunchArguments(UpdaterPath(root), root, waitForPid)) { UseShellExecute = true, WorkingDirectory = root };
        }
    }
}

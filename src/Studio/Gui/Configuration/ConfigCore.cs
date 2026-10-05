using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Localization;


namespace TiaMcpConfigurator
{
    public sealed class ServerSettings
    {
        public int Version { get; set; }
        public string ReleaseKey { get; set; }
        public string EffectiveReleaseKey { get { return string.IsNullOrEmpty(ReleaseKey) ? Version.ToString(CultureInfo.InvariantCulture) : ReleaseKey; } }
        public string Address { get; set; }
        public int Port { get; set; }
        public string TiaPath { get; set; }
        public string ProtectedKey { get; set; }
    }

    public static class ConfigCore
    {
        public static readonly string StateDirectory = TiaOpenness.Shared.DataLocations.Current.ConfigDirectory;
        public static string ClaudePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"); } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 256 }; }

        public static string Prefix(string address, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || port < 1 || port > 65535 ||
                address != ip.ToString())
                throw new ArgumentException(Loc.Current["Config.InvalidEndpoint"]);
            return "http://" + ip + ":" + port + "/";
        }

        public static string Protect(string key)
        {
            ValidateKey(key);
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        }
        public static string Unprotect(string value)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        public static void ValidateKey(string key)
        {
            if (String.IsNullOrWhiteSpace(key) || key.Any(Char.IsControl)) throw new ArgumentException(Loc.Current["Config.InvalidSecret"]);
        }

        // Windows CommandLineToArgvW/CRT quoting, including embedded quotes and trailing backslashes.
        public static string Quote(string arg)
        {
            return TiaOpenness.Shared.ProcessArguments.Quote(arg);
        }

        public static string Engine(string root, int version)
        {
            return Engine(root, version.ToString(CultureInfo.InvariantCulture));
        }

        public static string Engine(string root, string versionKey)
        {
            var version = TiaVersionCatalog.RequireRunnable(versionKey);
            // The configuration page supplies an explicit root; keep its spelling and
            // retain the original candidates when an incomplete bundle has no marker.
            root = TiaOpenness.Shared.BundleLayout.FindRoot(null, root) ?? root;
            var candidates = version.IsFullEngine ? new[] {
                Path.Combine(root, "runtime", version.RuntimeDirectory, "TiaMcpServer.exe"),
                Path.Combine(root, "src", "Engine", version.EngineOutputDirectory, "Release", "net48", "TiaMcpServer.exe") } : new[] {
                Path.Combine(root, "runtime", version.RuntimeDirectory, "TiaMcpServer.exe") };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null) throw new FileNotFoundException(Loc.Current.T("Config.EngineNotFound", version.DisplayName));
            return path;
        }

        /// <summary>
        /// 自动探测 Portal V{version} 安装根目录，顺序与引擎 Engineering.GetTiaPortalInstallPath 一致：
        /// TiaPortalLocation 环境变量（须指向该版本）→ 注册表 HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{version}\TIA_Opns\Path
        /// → 默认安装目录。返回 (路径, 来源)；找不到返回 (null, 说明)。只读，不抛异常。
        /// </summary>
        public static KeyValuePair<string, string> DetectTia(int version) { return DetectTia(version.ToString(CultureInfo.InvariantCulture)); }
        public static KeyValuePair<string, string> DetectTia(string releaseKey)
        {
            var descriptor = TiaVersionCatalog.RequireRunnable(releaseKey);
            int version = descriptor.MajorVersion;
            string env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env) && PathMatchesVersion(env, version) && HasOpenness(env, releaseKey))
                return new KeyValuePair<string, string>(env, Loc.Current["Config.TiaEnvironmentSource"]);
            try
            {
                string regPath = TiaOpenness.Shared.OpennessEnvironment.InstalledPath(Microsoft.Win32.RegistryView.Registry64, releaseKey == "15.1" ? "15_1" : version.ToString(CultureInfo.InvariantCulture), "TIA_Opns");
                if (!string.IsNullOrWhiteSpace(regPath) && Directory.Exists(regPath) && HasOpenness(regPath, releaseKey))
                    return new KeyValuePair<string, string>(regPath, Loc.Current.T("Config.TiaRegistrySource", version));
            }
            catch (Exception) /* swallow(env-probe): failed registry detection falls through to the default Siemens installation directories */ { }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "Siemens", "Automation", descriptor.InstallFolder);
                if (Directory.Exists(candidate) && HasOpenness(candidate, releaseKey)) return new KeyValuePair<string, string>(candidate, Loc.Current["Config.TiaDefaultSource"]);
            }
            return new KeyValuePair<string, string>(null, Loc.Current.T("Config.TiaNotFound", version));
        }

        private static bool PathMatchesVersion(string path, int version)
        {
            return TiaOpenness.Shared.OpennessEnvironment.PathMatchesVersion(path, version);
        }

        private static bool HasOpenness(string path, string version)
        {
            try { ValidateTia(path, version); return true; } catch (Exception) /* swallow(env-probe): a candidate that fails Openness validation is rejected by automatic installation discovery */ { return false; }
        }

        public static void ValidateTia(string path, int version) { ValidateTia(path, version.ToString(CultureInfo.InvariantCulture)); }
        public static void ValidateTia(string path, string releaseKey)
        {
            var release = TiaVersionCatalog.RequireRunnable(releaseKey);
            if (release.FindApiDirectory(path) == null)
                throw new DirectoryNotFoundException(Loc.Current.T("Config.TiaApiNotFound", release.DisplayName, release.ApiAssembly));
        }

        public static void AtomicJson(string path, object value)
        {
            AtomicText(path, Json().Serialize(value));
        }

        public static void AtomicText(string path, string text)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak_" + Guid.NewGuid().ToString("N"));
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void MergeServer(string path, string name, Dictionary<string, object> entry)
        {
            var root = File.Exists(path) ? Json().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object> : new Dictionary<string, object>();
            if (root == null) throw new InvalidDataException(Loc.Current["Config.InvalidClaudeJson"]);
            object raw;
            Dictionary<string, object> servers;
            if (!root.TryGetValue("mcpServers", out raw)) servers = new Dictionary<string, object>();
            else
            {
                servers = raw as Dictionary<string, object>;
                if (servers == null) throw new InvalidDataException(Loc.Current.T("Config.InvalidServerMap", "mcpServers"));
            }
            servers[name] = entry;
            root["mcpServers"] = servers;
            AtomicJson(path, root);
        }

        public static Dictionary<string, object> RemoteEntry(string address, int port, string key)
        {
            ValidateKey(key);
            return new Dictionary<string, object> {
                { "type", "http" }, { "url", Prefix(address, port) + "mcp" },
                { "headers", new Dictionary<string, object> { { "Authorization", "Bearer " + key } } } };
        }

        public static string VersionArgument(string releaseKey)
        { return TiaVersionCatalog.RequireRunnable(releaseKey).IsFullEngine ? "--tia-major-version" : "--release-key"; }

        public static string Arguments(ServerSettings settings, string key)
        {
            ValidateKey(key);
            return String.Join(" ", new[] { VersionArgument(settings.EffectiveReleaseKey), settings.EffectiveReleaseKey, "--tia-portal-location", settings.TiaPath,
                "--transport", "http", "--http-prefix", Prefix(settings.Address, settings.Port), "--http-api-key", key, "--logging", "1" }.Select(Quote));
        }

        public static void CheckListener(string prefix)
        {
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(prefix);
                listener.Start();
            }
        }

        private static Dictionary<string, object> GetJson(string url, string key)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            if (key != null) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return Json().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
        }

        public static string TestRemote(string address, int port, string key)
        {
            ValidateKey(key);
            string prefix = Prefix(address, port);
            var health = GetJson(prefix + "mcp/health", null);
            var ready = GetJson(prefix + "mcp/ready", key);
            object isReady;
            if (ready == null || !ready.TryGetValue("mcpHostReady", out isReady) || !(isReady is bool) || !(bool)isReady)
                throw new InvalidOperationException(Loc.Current["Config.RemoteNotReady"]);
            object version;
            string versionText = health != null && health.TryGetValue("fileVersion", out version) ? Convert.ToString(version) : Loc.Current["Config.UnknownVersion"];
            return Loc.Current.T("Config.RemoteReady", versionText);
        }

        private static void RunChecked(string executable, string arguments)
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(output.Result + "\n" + error.Result);
            }
        }

        public static void ConfigureNetwork(string address, int port, string userSid)
        {
            string prefix = Prefix(address, port);
            var sid = new SecurityIdentifier(userSid);
            // Inspect exact reservation. Never delete/replace a reservation belonging to someone else.
            // netsh show succeeds even for an absent entry on some Windows versions, so add via HTTP API
            // would be needed to query ownership precisely. Adding an existing identical reservation is
            // handled by first checking its SDDL in netsh output below.
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            var info = new ProcessStartInfo(netsh, "http show urlacl " + Quote("url=" + prefix)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            string existing;
            using (var check = Process.Start(info))
            {
                var error = check.StandardError.ReadToEndAsync();
                existing = check.StandardOutput.ReadToEnd(); check.WaitForExit();
            }
            string sddl = "D:(A;;GX;;;" + sid.Value + ")";
            if (!existing.Contains(sddl))
                RunChecked(netsh, "http add urlacl " + Quote("url=" + prefix) + " " + Quote("sddl=" + sddl));
            // All interpolated values are canonical IPv4/integer; no user-entered shell text.
            string rule = "TIA-MCP-" + address + "-" + port;
            string command = "$ErrorActionPreference='Stop'; $r=Get-NetFirewallRule -Name '" + rule + "' -ErrorAction SilentlyContinue; " +
                "if($r){Set-NetFirewallRule -Name '" + rule + "' -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalAddress '" + address + "' -LocalPort " + port + " -RemoteAddress LocalSubnet | Out-Null}" +
                "else{New-NetFirewallRule -Name '" + rule + "' -DisplayName 'TIA MCP TCP " + port + "' -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalAddress '" + address + "' -LocalPort " + port + " -RemoteAddress LocalSubnet | Out-Null}";
            RunChecked(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        }
    }

}

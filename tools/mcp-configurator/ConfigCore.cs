using System;
using System.Collections.Generic;
using System.Diagnostics;

using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;


namespace TiaMcpConfigurator
{
    public sealed class ServerSettings
    {
        public int Version { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public string TiaPath { get; set; }
        public string ProtectedKey { get; set; }
    }

    public static class ConfigCore
    {
        public static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaPortalMcp");
        public static string ClaudePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"); } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 256 }; }

        public static string Prefix(string address, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || port < 1 || port > 65535 ||
                address != ip.ToString())
                throw new ArgumentException("请填写完整 IPv4 地址和 1–65535 范围的端口，不要粘贴网页链接。示例：192.0.2.10");
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
            if (String.IsNullOrWhiteSpace(key) || key.Any(Char.IsControl)) throw new ArgumentException("连接密钥不能为空，也不能包含换行或控制字符。");
        }

        // Windows CommandLineToArgvW/CRT quoting, including embedded quotes and trailing backslashes.
        public static string Quote(string arg)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static string Engine(string root, int version)
        {
            if (version != 20 && version != 21) throw new ArgumentException("仅支持 V20 / V21。");
            var candidates = new[] {
                Path.Combine(root, "runtime", "v" + version, "TiaMcpServer.exe"),
                Path.Combine(root, "tools", "tiaportal-mcp", "src", "TiaMcpServer", version == 20 ? "bin-v20" : "bin", "Release", "net48", "TiaMcpServer.exe") };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null) throw new FileNotFoundException("找不到 V" + version + " 引擎。请将配置程序放在完整 Release 包的根目录，与 tia.cmd 同级。");
            return path;
        }

        public static void ValidateTia(string path, int version)
        {
            string api = Path.Combine(path, "PublicAPI");
            string dll = version == 21 ? "Siemens.Engineering.Base.dll" : "Siemens.Engineering.dll";
            if (!Directory.Exists(api) || !Directory.EnumerateFiles(api, dll, SearchOption.AllDirectories).Any())
                throw new DirectoryNotFoundException("该目录未找到 V" + version + " Openness API（" + dll + "）。请选择 Portal V" + version + " 安装根目录，不带 Bin，并确认安装了 Openness。");
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
            if (root == null) throw new InvalidDataException("现有 Claude 配置不是 JSON 对象，未修改。");
            object raw;
            Dictionary<string, object> servers;
            if (!root.TryGetValue("mcpServers", out raw)) servers = new Dictionary<string, object>();
            else
            {
                servers = raw as Dictionary<string, object>;
                if (servers == null) throw new InvalidDataException("现有 mcpServers 不是 JSON 对象，未修改。");
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

        public static string Arguments(ServerSettings settings, string key)
        {
            ValidateKey(key);
            return String.Join(" ", new[] { "--tia-major-version", settings.Version.ToString(), "--tia-portal-location", settings.TiaPath,
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
                throw new InvalidOperationException("HTTP 服务可达，但 MCP 尚未就绪。请检查虚拟机中的服务日志。");
            object version;
            string versionText = health != null && health.TryGetValue("fileVersion", out version) ? Convert.ToString(version) : "未知";
            return "HTTP 可达，鉴权通过，MCP 已就绪。版本：" + versionText + "。尚未验证 TIA 工程连接。";
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

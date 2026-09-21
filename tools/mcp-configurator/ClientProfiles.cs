using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpConfigurator
{
    public sealed class ClientProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Hint { get; set; }
        // Schema family that decides file layout and entry shape. Brand cards (Qwen → Qwen Code, DeepSeek / GLM / Grok → OpenCode …)
        // write another product's file, so several profiles may map onto one Client.
        public string Client { get; set; }
        public string Kind { get; set; }      // CLI / Desktop / IDE
        // 2.7.61: whether this machine shows traces of the client (config folder, executable on PATH, install folder,
        // uninstall registry entry) and what was found - so a user on any computer sees which cards apply here.
        public bool Detected { get; set; }
        public string Evidence { get; set; }
        public string DisplayName { get { return Id == "vscode" ? "VS Code · Copilot" : Name; } }
        // Tile subtitle without the detection state: the transport family plus, when the card is a model brand, the client that gets written.
        public string CategoryBase { get { string label = ClientProfiles.ClientLabel(Client); return label == null ? Kind : Kind + " · " + label; } }
        public string Category { get { return CategoryBase + " · " + (Detected ? "已检测" : "未检测到"); } }
        // Tooltip: the after-save instructions plus where the client was (not) found and the file that will be written.
        public string Tooltip { get { return Hint + "\n" + (Detected ? "已检测到：" : "未检测到：") + Evidence + "\n写入：" + Path; } }
        public override string ToString() { return Name; }
        public ClientProfile(string id, string name, string path, string hint, string client = null, string kind = "CLI")
        {
            Id = id; Name = name; Path = path; Hint = hint; Client = client ?? id; Kind = kind; Evidence = "";
        }
    }

    public static class ClientProfiles
    {
        public static string ClientLabel(string client)
        {
            switch (client)
            {
                case "opencode": return "OpenCode";
                case "qwen": return "Qwen Code";
                case "kimi": return "Kimi Code";
                case "codebuddy": return "CodeBuddy";
                default: return null;   // native client: the card already carries its name
            }
        }

        // Order is the on-screen order: CLIs first, Claude Code and Codex on top, IDEs last.
        public static List<ClientProfile> All()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string codex = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codex)) codex = System.IO.Path.Combine(home, ".codex");
            string kimi = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (String.IsNullOrWhiteSpace(kimi)) kimi = System.IO.Path.Combine(home, ".kimi-code");
            string opencode = System.IO.Path.Combine(home, ".config", "opencode", "opencode.json");
            const string opencodeHint = "写入 OpenCode 的 opencode.json；模型在 OpenCode 的 provider 里选 {0}，MCP 配置与模型无关。重启 OpenCode 后新建会话。";
            // VS Code: the stable user folder, or Insiders when only that one exists on this machine.
            string vscodeUser = System.IO.Path.Combine(app, "Code", "User");
            if (!Directory.Exists(vscodeUser) && Directory.Exists(System.IO.Path.Combine(app, "Code - Insiders", "User"))) vscodeUser = System.IO.Path.Combine(app, "Code - Insiders", "User");
            var profiles = new List<ClientProfile> {
                new ClientProfile("claude-code", "Claude Code", System.IO.Path.Combine(home, ".claude.json"), "官方桌面客户端：重启后选择 Code → Local，新建会话。CLI 也使用此配置。"),
                new ClientProfile("codex", "Codex", System.IO.Path.Combine(codex, "config.toml"), "保存后重启 Codex 桌面应用 / CLI，重新打开任务以加载 MCP。"),
                new ClientProfile("gemini", "Gemini CLI", System.IO.Path.Combine(home, ".gemini", "settings.json"), "重启 Gemini CLI，使用 /mcp 检查服务器状态。"),
                // 国产模型与 Grok 没有自带 MCP 客户端，卡片按模型命名，实际写入各家官方 CLI 或 OpenCode。
                new ClientProfile("qwen", "Qwen", System.IO.Path.Combine(home, ".qwen", "settings.json"), "写入阿里 Qwen Code 的 settings.json（格式同 Gemini CLI）。重启 Qwen Code，用 /mcp 检查服务器。"),
                new ClientProfile("kimi", "Kimi", System.IO.Path.Combine(kimi, "mcp.json"), "写入月之暗面 Kimi Code CLI 的 mcp.json，尊重 KIMI_CODE_HOME。重启后状态栏显示 MCP 就绪即可新建会话。"),
                new ClientProfile("codebuddy", "Yuanbao", System.IO.Path.Combine(home, ".codebuddy", ".mcp.json"), "写入腾讯 CodeBuddy Code CLI 的 .mcp.json（混元 / DeepSeek 等模型在 CodeBuddy 里选）。重启后用 /mcp 检查。"),
                new ClientProfile("deepseek", "DeepSeek", opencode, String.Format(opencodeHint, "DeepSeek"), "opencode"),
                new ClientProfile("zhipu", "GLM", opencode, String.Format(opencodeHint, "智谱 GLM"), "opencode"),
                new ClientProfile("grok", "Grok", opencode, String.Format(opencodeHint, "xAI Grok"), "opencode"),
                // 千问工作助理（桌面应用）只读 ~/.qwen-agent/mcp.json（或项目下 .qwen-agent/mcp.json）：url + Bearer 头，不走 OAuth；改完必须完全退出进程再打开才会重新读取。
                new ClientProfile("qwen-agent", "Qwen Agent", System.IO.Path.Combine(home, ".qwen-agent", "mcp.json"), "写入千问工作助理（Qwen Agent）的 .qwen-agent\\mcp.json（url + Bearer 头，不走 OAuth）。必须完全退出千问工作助理进程（不是关窗口）再打开才会重新读取；只对一个项目生效时把同名文件放到 <项目>\\.qwen-agent\\mcp.json。", "qwen-agent", "Desktop"),
                new ClientProfile("cursor", "Cursor", System.IO.Path.Combine(home, ".cursor", "mcp.json"), "重启 Cursor，在 MCP 设置中检查 tia-portal-vm 并启用。", null, "IDE"),
                new ClientProfile("vscode", "VS Code / Copilot", System.IO.Path.Combine(vscodeUser, "mcp.json"), "适用于 VS Code 默认用户配置（本机只有 Insiders 时写 Insiders）。重载窗口，在 MCP 服务器列表中启动并信任该服务。", null, "IDE")
            };
            foreach (var profile in profiles) Detect(profile);
            return profiles;
        }

        // ---- 2.7.61: where is the client on THIS machine? ------------------------------------------------------------
        // Evidence, in order: the config folder / file the card writes, the executable on PATH (npm shims are .cmd), a known install
        // folder, an uninstall entry in the registry. Nothing here needs the client to be running; nothing is written.
        public static void Detect(ClientProfile profile)
        {
            var found = new List<string>();
            try
            {
                string dir = System.IO.Path.GetDirectoryName(profile.Path);
                if (File.Exists(profile.Path)) found.Add("配置文件 " + profile.Path);
                else if (dir != null && Directory.Exists(dir)) found.Add("配置目录 " + dir);
                foreach (var exe in ExecutableNames(profile.Client))
                {
                    string hit = OnPath(exe);
                    if (hit != null) { found.Add("PATH 上的 " + System.IO.Path.GetFileName(hit)); break; }
                }
                foreach (var folder in InstallFolders(profile.Client))
                    if (Directory.Exists(folder)) { found.Add("安装目录 " + folder); break; }
                string registry = UninstallEntry(RegistryKeywords(profile.Client));
                if (registry != null) found.Add("已安装程序 " + registry);
            }
            catch (Exception) { }
            profile.Detected = found.Count > 0;
            profile.Evidence = found.Count > 0 ? String.Join("；", found) : "本机没有它的配置目录、可执行文件、安装目录或卸载项（仍可写入，安装后即生效）";
        }

        private static string[] ExecutableNames(string client)
        {
            switch (client)
            {
                case "claude-code": return new[] { "claude" };
                case "codex": return new[] { "codex" };
                case "gemini": return new[] { "gemini" };
                case "qwen": return new[] { "qwen" };
                case "kimi": return new[] { "kimi" };
                case "codebuddy": return new[] { "codebuddy" };
                case "opencode": return new[] { "opencode" };
                case "cursor": return new[] { "cursor" };
                case "vscode": return new[] { "code", "code-insiders" };
                default: return new string[0];
            }
        }

        private static string[] InstallFolders(string client)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programs = System.IO.Path.Combine(local, "Programs");
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            switch (client)
            {
                case "claude-code": return new[] { System.IO.Path.Combine(programs, "claude"), System.IO.Path.Combine(local, "AnthropicClaude"), System.IO.Path.Combine(programs, "Claude") };
                case "cursor": return new[] { System.IO.Path.Combine(programs, "cursor"), System.IO.Path.Combine(programs, "Cursor") };
                case "vscode": return new[] { System.IO.Path.Combine(programs, "Microsoft VS Code"), System.IO.Path.Combine(pf, "Microsoft VS Code"), System.IO.Path.Combine(programs, "Microsoft VS Code Insiders") };
                case "qwen-agent": return new[] { System.IO.Path.Combine(programs, "qwen-agent"), System.IO.Path.Combine(programs, "QwenAgent"), System.IO.Path.Combine(local, "qwen-agent") };
                case "codex": return new[] { System.IO.Path.Combine(programs, "Codex"), System.IO.Path.Combine(programs, "codex") };
                default: return new string[0];
            }
        }

        private static string[] RegistryKeywords(string client)
        {
            switch (client)
            {
                case "claude-code": return new[] { "Claude" };
                case "cursor": return new[] { "Cursor" };
                case "vscode": return new[] { "Visual Studio Code" };
                case "qwen-agent": return new[] { "千问工作助理", "Qwen Agent", "qwen-agent", "通义千问" };
                case "codex": return new[] { "Codex" };
                default: return new string[0];
            }
        }

        // First file on PATH named <name> with a runnable extension (.exe / .cmd / .bat / .com), or null.
        public static string OnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var extensions = new[] { ".exe", ".cmd", ".bat", ".com" };
            foreach (string dir in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0) continue;
                foreach (string ext in extensions)
                {
                    try { string candidate = System.IO.Path.Combine(trimmed, name + ext); if (File.Exists(candidate)) return candidate; }
                    catch (Exception) { }
                }
            }
            return null;
        }

        // DisplayName of the first uninstall entry (HKCU / HKLM, 64- and 32-bit views) containing one of the keywords, or null.
        private static string UninstallEntry(string[] keywords)
        {
            if (keywords.Length == 0) return null;
            var roots = new[] {
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") };
            foreach (var root in roots)
            {
                try
                {
                    using (var key = root.Key.OpenSubKey(root.Value))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            using (var entry = key.OpenSubKey(sub))
                            {
                                string display = entry == null ? null : entry.GetValue("DisplayName") as string;
                                if (display != null && keywords.Any(k => display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return display;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        // OpenCode keeps servers under "mcp", VS Code under "servers"; everyone else uses "mcpServers".
        public static string RootKey(ClientProfile profile)
        {
            return profile.Client == "vscode" ? "servers" : profile.Client == "opencode" ? "mcp" : "mcpServers";
        }

        // Key that carries the endpoint in a remote entry, per client schema.
        public static string UrlKey(ClientProfile profile)
        {
            return profile.Client == "gemini" || profile.Client == "qwen" ? "httpUrl" : "url";
        }

        public static Dictionary<string, object> Entry(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia)
        {
            string client = profile.Client;
            if (!remote)
            {
                var localArgs = new[] { "--tia-major-version", version.ToString(), "--tia-portal-location", tia };
                // OpenCode: command is one array that starts with the executable.
                if (client == "opencode")
                    return new Dictionary<string, object> { { "type", "local" }, { "command", new[] { engine }.Concat(localArgs).ToArray() }, { "enabled", true } };
                var local = new Dictionary<string, object> { { "command", engine }, { "args", localArgs } };
                if (client == "claude-code" || client == "vscode" || client == "cursor" || client == "codebuddy") local["type"] = "stdio";
                return local;
            }
            ConfigCore.ValidateKey(key);
            string url = ConfigCore.Prefix(address, port) + "mcp";
            var entry = new Dictionary<string, object> {
                { UrlKey(profile), url },
                { "headers", new Dictionary<string, object> { { "Authorization", "Bearer " + key } } } };
            if (client == "claude-code" || client == "vscode" || client == "codebuddy") entry["type"] = "http";
            // qwen-agent / Kimi / Gemini / Qwen Code / Cursor: url (or httpUrl) + headers is the whole entry - a "type" field is not read.
            if (client == "opencode") { entry["type"] = "remote"; entry["enabled"] = true; }
            return entry;
        }

        public static void Save(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia)
        {
            var entry = Entry(profile, remote, address, port, key, engine, version, tia);
            string name = ServerName(profile, remote);
            if (profile.Client == "codex")
            {
                string original = File.Exists(profile.Path) ? File.ReadAllText(profile.Path) : "";
                ConfigCore.AtomicText(profile.Path, MergeToml(original, name, remote, address, port, key, engine, version, tia));
                return;
            }
            string rootKey = RootKey(profile);
            var root = File.Exists(profile.Path) ? ConfigCore.Json().DeserializeObject(StripJsonComments(File.ReadAllText(profile.Path))) as Dictionary<string, object> : new Dictionary<string, object>();
            if (root == null) throw new InvalidDataException("现有客户端配置不是 JSON 对象，未修改。");
            object raw;
            var servers = root.TryGetValue(rootKey, out raw) ? raw as Dictionary<string, object> : new Dictionary<string, object>();
            if (servers == null) throw new InvalidDataException("现有 " + rootKey + " 不是 JSON 对象，未修改。");
            servers[name] = entry; root[rootKey] = servers;
            ConfigCore.AtomicJson(profile.Path, root);
        }

        // Remote definitions are named tia-portal-vm everywhere; local stdio ones tia-portal, matching the plugin.
        public static string ServerName(ClientProfile profile, bool remote)
        {
            return remote ? "tia-portal-vm" : "tia-portal";
        }

        // JSONC comments/trailing commas are common in editor configs. Strings and URLs stay intact.
        public static string StripJsonComments(string text)
        {
            var result = new StringBuilder(); bool quoted = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted) { result.Append(c); if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') { quoted = true; result.Append(c); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; result.Append('\n'); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new InvalidDataException("JSON 注释未闭合，未修改配置。");
                    i = end + 1; result.Append(' '); continue;
                }
                result.Append(c);
            }
            text = result.ToString(); result.Clear(); quoted = false; escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!quoted && c == ',') { int j = i + 1; while (j < text.Length && Char.IsWhiteSpace(text[j])) j++; if (j < text.Length && (text[j] == '}' || text[j] == ']')) continue; }
                result.Append(c);
                if (escaped) escaped = false; else if (quoted && c == '\\') escaped = true; else if (c == '"') quoted = !quoted;
            }
            return result.ToString();
        }

        private static string TomlString(string text) { return ConfigCore.Json().Serialize(text); }

        // Conservative table editor: preserves all unrelated bytes. Complex root/dotted/inline
        // definitions are rejected rather than producing duplicate tables or guessing at user intent.
        public static string MergeToml(string text, string name, bool remote, string address, int port, string key, string engine, int version, string tia)
        {
            string token = "(?:" + Regex.Escape(name) + "|\"" + Regex.Escape(name) + "\"|'" + Regex.Escape(name) + "')";
            string mcp = "(?:mcp_servers|\"mcp_servers\"|'mcp_servers')";
            var target = new Regex(@"^\s*\[\s*" + mcp + @"\s*\.\s*" + token + @"\s*(?:\.[^\]]+)?\s*\]\s*(?:#.*)?$");
            var header = new Regex(@"^\s*\[.*\]\s*(?:#.*)?$");
            bool skipping = false, inParent = false;
            string multiline = null;
            var kept = new StringBuilder();
            foreach (string line in Regex.Split(text, "(?<=\n)"))
            {
                string trimmed = line.Trim();
                if (multiline == null && header.IsMatch(trimmed)) { skipping = target.IsMatch(trimmed); inParent = Regex.IsMatch(trimmed, @"^\[\s*" + mcp + @"\s*\]\s*(?:#.*)?$"); }
                if (multiline == null && (Regex.IsMatch(trimmed, @"^" + mcp + @"\s*(?:=|\.)") || (inParent && Regex.IsMatch(trimmed, "^" + token + @"\s*(?:=|\.)")) || trimmed.StartsWith("[[mcp_servers." + name)))
                    throw new InvalidDataException("Codex 配置使用内联或特殊 MCP 表定义，无法安全合并；原文件未修改。");
                if (!skipping) kept.Append(line);
                ScanTomlStrings(line, ref multiline);
            }
            if (multiline != null) throw new InvalidDataException("TOML 多行字符串未闭合，原文件未修改。");
            var block = new StringBuilder(); block.AppendLine("[mcp_servers." + name + "]");
            if (remote)
            {
                ConfigCore.ValidateKey(key);
                block.AppendLine("url = " + TomlString(ConfigCore.Prefix(address, port) + "mcp"));
                block.AppendLine("http_headers = { Authorization = " + TomlString("Bearer " + key) + " }");
            }
            else
            {
                block.AppendLine("command = " + TomlString(engine));
                block.AppendLine("args = [" + String.Join(", ", new[] { "--tia-major-version", version.ToString(), "--tia-portal-location", tia }.Select(TomlString)) + "]");
            }
            block.AppendLine("startup_timeout_sec = 120"); block.AppendLine("tool_timeout_sec = 300");
            return kept.ToString() + (kept.Length > 0 ? Environment.NewLine : "") + block;
        }

        private static void ScanTomlStrings(string line, ref string multiline)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                if (multiline != null)
                {
                    if (multiline == "\"\"\"" && line[i] == '\\') { i++; continue; }
                    if (i + 2 < line.Length && line.Substring(i, 3) == multiline)
                    {
                        char endQuote = multiline[0]; multiline = null; i += 2;
                        while (i + 1 < line.Length && line[i + 1] == endQuote) i++;
                    }
                    continue;
                }
                if (quote != '\0')
                {
                    if (quote == '"' && line[i] == '\\') i++;
                    else if (line[i] == quote) quote = '\0';
                    continue;
                }
                if (line[i] == '#') break;
                if (line[i] == '"' || line[i] == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == line[i] && line[i + 2] == line[i]) { multiline = line.Substring(i, 3); i += 2; }
                    else quote = line[i];
                }
            }
            if (quote != '\0') throw new InvalidDataException("TOML 字符串未闭合，原文件未修改。");
        }
    }
}

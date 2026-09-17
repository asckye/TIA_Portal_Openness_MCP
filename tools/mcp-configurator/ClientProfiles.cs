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
        public string DisplayName { get { return Id == "claude" ? "Claude Desktop" : Id == "vscode" ? "VS Code · Copilot" : Id == "cline" ? "Cline" : Name; } }
        public string Category { get { return Id == "claude" ? "CHAT" : Id == "cline" ? "VS CODE" : new[] { "claude-code", "codex", "gemini" }.Contains(Id) ? "CLI" : "IDE"; } }
        public override string ToString() { return Name; }
        public ClientProfile(string id, string name, string path, string hint) { Id = id; Name = name; Path = path; Hint = hint; }
    }

    public static class ClientProfiles
    {
        public static List<ClientProfile> All()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string codex = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codex)) codex = System.IO.Path.Combine(home, ".codex");
            return new List<ClientProfile> {
                new ClientProfile("claude-code", "Claude Code", System.IO.Path.Combine(home, ".claude.json"), "官方桌面客户端：重启后选择 Code → Local，新建会话。CLI 也使用此配置。"),
                new ClientProfile("codex", "Codex", System.IO.Path.Combine(codex, "config.toml"), "保存后重启 Codex 桌面应用 / CLI，重新打开任务以加载 MCP。"),
                new ClientProfile("cursor", "Cursor", System.IO.Path.Combine(home, ".cursor", "mcp.json"), "重启 Cursor，在 MCP 设置中检查 tia-portal-vm 并启用。"),
                new ClientProfile("vscode", "VS Code / Copilot", System.IO.Path.Combine(app, "Code", "User", "mcp.json"), "适用于 VS Code 默认用户配置。重载窗口，在 MCP 服务器列表中启动并信任该服务。"),
                new ClientProfile("claude", "Claude Desktop · Chat", System.IO.Path.Combine(app, "Claude", "claude_desktop_config.json"), "远程连接使用 mcp-remote 桥接：需安装 Node.js LTS（含 npx）。首次启动会联网下载 npm 包。Code 页请选 Claude Code。"),
                new ClientProfile("gemini", "Gemini CLI", System.IO.Path.Combine(home, ".gemini", "settings.json"), "重启 Gemini CLI，使用 /mcp 检查服务器状态。"),
                new ClientProfile("windsurf", "Windsurf", System.IO.Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"), "重启 Windsurf，在 Cascade 的 MCP 设置中刷新服务器。"),
                new ClientProfile("cline", "Cline · VS Code", System.IO.Path.Combine(app, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"), "适用于 VS Code 默认配置中的 Cline 扩展。重载窗口并在 MCP 设置中启用服务。")
            };
        }

        public static Dictionary<string, object> Entry(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia)
        {
            if (!remote)
            {
                var local = new Dictionary<string, object> { { "command", engine }, { "args", new[] { "--tia-major-version", version.ToString(), "--tia-portal-location", tia } } };
                if (profile.Id == "claude-code" || profile.Id == "vscode" || profile.Id == "cline" || profile.Id == "cursor") local["type"] = "stdio";
                return local;
            }
            ConfigCore.ValidateKey(key);
            string url = ConfigCore.Prefix(address, port) + "mcp";
            if (profile.Id == "claude")
                return new Dictionary<string, object> {
                    { "command", "cmd.exe" },
                    { "args", new[] { "/d", "/c", "npx", "-y", "mcp-remote", url, "--allow-http", "--transport", "http-only", "--header", "Authorization:${TIA_MCP_AUTH_HEADER}" } },
                    { "env", new Dictionary<string, object> { { "TIA_MCP_AUTH_HEADER", "Bearer " + key } } } };
            var entry = new Dictionary<string, object> {
                { profile.Id == "gemini" ? "httpUrl" : profile.Id == "windsurf" ? "serverUrl" : "url", url },
                { "headers", new Dictionary<string, object> { { "Authorization", "Bearer " + key } } } };
            if (profile.Id == "claude-code" || profile.Id == "vscode") entry["type"] = "http";
            if (profile.Id == "cline") { entry["type"] = "streamableHttp"; entry["disabled"] = false; entry["autoApprove"] = new string[0]; }
            return entry;
        }

        public static void Save(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia)
        {
            var entry = Entry(profile, remote, address, port, key, engine, version, tia);
            string name = ServerName(profile, remote);
            if (profile.Id == "codex")
            {
                string original = File.Exists(profile.Path) ? File.ReadAllText(profile.Path) : "";
                ConfigCore.AtomicText(profile.Path, MergeToml(original, name, remote, address, port, key, engine, version, tia));
                return;
            }
            string rootKey = profile.Id == "vscode" ? "servers" : "mcpServers";
            var root = File.Exists(profile.Path) ? ConfigCore.Json().DeserializeObject(StripJsonComments(File.ReadAllText(profile.Path))) as Dictionary<string, object> : new Dictionary<string, object>();
            if (root == null) throw new InvalidDataException("现有客户端配置不是 JSON 对象，未修改。");
            object raw;
            var servers = root.TryGetValue(rootKey, out raw) ? raw as Dictionary<string, object> : new Dictionary<string, object>();
            if (servers == null) throw new InvalidDataException("现有 " + rootKey + " 不是 JSON 对象，未修改。");
            servers[name] = entry; root[rootKey] = servers;
            ConfigCore.AtomicJson(profile.Path, root);
        }

        public static string ServerName(ClientProfile profile, bool remote)
        {
            // Claude Code also reads Desktop Chat configuration. Keep the bridge from
            // overriding the direct HTTP definition when both clients are selected.
            return remote ? (profile.Id == "claude" ? "tia-portal-vm-chat" : "tia-portal-vm") : "tia-portal";
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

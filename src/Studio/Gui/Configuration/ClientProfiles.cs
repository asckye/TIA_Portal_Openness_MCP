using System.Globalization;
using System.ComponentModel;
using System.Windows;
using TiaOpenness.Gui.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpConfigurator
{
    public sealed class ClientProfile : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        // Resolve guidance and detection evidence on read so existing cards follow language changes.
        private LocalizedText hint;
        public string Hint { get { return hint.Resolve(); } set { hint = LocalizedText.Literal(value); } }
        // Schema family that decides file layout and entry shape. Brand cards (Qwen → Qwen Code, DeepSeek / GLM / Grok → OpenCode …)
        // write another product's file, so several profiles may map onto one Client.
        public string Client { get; set; }
        public string Kind { get; set; }      // CLI / Desktop / IDE
        // whether this machine shows traces of the client (config folder, executable on PATH, install folder,
        // uninstall registry entry) and what was found - so a user on any computer sees which cards apply here.
        public bool Detected { get; set; }
        private string evidence = "";
        private IReadOnlyList<LocalizedText>? detectionEvidence;
        public string Evidence
        {
            get { return detectionEvidence == null ? evidence : detectionEvidence.Count == 0 ? Loc.Current["Config.ClientNotFound"] : String.Join(Loc.Current["Config.ReasonSeparator"], detectionEvidence.Select(x => x.Resolve())); }
            set { evidence = value; detectionEvidence = null; }
        }
        internal void SetDetectionEvidence(IReadOnlyList<LocalizedText> found) { detectionEvidence = found; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        public string DisplayName { get { return Id == "vscode" ? "VS Code · Copilot" : Name; } }
        // Tile subtitle without the detection state: the transport family plus, when the card is a model brand, the client that gets written.
        public string CategoryBase { get { string label = ClientProfiles.ClientLabel(Client); return label == null ? Kind : Kind + " · " + label; } }
        public string Category { get { return CategoryBase + " · " + (Detected ? Loc.Current["Config.ClientDetected"] : Loc.Current["Config.NotDetected"]); } }
        // Tooltip: the after-save instructions plus where the client was (not) found and the file that will be written.
        public string Tooltip { get { return Loc.Current.T(Detected ? "Config.ClientTooltipDetected" : "Config.ClientTooltipNotDetected", Hint, Evidence, Path); } }
        public override string ToString() { return Name; }
        public ClientProfile(string id, string name, string path, string hint, string client = null, string kind = "CLI")
        {
            Id = id; Name = name; Path = path; Hint = hint; Client = client ?? id; Kind = kind; Evidence = "";
            PropertyChangedEventManager.AddHandler(Loc.Current, OnLanguageChanged, nameof(Loc.Language));
        }
        internal ClientProfile(string id, string name, string path, LocalizedText hint, string client = null, string kind = "CLI")
            : this(id, name, path, "", client, kind)
        {
            this.hint = hint;
        }
    }

    public static class ClientProfiles
    {
        public static string ConnectionSnippet(string address, int port)
        {
            var profile = new ClientProfile("claude-code", "Claude Code", "", "");
            var servers = new Dictionary<string, object> { { ServerName(profile, true), Entry(profile, true, address, port, "••••", "", "21", "") } };
            return ConfigCore.Json().Serialize(new Dictionary<string, object> { { RootKey(profile), servers } });
        }

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
            // VS Code: the stable user folder, or Insiders when only that one exists on this machine.
            string vscodeUser = System.IO.Path.Combine(app, "Code", "User");
            if (!Directory.Exists(vscodeUser) && Directory.Exists(System.IO.Path.Combine(app, "Code - Insiders", "User"))) vscodeUser = System.IO.Path.Combine(app, "Code - Insiders", "User");
            var profiles = new List<ClientProfile> {
                new ClientProfile("claude-code", "Claude Code", System.IO.Path.Combine(home, ".claude.json"), LocalizedText.Key("Config.ClientHint.ClaudeCode")),
                new ClientProfile("codex", "Codex", System.IO.Path.Combine(codex, "config.toml"), LocalizedText.Key("Config.ClientHint.Codex")),
                new ClientProfile("gemini", "Gemini CLI", System.IO.Path.Combine(home, ".gemini", "settings.json"), LocalizedText.Key("Config.ClientHint.Gemini")),
                // 国产模型与 Grok 没有自带 MCP 客户端，卡片按模型命名，实际写入各家官方 CLI 或 OpenCode。
                new ClientProfile("qwen", "Qwen", System.IO.Path.Combine(home, ".qwen", "settings.json"), LocalizedText.Key("Config.ClientHint.Qwen")),
                new ClientProfile("kimi", "Kimi", System.IO.Path.Combine(kimi, "mcp.json"), LocalizedText.Key("Config.ClientHint.Kimi")),
                new ClientProfile("codebuddy", "Yuanbao", System.IO.Path.Combine(home, ".codebuddy", ".mcp.json"), LocalizedText.Key("Config.ClientHint.CodeBuddy")),
                new ClientProfile("deepseek", "DeepSeek", opencode, LocalizedText.Key("Config.ClientHint.OpenCode", "DeepSeek"), "opencode"),
                new ClientProfile("zhipu", "GLM", opencode, LocalizedText.Key("Config.ClientHint.OpenCode", LocalizedText.Key("Config.ClientModel.GLM")), "opencode"),
                new ClientProfile("grok", "Grok", opencode, LocalizedText.Key("Config.ClientHint.OpenCode", "xAI Grok"), "opencode"),
                // 千问工作助理（桌面应用）只读 ~/.qwen-agent/mcp.json（或项目下 .qwen-agent/mcp.json）：url + Bearer 头，不走 OAuth；改完必须完全退出进程再打开才会重新读取。
                new ClientProfile("qwen-agent", "Qwen Agent", System.IO.Path.Combine(home, ".qwen-agent", "mcp.json"), LocalizedText.Key("Config.ClientHint.QwenAgent"), "qwen-agent", "Desktop"),
                new ClientProfile("cursor", "Cursor", System.IO.Path.Combine(home, ".cursor", "mcp.json"), LocalizedText.Key("Config.ClientHint.Cursor"), null, "IDE"),
                new ClientProfile("vscode", "VS Code / Copilot", System.IO.Path.Combine(vscodeUser, "mcp.json"), LocalizedText.Key("Config.ClientHint.VsCode"), null, "IDE")
            };
            foreach (var profile in profiles) Detect(profile);
            return profiles;
        }

        // ---- where is the client on THIS machine? ------------------------------------------------------------
        // Evidence, in order: the config folder / file the card writes, the executable on PATH (npm shims are .cmd), a known install
        // folder, an uninstall entry in the registry. Nothing here needs the client to be running; nothing is written.
        public static void Detect(ClientProfile profile)
        {
            var found = new List<LocalizedText>();
            try
            {
                string dir = System.IO.Path.GetDirectoryName(profile.Path);
                if (File.Exists(profile.Path)) found.Add(LocalizedText.Key("Config.ClientConfigFile", profile.Path));
                else if (dir != null && Directory.Exists(dir)) found.Add(LocalizedText.Key("Config.ClientConfigDirectory", dir));
                foreach (var exe in ExecutableNames(profile.Client))
                {
                    string hit = OnPath(exe);
                    if (hit != null) { found.Add(LocalizedText.Key("Config.ClientExecutable", System.IO.Path.GetFileName(hit))); break; }
                }
                foreach (var folder in InstallFolders(profile.Client))
                    if (Directory.Exists(folder)) { found.Add(LocalizedText.Key("Config.ClientInstallDirectory", folder)); break; }
                string registry = UninstallEntry(RegistryKeywords(profile.Client));
                if (registry != null) found.Add(LocalizedText.Key("Config.ClientInstalledProgram", registry));
            }
            catch (Exception) /* swallow(env-probe): client detection is advisory; retain evidence collected before a local installation probe failed */ { }
            profile.Detected = found.Count > 0;
            profile.SetDetectionEvidence(found);
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
                    catch (Exception) /* swallow(env-probe): an invalid or inaccessible PATH candidate does not prevent checking the remaining executable locations */ { }
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
                catch (Exception) /* swallow(env-probe): an inaccessible uninstall registry root does not prevent checking the remaining roots */ { }
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

        public static Dictionary<string, object> Entry(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia) { return Entry(profile, remote, address, port, key, engine, version.ToString(CultureInfo.InvariantCulture), tia); }

        public static Dictionary<string, object> Entry(ClientProfile profile, bool remote, string address, int port, string key, string engine, string version, string tia)
        {
            string client = profile.Client;
            if (!remote)
            {
                var localArgs = ConfigCore.LocalArguments(engine, version, tia);
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

        public static void Save(ClientProfile profile, bool remote, string address, int port, string key, string engine, int version, string tia, bool confirmed = false)
        { Save(profile, remote, address, port, key, engine, version.ToString(CultureInfo.InvariantCulture), tia, confirmed); }

        public static void Save(ClientProfile profile, bool remote, string address, int port, string key, string engine, string version, string tia, bool confirmed = false)
        { PrepareSave(profile, remote, address, port, key, engine, version, tia).Apply(confirmed); }

        public static ClientConfigurationChange PrepareSave(ClientProfile profile, bool remote, string address, int port, string key, string engine, string version, string tia)
        {
            var entry = Entry(profile, remote, address, port, key, engine, version, tia);
            string name = ServerName(profile, remote);
            byte[] original = File.Exists(profile.Path) ? File.ReadAllBytes(profile.Path) : null;
            string text = "";
            if (original != null)
                using (var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, true)) text = reader.ReadToEnd();
            if (profile.Client == "codex")
            {
                string merged = MergeToml(text, name, remote, address, port, key, engine, version, tia);
                bool migration = false;
                if (!remote) merged = MigrateToml(text, name, engine, ConfigCore.LocalArguments(engine, version, tia), merged, out migration);
                return new ClientConfigurationChange(profile.Path, original, merged, migration, remote ? null : engine);
            }
            string rootKey = RootKey(profile);
            var root = original != null ? ConfigCore.Json().DeserializeObject(StripJsonComments(text)) as Dictionary<string, object> : new Dictionary<string, object>();
            if (root == null) throw new InvalidDataException(Loc.Current["Config.InvalidClientJson"]);
            object raw;
            var servers = root.TryGetValue(rootKey, out raw) ? raw as Dictionary<string, object> : new Dictionary<string, object>();
            if (servers == null) throw new InvalidDataException(Loc.Current.T("Config.InvalidServerMap", rootKey));
            bool migrate = false;
            if (!remote && servers.TryGetValue(name, out var previous))
            {
                var existing = previous as Dictionary<string, object>;
                if (existing == null) throw new InvalidDataException(Loc.Current["Config.InvalidClientJson"]);
                migrate = existing.TryGetValue("command", out var command)
                    && ConfigCore.Json().Serialize(command) != ConfigCore.Json().Serialize(entry["command"]);
                migrate |= entry.TryGetValue("args", out var desiredArgs)
                    && (!existing.TryGetValue("args", out var previousArgs) || ConfigCore.Json().Serialize(previousArgs) != ConfigCore.Json().Serialize(desiredArgs));
                // A product migration replaces only command/args; URLs, auth, env and
                // client-specific options on the existing entry remain intact.
                if (existing.ContainsKey("command"))
                {
                    existing["command"] = entry["command"];
                    if (entry.TryGetValue("args", out var arguments)) existing["args"] = arguments;
                    entry = existing;
                }
            }
            servers[name] = entry; root[rootKey] = servers;
            return new ClientConfigurationChange(profile.Path, original, ConfigCore.Json().Serialize(root), migrate, remote ? null : engine);
        }

        private static string MigrateToml(string text, string name, string engine, string[] arguments, string added, out bool migration)
        {
            // MergeToml has already rejected ambiguous root/dotted/inline definitions.
            // Keep all bytes except the two local launch fields in the exact table.
            string token = "(?:" + Regex.Escape(name) + "|\"" + Regex.Escape(name) + "\"|'" + Regex.Escape(name) + "')";
            var target = new Regex(@"^\s*\[\s*(?:mcp_servers|" + "\"mcp_servers\"|'mcp_servers')" + @"\s*\.\s*" + token + @"\s*\]\s*(?:#.*)?$");
            var header = new Regex(@"^\s*\[.*\]\s*(?:#.*)?$");
            var result = new StringBuilder();
            bool inside = false, found = false, command = false, args = false;
            string multiline = null;
            migration = false;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            Action finish = () =>
            {
                if (!inside) return;
                if (result.Length > 0 && result[result.Length - 1] != '\n') result.Append(newline);
                if (!command) result.Append("command = " + TomlString(engine) + newline);
                if (!args) result.Append("args = [" + String.Join(", ", arguments.Select(TomlString)) + "]" + newline);
            };
            foreach (string line in Regex.Split(text, "(?<=\n)"))
            {
                string trimmed = line.Trim();
                if (multiline == null && header.IsMatch(trimmed))
                {
                    finish(); inside = target.IsMatch(trimmed);
                    if (inside && found) throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                    found |= inside;
                }
                var field = multiline == null && inside ? Regex.Match(trimmed, @"^(command|args)\s*=") : Match.Empty;
                if (field.Success)
                {
                    string value = trimmed.Substring(field.Length).Trim();
                    if (value.Contains("\"\"\"") || value.Contains("'''")) throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                    if (field.Groups[1].Value == "command")
                    {
                        if (command) throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                        command = true;
                        // Accept basic and literal single-line strings, preserving the rest.
                        if (!Regex.IsMatch(value, "^(\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*')\\s*(?:#.*)?$"))
                            throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                        result.Append("command = " + TomlString(engine) + newline);
                    }
                    else
                    {
                        if (args || !value.StartsWith("[") || !Regex.IsMatch(value, @"\]\s*(?:#.*)?$"))
                            throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                        args = true;
                        result.Append("args = [" + String.Join(", ", arguments.Select(TomlString)) + "]" + newline);
                    }
                }
                else result.Append(line);
                ScanTomlStrings(line, ref multiline);
            }
            finish();
            migration = found;
            return found ? result.ToString() : added;
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
                    if (end < 0) throw new InvalidDataException(Loc.Current["Config.UnclosedJsonComment"]);
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
        public static string MergeToml(string text, string name, bool remote, string address, int port, string key, string engine, int version, string tia) { return MergeToml(text, name, remote, address, port, key, engine, version.ToString(CultureInfo.InvariantCulture), tia); }

        public static string MergeToml(string text, string name, bool remote, string address, int port, string key, string engine, string version, string tia)
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
                    throw new InvalidDataException(Loc.Current["Config.UnsupportedTomlTable"]);
                if (!skipping) kept.Append(line);
                ScanTomlStrings(line, ref multiline);
            }
            if (multiline != null) throw new InvalidDataException(Loc.Current["Config.UnclosedTomlMultiline"]);
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
                block.AppendLine("args = [" + String.Join(", ", ConfigCore.LocalArguments(engine, version, tia).Select(TomlString)) + "]");
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
            if (quote != '\0') throw new InvalidDataException(Loc.Current["Config.UnclosedTomlString"]);
        }
    }
}

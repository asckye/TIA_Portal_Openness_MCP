using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TiaMcpConfigurator
{
    public static class Tests
    {
        private static int passed;
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            Assert(rejected, name);
        }

        private static void Probe(bool unauthorized)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(delegate {
                for (int i = 0; i < 2; i++)
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string request = reader.ReadLine(), line;
                        bool auth = false;
                        while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                            if (line.Equals("Authorization: Bearer test-secret", StringComparison.OrdinalIgnoreCase)) auth = true;
                        if (i == 0 && !request.Contains("/mcp/health")) throw new Exception("Wrong health path");
                        if (i == 1 && (!request.Contains("/mcp/ready") || !auth)) throw new Exception("Missing readiness auth");
                        bool failure = unauthorized && i == 1;
                        string body = i == 0 ? "{\"fileVersion\":\"test\"}" : "{\"mcpHostReady\":true}";
                        string response = "HTTP/1.1 " + (failure ? "401 Unauthorized" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                        byte[] bytes = Encoding.UTF8.GetBytes(response); stream.Write(bytes, 0, bytes.Length);
                    }
                }
            });
            try
            {
                if (unauthorized) Reject(() => ConfigCore.TestRemote("127.0.0.1", port, "test-secret"), "HTTP 401 is not reported as ready");
                else Assert(ConfigCore.TestRemote("127.0.0.1", port, "test-secret").Contains("test"), "authenticated remote readiness and health");
                serving.GetAwaiter().GetResult();
            }
            finally { listener.Stop(); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length > 0 && args[0] == "--echo")
            {
                Console.WriteLine(ConfigCore.Json().Serialize(args.Skip(1).ToArray())); return 0;
            }
            try
            {
                string output = Path.GetFullPath(args[0]);
                string temp = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
                Assert(ConfigCore.Prefix("192.0.2.10", 8765) == "http://192.0.2.10:8765/", "canonical IPv4 endpoint");
                Reject(() => ConfigCore.Prefix("[http://192.0.2.10/](http://192.0.2.10/)", 8765), "reject markdown URL");
                Reject(() => ConfigCore.Prefix("0.0.0.0", 8765), "reject wildcard binding");
                Reject(() => ConfigCore.Prefix("192.0.2.10", 0), "reject invalid port");
                Reject(() => ConfigCore.Protect(""), "reject empty key");
                Reject(() => ConfigCore.RemoteEntry("192.0.2.10", 8765, "a\r\nb"), "reject header injection");
                // 安装目录自动探测：只读且不抛异常；环境变量指向没有 Openness 的目录时必须拒绝，而不是把它当成安装根
                string savedLocation = Environment.GetEnvironmentVariable("TiaPortalLocation");
                try
                {
                    string fakeRoot = Path.Combine(temp, "Portal V21"); Directory.CreateDirectory(fakeRoot);
                    Environment.SetEnvironmentVariable("TiaPortalLocation", fakeRoot);
                    var detected = ConfigCore.DetectTia(21);
                    Assert(detected.Key != fakeRoot, "env var without PublicAPI is not accepted as V21 install root");
                    Assert(detected.Key == null || Directory.Exists(Path.Combine(detected.Key, "PublicAPI")), "detected V21 root, if any, carries PublicAPI");
                    Assert(!string.IsNullOrEmpty(detected.Value), "detection always explains its source or failure");
                    Directory.CreateDirectory(Path.Combine(fakeRoot, "PublicAPI", "V21", "net48"));
                    File.WriteAllText(Path.Combine(fakeRoot, "PublicAPI", "V21", "net48", "Siemens.Engineering.Base.dll"), "stub");
                    Assert(ConfigCore.DetectTia(21).Key == fakeRoot, "env var with a V21 PublicAPI is detected first");
                    Assert(ConfigCore.DetectTia(20).Key != fakeRoot, "a V21 path is never reported for V20");
                }
                finally { Environment.SetEnvironmentVariable("TiaPortalLocation", savedLocation); }
                string secret = "test spaces % ! & * \\\"中文";
                string encrypted = ConfigCore.Protect(secret);
                Assert(encrypted != secret && ConfigCore.Unprotect(encrypted) == secret, "DPAPI roundtrip including special characters");

                string config = Path.Combine(temp, "claude.json");
                string original = "{\"theme\":\"dark\",\"projects\":{\"D:/demo\":{\"allowedTools\":[\"one\"]}},\"mcpServers\":{\"other\":{\"command\":\"keep.exe\"},\"tia-portal-vm\":{\"url\":\"old\"}}}";
                File.WriteAllText(config, original);
                ConfigCore.MergeServer(config, "tia-portal-vm", ConfigCore.RemoteEntry("192.0.2.10", 8765, secret));
                var root = ConfigCore.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(config));
                var servers = (Dictionary<string, object>)root["mcpServers"];
                Assert((string)root["theme"] == "dark" && root.ContainsKey("projects") && servers.ContainsKey("other"), "merge preserves unrelated settings and servers");
                Assert(File.ReadAllText(Directory.GetFiles(temp, "claude.json.bak_*").Single()) == original, "exact pre-write backup");
                var remote = (Dictionary<string, object>)servers["tia-portal-vm"];
                Assert((string)remote["url"] == "http://192.0.2.10:8765/mcp" && (string)((Dictionary<string, object>)remote["headers"])["Authorization"] == "Bearer " + secret, "remote config schema and secret roundtrip");
                string bad = Path.Combine(temp, "bad.json"); File.WriteAllText(bad, "{broken");
                Reject(() => ConfigCore.MergeServer(bad, "x", remote), "malformed JSON is rejected");
                Assert(File.ReadAllText(bad) == "{broken", "malformed config untouched");
                File.WriteAllText(bad, "{\"mcpServers\":[]}");
                Reject(() => ConfigCore.MergeServer(bad, "x", remote), "non-object server map rejected");
                Reject(() => ConfigCore.Engine(temp, 21), "missing runtime gives actionable failure");
                Reject(() => ConfigCore.ValidateTia(temp, 21), "missing TIA API rejected");

                string[] roundtrip = { "", secret, "C:\\space path\\", "a\\\"b", "\"", "a&echo nope", "end\\\\" };
                var info = new ProcessStartInfo(Application.ExecutablePath, "--echo " + String.Join(" ", roundtrip.Select(ConfigCore.Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
                // Child console uses the same explicit UTF-8 encoding below.
                using (var child = Process.Start(info))
                {
                    string text = child.StandardOutput.ReadToEnd(); child.WaitForExit();
                    var actual = ConfigCore.Json().Deserialize<string[]>(text);
                    Assert(actual.SequenceEqual(roundtrip), "native process argument quoting preserves secrets and paths");
                }
                Probe(false); Probe(true);
                var profiles = ClientProfiles.All();
                Assert(profiles.Count == 12, "twelve cards: Claude Code, Codex, Gemini CLI, Qwen, Kimi, Yuanbao, DeepSeek, Zhipu GLM, Grok, Qwen Agent, Cursor, VS Code");
                Assert(profiles.All(x => x.Name.All(c => c < 128) && x.Kind.All(c => c < 128)), "2.7.62: every client name and kind is English (maintainer)");
                Assert(profiles[0].Id == "claude-code" && profiles[1].Id == "codex", "Claude Code and Codex are the first two cards");
                Assert(profiles.TakeWhile(x => x.Kind == "CLI").Count() == 9 && profiles[9].Kind == "Desktop" && profiles.Skip(10).All(x => x.Kind == "IDE"), "CLI cards, then the desktop assistant, then IDE cards");
                // 2.7.61: detection never throws, every card carries evidence text, and the qwen-agent file is url + headers without a type
                Assert(profiles.All(x => !String.IsNullOrEmpty(x.Evidence) && x.Category.EndsWith(x.Detected ? "已检测" : "未检测到") && x.Tooltip.Contains(x.Path)), "every card reports what was (not) found on this machine");
                var agent = profiles.First(x => x.Id == "qwen-agent");
                var agentEntry = ClientProfiles.Entry(agent, true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert(agent.Path.EndsWith(Path.Combine(".qwen-agent", "mcp.json")) && ClientProfiles.RootKey(agent) == "mcpServers" && agentEntry.ContainsKey("url") && !agentEntry.ContainsKey("type") && ((Dictionary<string, object>)agentEntry["headers"]).ContainsKey("Authorization"), "Qwen Agent writes ~/.qwen-agent/mcp.json with url + Bearer header and no type");
                Assert(ClientProfiles.OnPath("cmd") != null && ClientProfiles.OnPath("no-such-executable-2761") == null, "PATH lookup finds cmd.exe and nothing for a bogus name");
                Assert(profiles.First(x => x.Id == "vscode").Path.EndsWith(Path.Combine("User", "mcp.json")), "VS Code writes the user mcp.json of the edition present on this machine");
                Assert(!profiles.Any(x => x.Id == "windsurf" || x.Id == "cline" || x.Id == "claude"), "Windsurf, Cline and Claude Desktop cards are gone");
                Assert(profiles.Select(x => x.Id).Distinct().Count() == profiles.Count, "card ids are unique");
                foreach (var profile in profiles)
                {
                    var testProfile = new ClientProfile(profile.Id, profile.Name, Path.Combine(temp, profile.Id + (profile.Client == "codex" ? ".toml" : ".json")), profile.Hint, profile.Client, profile.Kind);
                    if (profile.Client == "codex") File.WriteAllText(testProfile.Path, "# preserved\r\nmodel = \"keep\"\r\n[mcp_servers.other]\r\ncommand = \"other.exe\"\r\n");
                    else File.WriteAllText(testProfile.Path, "{ // existing settings\n\"keep\": true, \"" + ClientProfiles.RootKey(profile) + "\": {\"other\":{\"command\":\"keep.exe\"},},}");
                    ClientProfiles.Save(testProfile, true, "192.0.2.10", 8765, secret, null, 21, null);
                    string saved = File.ReadAllText(testProfile.Path);
                    Assert(saved.Contains("other") && saved.Contains("keep") && saved.Contains("tia-portal-vm"), profile.Name + " remote merge preserves other config");
                    if (profile.Client != "codex")
                    {
                        var doc = ConfigCore.Json().Deserialize<Dictionary<string, object>>(saved);
                        var map = (Dictionary<string, object>)doc[ClientProfiles.RootKey(profile)];
                        var entry = (Dictionary<string, object>)map[ClientProfiles.ServerName(profile, true)];
                        Assert((string)entry[ClientProfiles.UrlKey(profile)] == "http://192.0.2.10:8765/mcp", profile.Name + " native HTTP schema");
                    }
                    ClientProfiles.Save(testProfile, false, null, 0, null, @"C:\bundle space\runtime\v21\TiaMcpServer.exe", 21, @"C:\Siemens\Portal V21");
                    Assert(File.ReadAllText(testProfile.Path).Contains("--tia-portal-location"), profile.Name + " local stdio saves explicit TIA path");
                }
                // 国产模型入口的客户端各自有独立 schema，泛型循环只核对了 URL 字段；这里盯住会被静默接受但客户端读不懂的形状。
                var qwen = ClientProfiles.Entry(profiles.First(x => x.Id == "qwen"), true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert(qwen.ContainsKey("httpUrl") && !qwen.ContainsKey("url") && !qwen.ContainsKey("type"), "Qwen → Qwen Code uses Gemini-style httpUrl without a type field");
                var kimi = ClientProfiles.Entry(profiles.First(x => x.Id == "kimi"), true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert(kimi.ContainsKey("url") && !kimi.ContainsKey("type") && ((Dictionary<string, object>)kimi["headers"]).ContainsKey("Authorization"), "Kimi → Kimi Code CLI uses plain url + headers");
                Assert(profiles.First(x => x.Id == "kimi").Path.EndsWith("mcp.json"), "Kimi Code CLI writes mcp.json, not config.toml");
                var buddy = ClientProfiles.Entry(profiles.First(x => x.Id == "codebuddy"), true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert((string)buddy["type"] == "http" && buddy.ContainsKey("url") && profiles.First(x => x.Id == "codebuddy").Path.EndsWith(".mcp.json"), "Yuanbao → CodeBuddy uses type=http in .codebuddy\\.mcp.json");
                var brands = profiles.Where(x => x.Client == "opencode").ToList();
                Assert(brands.Select(x => x.Id).SequenceEqual(new[] { "deepseek", "zhipu", "grok" }) && brands.Select(x => x.Path).Distinct().Count() == 1, "DeepSeek / 智谱 / Grok are brand cards over one OpenCode file");
                Assert(brands.All(x => x.CategoryBase == "CLI · OpenCode") && profiles.First(x => x.Id == "qwen").CategoryBase == "CLI · Qwen Code" && profiles.First(x => x.Id == "codex").CategoryBase == "CLI" && agent.CategoryBase == "Desktop", "brand cards show the client they write to; native cards show only the kind");
                var openRemote = ClientProfiles.Entry(brands[0], true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert((string)openRemote["type"] == "remote" && (bool)openRemote["enabled"] && (string)openRemote["url"] == "http://192.0.2.10:8765/mcp", "OpenCode remote entry carries type=remote and enabled");
                var openLocal = ClientProfiles.Entry(brands[0], false, null, 0, null, @"C:\r\TiaMcpServer.exe", 21, @"C:\Siemens\Portal V21");
                var openCommand = (string[])openLocal["command"];
                Assert((string)openLocal["type"] == "local" && openCommand[0] == @"C:\r\TiaMcpServer.exe" && openCommand.Contains("--tia-portal-location") && !openLocal.ContainsKey("args"), "OpenCode local entry is one command array starting with the executable");
                Assert(ClientProfiles.RootKey(brands[0]) == "mcp" && ClientProfiles.RootKey(profiles.First(x => x.Id == "qwen")) == "mcpServers", "OpenCode servers live under 'mcp', the CLIs under 'mcpServers'");
                string originalToml = "model = \"keep\"\r\n[mcp_servers.\"tia-portal-vm\"] # old\r\nurl = \"old\"\r\n[mcp_servers.\"tia-portal-vm\".http_headers]\r\nAuthorization = \"oldsecret\"\r\n[projects.\"D:/work\"]\r\ntrust_level = \"trusted\"\r\n";
                string changedToml = ClientProfiles.MergeToml(originalToml, "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert(changedToml.Contains("[projects.\"D:/work\"]\r\ntrust_level = \"trusted\"\r\n") && !changedToml.Contains("oldsecret"), "Codex removes only target tables and preserves unrelated bytes");
                Reject(() => ClientProfiles.MergeToml("mcp_servers = {}", "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null), "Codex inline MCP table rejected without destructive merge");
                string multi = "instructions = \"\"\"\n[mcp_servers.tia-portal-vm]\nthis is text, not a table\n\"\"\"\n";
                Assert(ClientProfiles.MergeToml(multi, "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null).StartsWith(multi), "Codex multiline instructions remain byte-identical");
                Assert(profiles.All(x => ClientProfiles.ServerName(x, true) == "tia-portal-vm" && ClientProfiles.ServerName(x, false) == "tia-portal"), "every card uses tia-portal-vm remotely and tia-portal locally, matching the plugin");
                string jsonc = "{\"url\":\"http://example/a/*b*/\",/*comment*/\"list\":[1,],}";
                var parsedJsonc = ConfigCore.Json().Deserialize<Dictionary<string, object>>(ClientProfiles.StripJsonComments(jsonc));
                Assert((string)parsedJsonc["url"] == "http://example/a/*b*/", "JSONC URLs preserved while removing comments and trailing commas");
                using (var form = new ConfigWindow(false))
                {
                    var window = form.Window;
                    var generate = (System.Windows.Controls.Button)window.FindName("GenerateKey");
                    generate.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    var password = (System.Windows.Controls.PasswordBox)window.FindName("Key");
                    Assert(password.Password.Length >= 24 && ((System.Windows.Controls.TextBlock)window.FindName("KeyPlaceholder")).Visibility == System.Windows.Visibility.Collapsed, "generated key updates masked input and placeholder");
                    var reveal = (System.Windows.Controls.CheckBox)window.FindName("ShowKey");
                    reveal.IsChecked = true; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert(((System.Windows.Controls.TextBox)window.FindName("KeyVisible")).Text == password.Password, "show-key control preserves generated value");
                    reveal.IsChecked = false; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    password.Password = ""; ((System.Windows.Controls.TextBox)window.FindName("KeyVisible")).Text = "";
                    var choices = (System.Windows.Controls.ListBox)window.FindName("ClientChoices");
                    choices.SelectedItems.Add(choices.Items[1]);
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("ClientSelection")).Text.Contains("2"), "client multi-select updates live count");
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("LinkClient")).Text.Contains("2"), "link bar follows the client selection");
                    choices.SelectedItems.Clear(); choices.SelectedItems.Add(choices.Items[6]);
                    ((System.Windows.Controls.TextBox)window.FindName("ServerAddress")).Text = "192.0.2.10";
                    form.CapturePage(Path.Combine(output, "remote.png"), 0);
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("LinkEndpoint")).Text == "192.0.2.10:8765"
                        && ((System.Windows.Controls.TextBlock)window.FindName("LinkState")).Text == "idle"
                        && ((System.Windows.Controls.TextBlock)window.FindName("LinkClient")).Text == "DeepSeek", "remote mode shows the live endpoint, state and selected client");
                    Assert(((System.Windows.Controls.Border)window.FindName("SecretBand")).Visibility == System.Windows.Visibility.Visible
                        && ((System.Windows.Controls.Grid)window.FindName("AddressRow")).Visibility == System.Windows.Visibility.Visible, "remote mode shows the shared secret band and the address row");
                    form.CapturePage(Path.Combine(output, "local.png"), 1);
                    // stdio 本机模式既不用地址也不用密钥：这两块必须真的消失，否则界面在教用户填无用的值。
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("LinkEndpoint")).Text.StartsWith("stdio")
                        && ((System.Windows.Controls.TextBlock)window.FindName("LinkState")).Text == "local", "local mode reports the stdio transport");
                    Assert(((System.Windows.Controls.Border)window.FindName("SecretBand")).Visibility == System.Windows.Visibility.Collapsed
                        && ((System.Windows.Controls.Grid)window.FindName("AddressRow")).Visibility == System.Windows.Visibility.Collapsed
                        && ((System.Windows.Controls.Grid)window.FindName("ServerActions")).Visibility == System.Windows.Visibility.Collapsed
                        && ((System.Windows.Controls.Border)window.FindName("LocalNote")).Visibility == System.Windows.Visibility.Visible, "local mode hides address, secret and service controls");
                    // 右栏的卡片列表必须在卡内滚动：否则 11 张卡片会把右栏拉长，左栏被迫留一大片空白。
                    form.CapturePage(Path.Combine(output, "remote.png"), 0);
                    var list = (System.Windows.Controls.ListBox)window.FindName("ClientChoices");
                    var serverPane = (System.Windows.Controls.Border)window.FindName("ServerPane");
                    var clientPane = (System.Windows.Controls.Border)window.FindName("ClientPane");
                    Assert(list.ActualHeight <= list.MaxHeight + 1 && list.MaxHeight < 400, "client list is capped so it scrolls inside its card");
                    Assert(Math.Abs(serverPane.ActualHeight - clientPane.ActualHeight) < 1 && clientPane.ActualHeight < 460, "both panes render at the same, bounded height");
                    // 截图曾按面板宽度建位图却在其外边距偏移处绘制，右边 18px 连同状态胶囊一起被切掉。
                    var shell = (System.Windows.FrameworkElement)window.Content;
                    var pill = (System.Windows.Controls.TextBlock)window.FindName("Status");
                    double pillRight = pill.TransformToAncestor(shell).Transform(new System.Windows.Point(pill.ActualWidth, 0)).X;
                    Assert(pillRight < shell.ActualWidth, "status pill stays inside the panel");
                    using (var png = File.OpenRead(Path.Combine(output, "remote.png")))
                        Assert(System.Windows.Media.Imaging.BitmapFrame.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).PixelWidth
                            >= shell.ActualWidth + shell.Margin.Left + shell.Margin.Right - 1, "capture covers the full window, margins included");
                    form.CapturePage(Path.Combine(output, "remote-bottom.png"), 0, true);
                    window.Width = 1000; window.Height = 720;
                    form.CapturePage(Path.Combine(output, "remote-compact.png"), 0);
                    Assert((int)window.Resources["ClientColumns"] == 2, "compact window uses two client columns");
                }
                Assert(File.Exists(Path.Combine(output, "remote.png")) && File.Exists(Path.Combine(output, "local.png")), "both modes render");
                Console.WriteLine("Passed: " + passed); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}

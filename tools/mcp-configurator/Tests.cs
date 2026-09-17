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
                Assert(profiles.Count == 8, "all eight original clients selectable");
                foreach (var profile in profiles)
                {
                    var testProfile = new ClientProfile(profile.Id, profile.Name, Path.Combine(temp, profile.Id + (profile.Id == "codex" ? ".toml" : ".json")), profile.Hint);
                    if (profile.Id == "codex") File.WriteAllText(testProfile.Path, "# preserved\r\nmodel = \"keep\"\r\n[mcp_servers.other]\r\ncommand = \"other.exe\"\r\n");
                    else File.WriteAllText(testProfile.Path, "{ // existing settings\n\"keep\": true, \"" + (profile.Id == "vscode" ? "servers" : "mcpServers") + "\": {\"other\":{\"command\":\"keep.exe\"},},}");
                    ClientProfiles.Save(testProfile, true, "192.0.2.10", 8765, secret, null, 21, null);
                    string saved = File.ReadAllText(testProfile.Path);
                    Assert(saved.Contains("other") && saved.Contains("keep") && saved.Contains("tia-portal-vm"), profile.Name + " remote merge preserves other config");
                    if (profile.Id != "codex")
                    {
                        var doc = ConfigCore.Json().Deserialize<Dictionary<string, object>>(saved);
                        var map = (Dictionary<string, object>)doc[profile.Id == "vscode" ? "servers" : "mcpServers"];
                        var entry = (Dictionary<string, object>)map[ClientProfiles.ServerName(profile, true)];
                        string field = profile.Id == "gemini" ? "httpUrl" : profile.Id == "windsurf" ? "serverUrl" : "url";
                        if (profile.Id == "claude")
                        {
                            Assert(saved.Contains("--allow-http") && saved.Contains("http-only") && saved.Contains("TIA_MCP_AUTH_HEADER"), "Desktop Chat HTTP bridge includes private HTTP and env-based header");
                        }
                        else Assert((string)entry[field] == "http://192.0.2.10:8765/mcp", profile.Name + " native HTTP schema");
                    }
                    ClientProfiles.Save(testProfile, false, null, 0, null, @"C:\bundle space\runtime\v21\TiaMcpServer.exe", 21, @"C:\Siemens\Portal V21");
                    Assert(File.ReadAllText(testProfile.Path).Contains("--tia-portal-location"), profile.Name + " local stdio saves explicit TIA path");
                }
                string originalToml = "model = \"keep\"\r\n[mcp_servers.\"tia-portal-vm\"] # old\r\nurl = \"old\"\r\n[mcp_servers.\"tia-portal-vm\".http_headers]\r\nAuthorization = \"oldsecret\"\r\n[projects.\"D:/work\"]\r\ntrust_level = \"trusted\"\r\n";
                string changedToml = ClientProfiles.MergeToml(originalToml, "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null);
                Assert(changedToml.Contains("[projects.\"D:/work\"]\r\ntrust_level = \"trusted\"\r\n") && !changedToml.Contains("oldsecret"), "Codex removes only target tables and preserves unrelated bytes");
                Reject(() => ClientProfiles.MergeToml("mcp_servers = {}", "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null), "Codex inline MCP table rejected without destructive merge");
                string multi = "instructions = \"\"\"\n[mcp_servers.tia-portal-vm]\nthis is text, not a table\n\"\"\"\n";
                Assert(ClientProfiles.MergeToml(multi, "tia-portal-vm", true, "192.0.2.10", 8765, secret, null, 21, null).StartsWith(multi), "Codex multiline instructions remain byte-identical");
                Assert(ClientProfiles.ServerName(profiles.First(x => x.Id == "claude"), true) != ClientProfiles.ServerName(profiles.First(x => x.Id == "claude-code"), true), "Desktop bridge cannot override Claude Code direct HTTP server");
                string jsonc = "{\"url\":\"http://example/a/*b*/\",/*comment*/\"list\":[1,],}";
                var parsedJsonc = ConfigCore.Json().Deserialize<Dictionary<string, object>>(ClientProfiles.StripJsonComments(jsonc));
                Assert((string)parsedJsonc["url"] == "http://example/a/*b*/", "JSONC URLs preserved while removing comments and trailing commas");
                using (var form = new ConfigWindow(false))
                {
                    var window = form.Window;
                    var generate = (System.Windows.Controls.Button)window.FindName("GenerateKey");
                    generate.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    var password = (System.Windows.Controls.PasswordBox)window.FindName("ServerKey");
                    Assert(password.Password.Length >= 24 && ((System.Windows.Controls.TextBlock)window.FindName("ServerKeyPlaceholder")).Visibility == System.Windows.Visibility.Collapsed, "generated key updates masked input and placeholder");
                    var reveal = (System.Windows.Controls.CheckBox)window.FindName("ShowServerKey");
                    reveal.IsChecked = true; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert(((System.Windows.Controls.TextBox)window.FindName("ServerKeyVisible")).Text == password.Password, "show-key control preserves generated value");
                    reveal.IsChecked = false; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    password.Password = ""; ((System.Windows.Controls.TextBox)window.FindName("ServerKeyVisible")).Text = "";
                    var choices = (System.Windows.Controls.ListBox)window.FindName("ClientChoices");
                    choices.SelectedItems.Add(choices.Items[1]);
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("ClientSelection")).Text.Contains("2"), "client multi-select updates live count");
                    choices.SelectedItems.Clear(); choices.SelectedItems.Add(choices.Items[4]);
                    var localChoices = (System.Windows.Controls.ListBox)window.FindName("LocalChoices");
                    localChoices.SelectedItems.Clear(); localChoices.SelectedItems.Add(localChoices.Items[4]);
                    form.CapturePage(Path.Combine(output, "server.png"), 0);
                    form.CapturePage(Path.Combine(output, "client.png"), 1);
                    form.CapturePage(Path.Combine(output, "local.png"), 2);
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("ConnectionProtocol")).Text == "STDIO" && ((System.Windows.Controls.TextBlock)window.FindName("LocalSelection")).Text == "Claude Desktop", "local page uses actual transport and selected client");
                    window.Width = 1000; window.Height = 720;
                    form.CapturePage(Path.Combine(output, "client-compact.png"), 1);
                    form.CapturePage(Path.Combine(output, "client-compact-bottom.png"), 1, true);
                    Assert((int)window.Resources["ClientColumns"] == 4, "compact window uses four client columns");
                }
                Assert(File.Exists(Path.Combine(output, "server.png")), "all GUI pages render");
                Console.WriteLine("Passed: " + passed); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}

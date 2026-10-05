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
using TiaMcp.Versioning;
using TiaOpenness.Gui;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;

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

        private static void Reject<TException>(Action action, string name) where TException : Exception
        {
            bool rejected = false;
            try { action(); } catch (TException) { rejected = true; }
            Assert(rejected, name);
        }

        private static void VersionCatalogTests(string temp)
        {
            Directory.CreateDirectory(Path.Combine(temp, "manifest"));
            File.WriteAllText(Path.Combine(temp, "manifest", "package-manifest.json"), "{}");
            Assert(TiaVersionCatalog.All.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }),
                "catalog preserves distinct V14 SP1 and V15.1 planned version keys");
            Assert(TiaVersionCatalog.Runnable.Select(x => x.Key).SequenceEqual(new[] { "21", "20", "19", "18", "17", "16", "15.1", "14sp1" }),
                "catalog offers only runnable V21 / V20 in descending order");
            Reject<ArgumentException>(() => ConfigCore.Engine(temp, "22"), "unknown version does not fall back to a runnable engine");
            foreach (string excluded in new[] { "14", "15" })
                Reject<ArgumentException>(() => ConfigCore.Engine(temp, excluded), "excluded release has no engine: " + excluded);
            Reject<ArgumentException>(() => ConfigCore.Engine(temp, "15.0"), "unknown fractional version is not rounded to an existing key");
            Reject<ArgumentException>(() => ConfigCore.Engine(temp, (string)null), "missing version key is rejected before engine lookup");
            foreach (var version in TiaVersionCatalog.Runnable)
            {
                Reject<FileNotFoundException>(() => ConfigCore.Engine(temp, version.Key), version.DisplayName + " missing runtime is rejected");
                if (version.IsFullEngine) {
                string sourceEngine = Path.Combine(temp, "src", "Engine", version.EngineOutputDirectory, "Release", "net48", "TiaMcp.Engine.V" + version.MajorVersion + ".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(sourceEngine)); File.WriteAllText(sourceEngine, "stub");
                Reject<FileNotFoundException>(() => ConfigCore.Engine(temp, version.Key), version.DisplayName + " missing install never probes source output");
                string gui = Path.Combine(temp, "src", "Studio", "Gui", "bin", "Release", "net10.0-windows");
                Directory.CreateDirectory(gui);
                Assert(ConfigCore.Engine(temp, version.Key, gui) == sourceEngine, version.DisplayName + " formal GUI output selects matching engine development output");
                }
                string runtimeEngine = Path.Combine(temp, "runtime", version.RuntimeDirectory, version.IsFullEngine ? "TiaMcp.Engine.V" + version.MajorVersion + ".exe" : "TiaMcp.FoundationHost.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(runtimeEngine)); File.WriteAllText(runtimeEngine, "stub");
                Assert(ConfigCore.Engine(temp, version.Key) == runtimeEngine, version.DisplayName + " packaged runtime takes precedence over source output");
                var settings = new ServerSettings { Version = version.MajorVersion, ReleaseKey = version.Key };
                string serialized = ConfigCore.Json().Serialize(settings);
                Assert(serialized.Contains("\"Version\":" + version.MajorVersion) &&
                    ConfigCore.Json().Deserialize<ServerSettings>(serialized).EffectiveReleaseKey == version.Key,
                    version.DisplayName + " settings retain the persisted integer Version schema");
            }
            var legacy = new ServerSettings { Version = 15, ReleaseKey = "15.1", TiaPath = temp, Address = "127.0.0.1", Port = 8735 };
            Assert(ConfigCore.Arguments(legacy, "test-key", temp).Contains("--release-key") && ConfigCore.Arguments(legacy, "test-key", temp).Contains("15.1"), "V15.1 launch arguments retain exact minor identity");
            Assert(ConfigCore.Json().Deserialize<ServerSettings>("{\"Version\":20}").EffectiveReleaseKey == "20", "old saved V20 configuration remains readable");
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

        private static void BundleConfigurationTests(string temp)
        {
            VersionCatalogTests(temp);
            for (int inputs = 0; inputs < 8; inputs++)
            {
                string matrix = Path.Combine(temp, "matrix-" + inputs);
                string anchor = Path.Combine(matrix, "anchor"), cli = Path.Combine(matrix, "CLI 中文"), environment = Path.Combine(matrix, "environment");
                Directory.CreateDirectory(anchor);
                foreach (var item in new[] { (Flag: 1, Root: anchor), (Flag: 2, Root: environment), (Flag: 4, Root: cli) })
                    if ((inputs & item.Flag) != 0)
                    {
                        Directory.CreateDirectory(Path.Combine(item.Root, "manifest"));
                        File.WriteAllText(Path.Combine(item.Root, "manifest", "package-manifest.json"), "{}");
                    }
                string expected = (inputs & 4) != 0 ? cli : (inputs & 2) != 0 ? environment : (inputs & 1) != 0 ? anchor : null;
                Assert(expected == TiaOpenness.Shared.BundleLayout.ResolveWorkbenchRoot(anchor, (inputs & 4) != 0 ? cli : null, (inputs & 2) != 0 ? environment : null), "Workbench root precedence " + inputs);
            }
            foreach (var profile in ClientProfiles.All())
            foreach (var release in TiaVersionCatalog.Runnable)
            {
                string engine = ConfigCore.Engine(temp, release.Key, temp);
                var local = ClientProfiles.Entry(profile, false, null, 0, null, engine, release.Key, "TIA path");
                string[] launch = profile.Client == "opencode" ? ((string[])local["command"]).Skip(1).ToArray() : (string[])local["args"];
                Assert(launch.SequenceEqual(new[] { "--bundle-root", temp, ConfigCore.VersionArgument(release.Key), release.Key, "--tia-portal-location", "TIA path" }), profile.Id + " " + release.Key + " product arguments");
                var remote = ClientProfiles.Entry(profile, true, "127.0.0.1", 8765, "fixture-key", null, release.Key, null);
                Assert(ClientProfiles.ServerName(profile, false) == "tia-portal" && ClientProfiles.ServerName(profile, true) == "tia-portal-vm"
                    && (string)remote[ClientProfiles.UrlKey(profile)] == "http://127.0.0.1:8765/mcp"
                    && (string)((Dictionary<string, object>)remote["headers"])["Authorization"] == "Bearer fixture-key", profile.Id + " " + release.Key + " stable connection fields");
            }
            string path = Path.Combine(temp, "migration.json");
            var target = new ClientProfile("claude-code", "Claude Code", path, "");
            string original = "{\"mcpServers\":{\"tia-portal\":{\"command\":\"" + "TiaMcp" + "Server.exe\"}}}";
            File.WriteAllText(path, original);
            var change = ClientProfiles.PrepareSave(target, false, null, 0, null, ConfigCore.Engine(temp, "21", temp), "21", "TIA path");
            Assert(change.RequiresMigration && File.ReadAllText(path) == original, "migration preview is read-only");
            Reject<InvalidOperationException>(() => change.Apply(false), "migration refusal does not write");
            Assert(change.BackupPath == null, "refusal does not create a backup");
            Reject<IOException>(() => change.Apply(true, (file, text) =>
            {
                Assert(File.ReadAllText(change.BackupPath) == original, "backup precedes the write");
                File.WriteAllText(file, "partial"); throw new IOException("injected write failure");
            }), "migration write failure is reported");
            Assert(File.ReadAllText(path) == original, "migration write failure restores original bytes");
        }

        private static void WorkbenchUiTests(string output)
        {
            Directory.CreateDirectory(output);
            int previousChecks = passed;
            var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            // Drain the queued application startup before pinning this offline view's appearance.
            app.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ThemeManager.Current.Theme = AppTheme.Light;
            Loc.Current.Language = AppLanguage.English;
            var window = new MainWindow(new TiaOpenness.Gui.ViewModels.MainViewModel(), false);
            // No explicit root: the test output directory is a known development anchor (P6-38 strict root rules).
            window.ShowConfiguration(false);
            using (var form = window.Configuration)
            {
                var versions = (System.Windows.Controls.ComboBox)form.FindName("Version");
                Assert(versions.Items.Cast<TiaVersionDescriptor>().Select(x => x.Key).SequenceEqual(TiaVersionCatalog.Runnable.Select(x => x.Key))
                    && versions.Items.Cast<TiaVersionDescriptor>().All(x => x.IsRunnable), "version picker is populated only from runnable catalog descriptors");
                Assert(versions.DisplayMemberPath == "DisplayName" && versions.SelectedValuePath == "Key" && (string)versions.SelectedValue == "21",
                    "version picker displays catalog names, selects stable keys and defaults to V21");
                versions.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("MajorVersion", System.ComponentModel.ListSortDirection.Ascending));
                versions.SelectedValue = "20";
                Assert(versions.SelectedIndex == versions.Items.Count - 2 && ((System.Windows.Controls.TextBlock)form.FindName("LinkServer")).Text.EndsWith("V20"),
                    "selecting the V20 catalog key uses its identity even when the display order is reversed");
                versions.Items.SortDescriptions.Clear();
                versions.SelectedValue = "21";
                Assert(((System.Windows.Controls.TextBlock)form.FindName("LinkServer")).Text.EndsWith("V21"),
                    "selecting the V21 catalog key restores the active engine version");
                var settings = (TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent");
                var runUpdate = (System.Windows.Controls.Button)settings.FindName("RunUpdate");
                var installedItem = (System.Windows.Controls.TextBlock)settings.FindName("UpdateInstalledItem");
                string bundleRoot = MainWindow.FindBundleRoot(AppDomain.CurrentDomain.BaseDirectory);
                Assert(File.Exists(Path.Combine(bundleRoot, "manifest", "package-manifest.json"))
                    && !String.Equals(bundleRoot.TrimEnd(Path.DirectorySeparatorChar), AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "configuration console output resolves its bundle through the development anchor");
                string installed = UpdateCheck.Installed(null);
                bool sourceRepository = UpdateCheck.IsSourceRepository(bundleRoot);
                string updateStateKey = sourceRepository ? "Config.SourceRepositoryUpdate" : "Config.NotChecked";
                string ExpectedInstalled() => installed ?? Loc.Current["Config.EngineOutsideBundle"];
                Assert(window.FindName("MenuBar") == null && form.FindName("UpdateBand") == null && !runUpdate.IsEnabled
                    && runUpdate.Visibility == System.Windows.Visibility.Collapsed
                    && ((System.Windows.Controls.Button)settings.FindName("CheckUpdate")).IsEnabled == (installed != null && !sourceRepository)
                    && ((System.Windows.Controls.Button)settings.FindName("CheckUpdate")).Content.ToString() == "Check updates",
                    "settings own update actions; source updates are disabled and run stays hidden until a newer release is known");
                Assert(installedItem.Text == ExpectedInstalled(), "English settings name the installed engine version from the delivery manifest");
                void AssertShellLabels(bool chinese)
                {
                    string Content(string name) => ((System.Windows.Controls.ContentControl)(window.FindName(name) ?? settings.FindName(name))).Content.ToString();
                    var labels = new (Func<string> Read, string En, string Zh)[] {
                        (() => Content("RailEngineering"), "Project operations", "工程操作"),
                        (() => Content("RailMcp"), "MCP & clients", "MCP 与客户端"),
                        (() => Content("SettingsButton"), "Engine & MCP settings…", "引擎 & MCP 设置…"),
                        (() => Content("CheckUpdate"), "Check updates", "检查更新"),
                        (() => Content("RunUpdate"), "Update engine…", "更新引擎…"),
                        (() => Content("OpenReleases"), "Open", "打开"),
                        (() => Content("ExportDiagnostics"), "Export diagnostics", "导出诊断包"),
                        (() => Content("OpenProjectPage"), "Open", "打开"),
                        (() => Content("RailLog"), "Log", "日志")
                    };
                    Assert(((System.Windows.Controls.Button)form.FindName("SaveBoth")).Content.ToString() == Loc.Current["Config.SaveBoth"],
                        (chinese ? "Chinese" : "English") + " save both label");
                    Assert(((System.Windows.Controls.TextBlock)window.FindName("McpStatusText")).Text.StartsWith(chinese ? "MCP 服务 ·" : "MCP service ·")
                        && ((System.Windows.Controls.RadioButton)window.FindName("RailMcp")).IsChecked == true
                        && ((System.Windows.Controls.RadioButton)window.FindName("RailEngineering")).IsChecked == false,
                        (chinese ? "Chinese" : "English") + " caption and rail track the configuration page");
                    foreach (var (read, en, zh) in labels)
                        Assert(read() == (chinese ? zh : en), (chinese ? "Chinese" : "English") + " shell/settings label: " + en);
                    Assert(((System.Windows.Controls.TextBlock)settings.FindName("UpdateStateItem")).Text == Loc.Current[updateStateKey],
                        (chinese ? "Chinese" : "English") + " update state follows the selected bundle and survives language changes");
                }
                // The window is not shown yet, so bindings refresh when the dispatcher runs.
                window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                AssertShellLabels(false);
                Assert(form.FindName("MenuBar") == null, "configuration has no second menu");
                Assert(((System.Windows.Controls.Button)form.FindName("SaveBoth")).Visibility == System.Windows.Visibility.Visible,
                    "save both is a visible page action");
                Loc.Current.Language = AppLanguage.Chinese;
                window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                Assert(installedItem.Text == ExpectedInstalled(), "Chinese settings name the installed engine version from the delivery manifest");
                AssertShellLabels(true);
                Loc.Current.Language = AppLanguage.English;
                window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                AssertShellLabels(false);
                Assert(((System.Windows.Controls.TextBlock)form.FindName("ClientSelection")).ToolTip != null
                    && settings.FindName("OpenProjectPage") != null && settings.FindName("VersionText") != null && settings.FindName("OpenReleases") != null,
                    "client instructions and settings project home, about version and GitHub Releases remain reachable");
                var generate = (System.Windows.Controls.Button)form.FindName("GenerateKey");
                generate.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var password = (System.Windows.Controls.PasswordBox)form.FindName("Key");
                Assert(password.Password.Length >= 24 && ((System.Windows.Controls.TextBlock)form.FindName("KeyPlaceholder")).Visibility == System.Windows.Visibility.Collapsed, "generated key updates masked input and placeholder");
                var reveal = (System.Windows.Controls.CheckBox)form.FindName("ShowKey");
                reveal.IsChecked = true; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert(((System.Windows.Controls.TextBox)form.FindName("KeyVisible")).Text == password.Password, "show-key control preserves generated value");
                reveal.IsChecked = false; reveal.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                password.Password = ""; ((System.Windows.Controls.TextBox)form.FindName("KeyVisible")).Text = "";
                var choices = (System.Windows.Controls.ListBox)form.FindName("ClientChoices");
                choices.SelectedItems.Add(choices.Items.Cast<ClientProfile>().First(x => !choices.SelectedItems.Contains(x)));
                Assert(((System.Windows.Controls.TextBlock)form.FindName("ClientSelection")).Text.Contains("2"), "client multi-select updates live count");
                Assert(((System.Windows.Controls.TextBlock)form.FindName("LinkClient")).Text.Contains("2"), "link bar follows the client selection");
                choices.SelectedItems.Clear(); choices.SelectedItems.Add(choices.Items[6]);
                ((System.Windows.Controls.TextBox)form.FindName("ServerAddress")).Text = "192.0.2.10";
                form.CapturePage(Path.Combine(output, "remote.png"), 0);
                Assert(((System.Windows.Controls.TextBlock)form.FindName("LinkEndpoint")).Text == "192.0.2.10:8765"
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkState")).Text == "idle"
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkClient")).Text == "1 selected"
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkClient")).ToolTip.ToString() == "DeepSeek", "remote mode shows the live endpoint, state and selected client");
                Assert(((System.Windows.Controls.Border)form.FindName("SecretBand")).Visibility == System.Windows.Visibility.Visible
                    && ((System.Windows.Controls.Grid)form.FindName("AddressRow")).Visibility == System.Windows.Visibility.Visible, "remote mode shows the shared secret band and the address row");
                form.CapturePage(Path.Combine(output, "local.png"), 1);
                // Local mode preserves configuration values while disabling HTTP-only operations.
                Assert(((System.Windows.Controls.TextBlock)form.FindName("LinkEndpoint")).Text.StartsWith("stdio")
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkState")).Text == "stdio", "local mode reports the stdio transport");
                Assert(((System.Windows.Controls.Border)form.FindName("SecretBand")).Visibility == System.Windows.Visibility.Visible
                    && !((System.Windows.Controls.Grid)form.FindName("SecretField")).IsEnabled
                    && !((System.Windows.Controls.Grid)form.FindName("AddressRow")).IsEnabled
                    && !((System.Windows.Controls.Grid)form.FindName("ServerActions")).IsEnabled
                    && ((System.Windows.Controls.Border)form.FindName("ServerPane")).Opacity == .45,
                    "local mode dims the HTTP card and disables address, secret and service actions");
                form.CapturePage(Path.Combine(output, "remote.png"), 0);
                var list = (System.Windows.Controls.ListBox)form.FindName("ClientChoices");
                var serverPane = (System.Windows.Controls.Border)form.FindName("ServerPane");
                var clientPane = (System.Windows.Controls.Border)form.FindName("ClientPane");
                Assert(list.ActualHeight <= clientPane.ActualHeight && list.ActualHeight < 400, "client list fits inside its card");
                Assert(Math.Abs(serverPane.ActualHeight - 158) < 1 && clientPane.ActualHeight > 200 && clientPane.ActualHeight < 400, "Glass service and client cards use the handoff heights");
                // 截图曾按面板宽度建位图却在其外边距偏移处绘制，右边 18px 连同状态胶囊一起被切掉。
                var shell = (System.Windows.FrameworkElement)window.Content;
                var pill = (System.Windows.Controls.TextBlock)form.FindName("LinkState");
                double pillRight = pill.TransformToAncestor(shell).Transform(new System.Windows.Point(pill.ActualWidth, 0)).X;
                Assert(pillRight < shell.ActualWidth, "status pill stays inside the panel");
                var caption = (System.Windows.FrameworkElement)window.FindName("CaptionBar");
                Assert(caption.ActualHeight == 40 && caption.TransformToAncestor(shell).Transform(new System.Windows.Point(0, 0)).Y == 0, "title bar occupies the top 40 pixels");
                using (var png = File.OpenRead(Path.Combine(output, "remote.png")))
                    Assert(System.Windows.Media.Imaging.BitmapFrame.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).PixelWidth
                        >= shell.ActualWidth + shell.Margin.Left + shell.Margin.Right - 1, "capture covers the full window, margins included");
                form.CapturePage(Path.Combine(output, "remote-bottom.png"), 0, true);
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                form.CapturePage(Path.Combine(output, "remote-compact.png"), 0);
                Assert(window.MinWidth == 1200 && window.MinHeight == 780 && list.ItemsPanel.LoadContent() is System.Windows.Controls.Primitives.UniformGrid columns && columns.Columns == 3,
                    "minimum window size keeps the three-column client layout");
                window.Width = 1200; window.Height = 780;
                var font = new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/" + typeof(ConfigurationView).Assembly.GetName().Name + ";component/"), "./Fonts/#Manrope");
                System.Windows.Media.GlyphTypeface glyph;
                Assert(new System.Windows.Media.Typeface(font, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal).TryGetGlyphTypeface(out glyph)
                    && glyph.FontUri.ToString().ToLowerInvariant().Contains("manrope"), "Manrope is loaded from the embedded font resource");
                ThemeManager.Current.Theme = AppTheme.Dark;
                Assert(((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Grid)window.FindName("Root")).Background).Color.ToString() == "#FF0B1420", "dark palette switches live");
                ThemeManager.Current.Theme = AppTheme.Light;
                Assert(((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Grid)window.FindName("Root")).Background).Color.ToString() == "#FFE7ECF1", "light palette switches live");
                string preservedSecret = "language-switch-fixture";
                password.Password = preservedSecret;
                Loc.Current.Language = AppLanguage.Chinese;
                window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                Assert(((System.Windows.Controls.TextBlock)form.FindName("PageTitle")).Text == "一页连接 TIA 与 AI"
                    && ((System.Windows.Controls.Button)form.FindName("SaveClient")).Content.ToString() == "写入客户端配置"
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkState")).Text == "空闲", "Chinese page updates title, action and service state");
                Assert(password.Password == preservedSecret && ((System.Windows.Controls.TextBox)form.FindName("ServerAddress")).Text == "192.0.2.10"
                    && choices.SelectedItems.Count == 1 && (string)versions.SelectedValue == "21", "language switch preserves the secret, address, client and version");
                ((System.Windows.Controls.RadioButton)form.FindName("LocalNav")).IsChecked = true;
                Assert(((System.Windows.Controls.TextBlock)form.FindName("PageTitle")).Text == "同一台电脑，一次完成"
                    && ((System.Windows.Controls.TextBlock)form.FindName("LinkState")).Text == "stdio", "Chinese local mode is localized");
                Loc.Current.Language = AppLanguage.English;
                window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                Assert(((System.Windows.Controls.RadioButton)form.FindName("LocalNav")).IsChecked == true
                    && ((System.Windows.Controls.TextBlock)form.FindName("PageTitle")).Text == "Same machine, one pass", "switching language preserves the selected transport");
                ((System.Windows.Controls.RadioButton)form.FindName("RemoteNav")).IsChecked = true;
                // Synthetic screenshot fixture only; the shipping view always uses ClientProfiles.All().
                string capture = Environment.GetEnvironmentVariable("TIA_GLASS_SCREENSHOTS");
                if (!String.IsNullOrEmpty(capture))
                {
                    Directory.CreateDirectory(capture);
                    var fixture = new[] {
                        new ClientProfile("claude-code", "Claude Code", @"~\.claude.json", "fixture") { Detected=true },
                        new ClientProfile("codex", "Codex", @"~\.codex\config.toml", "fixture") { Detected=true },
                        new ClientProfile("vscode", "VS Code", @"%AppData%\Code\User\mcp.json", "fixture") { Detected=true },
                        new ClientProfile("cursor", "Cursor", @"~\.cursor\mcp.json", "fixture")
                    };
                    list.ItemsSource = fixture;
                    for (int i=0;i<3;i++) list.SelectedItems.Add(fixture[i]);
                    password.Password="visual-fixture-not-a-real-secret";
                    ((System.Windows.Controls.TextBlock)form.FindName("DetectionSource")).Text="● Detected via registry";
                    ((System.Windows.Controls.TextBlock)form.FindName("DetectionSource")).SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Ui.Accent");
                    ((System.Windows.Controls.TextBlock)form.FindName("LastTest")).Text="Last test · HTTP reachable, auth passed, MCP ready. Version 1.4.2. TIA project connection not yet verified.";
                    ((TiaDesktop.Glass.GlassLogView)form.FindName("ActivityLog")).LogText="09:41:02   Config loaded · tia-portal-vm\n09:41:03   Install path detected (registry)\n09:41:03   Client scan · 3 detected\n09:42:17   Secret generated · [redacted]\n09:42:40   Both-side config saved\n09:43:05   Test · HTTP ok · auth ok · MCP ready\n09:43:05   Status · Connection OK";
                    ((System.Windows.Controls.TextBlock)form.FindName("LogCount")).Text="7 entries";
                    form.CapturePage(Path.Combine(capture,"configurator-light.png"),0);
                    ThemeManager.Current.Theme = AppTheme.Dark;
                    form.CapturePage(Path.Combine(capture,"configurator-dark.png"),0);
                    form.CapturePage(Path.Combine(capture,"configurator-local-dark.png"),1);
                    Loc.Current.Language = AppLanguage.Chinese;
                    window.Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Render);
                    ((System.Windows.Controls.TextBlock)form.FindName("DetectionSource")).Text="● 已通过注册表检测";
                    ((System.Windows.Controls.TextBlock)form.FindName("DetectionSource")).SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Ui.Accent");
                    ((System.Windows.Controls.TextBlock)form.FindName("LastTest")).Text="上次测试 · HTTP 可达，鉴权通过，MCP 就绪。版本 1.4.2。尚未验证 TIA 工程连接。";
                    ((TiaDesktop.Glass.GlassLogView)form.FindName("ActivityLog")).LogText="09:41:02   配置已载入 · tia-portal-vm\n09:41:03   已检测到安装路径（注册表）\n09:41:03   客户端扫描 · 已检测到 3 个\n09:42:17   密钥已生成 · [redacted]\n09:42:40   两端配置已保存\n09:43:05   测试 · HTTP 正常 · 鉴权通过 · MCP 就绪\n09:43:05   状态 · 连接正常";
                    ((System.Windows.Controls.TextBlock)form.FindName("LogCount")).Text="7 条记录";
                    form.CapturePage(Path.Combine(capture,"configurator-zh-dark.png"),0);
                    form.CapturePage(Path.Combine(capture,"configurator-local-zh-dark.png"),1);
                    ThemeManager.Current.Theme = AppTheme.Light;
                    form.CapturePage(Path.Combine(capture,"configurator-zh-light.png"),0);
                    form.CapturePage(Path.Combine(capture,"configurator-local-zh-light.png"),1);

                }

                window.Close();
            }
            Assert(File.Exists(Path.Combine(output, "remote.png")) && File.Exists(Path.Combine(output, "local.png")), "both modes render");
            if (passed - previousChecks < 68) throw new Exception("Expected at least 68 workbench UI checks.");
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            // Same culture on developer machines (often zh-CN) and the English CI runners; the checks switch
            // Loc.Current explicitly wherever a language matters.
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            if (args.Length > 0 && args[0] == "--echo")
            {
                Console.WriteLine(ConfigCore.Json().Serialize(args.Skip(1).ToArray())); return 0;
            }
            try
            {
                if (args.Length > 0 && args[0] == "--bundle-tests-only")
                {
                    string bundleOutput = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "bundle-test-output"));
                    BundleConfigurationTests(bundleOutput);
                    Console.WriteLine("Passed: " + passed); return 0;
                }
                bool uiOnly = args.Length > 0 && args[0] == "--ui-only";
                string output = Path.GetFullPath(args.Length > (uiOnly ? 1 : 0) ? args[uiOnly ? 1 : 0] : Path.Combine(AppContext.BaseDirectory, "test-output"));
                if (uiOnly)
                {
                    WorkbenchUiTests(output);
                    Console.WriteLine("UI checks passed: " + passed); return 0;
                }
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

                Directory.CreateDirectory(Path.Combine(temp, "manifest"));
                File.WriteAllText(Path.Combine(temp, "manifest", "package-manifest.json"), "{}");
                Reject<FileNotFoundException>(() => ConfigCore.Engine(temp, 21), "missing runtime in a valid bundle gives actionable failure");
                Reject(() => ConfigCore.ValidateTia(temp, 21), "missing TIA API rejected");

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
                VersionCatalogTests(Path.Combine(temp, "version-catalog"));

                string[] roundtrip = { "", secret, "C:\\space path\\", "a\\\"b", "\"", "a&echo nope", "end\\\\" };
                var info = new ProcessStartInfo(Application.ExecutablePath, "--echo " + String.Join(" ", roundtrip.Select(ConfigCore.Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
                // Child console uses the same explicit UTF-8 encoding below.
                using (var child = Process.Start(info))
                {
                    string text = child.StandardOutput.ReadToEnd(); child.WaitForExit();
                    var actual = ConfigCore.Json().Deserialize<string[]>(text);
                    Assert(actual.SequenceEqual(roundtrip), "native process argument quoting preserves secrets and paths");
                }
                if (Environment.GetEnvironmentVariable("TIA_MCP_TEST_NO_NETWORK") != "1") { Probe(false); Probe(true); }
                else Console.WriteLine("Skipped: 2 loopback HTTP checks (TIA_MCP_TEST_NO_NETWORK=1)");
                var profiles = ClientProfiles.All();
                Assert(profiles.Count == 12, "twelve cards: Claude Code, Codex, Gemini CLI, Qwen, Kimi, Yuanbao, DeepSeek, GLM, Grok, Qwen Agent, Cursor, VS Code");
                Assert(profiles.All(x => x.Name.All(c => c < 128) && x.Kind.All(c => c < 128)), "2.7.62: every client name and kind is English (maintainer)");
                Assert(profiles.First(x => x.Id == "zhipu").Name == "GLM", "2.8.0: the Zhipu card is named GLM (maintainer)");
                Assert(profiles[0].Id == "claude-code" && profiles[1].Id == "codex", "Claude Code and Codex are the first two cards");
                Assert(profiles.TakeWhile(x => x.Kind == "CLI").Count() == 9 && profiles[9].Kind == "Desktop" && profiles.Skip(10).All(x => x.Kind == "IDE"), "CLI cards, then the desktop assistant, then IDE cards");
                // 2.7.61: detection never throws, every card carries evidence text, and the qwen-agent file is url + headers without a type
                var previousLanguage = Loc.Current.Language;
                try
                {
                    foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
                    {
                        Loc.Current.Language = language;
                        string detected = language == AppLanguage.Chinese ? "已检测" : "Detected";
                        string missing = language == AppLanguage.Chinese ? "未检测到" : "Not detected";
                        Assert(profiles.All(x => !String.IsNullOrEmpty(x.Evidence) && x.Category.EndsWith(x.Detected ? detected : missing) && x.Tooltip.Contains(x.Path)), "every card reports detection in " + language);
                    }
                }
                finally { Loc.Current.Language = previousLanguage; }
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
                    ClientProfiles.Save(testProfile, true, "192.0.2.10", 8765, secret, null, 21, null, confirmed: true);
                    string saved = File.ReadAllText(testProfile.Path);
                    Assert(saved.Contains("other") && saved.Contains("keep") && saved.Contains("tia-portal-vm"), profile.Name + " remote merge preserves other config");
                    if (profile.Client != "codex")
                    {
                        var doc = ConfigCore.Json().Deserialize<Dictionary<string, object>>(saved);
                        var map = (Dictionary<string, object>)doc[ClientProfiles.RootKey(profile)];
                        var entry = (Dictionary<string, object>)map[ClientProfiles.ServerName(profile, true)];
                        Assert((string)entry[ClientProfiles.UrlKey(profile)] == "http://192.0.2.10:8765/mcp", profile.Name + " native HTTP schema");
                    }
                    ClientProfiles.Save(testProfile, false, null, 0, null, ConfigCore.Engine(Path.Combine(temp, "version-catalog"), "21"), 21, @"C:\Siemens\Portal V21", confirmed: true);
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
                var openLocal = ClientProfiles.Entry(brands[0], false, null, 0, null, ConfigCore.Engine(Path.Combine(temp, "version-catalog"), "21"), 21, @"C:\Siemens\Portal V21");
                var openCommand = (string[])openLocal["command"];
                Assert((string)openLocal["type"] == "local" && openCommand[0] == ConfigCore.Engine(Path.Combine(temp, "version-catalog"), "21") && openCommand.Contains("--tia-portal-location") && !openLocal.ContainsKey("args"), "OpenCode local entry is one command array starting with the executable");
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
                // 2.8.0 update band: pure logic without network, then the band itself in the rendered window.
                Assert(UpdateCheck.Compare("2.8.0", "2.7.62") > 0 && UpdateCheck.Compare("v2.7.62", "2.7.62") == 0 && UpdateCheck.Compare("2.7.9", "2.7.62") < 0 && UpdateCheck.Compare(null, "1.0.0") < 0, "update: numeric version comparison ignores a leading v and sorts empty lowest");
                Assert(UpdateCheck.TagFromLocation("https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v2.7.62") == "v2.7.62" && UpdateCheck.TagFromLocation("https://github.com/x/y/releases") == null, "update: release page redirect yields the tag");
                string releaseJson = "{\"tag_name\":\"v2.8.0\",\"html_url\":\"https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v2.8.0\",\"assets\":[{\"name\":\"TIA_MCP_Delivery_v2.8.0_20260921.zip\",\"size\":16147645},{\"name\":\"TIA_MCP_Delivery_v2.8.0_20260921.sha256\",\"size\":64}]}";
                var release = UpdateCheck.ParseRelease(releaseJson, "2.7.62", UpdateCheck.Repository);
                Assert(release.UpdateAvailable && release.Latest == "2.8.0" && release.ZipName == "TIA_MCP_Delivery_v2.8.0_20260921.zip" && release.ZipSize == 16147645 && release.HasSha256 && release.ZipSizeText == "15.4 MB" && release.Source == "api", "update: GitHub release JSON gives version, ZIP asset, size and the .sha256 sidecar");
                Assert(!UpdateCheck.ParseRelease(releaseJson, "2.8.0", UpdateCheck.Repository).UpdateAvailable && !UpdateCheck.ParseRelease(releaseJson, "2.9.0", UpdateCheck.Repository).UpdateAvailable, "update: same or newer installed version means nothing to do");
                Reject(() => UpdateCheck.ParseRelease("{\"tag_name\":\"latest\"}", "2.7.62", UpdateCheck.Repository), "update: a tag that is not a version is rejected");
                string delivery = Path.Combine(temp, "delivery"); Directory.CreateDirectory(Path.Combine(delivery, "manifest"));
                File.WriteAllText(Path.Combine(delivery, "manifest", "delivery.json"), "{\"release\":\"2.7.62\",\"package\":\"TIA_MCP_Delivery_v2.7.62_20260921\"}");
                File.WriteAllText(Path.Combine(delivery, "manifest", "package-manifest.json"), "{}");
                Directory.CreateDirectory(Path.Combine(delivery, "scripts", "operations"));
                File.WriteAllText(Path.Combine(delivery, "scripts", "operations", "Update-Engine.ps1"), "# fixture");
                Assert(UpdateCheck.Installed(delivery) == "2.7.62" && UpdateCheck.InstalledPackage(delivery) == "TIA_MCP_Delivery_v2.7.62_20260921", "update: installed version comes from manifest\\delivery.json, absent elsewhere");
                Directory.CreateDirectory(Path.Combine(delivery, ".git"));
                Assert(UpdateCheck.IsSourceRepository(delivery), "update: a .git folder marks the source repository (no in-place update there)");
                string launch = UpdateCheck.LaunchArguments(@"C:\TIA MCP\scripts\operations\Update-Engine.ps1", @"C:\TIA MCP\", 4242);
                Assert(launch.StartsWith("-NoProfile -ExecutionPolicy Bypass -NoExit -File \"C:\\TIA MCP\\scripts\\operations\\Update-Engine.ps1\" -InstallRoot \"C:\\TIA MCP\" -WaitForPid 4242 -RelaunchConfigurator"), "update: the updater is launched visibly with the install root (no trailing backslash), the caller pid and the relaunch switch");
                Reject<InvalidOperationException>(() => UpdateCheck.Launch(delivery, 1), "update: launch boundary refuses a worktree or checkout");
                Assert(UpdateCheck.UpdaterPath(delivery).EndsWith(@"scripts\operations\Update-Engine.ps1"), "update: updater path remains in selected bundle");
                Assert(UpdateCheck.RunningEngines().All(x => new[] { "TiaMcp.Engine.V20.exe PID ", "TiaMcp.Engine.V21.exe PID ", "TiaMcp.FoundationHost.exe PID " }.Any(x.StartsWith)), "update: running engines are listed by pid (the updater refuses while any runs)");
                WorkbenchUiTests(output);
                if (passed < 182) throw new Exception("Expected at least 182 configuration checks.");
                Console.WriteLine("Passed: " + passed); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}

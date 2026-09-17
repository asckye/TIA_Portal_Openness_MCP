using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("TIA MCP Configurator")]
[assembly: AssemblyDescription("TIA Portal V20/V21 service and AI client configuration")]
[assembly: AssemblyVersion("2.7.19.0")]
[assembly: AssemblyFileVersion("2.7.19.0")]

namespace TiaMcpConfigurator
{
    public sealed class ConfigWindow : IDisposable
    {
        public Window Window { get; private set; }
        private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        private Process server;
        private string runningKey;
        private bool busy, closing;
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private string Text(string name) { return Find<TextBox>(name).Text.Trim(); }
        private int Version { get { return Find<ComboBox>("Version").SelectedIndex == 1 ? 20 : 21; } }
        private string StatePath { get { return Path.Combine(ConfigCore.StateDirectory, "http-v" + Version + ".json"); } }
        private string Secret(string prefix) { return Find<CheckBox>("Show" + prefix + "Key").IsChecked == true ? Find<TextBox>(prefix + "KeyVisible").Text : Find<PasswordBox>(prefix + "Key").Password; }
        private void SetSecret(string prefix, string value) { Find<TextBox>(prefix + "KeyVisible").Text = value; Find<PasswordBox>(prefix + "Key").Password = value; }

        public ConfigWindow(bool loadExisting = true)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Window.Resources["ClientColumns"] = Window.Width < 1140 ? 4 : 5;
            Window.SizeChanged += delegate { Window.Resources["ClientColumns"] = Window.ActualWidth < 1140 ? 4 : 5; };
            var clients = ClientProfiles.All();
            Find<ListBox>("ClientChoices").ItemsSource = clients; Find<ListBox>("ClientChoices").SelectedIndex = 0;
            Find<ListBox>("LocalChoices").ItemsSource = clients; Find<ListBox>("LocalChoices").SelectedIndex = 0;
            Find<ListBox>("ClientChoices").SelectionChanged += delegate { UpdateInstructions(); };
            Find<TextBlock>("ClientSelection").MouseLeftButtonUp += delegate { MessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, "客户端使用说明", MessageBoxButton.OK, MessageBoxImage.Information); };
            Find<ListBox>("LocalChoices").SelectionChanged += delegate { UpdateLocalSummary(); };
            foreach (string prefix in new[] { "Server", "Client" })
            {
                string captured = prefix;
                Find<PasswordBox>(prefix + "Key").PasswordChanged += delegate { UpdateKeyPlaceholder(captured); };
                Find<TextBox>(prefix + "KeyVisible").TextChanged += delegate { UpdateKeyPlaceholder(captured); };
                Find<CheckBox>("Show" + prefix + "Key").Click += delegate {
                    bool show = Find<CheckBox>("Show" + captured + "Key").IsChecked == true;
                    if (show) Find<TextBox>(captured + "KeyVisible").Text = Find<PasswordBox>(captured + "Key").Password;
                    else Find<PasswordBox>(captured + "Key").Password = Find<TextBox>(captured + "KeyVisible").Text;
                    Find<TextBox>(captured + "KeyVisible").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    Find<PasswordBox>(captured + "Key").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                    UpdateKeyPlaceholder(captured);
                };
            }
            Find<RadioButton>("ServerNav").Checked += delegate { Page(0); };
            Find<RadioButton>("ClientNav").Checked += delegate { Page(1); };
            Find<RadioButton>("LocalNav").Checked += delegate { Page(2); };
            Click("GoServer", delegate { Find<RadioButton>("ServerNav").IsChecked = true; });
            Click("BrowseTia", delegate {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 Portal V20 / V21 安装目录，不带 Bin", SelectedPath = Text("TiaPath") })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Find<TextBox>("TiaPath").Text = dialog.SelectedPath;
            });
            Click("DetectTia", delegate { DetectTiaPath(true); });
            Click("GenerateKey", delegate { byte[] bytes = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); SetSecret("Server", Convert.ToBase64String(bytes)); });
            Click("SaveServer", SaveServer);
            Click("StartServer", StartServer);
            Click("StopServer", StopServer);
            Click("Network", async delegate { await Network(); });
            Click("TestClient", async delegate { await TestClient(); });
            Click("SaveClient", delegate { SaveClients(true); });
            Click("SaveLocal", delegate { SaveClients(false); });
            Find<ComboBox>("Version").SelectionChanged += delegate { Guard(LoadServer); UpdateLocalSummary(); };
            if (loadExisting)
            {
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 32));
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 32));
                Guard(LoadServer); Guard(LoadClient);
                if (!Directory.Exists(Text("TiaPath"))) Find<RadioButton>("ClientNav").IsChecked = true;
            }
            else { Find<TextBox>("TiaPath").Text = @"C:\Program Files\Siemens\Automation\Portal V21"; DetectTiaPath(false); }
            UpdateInstructions(); UpdateLocalSummary();
            Append("就绪。先配置虚拟机服务，再在宿主机选择 AI 客户端。连接测试不修改工程。");
            Window.Closing += OnClosing;
        }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void Status(string status) { Find<TextBlock>("Status").Text = status; }
        private void ServiceStatus(bool active)
        {
            Find<TextBlock>("ServiceState").Text = active ? "服务运行中" : "服务未启动";
            Find<System.Windows.Shapes.Ellipse>("ServiceDot").Fill = new SolidColorBrush(active ? Color.FromRgb(91, 191, 148) : Color.FromRgb(167, 180, 189));
        }
        private void UpdateKeyPlaceholder(string prefix) { Find<TextBlock>(prefix + "KeyPlaceholder").Visibility = String.IsNullOrEmpty(Secret(prefix)) ? Visibility.Visible : Visibility.Collapsed; }
        private void UpdateLocalSummary()
        {
            Find<TextBlock>("LocalVersion").Text = "V" + Version;
            var selected = Find<ListBox>("LocalChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LocalSelection").Text = selected.Count == 0 ? "尚未选择" : selected.Count == 1 ? selected[0].DisplayName : selected.Count + " 个客户端";
            Find<TextBlock>("LocalSelection").ToolTip = String.Join("、", selected.Select(x => x.DisplayName));
        }
        private void Page(int page)
        {
            string[] names = { "Server", "Client", "Local" };
            for (int i = 0; i < names.Length; i++) Find<StackPanel>(names[i] + "Page").Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("PageStep").Text = new[] { "STEP 01 · VIRTUAL MACHINE", "STEP 02 · HOST", "STEP 03 · SAME MACHINE" }[page];
            Find<TextBlock>("PageTitle").Text = new[] { "让虚拟机中的 TIA 就绪", "把工程能力连接到 AI", "同机直连，无需网络配置" }[page];
            Find<TextBlock>("PageSubtitle").Text = new[] { "配置 HTTP 服务，供宿主机上的 AI 客户端连接。", "选择你使用的客户端，接入同一台虚拟机。", "TIA 与客户端在一台电脑上的最短路径。" }[page];
            Find<TextBlock>("ConnectionProtocol").Text = page == 2 ? "STDIO" : "HTTP";
            Find<TextBlock>("ConnectionLeftLabel").Text = page == 2 ? "本机客户端" : "宿主机";
            Find<TextBlock>("ConnectionRightLabel").Text = page == 2 ? "本机引擎" : "虚拟机";
            Find<ScrollViewer>("ContentScroll").ScrollToTop();
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = selected.Count == 0 ? "可多选" : "已选 " + (selected.Count == 1 ? selected[0].DisplayName : selected.Count + " 个客户端");
            Find<TextBlock>("ClientSelection").ToolTip = String.Join("、", selected.Select(x => x.DisplayName));
            Find<TextBlock>("ClientInstructions").Text = selected.Count == 0 ? "选择一个或多个客户端，保存后将自动写入对应配置。" :
                String.Join("\n", selected.Select(x => x.Name + "：" + x.Hint));
            Find<TextBlock>("ClientSelection").ToolTip = Find<TextBlock>("ClientInstructions").Text;
        }
        private void Append(string message)
        {
            if (closing) return;
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action<string>(Append), message); return; }
            if (!String.IsNullOrEmpty(runningKey)) message = message.Replace(runningKey, "[redacted]");
            var log = Find<TextBox>("Log"); if (log.Text.Length > 40000) log.Clear();
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine); log.ScrollToEnd();
        }
        private void Report(Exception ex)
        {
            string message = ex.GetBaseException().Message;
            var http = ex as HttpListenerException;
            if (http != null) message = "HTTP 监听失败（" + http.NativeErrorCode + "）：" + message + "。拒绝访问时点击“配置网络权限”；端口占用时停止旧 MCP 或更换端口。";
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                message = response != null && response.StatusCode == HttpStatusCode.Unauthorized ? "鉴权失败（401）：请检查两端连接密钥是否一致。" :
                    "连接失败：" + message + " 请检查虚拟机服务、IP、端口和防火墙。";
                if (response != null) response.Close();
            }
            foreach (string prefix in new[] { "Server", "Client" }) { string secret = Secret(prefix); if (!String.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]"); }
            Status("需要处理"); Append(message); MessageBox.Show(Window, message, "需要处理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 自动探测安装目录：环境变量 → 注册表 → 默认目录（与引擎同一顺序）。explicit=false 时只在探测成功才覆盖文本框，找不到保持原值不打扰。
        private void DetectTiaPath(bool explicitRequest)
        {
            var found = ConfigCore.DetectTia(Version);
            if (found.Key != null) { Find<TextBox>("TiaPath").Text = found.Key; Append("已自动检测到 V" + Version + " 安装目录（" + found.Value + "）：" + found.Key); }
            else if (explicitRequest) Append("未自动检测到：" + found.Value + "。请用“浏览”手动选择 Portal V" + Version + " 安装根目录。");
        }
        private void LoadServer()
        {
            Find<TextBox>("TiaPath").Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", "Portal V" + Version);
            Find<TextBox>("ServerAddress").Text = ""; Find<TextBox>("ServerPort").Text = "8765"; SetSecret("Server", "");
            if (!File.Exists(StatePath)) { DetectTiaPath(false); return; }
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(StatePath));
            ConfigCore.Prefix(settings.Address, settings.Port);
            if (settings.Version != Version) throw new InvalidDataException("已保存配置中的 TIA 版本不匹配。");
            Find<TextBox>("TiaPath").Text = settings.TiaPath; Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString();
            SetSecret("Server", ConfigCore.Unprotect(settings.ProtectedKey)); Append("已载入 V" + Version + " 服务配置。");
        }
        private void LoadClient()
        {
            string path = Path.Combine(ConfigCore.StateDirectory, "client.json");
            if (!File.Exists(path)) return;
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(path));
            ConfigCore.Prefix(settings.Address, settings.Port);
            Find<TextBox>("ClientAddress").Text = settings.Address; Find<TextBox>("ClientPort").Text = settings.Port.ToString(); SetSecret("Client", ConfigCore.Unprotect(settings.ProtectedKey));
        }
        private ServerSettings Settings()
        {
            int port = Int32.Parse(Text("ServerPort")); ConfigCore.Prefix(Text("ServerAddress"), port);
            ConfigCore.Engine(root, Version); ConfigCore.ValidateTia(Text("TiaPath"), Version);
            return new ServerSettings { Version = Version, Address = Text("ServerAddress"), Port = port, TiaPath = Text("TiaPath"), ProtectedKey = ConfigCore.Protect(Secret("Server")) };
        }
        private void SaveServer() { ConfigCore.AtomicJson(StatePath, Settings()); Status("已保存"); Append("配置已按当前 Windows 用户加密保存。下一步：配置网络权限 → 启动服务。"); }

        private void SaveClients(bool remote)
        {
            var selected = Find<ListBox>(remote ? "ClientChoices" : "LocalChoices").SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请先选择一个或多个 AI 客户端。");
            string engine = null, ip = null, secret = null; int port = 0;
            if (remote) { ip = Text("ClientAddress"); port = Int32.Parse(Text("ClientPort")); secret = Secret("Client"); ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret); }
            else { engine = ConfigCore.Engine(root, Version); ConfigCore.ValidateTia(Text("TiaPath"), Version); }
            string message = "请先退出所选客户端，避免配置同时写入。\n将保留其它设置并备份原文件：\n\n" + String.Join("\n", selected.Select(x => x.Name + "\n" + x.Path));
            if (remote && selected.Any(x => x.Id == "claude")) message += "\n\nClaude Desktop Chat 需 Node.js LTS / npx，首次运行将联网下载 mcp-remote 桥接包。";
            message += "\n\n客户端配置按其格式保存密钥，请勿分享文件或备份。继续？";
            if (MessageBox.Show(Window, message, "配置所选客户端", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            int saved = 0; var errors = new List<string>();
            foreach (var profile in selected)
            {
                try { ClientProfiles.Save(profile, remote, ip, port, secret, engine, Version, Text("TiaPath")); saved++; Append(profile.Name + " 已保存：" + profile.Path); }
                catch (Exception ex) { errors.Add(profile.Name + "：" + ex.Message); }
            }
            if (remote && saved > 0) ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            Status("已配置 " + saved + " 个客户端");
            Append("重启已配置的客户端，使用 " + (remote ? "tia-portal-vm" : "tia-portal") + " 读取工程树。不要让多个 AI 同时修改同一工程。");
            if (errors.Count > 0) throw new InvalidOperationException("部分客户端未保存，其它成功项已保留：\n" + String.Join("\n", errors));
        }
        private void SetBusy(bool value) { busy = value; Find<Grid>("Pages").IsEnabled = !value; }
        private async Task TestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ClientAddress"), secret = Secret("Client"); int port = Int32.Parse(Text("ClientPort"));
                SetBusy(true); Status("正在测试…");
                Append(await Task.Run(() => ConfigCore.TestRemote(ip, port, secret))); Status("连接正常");
            }
            catch (Exception ex) { Report(ex); }
            finally { SetBusy(false); }
        }
        private async Task Network()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"); int port = Int32.Parse(Text("ServerPort")); ConfigCore.Prefix(ip, port);
                if (MessageBox.Show(Window, "为当前用户授权此 HTTP 地址，并放行本地子网到此端口。\n接下来会出现 Windows 管理员权限提示，继续？", "配置网络权限", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                var args = new[] { "--network", ip, port.ToString(), WindowsIdentity.GetCurrent().User.Value };
                var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, String.Join(" ", args.Select(ConfigCore.Quote))) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                SetBusy(true); Status("配置网络…");
                using (var process = Process.Start(info))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0) throw new InvalidOperationException("网络配置未完成，请查看管理员窗口中的错误信息。");
                }
                Status("网络已配置"); Append("网络权限配置完成。现在可以启动服务。");
            }
            catch (Exception ex) { Report(ex); }
            finally { SetBusy(false); }
        }
        private void StartServer()
        {
            if (server != null && !server.HasExited) throw new InvalidOperationException("此窗口已经启动 MCP。");
            var settings = Settings(); ConfigCore.CheckListener(ConfigCore.Prefix(settings.Address, settings.Port));
            ConfigCore.AtomicJson(StatePath, settings); runningKey = Secret("Server");
            var info = new ProcessStartInfo(ConfigCore.Engine(root, Version), ConfigCore.Arguments(settings, runningKey)) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.Exited += delegate {
                Append("MCP 已退出，退出码：" + process.ExitCode);
                if (!closing) Window.Dispatcher.BeginInvoke(new Action(delegate { Find<Button>("StartServer").IsEnabled = true; Find<Button>("StopServer").IsEnabled = false; Find<ComboBox>("Version").IsEnabled = true; Status("服务已停止"); ServiceStatus(false); }));
            };
            try { if (!process.Start()) throw new InvalidOperationException("MCP 进程未启动。"); }
            catch { process.Dispose(); throw; }
            server = process;
            Find<Button>("StartServer").IsEnabled = false; Find<Button>("StopServer").IsEnabled = true; Find<ComboBox>("Version").IsEnabled = false;
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); Status("服务进程运行中");
            ServiceStatus(true);
            Append("请检查日志中的 listening 提示，并在宿主机测试连接。保持此窗口打开。");
        }
        private void StopServer()
        {
            if (server == null || server.HasExited) return;
            if (MessageBox.Show(Window, "将停止本窗口启动的 MCP，中断客户端连接。\n请确认没有正在执行的工程操作。继续？", "停止 MCP", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            server.Kill(); server.WaitForExit();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (busy) { e.Cancel = true; Append("正在处理配置，请稍候再关闭。"); return; }
            try { StopServer(); if (server != null && !server.HasExited) { e.Cancel = true; return; } }
            catch (Exception ex) { e.Cancel = true; Report(ex); return; }
            closing = true;
        }
        public void CapturePage(string path, int page, bool scrollToBottom = false)
        {
            Find<RadioButton>(new[] { "ServerNav", "ClientNav", "LocalNav" }[page]).IsChecked = true;
            Window.Show(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render);
            if (scrollToBottom) { Find<ScrollViewer>("ContentScroll").ScrollToBottom(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render); }
            var content = (FrameworkElement)Window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        public void Dispose() { Window.Close(); if (server != null && server.HasExited) server.Dispose(); }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[0] == "--network") { ConfigCore.ConfigureNetwork(args[1], Int32.Parse(args[2]), args[3]); return 0; }
                var app = new Application(); using (var ui = new ConfigWindow()) return app.Run(ui.Window);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "TIA MCP 配置失败", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }
    }
}

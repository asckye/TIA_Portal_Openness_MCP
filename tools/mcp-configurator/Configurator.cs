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
[assembly: AssemblyVersion("2.7.58.0")]
[assembly: AssemblyFileVersion("2.7.58.0")]

namespace TiaMcpConfigurator
{
    public sealed class ConfigWindow : IDisposable
    {
        public Window Window { get; private set; }
        private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        private Process server;
        private string runningKey;
        private int logEntries;
        private bool busy, closing;
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private string Text(string name) { return Find<TextBox>(name).Text.Trim(); }
        private int Version { get { return Find<ComboBox>("Version").SelectedIndex == 1 ? 20 : 21; } }
        private string StatePath { get { return Path.Combine(ConfigCore.StateDirectory, "http-v" + Version + ".json"); } }
        // 虚拟机 ↔ 宿主机（HTTP）或同一台电脑（stdio）。两种模式共用同一页的 A/B 两栏。
        private bool Remote { get { return Find<RadioButton>("RemoteNav").IsChecked == true; } }
        private string Secret() { return Find<CheckBox>("ShowKey").IsChecked == true ? Text("KeyVisible") : Find<PasswordBox>("Key").Password; }
        private void SetSecret(string value) { Find<TextBox>("KeyVisible").Text = value; Find<PasswordBox>("Key").Password = value; }

        public ConfigWindow(bool loadExisting = true)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Window.Resources["ClientColumns"] = Window.Width < 1180 ? 2 : 3;
            Window.SizeChanged += delegate { Window.Resources["ClientColumns"] = Window.ActualWidth < 1180 ? 2 : 3; };
            var choices = Find<ListBox>("ClientChoices");
            choices.ItemsSource = ClientProfiles.All(); choices.SelectedIndex = 0;
            choices.SelectionChanged += delegate { UpdateInstructions(); };
            Find<TextBlock>("ClientSelection").MouseLeftButtonUp += delegate { MessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, "客户端使用说明", MessageBoxButton.OK, MessageBoxImage.Information); };
            Find<PasswordBox>("Key").PasswordChanged += delegate { UpdateKeyPlaceholder(); };
            Find<TextBox>("KeyVisible").TextChanged += delegate { UpdateKeyPlaceholder(); };
            Find<CheckBox>("ShowKey").Click += delegate {
                bool show = Find<CheckBox>("ShowKey").IsChecked == true;
                if (show) Find<TextBox>("KeyVisible").Text = Find<PasswordBox>("Key").Password;
                else Find<PasswordBox>("Key").Password = Find<TextBox>("KeyVisible").Text;
                Find<TextBox>("KeyVisible").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Find<PasswordBox>("Key").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                UpdateKeyPlaceholder();
            };
            Find<RadioButton>("RemoteNav").Checked += delegate { Mode(true); };
            Find<RadioButton>("LocalNav").Checked += delegate { Mode(false); };
            Find<TextBox>("ServerAddress").TextChanged += delegate { UpdateLink(); };
            Find<TextBox>("ServerPort").TextChanged += delegate { UpdateLink(); };
            Click("BrowseTia", delegate {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 Portal V20 / V21 安装目录，不带 Bin", SelectedPath = Text("TiaPath") })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Find<TextBox>("TiaPath").Text = dialog.SelectedPath;
            });
            Click("DetectTia", delegate { DetectTiaPath(true); });
            Click("GenerateKey", delegate { byte[] bytes = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); SetSecret(Convert.ToBase64String(bytes)); });
            Click("SaveBoth", SaveBoth);
            Click("StartServer", StartServer);
            Click("StopServer", StopServer);
            Click("Network", async delegate { await Network(); });
            Click("TestClient", async delegate { await TestClient(); });
            Click("SaveClient", delegate { SaveClients(Remote); });
            Find<ComboBox>("Version").SelectionChanged += delegate { Guard(LoadServer); UpdateLink(); };
            if (loadExisting)
            {
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 32));
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 32));
                Guard(LoadServer);
                if (Text("ServerAddress").Length == 0) Guard(LoadClient);
            }
            else { Find<TextBox>("TiaPath").Text = @"C:\Program Files\Siemens\Automation\Portal V21"; DetectTiaPath(false); }
            UpdateInstructions(); Mode(true);
            Append("就绪。两侧配置在同一页完成：A 为服务端，B 为客户端，共用下方密钥。");
            Window.Closing += OnClosing;
        }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void Status(string status) { Find<TextBlock>("Status").Text = status; }
        private void ServiceStatus(bool active)
        {
            Find<System.Windows.Shapes.Ellipse>("StatusDot").Fill = new SolidColorBrush(active ? Color.FromRgb(74, 145, 110) : Color.FromRgb(180, 183, 174));
            UpdateLink();
        }
        private void UpdateKeyPlaceholder() { Find<TextBlock>("KeyPlaceholder").Visibility = String.IsNullOrEmpty(Secret()) ? Visibility.Visible : Visibility.Collapsed; }

        // 模式切换只改文案与可见性：A/B 两栏本身在两种模式下都在同一页上。
        private void Mode(bool remote)
        {
            Find<TextBlock>("PageStep").Text = remote ? "TWO SIDES · ONE PASS" : "SINGLE MACHINE · ONE PASS";
            Find<TextBlock>("PageTitle").Text = remote ? "在一页里连接 TIA 与 AI" : "同机直连，一次配置完成";
            Find<TextBlock>("PageSubtitle").Text = remote
                ? "A 侧在装有 TIA 的虚拟机上启动服务，B 侧在宿主机上选择客户端，两侧共用同一把密钥。"
                : "TIA Portal 与 AI 客户端在同一台电脑上：客户端通过 stdio 自动启动引擎，不需要 IP、端口和密钥。";
            Find<TextBlock>("LinkLeftLabel").Text = remote ? "HOST · 客户端侧" : "LOCAL · 客户端侧";
            Find<TextBlock>("LinkRightLabel").Text = remote ? "VIRTUAL MACHINE · 服务侧" : "LOCALHOST · 服务侧";
            Find<TextBlock>("ServerCardTitle").Text = remote ? "虚拟机服务端" : "本机引擎";
            Find<TextBlock>("ServerCardNote").Text = remote ? "装有 TIA 的机器" : "stdio · 本机";
            Find<Grid>("AddressRow").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("ServerActions").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("LocalNote").Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("LocalActionsNote").Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            Find<Border>("SecretBand").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("TestClient").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            UpdateLink();
            Find<ScrollViewer>("ContentScroll").ScrollToTop();
        }
        private void UpdateLink()
        {
            bool remote = Remote;
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? "尚未选择" : selected.Count == 1 ? selected[0].DisplayName : selected.Count + " 个客户端";
            Find<TextBlock>("LinkClient").ToolTip = String.Join("、", selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = "MCP 服务 · TIA Portal V" + Version;
            bool running = server != null && !server.HasExited;
            string address = Text("ServerAddress");
            Find<TextBlock>("LinkEndpoint").Text = !remote ? "stdio · 无需网络" : address.Length == 0 ? "等待填写地址" : address + ":" + Text("ServerPort");
            Find<TextBlock>("LinkState").Text = !remote ? "local" : running ? "running" : "idle";
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = selected.Count == 0 ? "可多选" : "已选 " + (selected.Count == 1 ? selected[0].DisplayName : selected.Count + " 个客户端");
            Find<TextBlock>("ClientInstructions").Text = selected.Count == 0 ? "选择一个或多个客户端，保存后将自动写入对应配置。" :
                String.Join("\n", selected.Select(x => x.Name + "：" + x.Hint));
            Find<TextBlock>("ClientSelection").ToolTip = Find<TextBlock>("ClientInstructions").Text;
            UpdateLink();
        }
        private void Append(string message)
        {
            if (closing) return;
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action<string>(Append), message); return; }
            if (!String.IsNullOrEmpty(runningKey)) message = message.Replace(runningKey, "[redacted]");
            var log = Find<TextBox>("Log"); if (log.Text.Length > 40000) { log.Clear(); logEntries = 0; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine); log.ScrollToEnd();
            logEntries++; Find<TextBlock>("LogCount").Text = logEntries + (logEntries == 1 ? " entry" : " entries");
        }
        private void Report(Exception ex)
        {
            string message = ex.GetBaseException().Message;
            var http = ex as HttpListenerException;
            if (http != null) message = "HTTP 监听失败（" + http.NativeErrorCode + "）：" + message + "。拒绝访问时点击“网络权限”；端口占用时停止旧 MCP 或更换端口。";
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                message = response != null && response.StatusCode == HttpStatusCode.Unauthorized ? "鉴权失败（401）：请检查两端连接密钥是否一致。" :
                    "连接失败：" + message + " 请检查虚拟机服务、IP、端口和防火墙。";
                if (response != null) response.Close();
            }
            string secret = Secret(); if (!String.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]");
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
            Find<TextBox>("ServerAddress").Text = ""; Find<TextBox>("ServerPort").Text = "8765"; SetSecret("");
            if (!File.Exists(StatePath)) { DetectTiaPath(false); return; }
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(StatePath));
            ConfigCore.Prefix(settings.Address, settings.Port);
            if (settings.Version != Version) throw new InvalidDataException("已保存配置中的 TIA 版本不匹配。");
            Find<TextBox>("TiaPath").Text = settings.TiaPath; Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString();
            SetSecret(ConfigCore.Unprotect(settings.ProtectedKey)); Append("已载入 V" + Version + " 服务配置。");
        }
        // 宿主机上没有服务端配置，用上次填写的连接信息补齐同一组字段。
        private void LoadClient()
        {
            string path = Path.Combine(ConfigCore.StateDirectory, "client.json");
            if (!File.Exists(path)) return;
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(path));
            ConfigCore.Prefix(settings.Address, settings.Port);
            Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString(); SetSecret(ConfigCore.Unprotect(settings.ProtectedKey));
        }
        private ServerSettings Settings()
        {
            int port = Int32.Parse(Text("ServerPort")); ConfigCore.Prefix(Text("ServerAddress"), port);
            ConfigCore.Engine(root, Version); ConfigCore.ValidateTia(Text("TiaPath"), Version);
            return new ServerSettings { Version = Version, Address = Text("ServerAddress"), Port = port, TiaPath = Text("TiaPath"), ProtectedKey = ConfigCore.Protect(Secret()) };
        }
        // 一页两侧，所以保存也是两侧：客户端侧的连接信息总能保存；服务端配置只在本机确实装了
        // TIA 和引擎时才写，宿主机上校验失败不算错误，只记一行说明。
        private void SaveBoth()
        {
            int port = Int32.Parse(Text("ServerPort")); string ip = Text("ServerAddress"), secret = Secret();
            ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret);
            ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            Append("客户端侧连接信息已按当前 Windows 用户加密保存。");
            try { ConfigCore.AtomicJson(StatePath, Settings()); Append("服务端配置已保存。下一步：网络权限 → 启动服务。"); }
            catch (Exception ex) { Append("本机未通过服务端校验，只保存了客户端侧信息：" + ex.GetBaseException().Message); }
            Status("已保存");
        }

        private void SaveClients(bool remote)
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请先选择一个或多个 AI 客户端。");
            string engine = null, ip = null, secret = null; int port = 0;
            if (remote) { ip = Text("ServerAddress"); port = Int32.Parse(Text("ServerPort")); secret = Secret(); ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret); }
            else { engine = ConfigCore.Engine(root, Version); ConfigCore.ValidateTia(Text("TiaPath"), Version); }
            // DeepSeek / 智谱 / Grok are brand cards over the same OpenCode file: write it once, name every brand.
            var targets = selected.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Profile = g.First(), Names = String.Join(" / ", g.Select(x => x.Name)) }).ToList();
            string message = "请先退出所选客户端，避免配置同时写入。\n将保留其它设置并备份原文件：\n\n" + String.Join("\n", targets.Select(x => x.Names + "\n" + x.Profile.Path));
            message += "\n\n客户端配置按其格式保存密钥，请勿分享文件或备份。继续？";
            if (MessageBox.Show(Window, message, "写入客户端配置", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            int saved = 0; var errors = new List<string>();
            foreach (var target in targets)
            {
                try { ClientProfiles.Save(target.Profile, remote, ip, port, secret, engine, Version, Text("TiaPath")); saved++; Append(target.Names + " 已保存：" + target.Profile.Path); }
                catch (Exception ex) { errors.Add(target.Names + "：" + ex.Message); }
            }
            if (remote && saved > 0) ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            Status("已配置 " + saved + " 个客户端");
            Append("重启已配置的客户端，使用 " + (remote ? "tia-portal-vm" : "tia-portal") + " 读取工程树。不要让多个 AI 同时修改同一工程。");
            if (errors.Count > 0) throw new InvalidOperationException("部分客户端未保存，其它成功项已保留：\n" + String.Join("\n", errors));
        }
        private void SetBusy(bool value) { busy = value; Find<Grid>("Panes").IsEnabled = !value; }
        private async Task TestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"), secret = Secret(); int port = Int32.Parse(Text("ServerPort"));
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
                if (MessageBox.Show(Window, "为当前用户授权此 HTTP 地址，并放行本地子网到此端口。\n接下来会出现 Windows 管理员权限提示，继续？", "网络权限", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
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
            ConfigCore.AtomicJson(StatePath, settings); runningKey = Secret();
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
        public void CapturePage(string path, int mode, bool scrollToBottom = false)
        {
            Find<RadioButton>(mode == 0 ? "RemoteNav" : "LocalNav").IsChecked = true;
            Window.Show(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render);
            if (scrollToBottom) { Find<ScrollViewer>("ContentScroll").ScrollToBottom(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render); }
            // Render() draws the panel at its layout offset, so the bitmap must also cover the margin on
            // both sides — sizing it from ActualWidth alone cut the right edge (and the status pill with it).
            var content = (FrameworkElement)Window.Content;
            int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
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

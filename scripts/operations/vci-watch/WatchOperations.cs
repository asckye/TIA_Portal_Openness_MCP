using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.DiagnosticClients;

public static class WatchOperations
{
    public static bool HasOpenProject(IMcpClient client)
    {
        try
        {
            var result = McpSession.Call("ListPortalProcessProjects", client, new JsonObject());
            return (bool)result["ok"]! && (result["data"]?["items"] as JsonArray ?? []).Any(i => i!.GetValue<string>().Contains(" project=", StringComparison.Ordinal));
        }
        catch (Exception) { return false; } // Probe failure never permits a connection.
    }
    public static string[] BlockNames(IEnumerable<string> items) => items.Select(it => { var name = it.Split('|')[0].Trim(); return name.Split('_').LastOrDefault() is { Length: > 0 } block ? block : name; }).ToArray();
    public static double? ProjectSignal(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var newest = 0d;
        foreach (var directory in new[] { folder, Path.Combine(folder, "XRef"), Path.Combine(folder, "IM/SearchIndex"), Path.Combine(folder, "System") })
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    if (!Path.GetFileName(entry).Equals("vci", StringComparison.OrdinalIgnoreCase)) newest = Math.Max(newest, new DateTimeOffset(File.GetLastWriteTimeUtc(entry)).ToUnixTimeMilliseconds() / 1000d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return newest == 0 ? null : newest;
    }
    public static string TaskXml(string executable, string config, string user, int minutes, DateTimeOffset start)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentException("--interval-minutes must be between 1 and 1440");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? content = null) => new(ns + name, content);
        return new XDocument(new XDeclaration("1.0", "utf-16", null), new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "博途程序变更自动导出+git提交（只读附着，绝不打开工程、绝不写工程）")),
            E("Triggers", E("CalendarTrigger", new object[] { E("StartBoundary", start.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)), E("Enabled", "true"), E("Repetition", new object[] { E("Interval", $"PT{minutes}M"), E("Duration", "P3650D"), E("StopAtDurationEnd", "false") }), E("ScheduleByDay", E("DaysInterval", "1")) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"), E("UserId", user), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new object[] { E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT15M"), E("MultipleInstancesPolicy", "IgnoreNew"), E("Priority", "7") }),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"), E("Exec", new object[] { E("Command", executable), E("Arguments", "--config \"" + config + "\""), E("WorkingDirectory", Path.GetDirectoryName(config)) })))).ToString();
    }
    public static int RegisterTask(string executable, string config, int interval, bool remove)
    {
        if (remove) { var deleted = Shell("schtasks.exe", ["/Delete", "/TN", "TiaVciWatch", "/F"]); Require(deleted); return 0; }
        if (!File.Exists(executable) || Path.GetFileName(executable) != "watch.exe") throw new ArgumentException("Register the published bin-build/vci-watch/watch.exe");
        if (!File.Exists(config)) throw new ArgumentException("Create config.json before registering");
        var xml = Path.Combine(Path.GetTempPath(), "TiaVciWatch-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(xml, TaskXml(executable, config, Environment.UserDomainName + "\\" + Environment.UserName, interval, DateTimeOffset.Now.AddMinutes(1)), Encoding.Unicode);
            Require(Shell("schtasks.exe", ["/Create", "/TN", "TiaVciWatch", "/XML", xml, "/F"]));
        }
        finally { File.Delete(xml); }
        return 0;
    }
    private static (int Code, string Out, string Error) Shell(string executable, string[] args, string? cwd = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (cwd is not null) start.WorkingDirectory = cwd;
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start " + executable);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.WaitForExit();
        return (process.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
    }
    private static void Require((int Code, string Out, string Error) result) { if (result.Code != 0) throw new InvalidOperationException(result.Error + " " + result.Out); }
    public static int Run(string configPath)
    {
        var here = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var stateFile = Path.Combine(here, "watch.state.json"); var lockFile = Path.Combine(here, "watch.lock");
        JsonObject ReadState()
        {
            try { return JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject(); }
            catch (IOException) { return new JsonObject(); }
            catch (System.Text.Json.JsonException) { return new JsonObject(); }
        }
        void WriteState(JsonObject state) => File.WriteAllText(stateFile, PythonJson.Dumps(state, ensureAscii: false), new UTF8Encoding(false));
        void Log(string message)
        {
            var directory = Path.Combine(here, "log"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "watch-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message + "\n", new UTF8Encoding(false));
        }
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var key = (string?)config["releaseKey"] ?? config["tiaMajorVersion"]?.ToString() ?? "21";
        if (key is not ("20" or "21")) throw new ArgumentException("vci-watch requires a V20/V21 full-engine bundle");
        var launch = FoundationLaunch.Resolve((string?)config["bundleRoot"] ?? throw new ArgumentException("Configure bundleRoot (enginePath is retired)"), key);
        var workspace = (string)config["workspaceFolder"]!; var workspaceName = (string?)config["workspaceName"] ?? "";
        if (!WindowsProcesses.Snapshot().Any(WindowsProcesses.UserSession)) return 0;
        double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        double Number(string name, double fallback) => (double?)config[name] ?? fallback;
        var previous = ReadState(); var signal = ProjectSignal((string?)config["projectFolder"]);
        var unchanged = signal is not null && (double?)previous["lastSignal"] is { } last && Math.Abs(signal.Value - last) < .001;
        var since = Now() - ((double?)previous["lastFullCheck"] ?? 0);
        if (unchanged && since < Number("forceFullCheckMinutes", 60) * 60 || since < Number("minFullCheckMinutes", 10) * 60 || unchanged && previous["pendingCompile"] is not null && Now() < ((double?)previous["pendingUntil"] ?? 0)) return 0;
        if (File.Exists(lockFile))
        {
            var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile)).TotalSeconds;
            if (age < Number("lockTimeoutSeconds", 900)) return 0;
            Log($"发现超期的锁（{age:F0} 秒），按卡死处理"); File.Delete(lockFile);
        }
        FileStream held;
        try { held = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) { return 0; }
        held.Write(Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))); held.Flush();
        StdioClient? client = null; string? pending = null; var pendingUntil = 0d; var didSomething = false; var timedOut = false;
        using var reaperStop = new CancellationTokenSource(); Task? reaper = null;
        try
        {
            var old = ReadState();
            if ((int?)old["enginePid"] is { } pid && WindowsProcesses.MatchesStart(pid, (long?)old["hostStartTicks"]) && WindowsProcesses.OwnedHost(WindowsProcesses.CommandLine(pid), launch)) WindowsProcesses.Kill(pid, true);
            foreach (var recorded in old["spawnedTiaPids"] as JsonArray ?? [])
                if (WindowsProcesses.HeadlessPortal(WindowsProcesses.CommandLine((int)recorded!))) WindowsProcesses.Kill((int)recorded!);
            old.Remove("enginePid"); old.Remove("hostStartTicks"); old.Remove("spawnedTiaPids"); WriteState(old);
            client = new StdioClient(launch, "tia-vci-watch", shutdownTimeoutSeconds: 10);
            try { client.Process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            var state = ReadState(); state["enginePid"] = client.Pid; state["hostStartTicks"] = client.Process.StartTime.ToUniversalTime().Ticks; state["startedAt"] = Now(); WriteState(state);
            reaper = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Number("cycleTimeoutSeconds", 600)), reaperStop.Token); }
                catch (OperationCanceledException) { return; }
                timedOut = true; Log("本轮超时，掐断 FoundationHost 及其 worker"); WindowsProcesses.Kill(client.Pid, true);
            });
            if (!HasOpenProject(client)) return 0;
            JsonObject Call(string tool, JsonObject? arguments = null) => McpSession.Call(tool, client, arguments ?? new JsonObject(), 600);
            if (!(bool)Call("ConnectPortal")["ok"]!) return 1;
            var attached = Call("AttachOpenProject");
            if (!(bool)attached["ok"]!) { Log("没有已打开的工程可附着：" + attached["error"]); return 0; }
            var status = McpResults.Successful(Call("GetVersionControlStatus", new JsonObject { ["changedOnly"] = true, ["workspaceName"] = workspaceName }));
            string[] Items(JsonObject value) => (value["data"]?["items"] as JsonArray ?? []).Select(i => (string)i!).ToArray();
            var items = Items(status); if (items.Length == 0) return 0;
            didSomething = true; Log($"检测到 {items.Length} 个对象有变更：" + string.Join('、', BlockNames(items).Take(8)));
            Log(McpSession.ApprovalNotice);
            JsonObject Sync() => Call("SynchronizeVersionControlWorkspace", new JsonObject { ["direction"] = "ProjectToWorkspace", ["dryRun"] = false, ["workspaceName"] = workspaceName });
            var sync = Sync();
            if (McpSession.ResetRequired(sync)) throw new InvalidOperationException(sync["error"]?.ToString());
            var failures = Items(sync).Where(i => i.Contains("FAILED", StringComparison.Ordinal)).ToArray();
            var needCompile = failures.Where(i => i.Contains("inconsistent", StringComparison.Ordinal)).ToArray();
            if (needCompile.Length > 0)
            {
                var names = string.Join('、', BlockNames(needCompile).Take(8));
                if ((bool?)config["autoCompile"] == true)
                {
                    Log($"{needCompile.Length} 个块未编译，按配置自动编译：{names}");
                    foreach (var sw in config["compileSoftwarePaths"] as JsonArray ?? [])
                    {
                        var compiled = Call("CompilePlcSoftware", new JsonObject { ["softwarePath"] = sw!.DeepClone() });
                        Log("  编译 " + sw + " → " + (compiled["data"]?["summary"]?.ToString() ?? compiled["error"]?.ToString())); McpResults.Successful(compiled);
                    }
                    sync = McpResults.Successful(Sync()); failures = Items(sync).Where(i => i.Contains("FAILED", StringComparison.Ordinal)).ToArray();
                }
                else { pending = names; pendingUntil = Now() + Number("pendingCompileCooldownMinutes", 30) * 60; Log($"{needCompile.Length} 个块尚未编译，VCI 导不出：{names}；请在博途编译，冷却后重试"); }
            }
            Log("导出：" + (sync["data"]?["summary"]?.ToString() ?? sync["error"]?.ToString()));
            var hard = failures.Where(i => !i.Contains("inconsistent", StringComparison.Ordinal)).ToArray();
            foreach (var failure in hard) Log("  失败明细：" + failure.Replace('\r', ' ').Replace('\n', ' '));
            if (hard.Length > 0 || !(bool)sync["ok"]!) { Log("本轮不提交，留给人看"); return 1; }
            var git = Shell("git", ["status", "--porcelain"], workspace); Require(git);
            var real = git.Out.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.EndsWith("CHANGELOG.md", StringComparison.Ordinal)).ToArray();
            if (real.Length == 0) { Log("导出后文本无实际差异，不提交"); return 0; }
            var changelog = Path.Combine(workspace, "CHANGELOG.md");
            var head = File.Exists(changelog) ? "" : "# 博途程序变更记录\n\n由 TIA VCI 看门狗自动生成：每次检测到工程里的块与文本文件不一致，\n就把它导出并记在这里。**不含硬件组态**（VCI 不支持）。\n";
            var body = new List<string> { "", "## " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "" };
            foreach (var item in items) { var parts = item.Split('|').Select(p => p.Trim()).ToArray(); body.Add("- `" + parts[0] + "` — " + (parts.ElementAtOrDefault(1) == "Unequal" ? "工程侧有改动" : parts.ElementAtOrDefault(1) ?? "?")); }
            body.Add(""); File.AppendAllText(changelog, head + string.Join('\n', body), new UTF8Encoding(false));
            var author = (string?)config["gitAuthor"] ?? "tia-vci-watch <watch@local>"; var split = author.IndexOf('<');
            if (split < 1 || !author.EndsWith('>')) throw new ArgumentException("gitAuthor must be Name <email>");
            Require(Shell("git", ["add", "-A"], workspace));
            var commit = Shell("git", ["-c", "user.name=" + author[..split].Trim(), "-c", "user.email=" + author[(split + 1)..^1], "commit", "-m", $"auto: {real.Length} 个对象变更 —— " + string.Join('、', BlockNames(items).Take(6))], workspace);
            Log(commit.Code == 0 ? "已提交：" + commit.Out.Split('\n')[0] : "git commit 未成功：" + Campaign.Trim(commit.Out + commit.Error, 400)); return 0;
        }
        catch (Exception ex) { Log("本轮失败：" + ex.Message); return 1; }
        finally
        {
            reaperStop.Cancel(); reaper?.GetAwaiter().GetResult();
            if (client is not null)
            {
                var pid = client.Pid; long memory = 0;
                try { memory = client.Process.WorkingSet64 / 1024 / 1024; } catch (InvalidOperationException) { }
                // FoundationHost owns an engine worker. Include that worker's children,
                // but only terminate recorded Openness headless Portal instances.
                var snapshot = WindowsProcesses.Snapshot(); var owned = new HashSet<int> { pid };
                bool more; do { more = false; foreach (var row in snapshot) if (owned.Contains(row.ParentPid)) more |= owned.Add(row.Pid); } while (more);
                var orphans = snapshot.Where(p => owned.Contains(p.ParentPid) && WindowsProcesses.HeadlessPortal(p.CommandLine)).ToArray();
                var state = ReadState(); state["spawnedTiaPids"] = new JsonArray(orphans.Select(p => JsonValue.Create(p.Pid) as JsonNode).ToArray()); WriteState(state);
                foreach (var row in orphans) WindowsProcesses.Kill(row.Pid);
                client.Dispose(); if (timedOut || didSomething || memory > 300) Log($"本轮结束：FoundationHost 内存约 {memory} MB" + (timedOut ? "，已超时" : ""));
            }
            var final = ReadState(); final["lastSignal"] = ProjectSignal((string?)config["projectFolder"]); final["lastFullCheck"] = Now(); final["pendingCompile"] = pending; final["pendingUntil"] = pendingUntil; final.Remove("enginePid"); final.Remove("hostStartTicks"); final.Remove("startedAt"); WriteState(final);
            held.Dispose(); File.Delete(lockFile);
        }
    }
}

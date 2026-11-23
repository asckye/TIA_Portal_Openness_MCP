using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TiaMcp.Updater
{
    internal static class Program
    {
        private static string logPath;
        private static int Main(string[] args)
        {
            UpdaterOptions options;
            try { options = Parse(args); }
            catch (Exception error) { return Fail(error, UpdaterText.Bilingual("InvalidArguments", "invalid arguments")); }

            if (options.SelfTest) return SelfTest();
            string executable = Assembly.GetExecutingAssembly().Location;
            string root = options.InstallRoot;
            if (string.IsNullOrWhiteSpace(root)) root = FindInstallRoot(executable);
            try
            {
                options.InstallRoot = Path.GetFullPath(root);
                if (UpdaterEngine.HasPendingSwitch(options.InstallRoot)) UpdaterEngine.RequireInstallLocation(options.InstallRoot);
                else UpdaterEngine.RequireDelivery(options.InstallRoot);
            }
            catch (Exception error) { return Fail(error); }

            // The worker runs from TEMP, so neither its image nor a loaded updater assembly is replaced in place.
            if (!IsStaged(args) && (!options.Check || UpdaterEngine.HasPendingSwitch(options.InstallRoot))
                && (IsWithin(executable, options.InstallRoot) || IsWithin(executable, UpdaterEngine.SwitchDirectory(options.InstallRoot))))
            {
                try
                {
                    var stagedArgs = HasInstallRoot(args) ? args : args.Concat(new[] { "-InstallRoot", options.InstallRoot }).ToArray();
                    return StartStaged(stagedArgs);
                }
                catch (Exception error) {
                    return Fail(error);
                }
            }

            try { return UpdaterEngine.Run(options, Say, null, () => OpenLog(options.InstallRoot)); }
            catch (Exception error)
            {
                if (!UpdaterEngine.HasPendingSwitch(options.InstallRoot)) Relaunch(options);
                return Fail(error);
            }
        }

        private static void OpenLog(string root)
        {
            var data = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
            string logs = Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(root, "data") : Path.GetFullPath(data),
                "logs", "updater");
            Directory.CreateDirectory(logs);
            logPath = Path.Combine(logs, "updater-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
            Say("Updater log: " + logPath);
        }

        private static UpdaterOptions Parse(string[] args)
        {
            var options = new UpdaterOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].TrimStart('-', '/');
                string value = null;
                if (arg.Equals("Version", StringComparison.OrdinalIgnoreCase) || arg.Equals("Repository", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("InstallRoot", StringComparison.OrdinalIgnoreCase) || arg.Equals("TimeoutSeconds", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("WaitForPid", StringComparison.OrdinalIgnoreCase))
                {
                    if (++i >= args.Length) throw new ArgumentException("Missing value for -" + arg + ".");
                    value = args[i];
                }
                switch (arg.ToLowerInvariant())
                {
                    case "check": options.Check = true; break;
                    case "version": options.Version = value; break;
                    case "rollback": options.Rollback = true; break;
                    case "force": options.Force = true; break;
                    case "repository": options.Repository = value; break;
                    case "installroot": options.InstallRoot = value; break;
                    case "timeoutseconds": options.TimeoutSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "waitforpid": options.WaitForPid = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "relaunchconfigurator": options.RelaunchConfigurator = true; break;
                    case "selftest": options.SelfTest = true; break;
                    case "staged": break;
                    case "help": case "?": PrintUsage(); Environment.Exit(0); break;
                    default: throw new ArgumentException("Unknown option -" + arg + ".");
                }
            }
            if (options.Check && options.Rollback) throw new ArgumentException("-Check and -Rollback cannot be combined.");
            if (options.Rollback && (options.Version != null || options.Force))
                throw new ArgumentException("-Rollback cannot be combined with -Version or -Force.");
            return options;
        }

        private static bool IsStaged(string[] args) {
            return args.Any(a => a.Equals("-staged", StringComparison.OrdinalIgnoreCase) || a.Equals("--staged", StringComparison.OrdinalIgnoreCase)); }

        private static bool HasInstallRoot(string[] args)
        {
            return args.Any(a => a.TrimStart('-', '/').Equals("InstallRoot", StringComparison.OrdinalIgnoreCase));
        }

        private static string FindInstallRoot(string executable)
        {
            string delivery = null;
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executable))); directory != null; directory = directory.Parent)
            {
                string path = directory.FullName;
                if (UpdaterEngine.HasPendingSwitch(path)) return path;
                if (path.EndsWith(UpdaterEngine.SwitchSuffix, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(path, "plan.json")))
                    return path.Substring(0, path.Length - UpdaterEngine.SwitchSuffix.Length);
                if (delivery == null && File.Exists(Path.Combine(path, "manifest", "delivery.json"))) delivery = path;
            }
            return delivery ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private static int StartStaged(string[] args)
        {
            string source = Assembly.GetExecutingAssembly().Location;
            string stage = Path.Combine(Path.GetTempPath(), "tia-mcp-updater-" + Guid.NewGuid().ToString("N"));
            string target = UpdaterFileStaging.CopyTo(source, stage);
            var forwarded = args.Where(a => !a.Equals("-staged", StringComparison.OrdinalIgnoreCase)).Concat(new[] { "-staged" });
            var start = new ProcessStartInfo(target, string.Join(" ", forwarded.Select(Quote))) {
                WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = true };
            Process.Start(start);
            Say(UpdaterText.Bilingual("Staged", "updater staged outside the install and started") + ": " + stage);
            return 0;
        }

        private static string Quote(string value)
        {
            if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"')) return value;
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes).Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"'); return result.ToString();
        }

        private static bool IsWithin(string path, string root)
        {
            string file = Path.GetFullPath(path), directory = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return file.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }

        private static void Relaunch(UpdaterOptions options)
        {
            if (!options.RelaunchConfigurator || string.IsNullOrWhiteSpace(options.InstallRoot)) return;
            string executable = Path.Combine(options.InstallRoot, "TiaOpenness.exe");
            if (File.Exists(executable))
            {
                try { Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = options.InstallRoot, UseShellExecute = true }); }
                catch (Exception error) {
                    Console.Error
                        .WriteLine(UpdaterText.Bilingual("RelaunchFailed", "Workbench could not be relaunched") + ": " + error.GetBaseException().Message);
                }
            }
        }

        private static int SelfTest()
        {
            try
            {
                if (!UpdaterEngine.IsSafeRelativePath("runtime/v21/TiaMcp.Engine.V21.exe")
                    || UpdaterEngine.IsSafeRelativePath("../outside")
                    || UpdaterEngine.IsSafeRelativePath("data/config/user.json") == false
                    || UpdaterEngine.CompareVersions("4.0.0", "3.3.0") <= 0)
                    throw new InvalidOperationException("Path or version rules failed.");
                Say(UpdaterText.Bilingual("SelfTestPassed", "updater offline self-test passed") + ": 4 checks");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(UpdaterText.Bilingual("SelfTestFailed", "self-test failed") + ": " + error.Message); return 1; }
        }

        private static void Say(string message)
        {
            var line = "[" + DateTime.UtcNow.ToString("O") + "] " + message;
            Console.WriteLine(line);
            if (logPath == null) return;
            try { File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch (Exception io) when (io is IOException || io is UnauthorizedAccessException) { Console.Error.WriteLine(io.Message); }
        }

        private static int Fail(Exception error, string label = null)
        {
            string message = (label ?? UpdaterText.Bilingual("Failure", "FAIL")) + ": " + error.Message;
            Console.Error.WriteLine(message);
            if (logPath != null)
            {
                try { File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " " + error + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception io) when (io is IOException || io is UnauthorizedAccessException) { Console.Error.WriteLine(io.Message); }
            }
            if (Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(UpdaterText.Bilingual("PressEnter", "Press Enter to close."));
                Console.ReadLine();
            }
            return 1;
        }
        private static void PrintUsage()
        {
            Console
                .WriteLine("runtime/tools/TiaMcp.Updater.exe [-Check] [-Version vX.Y.Z] [-Rollback] [-Force] [-Repository owner/name] [-InstallRoot path] " +
                "[-TimeoutSeconds n] [-WaitForPid pid] [-RelaunchConfigurator]");
            Console
                .WriteLine(UpdaterText
                    .Bilingual("UsageCheck",
                        "From the bundle root, pass -InstallRoot .; use -Check to inspect a release or -Rollback to restore the newest backup."));
        }
    }
}

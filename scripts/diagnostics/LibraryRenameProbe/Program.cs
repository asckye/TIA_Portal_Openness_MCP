using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LibraryRenameProbe
{
    internal static class Program
    {
        [MTAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.SequenceEqual(new[] { "--self-test" }))
                {
                    int checks = Options.SelfTest();
                    if (checks != 0) return checks;
                    CampaignRunner.SelfTest();
                    return 0;
                }
                if (args.Length == 0) return RunDefaultCampaign();
                if (args.SequenceEqual(new[] { "--help" }))
                {
                    Console.WriteLine("V21 Unified library rename diagnostic. No native calls by default.\n" +
                        "Double-click this executable to run the VM campaign. --self-test is offline; --preflight reads installation metadata.\n" +
                        "Run --campaign --source <diagnostic .al21> [--timeout-seconds <60..1800>] to select an input.\n" +
                        "Native: --stage prepare|run --case <case> --output <owned directory> --source <diagnostic .al21>");
                    return 0;
                }
                if (args[0] == "--campaign") return RunDefaultCampaign(args.Skip(1).ToArray());
                bool preflight = args.SequenceEqual(new[] { "--preflight" });
                Options? options = preflight ? null : Options.Parse(args);
                string api = InstalledApi();
                string basePath = Path.Combine(api, "Siemens.Engineering.Base.dll");
                var identity = AssemblyName.GetAssemblyName(basePath);
                if (identity.Version.Major != 21 || BitConverter.ToString(identity.GetPublicKeyToken()).Replace("-", "").ToLowerInvariant() != "29bfe5fdf4ba5d3b")
                    throw new InvalidOperationException("Installed V21 API identity mismatch");
                Console.WriteLine("Installed API: " + identity.FullName + "; file version " + System.Diagnostics.FileVersionInfo.GetVersionInfo(basePath).FileVersion);
                if (preflight) return 0;
                AppDomain.CurrentDomain.AssemblyResolve += (_, ev) =>
                {
                    var requested = new AssemblyName(ev.Name);
                    if (!requested.Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
                    string path = Path.Combine(api, requested.Name + ".dll");
                    if (!File.Exists(path)) return null;
                    if (!string.Equals(AssemblyName.GetAssemblyName(path).FullName, requested.FullName, StringComparison.OrdinalIgnoreCase))
                        throw new FileLoadException("Exact installed API identity mismatch: " + requested.Name);
                    return Assembly.LoadFrom(path);
                };
                return Native.Run(options!);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }

        private static int RunDefaultCampaign(string[]? args = null)
        {
            string source = @"C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\library-general-1133\MCP_GeneralScripts_Rename_20261001_1133\MCP_GeneralScripts_Rename_20261001_1133.al21";
            int timeout = 600;
            args = args ?? Array.Empty<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--source" && i + 1 < args.Length) source = args[++i];
                else if (args[i] == "--timeout-seconds" && i + 1 < args.Length && int.TryParse(args[++i], out var seconds)) timeout = seconds;
                else throw new ArgumentException("Expected --source <diagnostic .al21> and/or --timeout-seconds <60..1800>.");
            }
            if (timeout < 60 || timeout > 1800) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be 60..1800 seconds.");
            int exitCode = 2;
            try
            {
                if (Options.SelfTest() != 0) return 2;
                CampaignRunner.SelfTest();
                string api = InstalledApi();
                string basePath = Path.Combine(api, "Siemens.Engineering.Base.dll");
                var identity = AssemblyName.GetAssemblyName(basePath);
                if (identity.Version.Major != 21 || BitConverter.ToString(identity.GetPublicKeyToken()).Replace("-", "").ToLowerInvariant() != "29bfe5fdf4ba5d3b")
                    throw new InvalidOperationException("Installed V21 API identity mismatch");
                Console.WriteLine("Installed API: " + identity.FullName + "; file version " + System.Diagnostics.FileVersionInfo.GetVersionInfo(basePath).FileVersion);
                AppDomain.CurrentDomain.AssemblyResolve += (_, ev) =>
                {
                    var requested = new AssemblyName(ev.Name);
                    if (!requested.Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
                    string path = Path.Combine(api, requested.Name + ".dll");
                    if (!File.Exists(path)) return null;
                    if (!string.Equals(AssemblyName.GetAssemblyName(path).FullName, requested.FullName, StringComparison.OrdinalIgnoreCase))
                        throw new FileLoadException("Exact installed API identity mismatch: " + requested.Name);
                    return Assembly.LoadFrom(path);
                };
                exitCode = CampaignRunner.Run(Assembly.GetExecutingAssembly().Location, source,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results"), timeout, new WindowsCampaignHost());
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 2; }
            finally
            {
                Console.WriteLine("Campaign finished with exit code " + exitCode + ". Press any key to close.");
                try { Console.ReadKey(true); } catch (InvalidOperationException) { }
            }
            return exitCode;
        }

        static string InstalledApi()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP21\Global"))
                    if (key?.GetValue("Path") is string path && !string.IsNullOrWhiteSpace(path))
                        return Path.Combine(path, "PublicAPI", "V21", "net48");
            throw new InvalidOperationException("Installed TIA V21 not found. Run this package inside the TIA VM.");
        }
    }

    internal sealed class Options
    {
        internal const string SourceName = "LSicar_GeneralScripts";
        internal const string TargetName = "MCP_GeneralScripts_Renamed";
        internal const string SourceGuid = "04254662-9034-496e-a436-109c945e1de2";
        internal static readonly string[] Cases = { "control", "property", "attributes", "edit-property", "edit-attributes",
            "document-create-control", "document-update-control", "document-create-rename", "document-update-rename" };
        internal string Stage = "", Case = "", Output = "", Source = "";
        // Keep native project paths independent of ZIP extraction depth. The first VM
        // run failed at Projects.Create: 149 characters, with a reported limit of 143.
        internal string Workspace => ShortWorkspace(Output, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        internal string ProjectPath => Path.Combine(Workspace, "Probe", "Probe.ap21");
        internal string ImportControlPath => Path.Combine(Workspace, "ImportControl", "ImportControl.ap21");
        internal string Marker => Path.Combine(Output, "owner.json");

        internal static Options Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !new[] { "--stage", "--case", "--output", "--source" }.Contains(args[i]) || map.ContainsKey(args[i]))
                    throw new ArgumentException("Unknown, duplicate or incomplete option");
                map.Add(args[i], args[i + 1]);
            }
            if (map.Count != 4) throw new ArgumentException("All four options are required");
            var o = new Options { Stage = map["--stage"], Case = map["--case"], Output = Absolute(map["--output"]), Source = Absolute(map["--source"]) };
            if (!(o.Stage == "prepare" || o.Stage == "run") || !Cases.Contains(o.Case)) throw new ArgumentException("Unknown stage/case");
            ValidateNativePaths(o.Workspace);
            if (!File.Exists(o.Source) || !o.Source.EndsWith(".al21", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(o.Source).StartsWith("MCP_GeneralScripts_Rename_", StringComparison.Ordinal))
                throw new ArgumentException("Input must be the diagnostic MCP_GeneralScripts_Rename_*.al21 library; user projects are refused");
            if (Within(o.Output, Path.GetDirectoryName(o.Source)!) || Within(o.Source, o.Output))
                throw new ArgumentException("Input and output directories must be separate");
            if (Within(o.Source, o.Workspace) || Within(o.Workspace, Path.GetDirectoryName(o.Source)!))
                throw new ArgumentException("Input and native workspace must be separate");
            if (o.Stage == "prepare" && (File.Exists(o.Output) || Directory.Exists(o.Output)))
                throw new ArgumentException("Prepare requires a new directory");
            if (o.Stage == "prepare" && (File.Exists(o.Workspace) || Directory.Exists(o.Workspace)))
                throw new ArgumentException("Native workspace already exists; use a new results directory, never reuse a prior test");
            if (o.Stage == "run" && (!File.Exists(o.Marker) || !File.Exists(o.ProjectPath) || File.Exists(Path.Combine(o.Output, "run-started"))))
                throw new ArgumentException("Run requires a prepared, never-run diagnostic case");
            if (o.Stage == "run" && (!File.Exists(Path.Combine(o.Workspace, ".probe-owner")) ||
                File.ReadAllText(Path.Combine(o.Workspace, ".probe-owner")) != o.Output))
                throw new ArgumentException("Native workspace owner does not match this diagnostic case");
            return o;
        }

        internal static string ShortWorkspace(string output, string localAppData)
        {
            using (var sha = SHA256.Create())
            {
                string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(output).ToUpperInvariant())))
                    .Replace("-", "").Substring(0, 20).ToLowerInvariant();
                return Path.Combine(Absolute(localAppData), "TRP", key);
            }
        }

        internal static void ValidateNativePaths(string workspace)
        {
            Absolute(workspace); // Includes reparse/drive checks; no directories are created.
            foreach (string name in new[] { "Probe", "ImportControl" })
            {
                string projectDirectory = Path.Combine(workspace, name);
                if (projectDirectory.Length > 143)
                    throw new ArgumentException("Native project directory exceeds the observed V21 143-character limit before TIA startup: " + projectDirectory);
                Absolute(projectDirectory);
                Absolute(Path.Combine(projectDirectory, name + ".ap21"));
            }
        }

        internal static DirectoryInfo NativeDirectory(string path, bool create = false)
        {
            // .NET Framework Directory.CreateDirectory returns a DirectoryInfo whose
            // FullName is absolute but ToString() can be only the leaf name (5.1.1).
            // Use the public absolute-path constructor recommended by Siemens.
            var directory = new DirectoryInfo(Absolute(path));
            if (create) directory.Create();
            ValidateDirectoryRepresentation(directory);
            return directory;
        }

        internal static void ValidateDirectoryRepresentation(DirectoryInfo directory)
        {
            if (!string.Equals(Absolute(directory.ToString()), directory.FullName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Native DirectoryInfo must retain its full absolute path in both FullName and ToString");
        }

        internal static string Absolute(string path)
        {
            if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || path.IndexOf('"') >= 0)
                throw new ArgumentException("Use an absolute local drive path");
            string full = Path.GetFullPath(path);
            if (full.TrimEnd('\\') == Path.GetPathRoot(full).TrimEnd('\\')) throw new ArgumentException("Root path refused");
            for (var entry = new DirectoryInfo(full); entry != null; entry = entry.Parent)
                if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse paths refused");
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse file refused");
            return full;
        }
        internal static bool Within(string child, string parent) => Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        internal static int SelfTest()
        {
            int count = 0;
            void Check(bool value) { if (!value) throw new Exception("Self-test failed: " + count); count++; }
            foreach (var bad in new[] { "C:", "C:\\", "relative", "C:relative", "\\root", @"\\host\share", @"\\?\C:\temp" })
            {
                bool rejected = false; try { Absolute(bad); } catch (ArgumentException) { rejected = true; } Check(rejected);
            }
            foreach (var bad in new[] { new string[0], new[] { "--stage", "run" }, new[] { "--pid", "5268" }, new[] { "--project", "AutomaticDipCoatingMachine" }, new[] { "--stage", "run", "--stage", "run" } })
            {
                bool rejected = false; try { Parse(bad); } catch (ArgumentException) { rejected = true; } Check(rejected);
            }
            Check(Within(@"C:\tests\child", @"C:\tests"));
            Check(!Within(@"C:\tests-other\child", @"C:\tests"));
            Check(!Within(@"C:\tests\..\outside", @"C:\tests"));
            const string vmAppData = @"C:\Users\SIEMENS\AppData\Local";
            const string vmResults = @"C:\Users\SIEMENS\Desktop\LibraryRenameProbe-V21-20261001-121722\LibraryRenameProbe-V21-20261001-121722\results\20261001-122041-40b08e26";
            Check(Path.Combine(vmResults, "control", "Probe").Length == 149);
            var workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Cases)
            {
                string path = ShortWorkspace(Path.Combine(vmResults, name), vmAppData);
                ValidateNativePaths(path);
                Check(Path.Combine(path, "ImportControl").Length <= 143);
                Check(workspaces.Add(path));
                Check(path == ShortWorkspace(Path.Combine(vmResults, name).ToLowerInvariant(), vmAppData));
            }
            ValidateNativePaths(@"C:\" + new string('a', 126)); // 3 + 126 + 1 + 13 = 143
            Check(true);
            bool tooLongRejected = false;
            try { ValidateNativePaths(@"C:\" + new string('a', 127)); } catch (ArgumentException) { tooLongRejected = true; }
            Check(tooLongRejected);
            string fixture = Path.Combine(Path.GetTempPath(), "trp-directory-" + Guid.NewGuid().ToString("N"));
            var fixtureDirectories = new List<string>();
            try
            {
                foreach (string leaf in new[] { "5.1.1", "import", "unicode_\u6d4b\u8bd5 space" })
                {
                    string path = Path.Combine(fixture, leaf);
                    var frameworkCreated = Directory.CreateDirectory(path);
                    fixtureDirectories.Add(path);
                    Check(frameworkCreated.FullName == path);
                    Check(!Path.IsPathRooted(frameworkCreated.ToString())); // Reproduce the net48 regression.
                    bool displayRejected = false;
                    try { ValidateDirectoryRepresentation(frameworkCreated); } catch (ArgumentException) { displayRejected = true; }
                    Check(displayRejected);
                    var absolute = NativeDirectory(frameworkCreated.FullName, true);
                    Check(absolute.FullName == path && absolute.ToString() == path);
                    Check(absolute.Exists);
                }
                string uncreated = Path.Combine(fixture, "not-created");
                Check(NativeDirectory(uncreated).ToString() == uncreated && !Directory.Exists(uncreated));
            }
            finally
            {
                // Only the exact empty directories created above, with no recursive delete.
                foreach (string path in fixtureDirectories) Directory.Delete(path, false);
                if (Directory.Exists(fixture)) Directory.Delete(fixture, false);
            }
            Check(Cases.Distinct().Count() == 9);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)));
            Check(System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.MTA);
            Console.WriteLine(count + " offline checks passed; native tests NOT RUN."); return 0;
        }
    }
}

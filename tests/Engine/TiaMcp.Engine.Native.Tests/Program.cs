using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace NativeTests
{
    // No Siemens types in bootstrap, argument checks, registry preflight or self-tests.
    internal static class Program
    {
#if TIA_V20
        internal const int Major = 20;
        private const string BaseAssembly = "Siemens.Engineering";
#else
        internal const int Major = 21;
        private const string BaseAssembly = "Siemens.Engineering.Base";
#endif
        [MTAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.SequenceEqual(new[] { "--self-test" })) return Safety.SelfTest();
                if (args.Length == 0 || args.SequenceEqual(new[] { "--help" }))
                {
                    Console.WriteLine("Native lifecycle smoke tests V" + Major + ". Default: NOT RUN.\n" +
                        "--self-test: offline safety checks; --preflight: registry/API identity only.\n" +
                        "Use scripts/checks/Test-NativeLifecycle.py for supervised live execution.\n" +
                        "Live: --run-live --confirm-new-portal --output <new absolute directory> --iterations <1..100>");
                    return 0;
                }
                bool preflight = args.SequenceEqual(new[] { "--preflight" });
                var options = preflight ? null : Safety.Parse(args);
                string api = InstalledApi();
                var assembly = AssemblyName.GetAssemblyName(Path.Combine(api, BaseAssembly + ".dll"));
                if (assembly.Version.Major != Major) throw new InvalidOperationException("Installed API major version mismatch.");
                Console.WriteLine("PREFLIGHT V" + Major + ": " + assembly.FullName + " at " + api +
                    "; installation/group/license/firewall execution remains UNVERIFIED.");
                if (preflight) return 0;
                // Install-relative resolution is registered BEFORE the separate NoInlining entry point is JIT compiled.
                AppDomain.CurrentDomain.AssemblyResolve += (_, ev) => Resolve(api, ev.Name);
                return LiveSuite.Run(options!);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("NOT PASSED: " + ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        private static string InstalledApi()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP" + Major + @"\Global"))
                {
                    var install = key?.GetValue("Path") as string;
                    if (!string.IsNullOrWhiteSpace(install))
                        return Path.Combine(install, "PublicAPI", "V" + Major, Major == 21 ? "net48" : "");
                }
            }
            throw new InvalidOperationException("Matching TIA installation not registered; a PublicAPI copy is only sufficient for compilation.");
        }

        private static Assembly? Resolve(string api, string requested)
        {
            var name = new AssemblyName(requested);
            if (!(name.Name == "Siemens.Engineering" || name.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))) return null;
            var candidate = Path.Combine(api, name.Name + ".dll");
            if (!File.Exists(candidate)) return null;
            if (!string.Equals(AssemblyName.GetAssemblyName(candidate).FullName, name.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("Exact installed API identity mismatch: " + name.Name);
            return Assembly.LoadFrom(candidate);
        }
    }

    internal sealed class Options
    {
        internal string Output = "";
        internal int Iterations;
    }

    internal static class Safety
    {
        internal static Options Parse(string[] args)
        {
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!flags.Add(key)) throw new ArgumentException("Duplicate option: " + key);
                if (key == "--run-live" || key == "--confirm-new-portal") continue;
                if (key != "--output" && key != "--iterations") throw new ArgumentException("Unknown option: " + key);
                if (++i == args.Length) throw new ArgumentException("Missing value: " + key);
                values.Add(key, args[i]);
            }
            if (!flags.Contains("--run-live") || !flags.Contains("--confirm-new-portal"))
                throw new ArgumentException("Both live opt-in flags are required.");
            if (!values.TryGetValue("--output", out var output)) throw new ArgumentException("A new absolute output directory is required.");
            ValidateOutput(output);
            int iterations = 1;
            if (values.TryGetValue("--iterations", out var count) && (!int.TryParse(count, out iterations) || iterations < 1 || iterations > 100))
                throw new ArgumentException("Iterations must be 1..100.");
            return new Options { Output = Path.GetFullPath(output), Iterations = iterations };
        }

        internal static void ValidateOutput(string path)
        {
            // Reject drive-relative, rooted-relative, network and device paths before native work.
            if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                throw new ArgumentException("Output must be an absolute local drive path.");
            var full = Path.GetFullPath(path);
            if (full.TrimEnd('\\', '/') == Path.GetPathRoot(full).TrimEnd('\\', '/') || Directory.Exists(full) || File.Exists(full))
                throw new ArgumentException("Output must be a new non-root path; existing data is never reused.");
            RejectReparseAncestors(full);
        }

        internal static void RejectReparseAncestors(string path)
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse point is not allowed: " + dir.FullName);
        }

        internal static bool IsWithin(string child, string parent) => Path.GetFullPath(child).StartsWith(
            Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        internal static int SelfTest()
        {
            int passed = 0;
            void Assert(bool value) { if (!value) throw new Exception("Safety assertion " + (passed + 1) + " failed"); passed++; }
            void Reject(params string[] args) { try { Parse(args); } catch (ArgumentException) { passed++; return; } throw new Exception("Unsafe arguments accepted"); }
            string fresh = Path.Combine(Path.GetTempPath(), "tia-native-selftest-" + Guid.NewGuid().ToString("N"));
            var valid = new[] { "--run-live", "--confirm-new-portal", "--output", fresh };
            Assert(Parse(valid).Iterations == 1);
            Assert(Parse(valid.Concat(new[] { "--iterations", "100" }).ToArray()).Iterations == 100);
            Reject(); Reject("--run-live"); Reject("--confirm-new-portal");
            Reject(valid.Concat(new[] { "--pid", "123" }).ToArray());
            Reject(valid.Concat(new[] { "--project", "AutomaticDipCoatingMachine" }).ToArray());
            Reject(valid.Concat(new[] { "--run-live" }).ToArray());
            foreach (var count in new[] { "0", "101", "-1", "abc" }) Reject(valid.Concat(new[] { "--iterations", count }).ToArray());
            foreach (var path in new[] { "relative", @"C:relative", @"\rooted", @"\\host\share", @"\\?\C:\device", @"C:\", Path.GetTempPath() })
                Reject("--run-live", "--confirm-new-portal", "--output", path);
            Assert(IsWithin(Path.Combine(fresh, "Scratch", "child"), fresh));
            Assert(!IsWithin(fresh + "-sibling", fresh));
            Assert(!IsWithin(Path.Combine(fresh, "..", "outside"), fresh));
            Assert(!Directory.Exists(fresh));
            Assert(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)));
            Assert(System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.MTA);
            Console.WriteLine("COMPLETE: " + passed + " native harness safety checks passed; live TIA tests NOT RUN");
            return 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Version-aware self-routing (Siemens-free — safe to JIT before any Openness assembly
    /// resolves). The bundle ships one exe per TIA major version because the IL is hard-bound
    /// to that version's assembly identities, so running the V21 exe on a V20 machine crashes
    /// at the first Siemens type load. Instead of crashing, Main asks this class to re-exec
    /// the sibling exe that matches the effective TIA version, inheriting stdio so MCP/CLI
    /// callers never notice (GitHub issue #8).
    /// </summary>
    public static class EngineRouter
    {
        /// <summary>TIA major version this binary's IL is bound to.</summary>
        public const int CompiledTiaMajorVersion =
#if TIA_V20
            20;
#else
            21;
#endif

        private const string RedirectGuardVar = "TIAMCP_NO_REDIRECT";

        /// <summary>
        /// Locate the sibling exe built for <paramref name="version"/> next to this one.
        /// Understands both bundle layouts: source-tree (bin\ / bin-v20\ Release\net48) and
        /// plugin runtime (runtime\v20\ / runtime\v21\). Returns null when not found.
        /// </summary>
        public static string? FindSiblingExe(int version)
            => FindSiblingExe(version, () => Process.GetCurrentProcess().MainModule.FileName);

        // The repository override is intentionally ignored, as in the original lookup.
        internal static string? FindSiblingExe(int version, Func<string> ownExePath, string? repositoryRoot = null)
        {
            var target = TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(version);
            try
            {
                string own = ownExePath();
                string exeName = target.IsFullEngine ? "TiaMcp.Engine.V" + target.MajorVersion + ".exe" : "TiaMcp.FoundationHost.exe";
                string dir = Path.GetDirectoryName(own) ?? "";
                var candidates = new List<string>();

                var root = TiaOpenness.Shared.BundleLayout.FindRoot(dir);
                if (root != null)
                {
                    var output = new DirectoryInfo(dir);
                    if (string.Equals(output.Parent?.FullName, Path.Combine(root, "runtime"), StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(output.Name, "v20", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(output.Name, "v21", StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(Path.Combine(output.Parent!.FullName, target.RuntimeDirectory, exeName));
                    else if (target.IsFullEngine && string.Equals(output.Parent?.Name, "Release", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(output.Parent?.Parent?.Parent?.FullName,
                            Path.Combine(root, "src", "Engine"), StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve the original prefix spelling, separators and literal Release/net48 suffix.
                        string source = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir)))!;
                        candidates.Add(Path.Combine(source, target.EngineOutputDirectory, "Release", "net48", exeName));
                    }
                }

                // Keep the original layout probes for incomplete bundles and unrecognized outputs (D-G7-3).
                var m = Regex.Match(dir, @"^(.*)[\\/]bin(-v20)?[\\/]Release[\\/]net48$", RegexOptions.IgnoreCase);
                if (m.Success && target.IsFullEngine)
                {
                    string binDir = target.EngineOutputDirectory;
                    candidates.Add(Path.Combine(m.Groups[1].Value, binDir, "Release", "net48", exeName));
                }

                var parent = new DirectoryInfo(dir);
                if (parent.Parent != null && Regex.IsMatch(parent.Name, @"^v\d+$", RegexOptions.IgnoreCase))
                {
                    candidates.Add(Path.Combine(parent.Parent.FullName, target.RuntimeDirectory, exeName));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c) &&
                        !string.Equals(Path.GetFullPath(c), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                    {
                        return c;
                    }
                }
            }
            catch /* swallow(env-probe): lookup failure retains the original null result so callers fail closed */
            {
                // Lookup failure is returned to the caller, which must fail closed.
            }
            return null;
        }

        /// <summary>
        /// Re-exec the sibling exe for <paramref name="version"/> with identical args.
        /// stdio handles are inherited, so MCP stdio and CLI output pass through unchanged.
        /// Returns false when no sibling exists or the redirect guard is set (loop protection).
        /// </summary>
        public static bool TryRedirect(int version, string[] args, Action<string> log, out int exitCode)
        {
            exitCode = 0;
            if (Environment.GetEnvironmentVariable(RedirectGuardVar) == "1")
            {
                log("EngineRouter: redirect guard set; staying in this exe.");
                return false;
            }

            string? sibling = FindSiblingExe(version);
            if (sibling == null) return false;

            log($"EngineRouter: TIA V{version} requested but this exe is built for V{CompiledTiaMajorVersion}; rerouting to {sibling}");
            var psi = new ProcessStartInfo
            {
                FileName = sibling,
                Arguments = QuoteArgs(args),
                UseShellExecute = false,
            };
            psi.EnvironmentVariables[RedirectGuardVar] = "1";
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                exitCode = p.ExitCode;
            }
            return true;
        }

        /// <summary>Windows-correct argument re-quoting (spaces, quotes, trailing backslashes).</summary>
        public static string QuoteArgs(string[] args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    sb.Append(a);
                    continue;
                }
                sb.Append(TiaOpenness.Shared.ProcessArguments.Quote(a));
            }
            return sb.ToString();
        }
    }
}

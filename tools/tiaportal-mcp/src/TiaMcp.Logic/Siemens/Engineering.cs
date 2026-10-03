using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.Siemens
{
    // Manual Siemens.Engineering.dll resolve
    public class Engineering
    {
        public static int TiaMajorVersion { get; set; }

        // Optional explicit override (CLI --tia-portal-location), e.g. D:\app\TIA20\Portal V20.
        // Takes precedence over TiaPortalLocation env var and registry lookup.
        public static string? TiaPortalLocationOverride { get; set; }

        // When true, launch TIA Portal with its full GUI (slower cold start, allows visual inspection).
        // Default false = headless (WithoutUserInterface), which starts much faster. Set via --with-ui.
        // Lives here (not on Portal) because Program.Main must set it without forcing the CLR to load the
        // Portal type — Portal's Siemens.Engineering field types would be needed before Resolver is wired up.
        public static bool LaunchWithUserInterface { get; set; } = false;

        public static Assembly? Resolver(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);
            if (!assemblyName.Name.StartsWith("Siemens.Engineering"))
            {
                return null;
            }

            var tiaInstallPath = GetTiaPortalInstallPath();
            if (string.IsNullOrEmpty(tiaInstallPath))
            {
                throw new InvalidOperationException($"Could not find TIA Portal installation path for version {TiaMajorVersion} in the registry.");
            }

            var tiaMajorVersionString = TiaMajorVersion.ToString();
            var searchDirectories = new[]
            {
                Path.Combine(tiaInstallPath, "PublicAPI", $"V{tiaMajorVersionString}"),
                Path.Combine(tiaInstallPath, "Bin", "PublicAPI")
            };

            // IEnumerable without given majorVersionString
            var excludedTiaMajorVersions = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20" }
                                    .Where(v => v != $"V{tiaMajorVersionString}");

            foreach (var dir in searchDirectories)
            {
                var assemblyPath = FindAssemblyRecursive(dir, assemblyName.Name + ".dll", excludedTiaMajorVersions);
                if (assemblyPath != null)
                {
                    // Inspect metadata before LoadFrom can put the wrong API into this AppDomain.
                    EngineeringAssemblyIdentity.RequireMatch(assemblyName, AssemblyName.GetAssemblyName(assemblyPath), assemblyPath);
                    return Assembly.LoadFrom(assemblyPath);
                }
            }

            throw new FileNotFoundException($"Could not find DLL '{assemblyName.Name}' for TIA Portal version {TiaMajorVersion} in the installation directories.");
        }

        /// <summary>
        /// Detects the highest installed TIA Portal major version without requiring a CLI flag.
        /// Detection order:
        ///   1. TiaPortalLocation env var — extract version from path (e.g. "Portal V21" → 21)
        ///   2. Registry: HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP*\TIA_Opns
        /// Explicit path version wins; otherwise inspect installed-software registry entries.
        /// Returns the highest registered version, or null if nothing detected.
        /// </summary>
        public static int? DetectTiaMajorVersion()
        {
            var candidates = new List<int>();

            // 0. Explicit override (CLI --tia-portal-location)
            if (!string.IsNullOrWhiteSpace(TiaPortalLocationOverride))
            {
                var version = EngineeringAssemblyIdentity.PathVersion(TiaPortalLocationOverride);
                if (version.HasValue) return version;
            }

            // 1. TiaPortalLocation env var
            var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!string.IsNullOrWhiteSpace(env))
            {
                var version = EngineeringAssemblyIdentity.PathVersion(env);
                if (version.HasValue) return version;
            }

            // 2. Registry scan
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
              try
              {
                using var regBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var installedSw = regBase.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW");
                if (installedSw != null)
                {
                    foreach (var subName in installedSw.GetSubKeyNames())
                    {
                        // Key names follow pattern TIAP21, TIAP20, etc.
                        var numMatch = System.Text.RegularExpressions.Regex.Match(subName, @"TIAP(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!numMatch.Success || !int.TryParse(numMatch.Groups[1].Value, out int regVer)) continue;

                        foreach (var suffix in new[] { @"\TIA_Opns", @"\Global" })
                        {
                            using var opnsKey = installedSw.OpenSubKey(subName + suffix);
                            if (opnsKey?.GetValue("Path") is string path && Directory.Exists(path))
                                candidates.Add(regVer);
                        }
                    }
                }
              }
              catch /* swallow(env-probe): an inaccessible registry view must not discard versions found through other installation probes */ { }
            }

            return candidates.Count > 0 ? candidates.Max() : (int?)null;
        }

        /// <summary>
        /// Answer "will Resolver() actually find Siemens.Engineering at load time?" by walking the
        /// exact same install path and search directories it does. A check that only asks the
        /// registry whether TIA is installed says OK on a machine where Openness was never
        /// installed — and the engine then dies with FileLoadException / "Could not find
        /// installation path" on the first real call. Reuses the resolver's own private helpers so
        /// the two cannot drift apart.
        /// </summary>
        public static (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) ProbeOpennessAssemblies()
        {
            string? installPath;
            try { installPath = GetTiaPortalInstallPath(); }
            catch (Exception ex) { return (false, null, null, "install path lookup threw: " + ex.Message); }

            if (string.IsNullOrEmpty(installPath))
            {
                return (false, null, null,
                    $"no TIA Portal V{TiaMajorVersion} install path (registry TIAP{TiaMajorVersion}\\TIA_Opns / Global in both views, " +
                    "explicit CLI path and matching TiaPortalLocation env var checked)");
            }

            var versionString = TiaMajorVersion.ToString();
            var searchDirectories = new[]
            {
                Path.Combine(installPath, "PublicAPI", $"V{versionString}"),
                Path.Combine(installPath, "Bin", "PublicAPI")
            };
            var excluded = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20" }
                .Where(v => v != $"V{versionString}");

            // V20 ships the monolithic Siemens.Engineering.dll; V21 splits it into
            // Siemens.Engineering.Base/Step7/... — either one proves Openness is present.
            foreach (var dll in new[] { "Siemens.Engineering.dll", "Siemens.Engineering.Base.dll" })
            {
                foreach (var dir in searchDirectories)
                {
                    string? found;
                    try { found = FindAssemblyRecursive(dir, dll, excluded); }
                    catch /* swallow(env-probe): an inaccessible SDK search directory does not rule out the remaining assembly locations */ { continue; }
                    if (found != null) return (true, installPath, found, null);
                }
            }

            return (false, installPath, null,
                $"found the TIA folder but no Siemens.Engineering(.Base).dll under {string.Join(" or ", searchDirectories)}");
        }

        private static string? GetTiaPortalInstallPath()
        {
            // 1. Explicit CLI override (--tia-portal-location). Highest priority — needed when TIA
            //    is installed at a non-default location (e.g. D:\app\TIA20\Portal V20) and the
            //    registry/env var path is wrong or absent.
            if (!string.IsNullOrWhiteSpace(TiaPortalLocationOverride) && Directory.Exists(TiaPortalLocationOverride))
            {
                return TiaPortalLocationOverride;
            }

            // 2. env var (Cursor MCP env or user env) — but it is version-agnostic and on
            //    multi-version machines it typically points at ONE install (e.g. V21), which
            //    used to hijack V20 assembly resolution ("Could not find DLL ... for version 20").
            //    Only trust it when its path names the version we need (or names no version).
            var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            bool envUsable = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env);
            if (envUsable && PathMatchesVersion(env!, TiaMajorVersion))
            {
                return env;
            }

            // 3. Version-specific registry entry — authoritative on multi-version machines.
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
              try
              {
                foreach (var section in new[] { "TIA_Opns", "Global" })
                {
                    var regPath = TiaOpenness.Shared.OpennessEnvironment.InstalledPath(view, TiaMajorVersion, section);
                    if (!string.IsNullOrWhiteSpace(regPath) && Directory.Exists(regPath)) return regPath;
                }
              }
              catch (System.Security.SecurityException) /* swallow(env-probe): registry security restrictions leave the other installation view available for probing */ { }
              catch (UnauthorizedAccessException) /* swallow(env-probe): registry access denial leaves the other installation view available for probing */ { }
            }
            return null; // A path explicitly naming another version is not a fallback.
        }

        /// <summary>True when the path names no version at all, or names exactly V{version}.</summary>
        private static bool PathMatchesVersion(string path, int version)
        {
            var named = EngineeringAssemblyIdentity.PathVersion(path);
            return !named.HasValue || named.Value == version;
        }

        private static string? FindAssemblyRecursive(string directory, string fileName, IEnumerable<string> excludedTiaMajorVersions)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var filePath = Path.Combine(directory, fileName);
            if (File.Exists(filePath))
            {
                return filePath;
            }

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var subDirName = new DirectoryInfo(subDir).Name;
                if (excludedTiaMajorVersions.Contains(subDirName))
                {
                    continue;
                }

                var result = FindAssemblyRecursive(subDir, fileName, excludedTiaMajorVersions);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}

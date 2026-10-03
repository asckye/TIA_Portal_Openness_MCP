using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Environment
{
    /// <summary>
    /// Finds the Siemens.Engineering assemblies for every TIA Portal installed on this machine.
    ///
    /// Two layouts exist and both must be handled:
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>V15.1 - V20 (monolithic)</b> — one <c>Siemens.Engineering.dll</c> at
    /// <c>...\Portal V20\PublicAPI\V20\</c>. The registry path is
    /// <c>Openness\20.0\PublicAPI\20.0</c> and the DLL path is the key's default value.
    /// </item>
    /// <item>
    /// <b>V21+ (modular)</b> — Siemens split the assembly into <c>Siemens.Engineering.Base.dll</c>,
    /// <c>.Step7.dll</c>, <c>.WinCC.dll</c> and others, moved them into a <c>net48</c> subfolder,
    /// and re-signed them. The registry path gained a four-part version and a <c>net48</c> level
    /// (<c>Openness\21.0\PublicAPI\21.0.0.0\net48</c>) and the path lives in a <em>named</em>
    /// value, <c>Siemens.Engineering.Base</c>, not the default one.
    /// </item>
    /// </list>
    ///
    /// The registry scheme here mirrors the one in Siemens' own
    /// <c>ReferenceSiemensEngineeringAssemblies.targets</c>, with a filesystem probe as fallback
    /// for installations whose registry entries a partial uninstall damaged.
    /// </summary>
    public static class OpennessLocator
    {
        private const string OpennessKey = @"SOFTWARE\Siemens\Automation\Openness";
        private const string MonolithicAssembly = "Siemens.Engineering.dll";
        private const string ModularAssembly = "Siemens.Engineering.Base.dll";
        private const string ModularSubfolder = "net48";

        /// <summary>All installations found, newest version first.</summary>
        public static IReadOnlyList<OpennessInstallation> FindAll()
        {
            var found = new Dictionary<string, OpennessInstallation>(StringComparer.OrdinalIgnoreCase);

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var install in FromRegistry(view))
                {
                    if (!found.ContainsKey(install.Version)) found[install.Version] = install;
                }
            }

            foreach (var install in FromFilesystem())
            {
                if (!found.ContainsKey(install.Version)) found[install.Version] = install;
            }

            foreach (var install in found.Values) FillAssemblies(install);

            return found.Values
                .Where(i => TiaMcp.Versioning.TiaVersionCatalog.Runnable.Any(v => v.MajorVersion == ParseVersion(i.Version).Major))
                .OrderByDescending(i => ParseVersion(i.Version))
                .ToList();
        }

        /// <summary>The installation matching <paramref name="version"/>, or the newest one when null.</summary>
        public static OpennessInstallation Resolve(string version)
        {
            var all = FindAll();
            if (all.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(version)) return all[0];

            var wanted = Normalize(version);
            return all.FirstOrDefault(i => Normalize(i.Version) == wanted);
        }

        // ---- registry ------------------------------------------------------

        private static IEnumerable<OpennessInstallation> FromRegistry(RegistryView view)
        {
            RegistryKey root;
            try
            {
                root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(OpennessKey);
            }
            catch (Exception)
            {
                yield break;
            }
            if (root == null) yield break;

            using (root)
            {
                foreach (var versionName in root.GetSubKeyNames())
                {
                    OpennessInstallation install = null;
                    try
                    {
                        using (var versionKey = root.OpenSubKey(versionName))
                        {
                            if (versionKey != null) install = ReadVersionKey(versionKey, versionName);
                        }
                    }
                    catch (Exception)
                    {
                        install = null;
                    }
                    if (install != null) yield return install;
                }
            }
        }

        private static OpennessInstallation ReadVersionKey(RegistryKey versionKey, string versionName)
        {
            using (var publicApi = versionKey.OpenSubKey("PublicAPI"))
            {
                if (publicApi != null)
                {
                    // V21 writes both "21.0" and "21.0.0.0"; prefer the longest, which is the
                    // modular one, so a machine carrying both entries resolves to net48.
                    foreach (var apiVersion in publicApi.GetSubKeyNames().OrderByDescending(n => n.Length))
                    {
                        using (var apiKey = publicApi.OpenSubKey(apiVersion))
                        {
                            if (apiKey == null) continue;

                            var modular = ReadModular(apiKey, versionName);
                            if (modular != null) return modular;

                            var monolithic = ReadMonolithic(apiKey, versionName);
                            if (monolithic != null) return monolithic;
                        }
                    }
                }
            }

            var direct = versionKey.GetValue("LibraryPath") as string;
            if (!string.IsNullOrWhiteSpace(direct))
            {
                var path = direct.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? direct
                    : Path.Combine(direct, MonolithicAssembly);
                return File.Exists(path) ? Monolithic(versionName, path, "Registry") : null;
            }
            return null;
        }

        /// <summary>V21+: <c>PublicAPI\21.0.0.0\net48</c>, value <c>Siemens.Engineering.Base</c>.</summary>
        private static OpennessInstallation ReadModular(RegistryKey apiKey, string versionName)
        {
            using (var net48 = apiKey.OpenSubKey(ModularSubfolder))
            {
                var path = net48?.GetValue("Siemens.Engineering.Base") as string;
                if (string.IsNullOrWhiteSpace(path)) path = net48?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                return new OpennessInstallation
                {
                    Version = versionName,
                    EngineeringDllPath = path,
                    PublicApiDirectory = Path.GetDirectoryName(path),
                    DiscoveredBy = "Registry",
                    IsModular = true,
                    PrimaryAssembly = Path.GetFileName(path),
                };
            }
        }

        /// <summary>V15.1 - V20: the DLL path is the API key's default value.</summary>
        private static OpennessInstallation ReadMonolithic(RegistryKey apiKey, string versionName)
        {
            var path = apiKey.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(path)) path = apiKey.GetValue("Siemens.Engineering") as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            return Monolithic(versionName, path, "Registry");
        }

        private static OpennessInstallation Monolithic(string versionName, string path, string discoveredBy)
        {
            return new OpennessInstallation
            {
                Version = versionName,
                EngineeringDllPath = path,
                PublicApiDirectory = Path.GetDirectoryName(path),
                DiscoveredBy = discoveredBy,
                IsModular = false,
                PrimaryAssembly = Path.GetFileName(path),
            };
        }

        // ---- filesystem ----------------------------------------------------

        /// <summary>
        /// Probes the default install layout, checking the V21 <c>net48</c> subfolder before the
        /// flat V20 one.
        /// </summary>
        private static IEnumerable<OpennessInstallation> FromFilesystem()
        {
            var roots = new[]
            {
                System.Environment.GetEnvironmentVariable("ProgramFiles"),
                System.Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                @"C:\Program Files",
                @"D:\Program Files",
            };

            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
            {
                var automation = Path.Combine(root, "Siemens", "Automation");
                if (!SafeDirExists(automation)) continue;

                foreach (var portalDir in SafeDirs(automation, "Portal V*"))
                {
                    var publicApi = Path.Combine(portalDir, "PublicAPI");
                    if (!SafeDirExists(publicApi)) continue;

                    foreach (var apiDir in SafeDirs(publicApi, "V*"))
                    {
                        var version = VersionFromFolder(apiDir);

                        var modular = Path.Combine(apiDir, ModularSubfolder, ModularAssembly);
                        if (File.Exists(modular))
                        {
                            yield return new OpennessInstallation
                            {
                                Version = version,
                                EngineeringDllPath = modular,
                                PublicApiDirectory = Path.GetDirectoryName(modular),
                                DiscoveredBy = "Probe",
                                IsModular = true,
                                PrimaryAssembly = ModularAssembly,
                            };
                            continue;
                        }

                        var monolithic = Path.Combine(apiDir, MonolithicAssembly);
                        if (File.Exists(monolithic))
                        {
                            yield return Monolithic(version, monolithic, "Probe");
                        }
                    }
                }
            }
        }

        /// <summary>Records every Siemens.Engineering* assembly next to the anchor one.</summary>
        private static void FillAssemblies(OpennessInstallation install)
        {
            try
            {
                install.Assemblies = Directory
                    .GetFiles(install.PublicApiDirectory, "Siemens.Engineering*.dll")
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                install.Assemblies = new List<string> { install.PrimaryAssembly };
            }
        }

        // ---- helpers -------------------------------------------------------

        private static string VersionFromFolder(string apiDir)
        {
            // "V21" -> "21.0"
            var raw = Path.GetFileName(apiDir).TrimStart('V', 'v');
            return raw.Contains(".") ? raw : raw + ".0";
        }

        private static bool SafeDirExists(string path)
        {
            try { return Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        private static IEnumerable<string> SafeDirs(string path, string pattern)
        {
            try { return Directory.GetDirectories(path, pattern); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        private static string Normalize(string version)
        {
            var v = (version ?? string.Empty).Trim().TrimStart('V', 'v');
            // "21.0.0.0" and "21.0" name the same installation.
            var parts = v.Split('.');
            if (parts.Length >= 2) return parts[0] + "." + parts[1];
            return parts.Length == 1 && parts[0].Length > 0 ? parts[0] + ".0" : v;
        }

        private static Version ParseVersion(string version)
        {
            Version parsed;
            return Version.TryParse(Normalize(version), out parsed) ? parsed : new Version(0, 0);
        }
    }
}

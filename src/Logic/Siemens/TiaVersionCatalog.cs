using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.IO;

namespace TiaMcp.Versioning
{
    /// <summary>A release identity, not a claim that an installed SDK or engine is usable.</summary>
#if TIA_ADAPTER_INTERNAL_VERSIONING
    internal sealed class TiaVersionDescriptor
#else
    public sealed class TiaVersionDescriptor
#endif
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public int MajorVersion { get; private set; }
        public bool IsRunnable { get; private set; }
        public string RuntimeDirectory { get; private set; }
        public string EngineOutputDirectory { get; private set; }
        public bool IsFullEngine { get { return MajorVersion >= 20; } }
        public string HostKind { get { return "foundation"; } }
        public string WorkerKind { get { return MajorVersion >= 20 ? "engine" : "plc"; } }
        public string SupportState { get { return IsFullEngine ? "existing-engine" : "plc-foundation"; } }
        public string ApiVersion { get { return Key == "14sp1" ? "14.0.1.0" : Key == "15.1" ? "15.1.0.0" : Key + ".0.0.0"; } }
        public string ApiFolder { get { return Key == "14sp1" ? "V14 SP1" : "V" + Key; } }
        public string InstallFolder { get { return "Portal V" + (Key == "14sp1" ? "14" : Key == "15.1" ? "15_1" : Key); } }
        public string ApiAssembly { get { return MajorVersion == 21 ? "Siemens.Engineering.Base.dll" : "Siemens.Engineering.dll"; } }
        public string FindApiDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            var folder = Path.Combine(root, "PublicAPI", ApiFolder);
            var candidates = new[] { root, Path.Combine(folder, "net48"), folder,
                Path.Combine(root, ApiFolder, "net48"), Path.Combine(root, ApiFolder) };
            return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, ApiAssembly)));
        }

        internal TiaVersionDescriptor(string key, string displayName, int majorVersion,
            bool runnable, string runtimeDirectory, string engineOutputDirectory)
        {
            Key = key;
            DisplayName = displayName;
            MajorVersion = majorVersion;
            IsRunnable = runnable;
            RuntimeDirectory = runtimeDirectory;
            EngineOutputDirectory = engineOutputDirectory;
        }
    }

    /// <summary>
    /// Shared by the engines and configurator. Precise keys intentionally distinguish
    /// V14 SP1 and V15.1; original V14 and V15 are outside the target scope.
    /// Legacy entries use the PLC foundation host and one exact-release worker.
    /// Existing-engine means a build target exists, not that every tool was validated.
    /// Keep this file compatible with the configurator's .NET Framework C# 5 compiler.
    /// </summary>
#if TIA_ADAPTER_INTERNAL_VERSIONING
    internal static class TiaVersionCatalog
#else
    public static class TiaVersionCatalog
#endif
    {
        private static readonly ReadOnlyCollection<TiaVersionDescriptor> Entries =
            Array.AsReadOnly(new[]
            {
                Foundation("14sp1", "V14 SP1", 14),
                Foundation("15.1", "V15.1", 15),
                Foundation("16", "V16", 16),
                Foundation("17", "V17", 17),
                Foundation("18", "V18", 18),
                Foundation("19", "V19", 19),
                new TiaVersionDescriptor("20", "V20", 20, true, "v20", "bin-v20"),
                new TiaVersionDescriptor("21", "V21", 21, true, "v21", "bin")
            });

        private static TiaVersionDescriptor Foundation(string key, string displayName, int major)
        {
            return new TiaVersionDescriptor(key, displayName, major, true, "v" + key, null);
        }

        public static IEnumerable<TiaVersionDescriptor> All { get { return Entries; } }
        public static IEnumerable<TiaVersionDescriptor> Runnable
        {
            get { return Entries.Where(v => v.IsRunnable).Reverse(); }
        }

        // Only canonical keys are accepted; no float conversion or major-version collapse.
        public static TiaVersionDescriptor Get(string key)
        {
            var version = Entries.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal));
            if (version == null)
                throw new ArgumentException("Unsupported TIA version key '" + key + "'. Use: " +
                    string.Join(", ", Entries.Select(v => v.Key)) + ".", "key");
            return version;
        }

        public static TiaVersionDescriptor RequireRunnable(string key)
        {
            var version = Get(key);
            return version;
        }

        public static TiaVersionDescriptor RequireRunnable(int majorVersion)
        {
            return RequireRunnable(majorVersion.ToString(CultureInfo.InvariantCulture));
        }

        // UI/registry versions retain SP1 and minor release identity. Original V14/V15 are excluded.
        public static TiaVersionDescriptor FromApiVersion(string value)
        {
            var raw = (value ?? "").Trim();
            var direct = Entries.FirstOrDefault(v => string.Equals(v.Key, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.DisplayName, raw, StringComparison.OrdinalIgnoreCase));
            if (direct != null) return direct;
            Version parsed;
            if (Version.TryParse(raw.TrimStart('V', 'v'), out parsed))
            {
                var match = Entries.FirstOrDefault(v => {
                    var expected = new Version(v.ApiVersion);
                    return parsed.Major == expected.Major && parsed.Minor == expected.Minor
                        && (v.Key != "14sp1" || parsed.Build == 1);
                });
                if (match != null) return match;
            }
            throw new ArgumentException("Unsupported Openness API version: " + value, "value");
        }

        public static void RequireMatchingEngine(string requestedKey, string compiledKey)
        {
            var requested = RequireRunnable(requestedKey);
            var compiled = RequireRunnable(compiledKey);
            if (requested.Key != compiled.Key)
                throw new InvalidOperationException("Requested " + requested.DisplayName + " but this adapter is compiled for " + compiled.DisplayName + ".");
        }

        public static void RequireMatchingEngine(int requestedMajor, int compiledMajor)
        {
            var requested = RequireRunnable(requestedMajor);
            RequireRunnable(compiledMajor);
            if (requestedMajor != compiledMajor)
                throw new InvalidOperationException("Requested " + requested.DisplayName +
                    " but this engine is compiled for V" + compiledMajor +
                    ". A matching engine is required; cross-version execution is blocked.");
        }
    }
}

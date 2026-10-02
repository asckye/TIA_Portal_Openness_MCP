using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace TiaMcp.Versioning
{
    /// <summary>A release identity, not a claim that an installed SDK or engine is usable.</summary>
    public sealed class TiaVersionDescriptor
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public int MajorVersion { get; private set; }
        public bool IsRunnable { get; private set; }
        public string RuntimeDirectory { get; private set; }
        public string EngineOutputDirectory { get; private set; }
        public string SupportState { get { return IsRunnable ? "existing-engine" : "planned"; } }

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
    /// Planned entries have no executable paths.
    /// Existing-engine means a build target exists, not that every tool was validated.
    /// Keep this file compatible with the configurator's .NET Framework C# 5 compiler.
    /// </summary>
    public static class TiaVersionCatalog
    {
        private static readonly ReadOnlyCollection<TiaVersionDescriptor> Entries =
            Array.AsReadOnly(new[]
            {
                Planned("14sp1", "V14 SP1", 14),
                Planned("15.1", "V15.1", 15),
                Planned("16", "V16", 16),
                Planned("17", "V17", 17),
                Planned("18", "V18", 18),
                Planned("19", "V19", 19),
                new TiaVersionDescriptor("20", "V20", 20, true, "v20", "bin-v20"),
                new TiaVersionDescriptor("21", "V21", 21, true, "v21", "bin")
            });

        private static TiaVersionDescriptor Planned(string key, string displayName, int major)
        {
            return new TiaVersionDescriptor(key, displayName, major, false, null, null);
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
            if (!version.IsRunnable)
                throw new InvalidOperationException(version.DisplayName +
                    " is planned only: no supported engine or native acceptance is available. " +
                    "This build can run only V20 and V21.");
            return version;
        }

        public static TiaVersionDescriptor RequireRunnable(int majorVersion)
        {
            return RequireRunnable(majorVersion.ToString(CultureInfo.InvariantCulture));
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

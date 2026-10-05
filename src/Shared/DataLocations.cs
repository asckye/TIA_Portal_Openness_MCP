#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace TiaOpenness.Shared
{
    // Linked assemblies share only BCL values so the engine and its adapters probe once.
    internal sealed class DataLocations
    {
        private const string ProcessCacheKey = "TiaOpenness.Shared.DataLocations.v1";
        private static readonly Lazy<DataLocations> ProcessLocations = new Lazy<DataLocations>(ForProcess);
        private readonly string localApplicationData;
        private readonly string temporaryDirectory;
        private readonly Lazy<string> configDirectory;
        private readonly Lazy<string> uiFilePath;

        internal static DataLocations Current => ProcessLocations.Value;
        internal string Root { get; }

        private DataLocations(string root, string localApplicationData, string temporaryDirectory)
        {
            Root = root;
            this.localApplicationData = localApplicationData;
            this.temporaryDirectory = temporaryDirectory;
            configDirectory = new Lazy<string>(PrepareConfig);
            uiFilePath = new Lazy<string>(PrepareUi);
        }

        private static DataLocations ForProcess()
        {
            // Each linked copy has its own statics; use an interned lock and AppDomain data
            // to keep the decision identical even if an environment variable later changes.
            lock (string.Intern(ProcessCacheKey))
            {
                var cached = AppDomain.CurrentDomain.GetData(ProcessCacheKey) as string[];
                if (cached == null)
                {
                    var locations = Resolve(AppContext.BaseDirectory,
                        Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY"),
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath());
                    cached = new[] { locations.Root, locations.localApplicationData, locations.temporaryDirectory };
                    AppDomain.CurrentDomain.SetData(ProcessCacheKey, cached);
                }
                return new DataLocations(cached[0], cached[1], cached[2]);
            }
        }

        // Explicit inputs let offline tests exercise isolated layouts without changing the host cache.
        internal static DataLocations Resolve(string baseDirectory, string configured, string localApplicationData, string temporaryDirectory)
        {
            string root = null;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var prefix = Path.GetPathRoot(configured);
                if (!Path.IsPathRooted(configured) ||
                    (Path.DirectorySeparatorChar == '\\' && prefix.Length < 3))
                    throw new ArgumentException("TIA_MCP_DATA_DIRECTORY must be an absolute path.");
                root = Path.GetFullPath(configured);
            }
            else
            {
                var bundle = BundleLayout.FindRoot(baseDirectory);
                if (bundle != null)
                {
                    var candidate = Path.Combine(bundle, "data");
                    if (CanWrite(candidate)) root = candidate;
                }
            }
            return new DataLocations(root, localApplicationData, temporaryDirectory);
        }

        private static bool CanWrite(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    stream.WriteByte(0);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Trace.TraceWarning("Bundle data directory is unavailable: " + ex.Message);
                return false;
            }
        }

        internal string DiagnosticsDirectory => Diagnostics(Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"));
        internal string Diagnostics(string configured) => !string.IsNullOrWhiteSpace(configured) ? configured :
            At("diagnostics", Path.Combine(localApplicationData, "TiaMcp", "diagnostics"));
        internal string LeasesDirectory => At("leases", Path.Combine(localApplicationData, "TiaMcp", "instance-leases"));
        internal string ConfigDirectory => configDirectory.Value;
        internal string UiFilePath => uiFilePath.Value;
        internal string LogsDirectory => Ensure(At("logs", temporaryDirectory));
        internal string ReportsDirectory => At("reports", Path.Combine(temporaryDirectory, "TiaMcpReports"));
        internal string TempDirectory => Ensure(At("temp", temporaryDirectory));

        private string At(string folder, string fallback) => Root == null ? fallback : Path.Combine(Root, folder);
        private static string Ensure(string directory)
        {
            Directory.CreateDirectory(directory);
            return directory;
        }

        private string PrepareConfig()
        {
            var previous = Path.Combine(localApplicationData, "TiaPortalMcp");
            var target = At("config", previous);
            if (Root != null && Directory.Exists(previous))
                foreach (var file in Directory.GetFiles(previous))
                    CopyIfMissing(file, Path.Combine(target, Path.GetFileName(file)));
            return target;
        }

        private string PrepareUi()
        {
            var previous = Path.Combine(localApplicationData, "TiaOpennessStudio", "ui.settings");
            var target = Root == null ? previous : Path.Combine(Root, "ui", "ui.settings");
            if (Root != null) CopyIfMissing(previous, target);
            return target;
        }

        private static void CopyIfMissing(string source, string target)
        {
            if (File.Exists(target) || !File.Exists(source)) return;
            Ensure(Path.GetDirectoryName(target));
            try { File.Copy(source, target, false); }
            catch (IOException ex) when (File.Exists(target))
            {
                // Another process may have migrated or saved the same settings first.
                Trace.TraceInformation("Keeping existing settings: " + ex.Message);
            }
        }
    }
}

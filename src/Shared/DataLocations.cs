#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaOpenness.Shared
{
    internal sealed class DataLocationIOException : IOException
    {
        internal string TargetPath { get; }
        internal DataLocationIOException(string path, string message) : base("IO_FAILED: " + message + ": " + path) { TargetPath = path; }
    }
    // Linked assemblies share only BCL values so the engine and its adapters probe once.
    internal sealed class DataLocations
    {
        private const string ProcessCacheKey = "TiaOpenness.Shared.DataLocations.v1";
        private static readonly Lazy<DataLocations> ProcessLocations = new Lazy<DataLocations>(ForProcess);
        internal static readonly string ProcessKey = Process.GetCurrentProcess().Id + "-"
            + Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        private const string HostKey = "TiaOpenness.Shared.DataLocations.host";
        internal static string HostReleaseKey => AppDomain.CurrentDomain.GetData(HostKey) as string ?? "studio";
        internal static void InitializeHost(string releaseKey)
        {
            TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(releaseKey);
            AppDomain.CurrentDomain.SetData(HostKey, releaseKey);
        }
        private readonly string primaryDataRoot;
        private readonly string localApplicationData;
        private readonly string temporaryDirectory;
        private readonly bool explicitDataRoot;
        private static readonly object LogGate = new object();
        private readonly Lazy<string> configDirectory;
        private readonly Lazy<string> uiFilePath;

        internal static DataLocations Current => ProcessLocations.Value;
        internal string Root { get; }
        internal string WorkbenchDataDirectory => Root ?? Path.Combine(localApplicationData, "TiaOpennessStudio", "data");
        internal string LocalApplicationDataDirectory => localApplicationData;
        internal string SystemTempDirectory => temporaryDirectory;

        private DataLocations(string root, string localApplicationData, string temporaryDirectory, bool explicitDataRoot = false, string primaryDataRoot = null)
        {
            Root = root;
            this.primaryDataRoot = primaryDataRoot ?? root;
            this.localApplicationData = localApplicationData;
            this.temporaryDirectory = temporaryDirectory;
            this.explicitDataRoot = explicitDataRoot;
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
                    cached = new[] { locations.Root, locations.localApplicationData, locations.temporaryDirectory, locations.explicitDataRoot.ToString(), locations.primaryDataRoot };
                    AppDomain.CurrentDomain.SetData(ProcessCacheKey, cached);
                }
                return new DataLocations(cached[0], cached[1], cached[2], bool.Parse(cached[3]), cached[4]);
            }
        }

        // Explicit inputs let offline tests exercise isolated layouts without changing the host cache.
        internal static DataLocations Resolve(string baseDirectory, string configured, string localApplicationData, string temporaryDirectory)
        {
            string root = null, primary = null;
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
#if TIA_BUNDLE_LAYOUT_STUDIO
                var bundle = BundleLayout.FindWorkbenchRoot(baseDirectory);
#else
                var bundle = BundleLayout.FindRoot(baseDirectory) ?? BundleLayout.FindWorkbenchRoot(baseDirectory);
#endif
                if (bundle != null)
                {
                    primary = Path.Combine(bundle, "data");
                    if (CanWrite(primary)) root = primary;
                }
            }
            return new DataLocations(root, localApplicationData, temporaryDirectory, !string.IsNullOrWhiteSpace(configured), primary);
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) /* swallow(logging-failure): probe failure selects a purpose fallback; the final write failure is reported */
            {
                // A failed probe selects the purpose fallback; only a failed final write is reported.
                return false;
            }
        }

        internal string DiagnosticsDirectory => Diagnostics(Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"));
        internal string Diagnostics(string configured) => !string.IsNullOrWhiteSpace(configured) ? configured :
            Root != null ? Path.Combine(Root, "diagnostics") : UserPath(localApplicationData, "TiaMcp/diagnostics", "DIAGNOSTIC_WRITE_FAILED");
        internal string LeasesDirectory => At("leases", Path.Combine(localApplicationData, "TiaMcp", "instance-leases"));
        internal string ConfigDirectory => configDirectory.Value;
        internal string UiFilePath => uiFilePath.Value;
        internal string LogsDirectory => WritableDirectory(Root == null ? null : Path.Combine(Root, "logs"), FallbackPath(temporaryDirectory, "TiaMcp/logs"), "IO_FAILED");
        internal string[] LogReadRoots => ReadRoots(primaryDataRoot == null ? null : Path.Combine(primaryDataRoot, "logs"), FallbackPath(temporaryDirectory, "TiaMcp/logs"));
        internal string[] AuditReadRoots
        {
            get
            {
                var primary = primaryDataRoot == null ? null : Path.Combine(primaryDataRoot, "logs", "audit");
                var fallback = FallbackPath(localApplicationData, "TiaMcp/logs/audit");
                var roots = new List<string>();
                if (primary != null && (Root != null || Directory.Exists(primary))) roots.Add(primary);
                if (!explicitDataRoot && fallback != null && (roots.Count == 0 || Directory.Exists(fallback))) roots.Add(fallback);
                return roots.ToArray();
            }
        }
        private string[] ReadRoots(string primary, string fallback)
        {
            var roots = new List<string>();
            if (primary != null) roots.Add(primary);
            if (!explicitDataRoot && fallback != null && Path.IsPathRooted(fallback) && fallback != primary) roots.Add(fallback);
            return roots.ToArray();
        }
        internal string AuditDirectory => Root != null ? Path.Combine(Root, "logs", "audit")
            : UserPath(localApplicationData, "TiaMcp/logs/audit", "DIAGNOSTIC_WRITE_FAILED");
        internal string WritableAuditDirectory => WritableDirectory(Root == null ? null : Path.Combine(Root, "logs", "audit"),
            FallbackPath(localApplicationData, "TiaMcp/logs/audit"), "DIAGNOSTIC_WRITE_FAILED");
        internal string LogDirectory(string releaseKey)
        {
            if (releaseKey != "studio") TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(releaseKey);
            return WritableDirectory(Root == null ? null : Path.Combine(Root, "logs", releaseKey),
                FallbackPath(temporaryDirectory, "TiaMcp/logs/" + releaseKey), "IO_FAILED");
        }
        internal string LogFile(string name, string releaseKey, string session = null)
        {
            if (Path.GetFileName(name) != name || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A log filename is required.");
            if (session != null && !Guid.TryParseExact(session, "N", out _)) throw new ArgumentException("Session must be a GUID.");
            return Path.Combine(LogDirectory(releaseKey), Path.GetFileNameWithoutExtension(name) + "-" + ProcessKey
                + (session == null ? "" : "-" + session) + Path.GetExtension(name));
        }
        internal void AppendLog(string name, string releaseKey, string message)
        {
            lock (LogGate)
                File.AppendAllText(LogFile(name, releaseKey), message + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        private const string FailureCacheKey = "TiaOpenness.Shared.DataLocations.logFailures";
        internal static void ReportLogFailure(string purpose, Exception error)
        {
            // Linked copies share the suppression set as well as the selected paths.
            lock (string.Intern(FailureCacheKey))
            {
                var reported = AppDomain.CurrentDomain.GetData(FailureCacheKey) as HashSet<string>;
                if (reported == null) AppDomain.CurrentDomain.SetData(FailureCacheKey, reported = new HashSet<string>(StringComparer.Ordinal));
                if (!reported.Add(purpose)) return;
            }
            ReportFailure("IO_FAILED", error);
        }
        internal static void ReportFailure(string code, Exception error)
        {
            string message = code + ": " + error.Message;
            try
            {
                // Trace.TraceError is conditional on TRACE, which some release/test builds omit.
                foreach (TraceListener listener in Trace.Listeners)
                {
                    listener.TraceEvent(new TraceEventCache(), "TiaMcp", TraceEventType.Error, 0, message);
                    if (Trace.AutoFlush) listener.Flush();
                }
            }
            catch (Exception) /* swallow(logging-failure): an unavailable Trace listener must not replace the original failure */ { }
            try { Console.Error.WriteLine(message); }
            catch (Exception) /* swallow(logging-failure): unavailable stderr must not replace the original file-write failure */ { }
        }

        internal const int PlainLogCopies = 32;
        private static readonly Regex PlainLogName = new Regex(@"^(?<purpose>TiaMcpServer(?:\.hmi-read|\.native-export)?|TiaOpenness\.crash)-(?<pid>[1-9][0-9]*)-(?<start>[0-9]{8}-[0-9]{6}-[0-9]{7})-[a-f0-9]{32}(?:-[a-f0-9]{32})?\.log$", RegexOptions.CultureInvariant);
        internal static void PruneLogsAtStartup()
        {
            try { Current.PruneLogs(); }
            catch (Exception ex) { ReportLogFailure("plain-log-retention", ex); }
        }
        internal void PruneLogs()
        {
            // Retention examines both product roots even when an explicit data root disables write fallback.
            var roots = new[] { primaryDataRoot == null ? null : Path.Combine(primaryDataRoot, "logs"), FallbackPath(temporaryDirectory, "TiaMcp/logs") };
            foreach (var root in roots.Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var key in TiaMcp.Versioning.TiaVersionCatalog.Runnable.Select(r => r.Key).Concat(new[] { "studio" }))
                {
                    string directory = Path.Combine(root, key);
                    try { PruneDirectory(directory); }
                    catch (Exception ex) { ReportLogFailure("plain-log-retention", new IOException(directory + ": " + ex.Message, ex)); }
                }
        }
        private static void PruneDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var files = Directory.GetFiles(directory, "*.log").Select(path => new { Path = path, Match = PlainLogName.Match(Path.GetFileName(path)) })
                .Where(file => file.Match.Success && int.TryParse(file.Match.Groups["pid"].Value, out _)
                    && DateTime.TryParseExact(file.Match.Groups["start"].Value, "yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _));
            foreach (var purpose in files.GroupBy(file => file.Match.Groups["purpose"].Value))
                foreach (var file in purpose.OrderByDescending(file => File.GetLastWriteTimeUtc(file.Path)).ThenBy(file => file.Path, StringComparer.Ordinal).Skip(PlainLogCopies))
                {
                    if ((File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0 || IsLiveProcess(file.Match)) continue;
                    try
                    {
                        // Exclusive open rejects readers and writers; deletion happens under this handle.
                        using (new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    }
                    catch (FileNotFoundException) /* swallow(logging-failure): another startup may already have pruned this file */ { }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33) /* swallow(logging-failure): open or locked logs remain for a later startup */ { }
                    catch (Exception ex) { ReportLogFailure("plain-log-retention", new IOException(file.Path + ": " + ex.Message, ex)); }
                }
        }
        private static bool IsLiveProcess(Match name)
        {
            try
            {
                using (var process = Process.GetProcessById(int.Parse(name.Groups["pid"].Value, CultureInfo.InvariantCulture)))
                    return process.StartTime.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) == name.Groups["start"].Value;
            }
            catch (ArgumentException) /* swallow(logging-failure): the owning process has exited */ { return false; }
            catch (InvalidOperationException) /* swallow(logging-failure): the process exited while its start time was read */ { return false; }
            catch (System.ComponentModel.Win32Exception) /* swallow(logging-failure): retain a file when its process start time cannot be inspected */ { return true; }
            catch (SecurityException) /* swallow(logging-failure): retain a file when process inspection is denied */ { return true; }
        }
        internal string ReportsDirectory => At("reports", Path.Combine(temporaryDirectory, "TiaMcpReports"));
        internal string TempDirectory => Ensure(At("temp", temporaryDirectory));

        private string At(string folder, string fallback) => Root == null ? fallback : Path.Combine(Root, folder);
        private static string UserPath(string root, string relative, string code)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                throw new IOException(code + ": User directory is unavailable: " + (root ?? "<missing>") + "/" + relative);
            return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        private static string FallbackPath(string root, string relative) => string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)
            ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        private string WritableDirectory(string primary, string fallback, string code)
        {
            if (primary != null && CanWrite(primary)) return primary;
            if (!explicitDataRoot && !string.IsNullOrWhiteSpace(fallback) && Path.IsPathRooted(fallback) && CanWrite(fallback)) return fallback;
            throw new IOException(code + ": No writable directory: " + primary + "; fallback: " + (fallback ?? "<missing>"));
        }

        internal static string EcosystemPython(string configured, string localApplicationData)
        {
            if (configured != null)
            {
                if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathRooted(configured))
                    throw new DataLocationIOException(configured, "TIA_MCP_PLC_TOOLS_PYTHON must be an absolute path");
                if (!File.Exists(configured)) throw new DataLocationIOException(configured, "Python executable is unavailable");
                return configured;
            }
            if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathRooted(localApplicationData))
                throw new DataLocationIOException((localApplicationData ?? "<missing LocalAppData>") + "/TiaMcp/ecosystem-python", "LocalAppData is unavailable");
            string directory = Path.Combine(localApplicationData, "TiaMcp", "ecosystem-python");
            string python = Path.Combine(directory, "Scripts", "python.exe");
            if (!Directory.Exists(directory)) throw new DataLocationIOException(directory, "Python environment is unavailable");
            if (!CanWrite(directory)) throw new DataLocationIOException(directory, "Python environment is not writable");
            if (!File.Exists(python)) throw new DataLocationIOException(python, "Python executable is unavailable");
            return python;
        }
        internal string EcosystemPythonExecutable => EcosystemPython(Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON"), localApplicationData);
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

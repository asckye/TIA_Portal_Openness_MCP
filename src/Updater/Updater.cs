using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TiaMcp.Versioning;

namespace TiaMcp.Updater
{

    [DataContract]
    internal sealed class InventoryRecord
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "runtimeFiles")] public List<InventoryRecord> RuntimeFiles { get; set; }
        [DataMember(Name = "files")] public List<InventoryRecord> Files { get; set; }
    }

    public static partial class UpdaterEngine
    {
        private static readonly Regex ZipPattern =
            new Regex(@"^TIA_MCP_Delivery_v\d+\.\d+\.\d+(?:-(?:beta|rc)\.\d+)?_\d{8}\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const string UpdaterPath = "runtime/tools/TiaMcp.Updater.exe";
        private const string UpdaterConfigPath = UpdaterPath + ".config";

        public static int Run(UpdaterOptions options, Action<string> say, Func<string, IList<string>> runningProcesses = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (say == null) say = _ => { };
            string root = Path.GetFullPath(options.InstallRoot ?? AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            options.InstallRoot = root;
            if (options.TimeoutSeconds < 1 || options.TimeoutSeconds > 3600) throw new InvalidOperationException("TimeoutSeconds must be between 1 and 3600.");
            if (!IsValidRepository(options.Repository)) throw new InvalidOperationException("Repository must be owner/name.");
            if (options.Version != null && options.Version.Length > 0 && !IsVersion(options.Version))
                throw new InvalidOperationException("-Version must be vX.Y.Z, or vX.Y.Z-beta.N / vX.Y.Z-rc.N for a pre-release.");
            if (IsSourceCheckout(root))
                throw new InvalidOperationException("This folder is a source checkout or worktree. The updater only changes an installed delivery.");

            string deliveryFile = Combine(root, "manifest/delivery.json");
            if (!ExistsFile(deliveryFile)) throw new InvalidOperationException("Not a TIA MCP delivery: manifest/delivery.json is missing.");
            DeliveryManifest installed = ReadJson<DeliveryManifest>(deliveryFile);
            if (installed == null || string.IsNullOrWhiteSpace(installed.Release) || string.IsNullOrWhiteSpace(installed.Package))
                throw new InvalidOperationException("manifest/delivery.json has no release/package fields.");
            if (!Regex.IsMatch(installed.Package, @"^[A-Za-z0-9_.-]+$"))
                throw new InvalidOperationException("manifest/delivery.json has an unsafe package name.");
            say(UpdaterText.Bilingual("InstallRoot", "install root") + ": " + root + "; " + UpdaterText.Bilingual("InstalledVersion", "installed") + ": "
                    + installed.Release + " (" + installed.Package + ")");
            WaitForProcess(options, say);

            string backupRoot = Combine(root, ".previous");
            if (options.Rollback) return Rollback(root, installed, backupRoot, options, say, runningProcesses);

            ReleaseInfo release = FindRelease(options, installed.Release, say);
            string latest = NormalizeVersion(release.Tag);
            // A plain install is never offered a pre-release; only an explicit -Version selects one.
            if (string.IsNullOrEmpty(options.Version) && !UsesPrereleaseChannel(installed.Release) && (release.Prerelease || IsPrerelease(latest)))
                throw new InvalidOperationException("The latest release " + release.Tag
                        + " is a pre-release; a plain install is never offered one. Pass -Version to choose a release.");
            // Say so before anything is downloaded or replaced.
            if (IsPrerelease(latest))
                say(UpdaterText
                    .Bilingual("PrereleaseTarget", "the selected release is a pre-release, not verified on a real TIA Portal; see its release notes") + ": "
                    + release.Tag);
            int comparison = CompareVersions(latest, installed.Release);
            if (comparison > 0) say(UpdaterText.Bilingual("UpdateAvailable", "UPDATE AVAILABLE") + ": " + installed.Release + " -> " + latest);
            else if (comparison == 0) say(UpdaterText.Bilingual("UpToDate", "UP TO DATE") + ": " + installed.Release);
            else say(UpdaterText.Bilingual("NewerInstalled", "installed version is newer") + ": " + installed.Release + " > " + latest);
            if (options.Check)
            {
                say(UpdaterText.Bilingual("ReleaseAsset", "release asset") + ": " + SelectAsset(release, true).Url);
                return 0;
            }
            if (comparison <= 0 && !options.Force)
            {
                say(UpdaterText.Bilingual("NothingToDo", "nothing to do (pass -Force to reinstall)"));
                Relaunch(root, options, say);
                return 0;
            }
            RequireStopped(root, runningProcesses);
            ApplyRelease(root, installed, release, options, say);
            Relaunch(root, options, say);
            return 0;
        }

        public static bool IsSourceCheckout(string root)
        {
            for (DirectoryInfo directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
            {
                string path = directory.FullName;
                if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))
                    || (File.Exists(Path.Combine(path, "CLAUDE.md")) && File.Exists(Path.Combine(path, "Version.props")) && Directory
                            .Exists(Path.Combine(path, "src")))
                    || (File.Exists(Path.Combine(path, "Version.props")) && File
                            .Exists(Path.Combine(path, "src", "Studio", "Gui", "TiaOpenness.Gui.csproj")))) return true;
            }
            return false;
        }

        public static bool IsSafeRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOfAny(new[] { '\\', ':', '*', '?', '[', ']' }) >= 0) return false;
            string[] parts = relative.Split('/');
            return parts.All(p => p.Length > 0 && p != "." && p != "..") && !Path.IsPathRooted(relative);
        }

        private static void WaitForProcess(UpdaterOptions options, Action<string> say)
        {
            if (options.WaitForPid <= 0) return;
            Func<int, bool> exists = options.ProcessExists ?? (pid => { try { Process.GetProcessById(pid); return true; } catch (ArgumentException) /* swallow(probe-optional): a missing PID means the Workbench has exited */ { return false; } });
            DateTime deadline = DateTime.UtcNow.AddSeconds(options.WaitForPidTimeoutSeconds);
            while (exists(options.WaitForPid) && DateTime.UtcNow < deadline) Thread.Sleep(250);
            if (exists(options.WaitForPid))
                throw new InvalidOperationException("Process " + options.WaitForPid + " (the Workbench that launched this update) is still running after "
                    + options.WaitForPidTimeoutSeconds + " seconds.");
            say(UpdaterText.Bilingual("WorkbenchExited", "Workbench process exited") + ": PID " + options.WaitForPid);
        }

        private static void RequireStopped(string root, Func<string, IList<string>> processProbe)
        {
            IList<string> running = processProbe == null ? FindRunningProcesses(root) : processProbe(root);
            if (running != null && running.Count > 0)
                throw new InvalidOperationException(UpdaterText
                    .Bilingual("ProcessesRunning", "close these bundle processes first; the updater never kills them") + ": " + string.Join("; ", running));
        }

        public static IList<string> FindRunningProcesses(string root)
        {
            var result = new List<string>();
            string normalized = NormalizeForCompare(root);
            foreach (string name in BuildProcessNames(normalized))
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    string executable = "";
                    try { executable = process.MainModule.FileName; }
                    catch /* swallow(probe-optional): an unreadable image path is conservatively reported by PID */ { }
                    if (executable.Length == 0 || IsWithin(executable, normalized)) result
                        .Add(name + ".exe PID " + process.Id + (executable.Length > 0 ? " (" + executable + ")" : ""));
                    process.Dispose();
                }
            }
            return result;
        }

        private static string[] BuildProcessNames(string root)
        {
            string path = Combine(root, "scripts/operations/delivery-files.json");
            if (!ExistsFile(path)) return new[] { "TiaOpenness", "TiaMcpServer", "TiaMcpConfigurator" };
            var rules = ReadJson<DeliveryRules>(path);
            var files = (rules.RequiredFiles ?? new List<string>()).Concat(rules.Include.Files ?? new List<string>())
                .Concat(rules.LegacyCleanup.Files ?? new List<string>());
            string runtime = Combine(root, "runtime");
            if (Directory.Exists(runtime)) files = files.Concat(GetFilesRecursive(runtime).Select(file => RelativeTo(root, file)));
            return files.Where(file => IsSafeRelativePath(file) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileNameWithoutExtension(file)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void ApplyRelease(string root, DeliveryManifest installed, ReleaseInfo release, UpdaterOptions options, Action<string> say)
        {
            string work = Combine(root, ".update");
            string temporaryRoot = options.TemporaryDirectoryForTest ?? Path.GetTempPath();
            string extraction = Path.Combine(temporaryRoot, "tia-mcp-update-" + Guid.NewGuid().ToString("N"));
            string backup = Combine(Combine(root, ".previous"), installed.Package);
            EnsurePathSupported(work);
            EnsurePathSupported(extraction);
            EnsurePathSupported(backup);
            bool backupReady = false;
            bool changed = false;
            try
            {
                if (DirectoryExists(work)) RemoveTree(work);
                CreateDirectory(work);
                ReleaseAsset zipAsset = SelectAsset(release, true), hashAsset = SelectAsset(release, false);
                string zipFile = Combine(work, zipAsset.Name), hashFile = Combine(work, Path.GetFileNameWithoutExtension(zipAsset.Name) + ".sha256");
                say(UpdaterText.Bilingual("Downloading", "downloading") + ": " + zipAsset.Url);
                Download(zipAsset.Url, zipFile, options.TimeoutSeconds * 10, options);
                Download(hashAsset.Url, hashFile, options.TimeoutSeconds, options);
                string expected = Regex.Match(ReadAllText(hashFile).Trim(), @"^[0-9a-fA-F]{64}").Value.ToLowerInvariant();
                string actual = HashFile(zipFile);
                if (expected.Length != 64 || expected != actual)
                    throw new InvalidOperationException(UpdaterText.Bilingual("ChecksumMismatch", "SHA-256 mismatch; nothing was changed") + ": sidecar "
                        + expected + " vs file " + actual);
                say(UpdaterText.Bilingual("ChecksumVerified", "SHA-256 verified") + ": " + actual);

                string package = ExtractPackage(zipFile, extraction, say);
                DeliveryRules rules = ReadJson<DeliveryRules>(Combine(package, "scripts/operations/delivery-files.json"));
                if (rules == null || rules.SchemaVersion != 1 || rules.Include == null || rules.Exclude == null || rules.LegacyCleanup == null)
                    throw new InvalidOperationException("The package has invalid delivery-files.json rules.");
                DeliveryManifest next = ReadJson<DeliveryManifest>(Combine(package, "manifest/delivery.json"));
                if (next == null || NormalizeVersion(next.Release) != NormalizeVersion(release.Tag))
                    throw new InvalidOperationException("The ZIP delivery manifest does not match the selected release.");
                ValidatePackage(package, rules);
                Dictionary<string, string> newFiles = HashInventory(package);
                ValidateInstallPaths(package, root, backup);
                Dictionary<string, string> oldFiles = InstalledInventory(root);

                CreateDirectory(Combine(root, ".previous"));
                if (DirectoryExists(backup)) RemoveTree(backup);
                CreateDirectory(backup);
                say(UpdaterText.Bilingual("Backup", "backing up install") + ": " + backup);
                CopyTree(root, backup, true, null);
                WriteJson(backup + ".update-receipt.json", newFiles);
                Directory.SetLastWriteTimeUtc(Prefix(backup), DateTime.UtcNow);
                backupReady = true;
                PruneBackups(Combine(root, ".previous"), backup);

                say(UpdaterText.Bilingual("Overlay", "removing owned retired files and overlaying the delivery"));
                RemoveLegacyFiles(root, rules, oldFiles, newFiles, say);
                foreach (KeyValuePair<string, string> file in oldFiles)
                    if ((file.Key.StartsWith("runtime/", StringComparison.Ordinal) || file.Key.StartsWith("manifest/", StringComparison.Ordinal)) && !newFiles
                            .ContainsKey(file.Key))
                        RemoveOwnedFile(root, file.Key, file.Value, say);
                CopyPackage(package, root, options.BeforeDeployFile);
                changed = true;
                DeliveryManifest now = ReadJson<DeliveryManifest>(DeliveryFilePath(root));
                say(UpdaterText.Bilingual("UpdateDone", "DONE") + ": " + installed.Release + " -> " + now.Release + "; -Rollback restores " + installed.Package
                        + ".");
            }
            catch
            {
                if (backupReady)
                {
                    try
                    {
                        Dictionary<string, string> receipt = ReadReceipt(backup + ".update-receipt.json");
                        RemoveAddedFiles(root, backup, receipt, say);
                        CopyTree(backup, root, true, null);
                        say(UpdaterText.Bilingual("InterruptedRollback", "interrupted replacement rolled back from") + " " + backup);
                    }
                    catch (Exception rollbackError) {
                        throw new InvalidOperationException("Update failed and rollback also failed: " + rollbackError.GetBaseException().Message,
                                rollbackError);
                    }
                }
                throw;
            }
            finally
            {
                if (DirectoryExists(extraction)) RemoveTree(extraction);
                if (!changed && DirectoryExists(work)) { /* keep failed downloads for diagnosis, matching the script behavior */ }
                if (changed && DirectoryExists(work)) RemoveTree(work);
            }
        }

        private static string ExtractPackage(string zipFile, string extraction, Action<string> say)
        {
            using (ZipArchive archive = ZipFile.OpenRead(Prefix(zipFile)))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                    if (relative.Length == 0) continue;
                    if (!IsSafeRelativePath(relative)) throw new InvalidOperationException("Unsafe ZIP entry: " + entry.FullName);
                    EnsurePathSupported(Combine(extraction, relative));
                }
                CreateDirectory(extraction);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('\\', '/');
                    if (relative.EndsWith("/", StringComparison.Ordinal)) relative = relative.TrimEnd('/');
                    if (relative.Length == 0) continue;
                    if (!IsSafeRelativePath(relative)) throw new InvalidOperationException("Unsafe ZIP entry: " + entry.FullName);
                    string target = Combine(extraction, relative);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { CreateDirectory(target); continue; }
                    CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(Prefix(target), FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }
            string direct = DeliveryFilePath(extraction);
            if (ExistsFile(direct)) { say(UpdaterText.Bilingual("Extracted", "extracted package to") + ": " + extraction); return extraction; }
            string[] children = GetDirectories(extraction);
            if (children.Length == 1 && ExistsFile(DeliveryFilePath(children[0]))) {
                say(UpdaterText.Bilingual("Extracted", "extracted package to") + ": " + extraction); return children[0]; }
            throw new InvalidOperationException("The ZIP does not contain a delivery root with manifest/delivery.json.");
        }

        private static void ValidatePackage(string package, DeliveryRules rules)
        {
            if (rules.RequiredFiles == null || rules.RequiredFiles.Count == 0)
                throw new InvalidOperationException("The package has no required delivery files.");
            foreach (string must in rules.RequiredFiles)
                if (!IsSafeRelativePath(must) || !InDelivery(must, rules) || !ExistsFile(Combine(package, must)))
                    throw new InvalidOperationException("Package is incomplete: missing " + must);
            foreach (string file in GetFilesRecursive(package))
            {
                string relative = RelativeTo(package, file);
                if (!IsSafeRelativePath(relative) || !InDelivery(relative, rules))
                    throw new InvalidOperationException("File outside delivery set: " + relative);
            }
        }

        private static void ValidateInstallPaths(string package, string root, string backup)
        {
            foreach (string file in GetFilesRecursive(package))
            {
                string relative = RelativeTo(package, file);
                EnsurePathSupported(Combine(root, relative));
                EnsurePathSupported(Combine(backup, relative));
            }
        }

        internal static void EnsurePathSupported(string path)
        {
            const int maximum = 32767;
            if (path.Length >= maximum) throw PathLimitError(path);
            string full;
            try { full = Path.GetFullPath(path); }
            catch (PathTooLongException error) { throw PathLimitError(path, error); }
            int extendedLength = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (full.StartsWith("\\\\", StringComparison.Ordinal) ? full.Length + 6 : full.Length + 4)
                : full.Length;
            if (extendedLength >= maximum) throw PathLimitError(path);
        }

        private static InvalidOperationException PathLimitError(string path, Exception inner = null)
        {
            return new InvalidOperationException(UpdaterText
                    .Bilingual("PathTooLong", "Windows extended path limit exceeded (32,767 characters); shorten TEMP or the install path and retry") + ": "
                    + path, inner);
        }

        private static void PruneBackups(string previousRoot, string keep)
        {
            foreach (string directory in GetDirectories(previousRoot)
                    .Where(d => !string.Equals(NormalizeForCompare(d), NormalizeForCompare(keep), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(1))
            {
                File.Delete(Prefix(directory + ".update-receipt.json"));
                RemoveTree(directory);
            }
        }

        private static void Relaunch(string root, UpdaterOptions options, Action<string> say)
        {
            if (!options.RelaunchConfigurator) return;
            string executable = Combine(root, "TiaOpenness.exe");
            if (!ExistsFile(executable)) return;
            say(UpdaterText.Bilingual("Relaunching", "reopening Workbench") + ": " + executable);
            Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = true });
        }

        private static T ReadJson<T>(string path) where T : class
        {
            using (Stream stream = new FileStream(Prefix(path), FileMode.Open, FileAccess.Read, FileShare.Read)) return (T)JsonSerializer(typeof(T))
                .ReadObject(stream);
        }

        private static void WriteJson<T>(string path, T value)
        {
            using (Stream stream = new FileStream(Prefix(path), FileMode.Create, FileAccess.Write, FileShare.None)) JsonSerializer(typeof(T))
                .WriteObject(stream, value);
        }

        private static DataContractJsonSerializer JsonSerializer(Type type)
        {
            return new DataContractJsonSerializer(type, new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        }

        private static Dictionary<string, string> ReadReceipt(string path)
        {
            if (!ExistsFile(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            return ReadJson<Dictionary<string, string>>(path) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static string HashFile(string path)
        {
            using (SHA256 hash = SHA256.Create()) using (Stream input = new FileStream(Prefix(path), FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        private static string ReadAllText(string path) { using (var reader = new StreamReader(Prefix(path), Encoding.UTF8, true)) return reader.ReadToEnd(); }

        private static string DeliveryFilePath(string root) { return Combine(root, "manifest/delivery.json"); }
        private static bool InDelivery(string path, DeliveryRules rules) { return Matches(path, rules.Include) && !Matches(path, rules.Exclude); }
        private static bool Matches(string path, RuleSet rule)
        {
            if (rule == null) return false;
            if ((rule.Files ?? new List<string>()).Contains(path, StringComparer.Ordinal)) return true;
            return (rule.Prefixes ?? new List<string>()).Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool IsProtectedRoot(string relative)
        {
            string first = relative.Split('/')[0];
            return new[] { "data", "staging", ".previous", ".update", "TiaMcp_Output", ".git", "bin-build" }.Contains(first, StringComparer.OrdinalIgnoreCase);
        }
        private static string Combine(string root, string relative) {
            return root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar); }
        private static string RelativeTo(string root, string path) {
            string prefix = NormalizeForCompare(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; return NormalizeForCompare(path)
                .Substring(prefix.Length).Replace('\\', '/');
        }
        private static string NormalizeForCompare(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) full = "\\\\" + full.Substring(8);
            else if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) full = full.Substring(4);
            return full.TrimEnd('\\', '/');
        }
        private static bool IsWithin(string path, string root) {
            string normalized = NormalizeForCompare(path); return normalized
                .StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string
                .Equals(normalized, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string Prefix(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return path;
            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private static bool ExistsFile(string path) { return File.Exists(Prefix(path)); }
        private static bool DirectoryExists(string path) { return Directory.Exists(Prefix(path)); }
        private static bool ExistsAny(string path) { return ExistsFile(path) || DirectoryExists(path); }
        private static void CreateDirectory(string path) { if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(Prefix(path)); }
        private static string[] GetFiles(string path) {
            return Directory.Exists(Prefix(path)) ? Directory.GetFiles(Prefix(path), "*", SearchOption.TopDirectoryOnly) : new string[0]; }
        private static string[] GetDirectories(string path) {
            return Directory.Exists(Prefix(path)) ? Directory.GetDirectories(Prefix(path), "*", SearchOption.TopDirectoryOnly) : new string[0]; }
        private static string[] GetEntries(string path) { return Directory.Exists(Prefix(path)) ? Directory.GetFileSystemEntries(Prefix(path)) : new string[0];
        }

        private static void RemoveTree(string path)
        {
            if (!DirectoryExists(path)) { if (ExistsFile(path)) File.Delete(Prefix(path)); return; }
            foreach (string file in GetFilesRecursive(path)) if ((File.GetAttributes(Prefix(file)) & FileAttributes.ReparsePoint) == 0) File
                .Delete(Prefix(file));
            foreach (string dir in GetDirectoriesRecursive(path).OrderByDescending(p => p.Length))
                if ((File.GetAttributes(Prefix(dir)) & FileAttributes.ReparsePoint) == 0) Directory.Delete(Prefix(dir), false);
            Directory.Delete(Prefix(path), false);
        }

        private static IEnumerable<string> GetDirectoriesRecursive(string root)
        {
            var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0) foreach (string dir in GetDirectories(pending.Pop())) {
                result.Add(dir); if ((File.GetAttributes(Prefix(dir)) & FileAttributes.ReparsePoint) == 0) pending.Push(dir); }
            return result;
        }
    }
}

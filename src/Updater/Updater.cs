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

namespace TiaMcp.Updater
{
    public sealed class UpdaterOptions
    {
        public string InstallRoot { get; set; }
        public string Version { get; set; }
        public string Repository { get; set; } = "asckye/TIA_Portal_Openness_MCP";
        public int TimeoutSeconds { get; set; } = 60;
        public int WaitForPid { get; set; }
        public bool Check { get; set; }
        public bool Rollback { get; set; }
        public bool Force { get; set; }
        public bool RelaunchConfigurator { get; set; }
        public bool SelfTest { get; set; }
        // Overridden only by the offline suite. A real invocation always uses the GitHub URLs.
        public string ApiBaseUrl { get; set; }
        public string ReleasePageBaseUrl { get; set; }
        public Func<string, int, string> ReadTextForTest { get; set; }
        public Func<string, int, string> ReadRedirectForTest { get; set; }
        public Func<string, int, byte[]> ReadBytesForTest { get; set; }
        public int WaitForPidTimeoutSeconds { get; set; } = 30;
        public Action<string> BeforeDeployFile { get; set; }
        public Func<int, bool> ProcessExists { get; set; }
        internal string TemporaryDirectoryForTest { get; set; }
    }

    [DataContract]
    internal sealed class DeliveryRules
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "include")] public RuleSet Include { get; set; }
        [DataMember(Name = "exclude")] public RuleSet Exclude { get; set; }
        [DataMember(Name = "legacyCleanup")] public RuleSet LegacyCleanup { get; set; }
    }

    [DataContract]
    internal sealed class RuleSet
    {
        [DataMember(Name = "files")] public List<string> Files { get; set; }
        [DataMember(Name = "prefixes")] public List<string> Prefixes { get; set; }
    }

    [DataContract]
    internal sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets { get; set; }
    }

    [DataContract]
    internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    [DataContract]
    internal sealed class DeliveryManifest
    {
        [DataMember(Name = "release")] public string Release { get; set; }
        [DataMember(Name = "package")] public string Package { get; set; }
        [DataMember(Name = "engineRelease")] public string EngineRelease { get; set; }
    }

    [DataContract]
    internal sealed class InventoryRecord
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "runtimeFiles")] public List<InventoryRecord> RuntimeFiles { get; set; }
        [DataMember(Name = "files")] public List<InventoryRecord> Files { get; set; }
    }

    [DataContract]
    internal sealed class InventoryFileList
    {
        [DataMember(Name = "files")] public Dictionary<string, string> Files { get; set; }
    }

    public static class UpdaterEngine
    {
        private static readonly Regex VersionPattern = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ZipPattern = new Regex(@"^TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const string UpdaterPath = "runtime/tools/TiaMcp.Updater.exe";
        private const string UpdaterConfigPath = UpdaterPath + ".config";
        private static readonly string[] RuntimeProcessNames = BuildProcessNames();

        public static int Run(UpdaterOptions options, Action<string> say, Func<string, IList<string>> runningProcesses = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (say == null) say = _ => { };
            string root = Path.GetFullPath(options.InstallRoot ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            options.InstallRoot = root;
            if (options.TimeoutSeconds < 1 || options.TimeoutSeconds > 3600) throw new InvalidOperationException("TimeoutSeconds must be between 1 and 3600.");
            if (!IsValidRepository(options.Repository)) throw new InvalidOperationException("Repository must be owner/name.");
            if (options.Version != null && options.Version.Length > 0 && !VersionPattern.IsMatch(options.Version)) throw new InvalidOperationException("-Version must be vX.Y.Z.");
            if (IsSourceCheckout(root)) throw new InvalidOperationException("This folder is a source checkout or worktree. The updater only changes an installed delivery.");

            string deliveryFile = Combine(root, "manifest/delivery.json");
            if (!ExistsFile(deliveryFile)) throw new InvalidOperationException("Not a TIA MCP delivery: manifest/delivery.json is missing.");
            DeliveryManifest installed = ReadJson<DeliveryManifest>(deliveryFile);
            if (installed == null || String.IsNullOrWhiteSpace(installed.Release) || String.IsNullOrWhiteSpace(installed.Package)) throw new InvalidOperationException("manifest/delivery.json has no release/package fields.");
            if (!Regex.IsMatch(installed.Package, @"^[A-Za-z0-9_.-]+$")) throw new InvalidOperationException("manifest/delivery.json has an unsafe package name.");
            say(UpdaterText.Bilingual("InstallRoot", "install root") + ": " + root + "; " + UpdaterText.Bilingual("InstalledVersion", "installed") + ": " + installed.Release + " (" + installed.Package + ")");
            WaitForProcess(options, say);

            string backupRoot = Combine(root, ".previous");
            if (options.Rollback) return Rollback(root, installed, backupRoot, options, say, runningProcesses);

            ReleaseInfo release = FindRelease(options, installed.Release, say);
            string latest = NormalizeVersion(release.Tag);
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
                    || (File.Exists(Path.Combine(path, "CLAUDE.md")) && File.Exists(Path.Combine(path, "Version.props")) && Directory.Exists(Path.Combine(path, "src")))
                    || (File.Exists(Path.Combine(path, "Version.props")) && File.Exists(Path.Combine(path, "src", "Studio", "Gui", "TiaOpenness.Gui.csproj")))) return true;
            }
            return false;
        }

        public static bool IsSafeRelativePath(string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || relative.IndexOfAny(new[] { '\\', ':', '*', '?', '[', ']' }) >= 0) return false;
            string[] parts = relative.Split('/');
            return parts.All(p => p.Length > 0 && p != "." && p != "..") && !Path.IsPathRooted(relative);
        }

        public static string NormalizeVersion(string value)
        {
            Match match = VersionPattern.Match(value ?? "");
            if (!match.Success) throw new InvalidOperationException("Release tag is not a version: " + value);
            return String.Join(".", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        }

        public static int CompareVersions(string left, string right)
        {
            Version a, b;
            if (!Version.TryParse(NormalizeVersion(left), out a) || !Version.TryParse(NormalizeVersion(right), out b)) throw new InvalidOperationException("Version comparison received an invalid version.");
            return a.CompareTo(b);
        }

        private static void WaitForProcess(UpdaterOptions options, Action<string> say)
        {
            if (options.WaitForPid <= 0) return;
            Func<int, bool> exists = options.ProcessExists ?? (pid => { try { Process.GetProcessById(pid); return true; } catch (ArgumentException) /* swallow(probe-optional): a missing PID means the Workbench has exited */ { return false; } });
            DateTime deadline = DateTime.UtcNow.AddSeconds(options.WaitForPidTimeoutSeconds);
            while (exists(options.WaitForPid) && DateTime.UtcNow < deadline) Thread.Sleep(250);
            if (exists(options.WaitForPid)) throw new InvalidOperationException("Process " + options.WaitForPid + " (the Workbench that launched this update) is still running after " + options.WaitForPidTimeoutSeconds + " seconds.");
            say(UpdaterText.Bilingual("WorkbenchExited", "Workbench process exited") + ": PID " + options.WaitForPid);
        }

        private static void RequireStopped(string root, Func<string, IList<string>> processProbe)
        {
            IList<string> running = processProbe == null ? FindRunningProcesses(root) : processProbe(root);
            if (running != null && running.Count > 0) throw new InvalidOperationException(UpdaterText.Bilingual("ProcessesRunning", "close these bundle processes first; the updater never kills them") + ": " + String.Join("; ", running));
        }

        public static IList<string> FindRunningProcesses(string root)
        {
            var result = new List<string>();
            string normalized = NormalizeForCompare(root);
            foreach (string name in RuntimeProcessNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    string executable = "";
                    try { executable = process.MainModule.FileName; }
                    catch /* swallow(probe-optional): an unreadable image path is conservatively reported by PID */ { }
                    if (executable.Length == 0 || IsWithin(executable, normalized)) result.Add(name + ".exe PID " + process.Id + (executable.Length > 0 ? " (" + executable + ")" : ""));
                    process.Dispose();
                }
            }
            return result;
        }

        private static string[] BuildProcessNames()
        {
            var names = new List<string> { "TiaMcp.Engine.V20", "TiaMcp.Engine.V21", "TiaMcp.FoundationHost", "TiaMcpServer", "TiaOpenness", "TiaOpenness.Bridge", "TiaMcpConfigurator" };
            foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }) names.Add("TiaMcp.PlcWorker." + key);
            return names.ToArray();
        }

        private static ReleaseInfo FindRelease(UpdaterOptions options, string installed, Action<string> say)
        {
            string api = options.ApiBaseUrl ?? "https://api.github.com";
            string tag = String.IsNullOrEmpty(options.Version) ? "" : "v" + options.Version.TrimStart('v', 'V');
            string url = api.TrimEnd('/') + "/repos/" + options.Repository + "/releases/" + (tag.Length == 0 ? "latest" : "tags/" + tag);
            try
            {
                ReleaseInfo release = GetJson<ReleaseInfo>(url, "application/vnd.github+json", options.TimeoutSeconds, options);
                NormalizeVersion(release.Tag);
                if (SelectAsset(release, true) == null || SelectAsset(release, false) == null) throw new InvalidOperationException("The release does not contain both delivery ZIP and .sha256 assets.");
                say(UpdaterText.Bilingual("GitHubApi", "GitHub API") + ": " + release.Tag + " (" + (release.PublishedAt ?? "") + ")");
                return release;
            }
            catch (Exception apiError)
            {
                say(UpdaterText.Bilingual("ApiFallback", "GitHub API unavailable; using the release page") + ": " + apiError.GetBaseException().Message);
                string pageBase = options.ReleasePageBaseUrl ?? ("https://github.com/" + options.Repository);
                if (tag.Length == 0)
                {
                    string location = GetRedirect(pageBase.TrimEnd('/') + "/releases/latest", options.TimeoutSeconds, options);
                    Match redirect = Regex.Match(location ?? "", @"/releases/tag/([^/?#]+)$");
                    if (!redirect.Success) throw new InvalidOperationException("Could not determine the latest release tag from " + location);
                    tag = Uri.UnescapeDataString(redirect.Groups[1].Value);
                }
                var fallback = new ReleaseInfo { Tag = tag, Assets = new List<ReleaseAsset>() };
                string html = GetText(pageBase.TrimEnd('/') + "/releases/expanded_assets/" + Uri.EscapeDataString(tag), options.TimeoutSeconds, options);
                foreach (Match asset in Regex.Matches(html, @"/releases/download/" + Regex.Escape(tag) + @"/(?<name>TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.(?:zip|sha256))"))
                {
                    string name = Uri.UnescapeDataString(asset.Groups["name"].Value);
                    if (!fallback.Assets.Any(a => String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                        fallback.Assets.Add(new ReleaseAsset { Name = name, Url = pageBase.TrimEnd('/') + asset.Value });
                }
                SelectAsset(fallback, true);
                SelectAsset(fallback, false);
                say(UpdaterText.Bilingual("ReleasePage", "release page") + ": " + tag);
                return fallback;
            }
        }

        private static ReleaseAsset SelectAsset(ReleaseInfo release, bool zip)
        {
            if (release == null || release.Assets == null) return null;
            return release.Assets.FirstOrDefault(a => a != null && a.Name != null && (zip ? ZipPattern.IsMatch(a.Name) : ZipPattern.IsMatch(a.Name.Replace(".sha256", ".zip")) && a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)));
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
                if (expected.Length != 64 || expected != actual) throw new InvalidOperationException(UpdaterText.Bilingual("ChecksumMismatch", "SHA-256 mismatch; nothing was changed") + ": sidecar " + expected + " vs file " + actual);
                say(UpdaterText.Bilingual("ChecksumVerified", "SHA-256 verified") + ": " + actual);

                string package = ExtractPackage(zipFile, extraction, say);
                DeliveryRules rules = ReadJson<DeliveryRules>(Combine(package, "scripts/operations/delivery-files.json"));
                if (rules == null || rules.SchemaVersion != 1 || rules.Include == null || rules.Exclude == null || rules.LegacyCleanup == null) throw new InvalidOperationException("The package has invalid delivery-files.json rules.");
                DeliveryManifest next = ReadJson<DeliveryManifest>(Combine(package, "manifest/delivery.json"));
                if (next == null || NormalizeVersion(next.Release) != NormalizeVersion(release.Tag)) throw new InvalidOperationException("The ZIP delivery manifest does not match the selected release.");
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
                RemoveLegacyFiles(root, rules, oldFiles, say);
                foreach (KeyValuePair<string, string> file in oldFiles)
                    if ((file.Key.StartsWith("runtime/", StringComparison.Ordinal) || file.Key.StartsWith("manifest/", StringComparison.Ordinal)) && !newFiles.ContainsKey(file.Key))
                        RemoveOwnedFile(root, file.Key, file.Value, say);
                CopyPackage(package, root, options.BeforeDeployFile);
                changed = true;
                DeliveryManifest now = ReadJson<DeliveryManifest>(deliveryFilePath(root));
                say(UpdaterText.Bilingual("UpdateDone", "DONE") + ": " + installed.Release + " -> " + now.Release + "; -Rollback restores " + installed.Package + ".");
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
                    catch (Exception rollbackError) { throw new InvalidOperationException("Update failed and rollback also failed: " + rollbackError.GetBaseException().Message, rollbackError); }
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
            string direct = deliveryFilePath(extraction);
            if (ExistsFile(direct)) { say(UpdaterText.Bilingual("Extracted", "extracted package to") + ": " + extraction); return extraction; }
            string[] children = GetDirectories(extraction);
            if (children.Length == 1 && ExistsFile(deliveryFilePath(children[0]))) { say(UpdaterText.Bilingual("Extracted", "extracted package to") + ": " + extraction); return children[0]; }
            throw new InvalidOperationException("The ZIP does not contain a delivery root with manifest/delivery.json.");
        }

        private static void ValidatePackage(string package, DeliveryRules rules)
        {
            foreach (string must in new[] { "TiaOpenness.exe", UpdaterPath, UpdaterConfigPath, "runtime/v20/TiaMcp.Engine.V20.exe", "runtime/v21/TiaMcp.Engine.V21.exe", "manifest/package-manifest.json", "manifest/delivery.json" })
                if (!ExistsFile(Combine(package, must))) throw new InvalidOperationException("Package is incomplete: missing " + must);
            foreach (string file in GetFilesRecursive(package))
            {
                string relative = RelativeTo(package, file);
                if (!IsSafeRelativePath(relative) || !InDelivery(relative, rules)) throw new InvalidOperationException("File outside delivery set: " + relative);
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
            return new InvalidOperationException(UpdaterText.Bilingual("PathTooLong", "Windows extended path limit exceeded (32,767 characters); shorten TEMP or the install path and retry") + ": " + path, inner);
        }

        private static Dictionary<string, string> InstalledInventory(string root)
        {
            var inventory = new Dictionary<string, string>(StringComparer.Ordinal);
            string record = Combine(root, "manifest/release-file-hashes.json");
            if (ExistsFile(record))
            {
                InventoryFileList hashes = ReadJson<InventoryFileList>(record);
                if (hashes != null && hashes.Files != null) foreach (KeyValuePair<string, string> item in hashes.Files) inventory[item.Key] = item.Value;
                inventory["manifest/release-file-hashes.json"] = HashFile(record);
                return inventory;
            }
            foreach (string name in new[] { "release-build.json", "multi-version-build.json" })
            {
                string path = Combine(root, "manifest/" + name);
                if (!ExistsFile(path)) continue;
                InventoryRecord manifest = ReadJson<InventoryRecord>(path);
                foreach (InventoryRecord row in (manifest.RuntimeFiles ?? new List<InventoryRecord>()).Concat(manifest.Files ?? new List<InventoryRecord>()))
                    if (row != null && !String.IsNullOrEmpty(row.Path) && !String.IsNullOrEmpty(row.Sha256)) inventory[row.Path] = row.Sha256;
            }
            return inventory;
        }

        private static void RemoveLegacyFiles(string root, DeliveryRules rules, Dictionary<string, string> inventory, Action<string> say)
        {
            foreach (KeyValuePair<string, string> item in inventory.ToArray())
                if (Matches(item.Key, rules.LegacyCleanup) && !InDelivery(item.Key, rules)) RemoveOwnedFile(root, item.Key, item.Value, say);
        }

        private static Dictionary<string, string> HashInventory(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in GetFilesRecursive(root)) result[RelativeTo(root, file)] = HashFile(file);
            return result;
        }

        private static void RemoveOwnedFile(string root, string relative, string expected, Action<string> say)
        {
            string path = OwnedPath(root, relative);
            if (!ExistsFile(path)) return;
            if (!String.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase)) { say(UpdaterText.Bilingual("PreserveModified", "preserving modified file") + ": " + relative); return; }
            File.Delete(Prefix(path));
            string parent = Path.GetDirectoryName(path);
            while (IsWithin(parent, NormalizeForCompare(root)) && !String.Equals(NormalizeForCompare(parent), NormalizeForCompare(root), StringComparison.OrdinalIgnoreCase))
            {
                if (GetEntries(parent).Length != 0) break;
                Directory.Delete(Prefix(parent), false);
                parent = Path.GetDirectoryName(parent);
            }
        }

        private static void RemoveAddedFiles(string root, string backup, Dictionary<string, string> receipt, Action<string> say)
        {
            foreach (KeyValuePair<string, string> file in receipt)
                if (!ExistsFile(OwnedPath(backup, file.Key))) RemoveOwnedFile(root, file.Key, file.Value, say);
        }

        private static int Rollback(string root, DeliveryManifest installed, string previousRoot, UpdaterOptions options, Action<string> say, Func<string, IList<string>> runningProcesses)
        {
            if (!DirectoryExists(previousRoot)) throw new InvalidOperationException("Nothing to roll back to (.previous is missing).");
            string[] backups = GetDirectories(previousRoot).OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            if (backups.Length == 0) throw new InvalidOperationException("Nothing to roll back to (.previous is empty).");
            string backup = backups[0];
            DeliveryManifest prior = ReadJson<DeliveryManifest>(deliveryFilePath(backup));
            if (prior == null) throw new InvalidOperationException("Backup has no manifest/delivery.json: " + backup);
            RequireStopped(root, runningProcesses);
            say(UpdaterText.Bilingual("RollingBack", "rolling back") + ": " + installed.Release + " -> " + prior.Release + " from " + backup);
            Dictionary<string, string> receipt = ReadReceipt(backup + ".update-receipt.json");
            if (receipt.Count == 0) receipt = InstalledInventory(root);
            RemoveAddedFiles(root, backup, receipt, say);
            CopyTree(backup, root, true, null);
            DeliveryManifest now = ReadJson<DeliveryManifest>(deliveryFilePath(root));
            say(UpdaterText.Bilingual("RollbackDone", "DONE") + ": installed version is now " + now.Release + ".");
            Relaunch(root, options, say);
            return 0;
        }

        private static void CopyTree(string source, string destination, bool excludeUserData, Action<string> beforeFile)
        {
            CreateDirectory(destination);
            string sourceRoot = NormalizeForCompare(source);
            foreach (string directory in GetDirectories(source))
            {
                string relative = RelativeTo(sourceRoot, directory);
                if (excludeUserData && IsProtectedRoot(relative)) continue;
                if ((File.GetAttributes(Prefix(directory)) & FileAttributes.ReparsePoint) != 0) continue;
                CreateDirectory(Combine(destination, relative));
                CopyTree(directory, Combine(destination, relative), excludeUserData, beforeFile);
            }
            foreach (string file in GetFiles(source))
            {
                string relative = RelativeTo(sourceRoot, file);
                if (excludeUserData && (IsProtectedRoot(relative) || Path.GetFileName(relative).EndsWith(".log", StringComparison.OrdinalIgnoreCase))) continue;
                if ((File.GetAttributes(Prefix(file)) & FileAttributes.ReparsePoint) != 0) continue;
                beforeFile?.Invoke(relative);
                string target = Combine(destination, relative);
                CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(Prefix(file), Prefix(target), true);
                File.SetLastWriteTimeUtc(Prefix(target), File.GetLastWriteTimeUtc(Prefix(file)));
            }
        }

        private static void CopyPackage(string package, string root, Action<string> beforeFile)
        {
            foreach (string file in GetFilesRecursive(package))
            {
                string relative = RelativeTo(package, file);
                if (IsProtectedRoot(relative)) continue;
                beforeFile?.Invoke(relative);
                string target = Combine(root, relative);
                CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(Prefix(file), Prefix(target), true);
            }
        }

        private static void PruneBackups(string previousRoot, string keep)
        {
            foreach (string directory in GetDirectories(previousRoot).Where(d => !String.Equals(NormalizeForCompare(d), NormalizeForCompare(keep), StringComparison.OrdinalIgnoreCase))
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

        private static void Download(string url, string destination, int timeoutSeconds, UpdaterOptions options)
        {
#if UPDATER_TEST_SOURCE
            string fixture = Environment.GetEnvironmentVariable("TIA_MCP_UPDATER_TEST_SOURCE");
            if (!String.IsNullOrWhiteSpace(fixture))
            {
                string asset = Path.GetFileName(new Uri(url).AbsolutePath);
                string source = Path.Combine(fixture, asset);
                if (String.IsNullOrWhiteSpace(asset) || !File.Exists(source)) throw new FileNotFoundException("Test release asset was not found.", source);
                File.Copy(source, Prefix(destination), true);
                return;
            }
#endif
            if (options.ReadBytesForTest != null)
            {
                byte[] bytes = options.ReadBytesForTest(url, timeoutSeconds);
                using (var output = new FileStream(Prefix(destination), FileMode.Create, FileAccess.Write, FileShare.None)) output.Write(bytes, 0, bytes.Length);
                return;
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1";
            request.Accept = "application/vnd.github+json";
            request.Timeout = checked(timeoutSeconds * 1000);
            request.ReadWriteTimeout = request.Timeout;
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(Prefix(destination), FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        }

        private static string GetText(string url, int timeoutSeconds, UpdaterOptions options)
        {
            if (options.ReadTextForTest != null) return options.ReadTextForTest(url, timeoutSeconds);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.Timeout = timeoutSeconds * 1000; request.ReadWriteTimeout = request.Timeout;
            using (WebResponse response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        private static string GetRedirect(string url, int timeoutSeconds, UpdaterOptions options)
        {
            if (options.ReadRedirectForTest != null) return options.ReadRedirectForTest(url, timeoutSeconds);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.AllowAutoRedirect = false; request.Timeout = timeoutSeconds * 1000;
            try { using (WebResponse response = request.GetResponse()) return response.Headers[HttpResponseHeader.Location]; }
            catch (WebException error) when (error.Response != null) { using (error.Response) return error.Response.Headers[HttpResponseHeader.Location]; }
        }

        private static T GetJson<T>(string url, string accept, int timeoutSeconds, UpdaterOptions options) where T : class
        {
#if UPDATER_TEST_SOURCE
            string fixture = Environment.GetEnvironmentVariable("TIA_MCP_UPDATER_TEST_SOURCE");
            if (!String.IsNullOrWhiteSpace(fixture))
            {
                using (Stream stream = File.OpenRead(Path.Combine(fixture, "latest.json"))) return (T)JsonSerializer(typeof(T)).ReadObject(stream);
            }
#endif
            if (options.ReadTextForTest != null)
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(options.ReadTextForTest(url, timeoutSeconds)))) return (T)JsonSerializer(typeof(T)).ReadObject(stream);
            }
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TiaMcp-Update-Engine/1"; request.Accept = accept; request.Timeout = timeoutSeconds * 1000; request.ReadWriteTimeout = request.Timeout;
            using (WebResponse response = request.GetResponse()) using (Stream stream = response.GetResponseStream()) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static T ReadJson<T>(string path) where T : class
        {
            using (Stream stream = new FileStream(Prefix(path), FileMode.Open, FileAccess.Read, FileShare.Read)) return (T)JsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static void WriteJson<T>(string path, T value)
        {
            using (Stream stream = new FileStream(Prefix(path), FileMode.Create, FileAccess.Write, FileShare.None)) JsonSerializer(typeof(T)).WriteObject(stream, value);
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

        private static string deliveryFilePath(string root) { return Combine(root, "manifest/delivery.json"); }
        private static bool InDelivery(string path, DeliveryRules rules) { return Matches(path, rules.Include) && !Matches(path, rules.Exclude); }
        private static bool Matches(string path, RuleSet rule)
        {
            if (rule == null) return false;
            if ((rule.Files ?? new List<string>()).Contains(path, StringComparer.Ordinal)) return true;
            return (rule.Prefixes ?? new List<string>()).Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static string OwnedPath(string root, string relative)
        {
            if (!IsSafeRelativePath(relative) || IsProtectedRoot(relative)) throw new InvalidOperationException("Unsafe or protected package path: " + relative);
            string boundary = NormalizeForCompare(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string path = Combine(root, relative);
            if (!NormalizeForCompare(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Package path leaves install root: " + relative);
            for (string probe = path; IsWithin(probe, NormalizeForCompare(root)); probe = Path.GetDirectoryName(probe))
                if (ExistsAny(probe) && (File.GetAttributes(Prefix(probe)) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse point in package path: " + relative);
            return path;
        }

        private static bool IsProtectedRoot(string relative)
        {
            string first = relative.Split('/')[0];
            return new[] { "data", ".previous", ".update", "TiaMcp_Output", ".git", "bin-build" }.Contains(first, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsValidRepository(string value) { return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$") && !value.Contains(".."); }
        private static string Combine(string root, string relative) { return root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar); }
        private static string RelativeTo(string root, string path) { string prefix = NormalizeForCompare(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; return NormalizeForCompare(path).Substring(prefix.Length).Replace('\\', '/'); }
        private static string NormalizeForCompare(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) full = "\\\\" + full.Substring(8);
            else if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) full = full.Substring(4);
            return full.TrimEnd('\\', '/');
        }
        private static bool IsWithin(string path, string root) { string normalized = NormalizeForCompare(path); return normalized.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || String.Equals(normalized, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }

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
        private static void CreateDirectory(string path) { if (!String.IsNullOrEmpty(path)) Directory.CreateDirectory(Prefix(path)); }
        private static string[] GetFiles(string path) { return Directory.Exists(Prefix(path)) ? Directory.GetFiles(Prefix(path), "*", SearchOption.TopDirectoryOnly) : new string[0]; }
        private static string[] GetDirectories(string path) { return Directory.Exists(Prefix(path)) ? Directory.GetDirectories(Prefix(path), "*", SearchOption.TopDirectoryOnly) : new string[0]; }
        private static string[] GetEntries(string path) { return Directory.Exists(Prefix(path)) ? Directory.GetFileSystemEntries(Prefix(path)) : new string[0]; }
        private static string[] GetFilesRecursive(string root)
        {
            var files = new List<string>();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string dir in GetDirectories(current))
                {
                    if ((File.GetAttributes(Prefix(dir)) & FileAttributes.ReparsePoint) == 0) pending.Push(dir);
                }
                files.AddRange(GetFiles(current));
            }
            return files.ToArray();
        }

        private static void RemoveTree(string path)
        {
            if (!DirectoryExists(path)) { if (ExistsFile(path)) File.Delete(Prefix(path)); return; }
            foreach (string file in GetFilesRecursive(path)) if ((File.GetAttributes(Prefix(file)) & FileAttributes.ReparsePoint) == 0) File.Delete(Prefix(file));
            foreach (string dir in GetDirectoriesRecursive(path).OrderByDescending(p => p.Length))
                if ((File.GetAttributes(Prefix(dir)) & FileAttributes.ReparsePoint) == 0) Directory.Delete(Prefix(dir), false);
            Directory.Delete(Prefix(path), false);
        }

        private static IEnumerable<string> GetDirectoriesRecursive(string root)
        {
            var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0) foreach (string dir in GetDirectories(pending.Pop())) { result.Add(dir); if ((File.GetAttributes(Prefix(dir)) & FileAttributes.ReparsePoint) == 0) pending.Push(dir); }
            return result;
        }
    }

    internal static class UpdaterFileStaging
    {
        internal static string CopyTo(string source, string directory)
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, Path.GetFileName(source));
            File.Copy(source, target, true);
            string config = source + ".config";
            if (File.Exists(config)) File.Copy(config, target + ".config", true);
            return target;
        }
    }
}

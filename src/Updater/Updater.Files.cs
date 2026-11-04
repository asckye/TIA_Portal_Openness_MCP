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
    public static partial class UpdaterEngine
    {

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
            foreach (string name in new[] { "release-build.json", "multi-version-build.json", "configurator-build.json" })
            {
                string path = Combine(root, "manifest/" + name);
                if (!ExistsFile(path)) continue;
                InventoryRecord manifest = ReadJson<InventoryRecord>(path);
                foreach (InventoryRecord row in (manifest.RuntimeFiles ?? new List<InventoryRecord>()).Concat(manifest.Files ?? new List<InventoryRecord>()))
                    if (row != null && !string.IsNullOrEmpty(row.Path) && !string.IsNullOrEmpty(row.Sha256)) inventory[row.Path] = row.Sha256;
                // The record itself is owned too. A retired build record must be
                // removable after its older runtime inventory has been consumed.
                inventory["manifest/" + name] = HashFile(path);
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
            if (!string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase)) {
                say(UpdaterText.Bilingual("PreserveModified", "preserving modified file") + ": " + relative); return; }
            File.Delete(Prefix(path));
            string parent = Path.GetDirectoryName(path);
            while (IsWithin(parent, NormalizeForCompare(root)) && !string
                    .Equals(NormalizeForCompare(parent), NormalizeForCompare(root), StringComparison.OrdinalIgnoreCase))
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

        private static int Rollback(string root, DeliveryManifest installed, string previousRoot, UpdaterOptions options, Action<string> say, Func<string,
                IList<string>> runningProcesses)
        {
            if (!DirectoryExists(previousRoot)) throw new InvalidOperationException("Nothing to roll back to (.previous is missing).");
            string[] backups = GetDirectories(previousRoot).OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            if (backups.Length == 0) throw new InvalidOperationException("Nothing to roll back to (.previous is empty).");
            string backup = backups[0];
            DeliveryManifest prior = ReadJson<DeliveryManifest>(DeliveryFilePath(backup));
            if (prior == null) throw new InvalidOperationException("Backup has no manifest/delivery.json: " + backup);
            RequireStopped(root, runningProcesses);
            say(UpdaterText.Bilingual("RollingBack", "rolling back") + ": " + installed.Release + " -> " + prior.Release + " from " + backup);
            Dictionary<string, string> receipt = ReadReceipt(backup + ".update-receipt.json");
            if (receipt.Count == 0) receipt = InstalledInventory(root);
            RemoveAddedFiles(root, backup, receipt, say);
            CopyTree(backup, root, true, null);
            DeliveryManifest now = ReadJson<DeliveryManifest>(DeliveryFilePath(root));
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

        private static string OwnedPath(string root, string relative)
        {
            if (!IsSafeRelativePath(relative) || IsProtectedRoot(relative))
                throw new InvalidOperationException("Unsafe or protected package path: " + relative);
            string boundary = NormalizeForCompare(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string path = Combine(root, relative);
            if (!NormalizeForCompare(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Package path leaves install root: " + relative);
            for (string probe = path; IsWithin(probe, NormalizeForCompare(root)); probe = Path.GetDirectoryName(probe))
                if (ExistsAny(probe) && (File.GetAttributes(Prefix(probe)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse point in package path: " + relative);
            return path;
        }
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

        private static Dictionary<string, string> HashInventory(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in GetFilesRecursive(root)) result[RelativeTo(root, file)] = HashFile(file);
            return result;
        }

        private static T RetryLocked<T>(Func<T> operation)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return operation(); }
                catch (IOException ex) when (attempt < 5 && ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33))
                { Thread.Sleep(200); }
            }
        }

        internal static void RetryLocked(Action operation)
        {
            RetryLocked(() => { operation(); return true; });
        }

        private static void Publish(string root, string stage, IEnumerable<string> files, IEnumerable<KeyValuePair<string, string>> retired,
            Action<string> beforeDeploy, Action<string> say, Action<InstallTransaction> publishBackup = null)
        {
            // TxF makes all owned-file renames visible in one commit. A killed updater closes its transaction
            // handle and NTFS aborts it. User directories and unknown files keep their original filesystem identity.
            RetryLocked(() =>
            {
                using (var transaction = new InstallTransaction())
                {
                    foreach (var file in retired)
                    {
                        var path = OwnedPath(root, file.Key);
                        if (!ExistsFile(path)) continue;
                        if (string.Equals(HashFile(path), file.Value, StringComparison.OrdinalIgnoreCase)) transaction.Delete(path);
                        else say(UpdaterText.Bilingual("PreserveModified", "preserving modified file") + ": " + file.Key);
                    }
                    foreach (var relative in files)
                    {
                        beforeDeploy?.Invoke(relative);
                        var target = OwnedPath(root, relative);
                        transaction.EnsureDirectory(Path.GetDirectoryName(target));
                        transaction.Move(OwnedPath(stage, relative), target);
                    }
                    publishBackup?.Invoke(transaction);
                    transaction.Commit();
                }
                return true;
            });
        }

        private sealed class InstallTransaction : IDisposable
        {
            private readonly SafeFileHandle handle;
            private readonly HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal InstallTransaction()
            {
                handle = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, "Workbench update");
                Check(!handle.IsInvalid, "start install transaction");
            }
            internal void Move(string from, string to) =>
                Check(MoveFileTransacted(Prefix(from), Prefix(to), IntPtr.Zero, IntPtr.Zero, 1, handle), to);
            internal void Delete(string path) => Check(DeleteFileTransacted(Prefix(path), handle), path);
            internal void EnsureDirectory(string path)
            {
                if (!directories.Add(path) || DirectoryExists(path)) return;
                EnsureDirectory(Path.GetDirectoryName(path));
                Check(CreateDirectoryTransacted(null, Prefix(path), IntPtr.Zero, handle), path);
            }
            internal void Commit() => Check(CommitTransaction(handle), "commit install transaction");
            public void Dispose() => handle.Dispose();

            private static void Check(bool success, string path)
            {
                if (success) return;
                int code = Marshal.GetLastWin32Error();
                throw new IOException(path + ": " + new System.ComponentModel.Win32Exception(code).Message, unchecked((int)0x80070000) | code);
            }
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("ktmw32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr id,
                int options, int isolation, int flags, int timeout, string description);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("ktmw32.dll", SetLastError = true)]
            private static extern bool CommitTransaction(SafeFileHandle transaction);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool MoveFileTransacted(string from, string to, IntPtr progress, IntPtr data, uint flags,
                SafeFileHandle transaction);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool DeleteFileTransacted(string path, SafeFileHandle transaction);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CreateDirectoryTransacted(string template, string path, IntPtr attributes,
                SafeFileHandle transaction);
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
            string stage = Path.Combine(Directory.GetParent(root).FullName, "tia-mcp-rollback-" + Guid.NewGuid().ToString("N"));
            try
            {
                var expected = HashInventory(backup);
                CopyTree(backup, stage, true, null);
                VerifyStaged(stage, expected);
                Publish(root, stage, expected.Keys, receipt.Where(file => !expected.ContainsKey(file.Key)), options.BeforeDeployFile, say);
            }
            finally { if (DirectoryExists(stage)) RemoveTree(stage); }
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
                RetryLocked(() =>
                {
                    File.Copy(Prefix(file), Prefix(target), true);
                    File.SetLastWriteTimeUtc(Prefix(target), File.GetLastWriteTimeUtc(Prefix(file)));
                });
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
            UpdaterEngine.RetryLocked(() => File.Copy(source, target, true));
            string config = source + ".config";
            if (File.Exists(config)) UpdaterEngine.RetryLocked(() => File.Copy(config, target + ".config", true));
            return target;
        }
    }
}

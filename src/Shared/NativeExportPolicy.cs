using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    internal static class NativeExportPolicy
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        internal static void CheckWritableDirectory(string path, Action<string>? accessCheck = null)
        {
            var parent = new DirectoryInfo(path);
            while (!parent.Exists)
            {
                if (File.Exists(parent.FullName) || parent.Parent == null) throw new IOException("Directory is unavailable; attempted path: " + path);
                parent = parent.Parent;
            }
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Directory ancestry contains a reparse point; attempted path: " + path);
            if (accessCheck != null) { accessCheck(parent.FullName); return; }
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                using var handle = CreateFileW(parent.FullName, 6, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Directory is not writable; attempted path: " + path);
            }
        }
        internal sealed class RecoveryEvidence
        {
            internal string? Directory, Warning;
            internal readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            internal string Status => Warning != null ? "backup-skipped" : Directory != null ? "backup-ready" : "not-needed";
        }
        internal sealed class RecoveryTarget
        {
            internal string Object = "", Blocker = "";
            internal Action<FileInfo> Export = _ => { };
        }
        internal static string ExportBlocker(bool consistent, bool protectedObject = false) =>
            !consistent ? "inconsistent" : protectedObject ? "know-how-protected" : "";
        internal static RecoveryEvidence SingleImportRecovery(IEnumerable<Action<FileInfo>> backups, Func<string> directory, string attemptedPath)
            => SingleImportRecovery(backups.Select((export, index) => new RecoveryTarget { Object = index.ToString(), Export = export }), directory, attemptedPath);
        internal static RecoveryEvidence SingleImportRecovery(IEnumerable<RecoveryTarget> backups, Func<string> directory, string attemptedPath)
        {
            var result = new RecoveryEvidence();
            RecoveryTarget[] exports;
            try { exports = backups.ToArray(); }
            catch (Exception error) /* swallow(native-fallback): backup target inspection is best effort for single imports */
            { result.Warning = "Backup skipped: target inspection failed (" + error.GetType().Name + ")."; return result; }
            if (exports.Length == 0) return result;
            try { result.Directory = directory(); }
            catch (Exception error) /* swallow(native-fallback): single import recovery is best effort; retain the location and reason */
            { result.Warning = "Backup skipped for " + string.Join(", ", exports.Select(x => x.Object)) + ": recovery location unavailable (" + error.GetType().Name + "); attempted path: " + attemptedPath; return result; }
            var skipped = new List<string>(); long total = 0;
            for (int i = 0; i < exports.Length; i++)
            {
                var target = exports[i];
                if (target.Blocker != "") { skipped.Add(target.Object + ": " + target.Blocker); continue; }
                try
                {
                    var file = new FileInfo(Path.Combine(result.Directory, i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".xml"));
                    target.Export(file);
                    using var saved = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (saved.Length < 1 || saved.Length > 4 * 1024 * 1024 || (total += saved.Length) > 32 * 1024 * 1024) throw new IOException("Recovery export exceeds the admitted byte budget.");
                    using var sha = SHA256.Create(); result.Files[file.FullName] = BitConverter.ToString(sha.ComputeHash(saved)).Replace("-", "").ToLowerInvariant();
                }
                catch (Exception error) /* swallow(native-fallback): export refusal must not prevent a single overwrite import */
                { skipped.Add(target.Object + ": export or validation refused (" + error.GetType().Name + ")"); }
            }
            if (skipped.Count > 0) result.Warning = "Backup skipped for " + string.Join("; ", skipped) + ". Import proceeds without a complete recovery backup.";
            return result;
        }
        internal static void RequireConsistent(string kind, IEnumerable<string> inconsistent, string parameter)
        {
            var paths = inconsistent.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return;
            string noun = kind == "types" ? "type" : "block";
            throw new AdapterPreconditionException("Compile the inconsistent " + noun + " before export. Inconsistent objects: "
                + string.Join(", ", paths) + ".", parameter, false);
        }

        internal static void RequireSoftwarePath(string requested, string resolved, bool batch)
        {
            if (batch && !string.Equals(requested, resolved, StringComparison.Ordinal))
                throw new AdapterPreconditionException("Batch operation requires the exact software path, not an alias.", "softwarePath");
        }

        internal static bool ResolvedIdentityMatches(string requested, string resolved)
        {
            if (requested == resolved) return true;
            var actual = resolved.Split('/').Select(Uri.UnescapeDataString).ToArray();
            var input = requested.Split('/');
            if (actual.Length < 2 || input.Length < 1 || input.Any(p => p.Length == 0 || p == "." || p == "..")) return false;
            var groups = new List<string>(); int device = actual.Length - 2;
            if(actual[0] is "devices" or "ungrouped") device=1;
            else if(actual[0]=="device-groups")
            {
                int index=1;
                while(index<actual.Length-1)
                {
                    groups.Add(actual[index++]);
                    if(actual[index]=="devices") { device=index+1; break; }
                    if(actual[index++]!="groups") return false;
                }
            }
            else groups.AddRange(actual.Take(device));
            if(device>=actual.Length-1) return false;
            string host=actual[actual.Length-1];
            if(NativePathSelection.ShortAlias(input,actual[device],host)) return true;
            if (input.Length != groups.Count + 1 && input.Length != groups.Count + 2) return false;
            for (int i = 0; i < groups.Count; i++) if (input[i] != groups[i]) return false;
            if (input.Length == groups.Count + 1) return string.Equals(input[groups.Count],actual[device],StringComparison.OrdinalIgnoreCase)
                || string.Equals(input[groups.Count],host,StringComparison.OrdinalIgnoreCase);
            return string.Equals(input[groups.Count],actual[device],StringComparison.OrdinalIgnoreCase)
                && string.Equals(input[groups.Count+1],host,StringComparison.OrdinalIgnoreCase);
        }

        internal static string? BlockedReason(string status) => status == "inconsistent"
            ? "Compile the inconsistent object before export."
            : status == "semantics-unverified" ? "Export method evidence is insufficient for apply." : null;

        internal static void RequireApply(string status)
        {
            string? reason = BlockedReason(status);
            if (reason != null) throw new AdapterPreconditionException(reason, "export-plan", false);
        }
    }
}

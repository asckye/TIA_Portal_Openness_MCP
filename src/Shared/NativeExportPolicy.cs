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
            internal string Status => Warning != null ? "unavailable-import-without-backup" : Directory != null ? "backup-ready" : "not-needed";
        }
        internal static RecoveryEvidence SingleImportRecovery(IEnumerable<Action<FileInfo>> backups, Func<string> directory, string attemptedPath)
        {
            var result = new RecoveryEvidence();
            var exports = backups.ToArray();
            if (exports.Length == 0) return result;
            try { result.Directory = directory(); }
            catch (AdapterPreconditionException) /* swallow(native-fallback): no writable recovery location permits a single import with an explicit warning */
            { result.Warning = "Imported without a recovery backup: no writable recovery location; attempted path: " + attemptedPath; return result; }
            try
            {
                long total = 0;
                for (int i = 0; i < exports.Length; i++)
                {
                    var file = new FileInfo(Path.Combine(result.Directory, i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".xml"));
                    exports[i](file);
                    using var saved = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (saved.Length < 1 || saved.Length > 4 * 1024 * 1024 || (total += saved.Length) > 32 * 1024 * 1024) throw new IOException("Recovery export exceeds the admitted byte budget.");
                    using var sha = SHA256.Create(); result.Files[file.FullName] = BitConverter.ToString(sha.ComputeHash(saved)).Replace("-", "").ToLowerInvariant();
                }
                return result;
            }
            catch (Exception error) /* swallow(privacy): all backups precede import, so export/validation failures are typed refusals */
            {
                var refusal = new AdapterPreconditionException("Recovery export or validation failed before import.", "importPath", false, error);
                refusal.Data["recoveryDirectory"] = result.Directory; refusal.Data["recoveryFiles"] = result.Files;
                refusal.Data["recoveryStatus"] = "backup-failed-before-import";
                throw refusal;
            }
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

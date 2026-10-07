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
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int information, ref int disposition, uint size);
        internal static bool DeleteVerifiedFile(string path, Func<Stream, bool> matches)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Keep exclusive read/delete access through verification and delete the opened object.
                using var handle = CreateFileW(path, 0x80010000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Cannot exclusively open staged file: " + path);
                using var stream = new FileStream(handle, FileAccess.Read);
                if (!matches(stream)) return false;
                int disposition = 1;
                if (!SetFileInformationByHandle(handle, 4, ref disposition, 4)) throw new IOException("Cannot delete verified staged file: " + path);
                return true;
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!matches(stream)) return false;
                File.Delete(path); return true;
            }
        }
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
            internal readonly List<Dictionary<string, string>> Skipped = new List<Dictionary<string, string>>();
            internal readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            internal string Status => Warning != null ? "backup-skipped" : Directory != null ? "backup-ready" : "not-needed";
        }
        internal sealed class RecoveryTarget
        {
            internal string Object = "", Blocker = "";
            internal Func<string>? InspectBlocker;
            internal Action<FileInfo> Export = _ => { };
        }
        internal static string ExportBlocker(bool consistent, bool protectedObject = false) =>
            !consistent ? "inconsistent" : protectedObject ? "know-how-protected" : "";
        internal static string InspectBlocker(Func<string> inspect)
        {
            try { return inspect(); }
            catch (Exception) /* swallow(native-fallback): unreadable consistency/protection blocks only this replacement */
            { return "unknown-consistency"; }
        }
        internal static string BackupWarning(IEnumerable<Dictionary<string, string>> skipped) => "Backup skipped for "
            + string.Join("; ", skipped.Select(x => x["object"] + ": " + x["reason"])) + ". Import proceeds without a complete recovery backup.";
        internal static RecoveryEvidence SingleImportRecovery(IEnumerable<Action<FileInfo>> backups, Func<string> directory, string attemptedPath)
            => SingleImportRecovery(backups.Select((export, index) => new RecoveryTarget { Object = index.ToString(), Export = export }), directory, attemptedPath);
        internal static RecoveryEvidence SingleImportRecovery(IEnumerable<RecoveryTarget> backups, Func<string> directory, string attemptedPath)
        {
            var result = new RecoveryEvidence();
            RecoveryTarget[] exports;
            try { exports = backups.ToArray(); }
            catch (AdapterPreconditionException error) when (error.ParamName is "groupPath" or "softwarePath") { throw; }
            catch (Exception error) /* swallow(native-fallback): unexpected backup enumeration failure is best effort */
            {
                result.Skipped.Add(new Dictionary<string, string> { ["object"] = "<target-inspection>", ["reason"] = "target-inspection-failed (" + error.GetType().Name + ")" });
                result.Warning = BackupWarning(result.Skipped); return result;
            }
            if (exports.Length == 0) return result;
            var ready = new List<KeyValuePair<int, RecoveryTarget>>();
            void Skip(RecoveryTarget target, string reason) => result.Skipped.Add(new Dictionary<string, string> { ["object"] = target.Object, ["reason"] = reason });
            for (int index = 0; index < exports.Length; index++)
            {
                var target = exports[index];
                string blocker = target.InspectBlocker == null ? target.Blocker : InspectBlocker(target.InspectBlocker);
                if (blocker != "") Skip(target, blocker); else ready.Add(new KeyValuePair<int, RecoveryTarget>(index, target));
            }
            if (ready.Count > 0)
            {
                try { result.Directory = directory(); }
                catch (AdapterPreconditionException error) when (error.ParamName is "groupPath" or "softwarePath") { throw; }
                catch (Exception error) /* swallow(native-fallback): recovery location failures do not block a single import */
                { foreach (var target in ready) Skip(target.Value, "recovery-location-unavailable (" + error.GetType().Name + "); attempted path: " + attemptedPath); }
            }
            long total = 0;
            if (result.Directory != null)
                for (int i = 0; i < ready.Count; i++)
                {
                    var target = ready[i].Value;
                    try
                    {
                        var file = new FileInfo(Path.Combine(result.Directory, ready[i].Key.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".xml"));
                        target.Export(file);
                        using var saved = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (saved.Length < 1 || saved.Length > 4 * 1024 * 1024 || (total += saved.Length) > 32 * 1024 * 1024) throw new IOException("Recovery export exceeds the admitted byte budget.");
                        using var sha = SHA256.Create(); result.Files[file.FullName] = BitConverter.ToString(sha.ComputeHash(saved)).Replace("-", "").ToLowerInvariant();
                    }
                    catch (AdapterPreconditionException error) when (error.ParamName is "groupPath" or "softwarePath") { throw; }
                    catch (Exception error) /* swallow(native-fallback): export refusal must not prevent a single overwrite import */
                    { Skip(target, "export-or-validation-refused (" + error.GetType().Name + ")"); }
                }
            if (result.Skipped.Count > 0) result.Warning = BackupWarning(result.Skipped);
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

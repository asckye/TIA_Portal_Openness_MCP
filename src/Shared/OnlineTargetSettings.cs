using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Globalization;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    internal static class OnlineTargetSettings
    {
        internal static string SettingsPath => Path.Combine(DataLocations.Current.ConfigDirectory, "online-targets.settings");

        internal static (string Parameter, string? Condition, string Message)? RuntimeRefusal(string operation, string target)
        {
            if (OnlineTargetPolicy.AddressParameter(operation) is not ("ip" or "host" or "endpointUrl")) return null;
            return OnlineTargetPolicy.RuntimeRefusal(operation, target, LoadRuntimeTargets());
        }

        internal static (string Condition, string Message)? CompanionRefusal(string mode)
        {
            if (mode != "run") return null;
            var targets = LoadRuntimeTargets();
            return targets == null ? null : (targets.IsValid ? "online-target-not-allowed" : "online-target-list-invalid",
                "The companion cannot be limited to listed hosts; mode=run is refused while an online-target list is configured.");
        }

        private static OnlineTargetList? LoadRuntimeTargets()
        {
            OnlineTargetList? targets;
            try { targets = Load(SettingsPath); }
            catch (Exception) /* swallow(env-probe): settings/path failures must refuse runtime targets before approval or channel creation. */
            { targets = OnlineTargetList.Invalid; }
            return targets;
        }

        internal static OnlineTargetList? Load(string path)
        {
            try
            {
                // Atomic saves expose the old or new file. Reading must not create a settings directory or a lock file.
                return OnlineTargetPolicy.Parse(File.ReadAllText(path));
            }
            catch (FileNotFoundException) /* swallow(probe-optional): no file means the optional list is not configured (D1). */
            { return null; }
            catch (DirectoryNotFoundException) /* swallow(probe-optional): a missing settings directory also means no configured list. */
            { return null; }
            // Sharing/I/O errors, denied file access and denied security permission all make the configured list unavailable.
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Trace.TraceWarning("Online-target settings unavailable: " + ex.GetType().Name);
                return OnlineTargetList.Invalid;
            }
        }

        internal static (OnlineTargetList? List, string Revision, string? Problem) ReadSnapshot(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
                var list = OnlineTargetPolicy.Parse(reader.ReadToEnd());
                return (list, Revision(path, bytes), list.IsValid ? null : "invalid");
            }
            catch (FileNotFoundException) /* swallow(probe-optional): a missing list has a distinct revision and no configured entries. */
            { return (null, "missing", null); }
            catch (DirectoryNotFoundException) /* swallow(probe-optional): reading an absent configuration directory has no side effects. */
            { return (null, "missing", null); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
            {
                /* swallow(privacy): retain an unreadable file state and lock the editor without logging its contents. */
                return (OnlineTargetList.Invalid, "unreadable", "unreadable");
            }
        }

        private static string Revision(string path, byte[] bytes)
        {
            using var hash = SHA256.Create();
            return File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(hash.ComputeHash(bytes));
        }

        internal static void Save(string path, OnlineTargetList list, AuditLog? audit = null)
            => SaveCore(path, list, audit, null);

        internal static string SaveChecked(string path, OnlineTargetList list, string expectedRevision, AuditLog? audit = null)
            => SaveCore(path, list, audit, expectedRevision);

        private static string SaveCore(string path, OnlineTargetList list, AuditLog? audit, string? expectedRevision)
        {
            if (!list.IsValid) throw new ArgumentException("Cannot save an invalid online-target list.", nameof(list));
            string text = string.Concat(list.Entries.Select(entry => "target=" + entry + "\n"));
            bool changed;
            string revision;
            using (JournalFileLock.Acquire(path + ".lock"))
            {
                var current = ReadSnapshot(path);
                if (expectedRevision != null && current.Revision != expectedRevision) throw new OnlineTargetSettingsChangedException();
                changed = !File.Exists(path) || File.ReadAllText(path) != text;
                if (!changed) return current.Revision;
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    { writer.Write(text); writer.Flush(); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                revision = Revision(path, new UTF8Encoding(false).GetBytes(text));
            }
            (audit ?? AuditLog.Current).Append("online-targets-change", "", "workbench", "", "", actor: ActorScope.Workbench);
            return revision;
        }
    }

    internal sealed class OnlineTargetSettingsChangedException : IOException
    {
        internal OnlineTargetSettingsChangedException() : base("The online-target file changed. Reload before saving.") { }
    }
}

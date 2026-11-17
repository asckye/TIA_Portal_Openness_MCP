using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    internal static class OnlineTargetSettings
    {
        internal static string SettingsPath => Path.Combine(DataLocations.Current.ConfigDirectory, "online-targets.settings");

        internal static OnlineTargetList? Load(string path)
        {
            try
            {
                using (JournalFileLock.Acquire(path + ".lock"))
                {
                    try { return OnlineTargetPolicy.Parse(File.ReadAllText(path)); }
                    catch (FileNotFoundException) /* swallow(probe-optional): no file means the optional list is not configured (D1). */
                    { return null; }
                    catch (DirectoryNotFoundException) /* swallow(probe-optional): a missing settings directory also means no configured list. */
                    { return null; }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Trace.TraceWarning("Online-target settings unavailable: " + ex.GetType().Name);
                return OnlineTargetList.Invalid;
            }
        }

        internal static void Save(string path, OnlineTargetList list, AuditLog? audit = null)
        {
            if (!list.IsValid) throw new ArgumentException("Cannot save an invalid online-target list.", nameof(list));
            string text = string.Concat(list.Entries.Select(entry => "target=" + entry + "\n"));
            bool changed;
            using (JournalFileLock.Acquire(path + ".lock"))
            {
                changed = !File.Exists(path) || File.ReadAllText(path) != text;
                if (!changed) return;
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
            }
            try
            {
                (audit ?? AuditLog.Current).Append("online-targets-change", "", "workbench", "", "", actor: ActorScope.Workbench);
            }
            catch (Exception ex) { Trace.TraceWarning("Online-target settings audit unavailable: " + ex.GetType().Name); }
        }
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TiaOpenness.Shared
{
    internal sealed class ApprovalSettings
    {
        internal bool Enabled { get; }
        internal int TimeoutSeconds { get; }
        internal static string SettingsPath => Path.Combine(DataLocations.Current.ConfigDirectory, "approval.settings");
        internal static bool IsAdministrativeTarget(string path)
        {
            path = path.Trim();
            string normalized = path.Replace('\\', '/');
            string name = normalized.Substring(normalized.LastIndexOf('/') + 1);
            if (name.Length >= 2 && name[1] == ':') name = name.Substring(2);
            int stream = name.IndexOf(':'); if (stream >= 0) name = name.Substring(0, stream);
            name = name.TrimEnd(' ', '.');
            if (name.Equals("approval.settings", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(name, @"^APPROV~[0-9]+\.SET$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return true;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || string.IsNullOrWhiteSpace(path)) return false;
            // File IDs also cover short names, junctions and existing hard links. An
            // unreadable identity is refused; there is no write probe or native TIA call.
            using (var expected = OpenIdentity(SettingsPath, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (expected.IsInvalid) return !MissingIdentity(Marshal.GetLastWin32Error());
                using (var target = OpenIdentity(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (target.IsInvalid) return !MissingIdentity(Marshal.GetLastWin32Error());
                    if (!ReadIdentity(expected, out var a) || !ReadIdentity(target, out var b)) return true;
                    return a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow;
                }
            }
        }
        private static bool MissingIdentity(int error) => error == 2 || error == 3 || error == 123;
        [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
        { internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenIdentity(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadIdentity(SafeFileHandle handle, out FileIdentity identity);
        internal ApprovalSettings(bool enabled = true, int timeoutSeconds = 120)
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            Enabled = enabled; TimeoutSeconds = timeoutSeconds;
        }
        internal static ApprovalSettings Load(string path)
        {
            try
            {
                using (JournalFileLock.Acquire(path + ".lock"))
                {
                    if (!File.Exists(path)) return new ApprovalSettings();
                    var lines = File.ReadAllLines(path);
                    if (lines.Length != 2 || (lines[0] != "enabled=true" && lines[0] != "enabled=false")
                        || !lines[1].StartsWith("timeoutSeconds=", StringComparison.Ordinal)
                        || !int.TryParse(lines[1].Substring(15), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                        throw new InvalidDataException("Invalid approval settings.");
                    return new ApprovalSettings(lines[0] == "enabled=true", seconds);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { Trace.TraceWarning("Approval settings unavailable: " + ex.GetType().Name); return new ApprovalSettings(); }
        }
        internal void Save(string path, AuditLog? audit = null)
        {
            bool changed;
            using (JournalFileLock.Acquire(path + ".lock"))
            {
                var previous = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
                bool before = previous.Length == 0 || previous[0] != "enabled=false";
                changed = before != Enabled;
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write("enabled=" + (Enabled ? "true" : "false") + "\ntimeoutSeconds=" + TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "\n");
                    writer.Flush(); stream.Flush(true);
                }
            }
            try { if (changed) (audit ?? AuditLog.Current).ApprovalSwitch("workbench", Enabled); }
            catch (Exception ex) { Trace.TraceWarning("Approval switch audit unavailable: " + ex.GetType().Name); }
        }
    }
}
